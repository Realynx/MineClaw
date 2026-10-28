using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;

namespace MineClaw.App.Supervisor;

/// <summary>
/// The supervisor's admin-write routes (<c>/api/admin/*</c>, the only place POST is accepted):
/// <list type="bullet">
/// <item><c>POST /api/admin/memory</c> MemoryEdit → MemoryEditResult (200 ok, 400 rejected)</item>
/// <item><c>POST /api/admin/rebuild/estimate</c> RebuildRequest → RebuildEstimate</item>
/// <item><c>POST /api/admin/rebuild</c> RebuildRequest → 202 <c>{job_id}</c> (409 while one runs);
/// <c>GET /api/admin/rebuild/{job_id}</c> → the job; <c>POST /api/admin/rebuild/{job_id}/cancel</c></item>
/// <item><c>POST /api/admin/backup</c> → <c>{path}</c>; <c>GET /api/admin/audit?max=</c> → <c>{entries}</c>;
/// <c>GET /api/admin/schema</c> → kinds, ops and form fields</item>
/// <item><c>POST /api/admin/activity/compact</c> <c>{dry_run?, who?}</c> → the compaction report (a real run backs up first)</item>
/// <item><c>POST /api/admin/shard/import/preview</c> <c>{shard (base64 .mcshard), mode, conflict, include_rules, include_world, who}</c>
/// → the import diff; <c>POST /api/admin/shard/import</c> (same body) → applies it inside the bot with its own stores and starts
/// the scoped RAPTOR rebuild in the background (<c>job_id</c>, polled like a rebuild). Bodies up to <see cref="ShardMaxBodyBytes"/>,
/// read only once the flag and the token header check out.</item>
/// </list>
/// Every route is 403 unless <see cref="SupervisorOptions.AllowAdminWrites"/> is on AND a token is configured AND the request
/// carries it in the <c>X-Supervisor-Token</c> header (never <c>?token=</c>; constant-time compare).
/// </summary>
public static class SupervisorAdminRoutes
{
    public const string Prefix = "/api/admin";
    /// <summary>Request bodies above this are refused (413) without being read further.</summary>
    public const int MaxBodyBytes = 256 * 1024;
    /// <summary>Shard uploads (base64 in JSON) may be this big.</summary>
    public const int ShardMaxBodyBytes = 64 * 1024 * 1024;

    public static bool IsShardPath(string path) =>
        path.TrimEnd('/').StartsWith(Prefix + "/shard/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Marks a body that was over <see cref="MaxBodyBytes"/>.</summary>
    internal const string TooLarge = "\0body-too-large";

    public static bool IsAdminPath(string path)
    {
        var p = path.TrimEnd('/');
        return p.Equals(Prefix, StringComparison.OrdinalIgnoreCase) || p.StartsWith(Prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The body of a POST to an admin route (null otherwise), read up to the cap.</summary>
    /// <param name="o">With it, a shard upload's large body is read only when admin writes are on and the token header matches
    /// (an anonymous client can't make the bot buffer 64 MB); without it shard bodies are refused.</param>
    public static async Task<string?> ReadBodyAsync(HttpListenerRequest req, CancellationToken ct, SupervisorOptions? o = null)
    {
        var path = req.Url?.AbsolutePath ?? "/";
        if (!req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) || !IsAdminPath(path) || !req.HasEntityBody) return null;
        int cap = MaxBodyBytes;
        if (IsShardPath(path))
        {
            if (o is not { AllowAdminWrites: true } || string.IsNullOrEmpty(o.Token) || !TokenOk(req.Headers["X-Supervisor-Token"], o.Token)) return null;
            cap = ShardMaxBodyBytes;
        }
        if (req.ContentLength64 > cap) return TooLarge;
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        while (true)
        {
            int r = await req.InputStream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (r == 0) break;
            ms.Write(buf, 0, r);
            if (ms.Length > cap) return TooLarge;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static bool TokenOk(string? given, string token)
    {
        if (string.IsNullOrEmpty(given)) return false;
        var a = Encoding.UTF8.GetBytes(given);
        var b = Encoding.UTF8.GetBytes(token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static async Task<SupervisorReply> HandleAsync(SupervisorRequest r, SupervisorOptions o, ISupervisorAdmin? admin, CancellationToken ct)
    {
        if (!o.AllowAdminWrites) return Json(403, new { error = "admin writes are off (Supervisor:AllowAdminWrites)" });
        if (string.IsNullOrEmpty(o.Token)) return Json(403, new { error = "admin writes need Supervisor:Token" });
        if (!TokenOk(r.TokenHeader, o.Token)) return Json(403, new { error = "X-Supervisor-Token header required" });
        if (admin == null) return Json(403, new { error = "admin writes are not available in this process" });
        if (r.Body == TooLarge) return Json(413, new { error = $"body over {(IsShardPath(r.Path) ? ShardMaxBodyBytes : MaxBodyBytes)} bytes" });

        var tail = r.Path.TrimEnd('/')[Prefix.Length..].ToLowerInvariant();
        bool post = r.Method.Equals("POST", StringComparison.OrdinalIgnoreCase);
        bool get = r.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) || r.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
        try
        {
            switch (tail)
            {
                case "/memory":
                    if (!post) return NotAllowed("POST");
                    {
                        var res = await admin.ApplyAsync(ParseEdit(r.Body), ct).ConfigureAwait(false);
                        return Json(res.Ok ? 200 : 400, res);
                    }
                case "/rebuild/estimate":
                    return post ? Json(200, admin.Estimate(ParseRebuild(r.Body))) : NotAllowed("POST");
                case "/rebuild":
                    if (!post) return NotAllowed("POST");
                    try { return Json(202, new { job_id = admin.StartRebuild(ParseRebuild(r.Body)) }); }
                    catch (InvalidOperationException ex) { return Json(409, new { error = ex.Message }); }
                case "/backup":
                    if (!post) return NotAllowed("POST");
                    return Json(200, new { path = admin.Backup(Who(r.Body)) });
                case "/audit":
                    if (!get) return NotAllowed("GET");
                    {
                        int max = r.Query.TryGetValue("max", out var m) && int.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mv) ? Math.Clamp(mv, 1, 5000) : 200;
                        return Json(200, new { target = admin.Target, entries = admin.Audit(max) });
                    }
                case "/shard/import/preview":
                case "/shard/import":
                {
                    if (!post) return NotAllowed("POST");
                    if (!admin.SupportsShards) return Json(403, new { error = "memory shards are not available in this process" });
                    var (bytes, options) = ParseShard(r.Body);
                    try
                    {
                        if (tail == "/shard/import/preview") return Json(200, admin.PreviewShard(bytes, options));
                        var res = await admin.ImportShardAsync(bytes, options, ct).ConfigureAwait(false);
                        return Json(res.Ok ? 200 : 400, res);
                    }
                    catch (ShardFormatException ex) { return Json(400, new { error = "shard refused: " + ex.Message }); }
                }
                case "/activity/compact":
                    if (!post) return NotAllowed("POST");
                    {
                        var e = string.IsNullOrWhiteSpace(r.Body) ? default : Root(r.Body);
                        bool dry = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("dry_run", out var dv) && dv.ValueKind == JsonValueKind.True;
                        var res = await admin.CompactActivityAsync(dry, Who(r.Body), ct).ConfigureAwait(false);
                        return Json(res.Ok ? 200 : 400, res);
                    }
                case "/schema":
                    if (!get) return NotAllowed("GET");
                    return Json(200, new
                    {
                        target = admin.Target,
                        kinds = MemoryKinds.All.ToDictionary(k => k, k => new { ops = MemoryEditSchema.Ops(k), fields = MemoryEditSchema.For(k) }),
                    });
            }
            if (tail.StartsWith("/rebuild/", StringComparison.Ordinal))
            {
                var rest = r.Path.TrimEnd('/')[(Prefix.Length + "/rebuild/".Length)..];
                if (rest.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase))
                {
                    if (!post) return NotAllowed("POST");
                    var id = rest[..^"/cancel".Length];
                    return admin.CancelJob(id) ? Json(200, new { job_id = id, cancelled = true }) : Json(404, new { error = "no running job " + id });
                }
                if (!get) return NotAllowed("GET");
                return admin.Job(rest) is { } job ? Json(200, job) : Json(404, new { error = "no such job" });
            }
            return Json(404, new { error = "not found" });
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException) { return Json(400, new { error = "bad request body: " + ex.Message }); }
        catch (OperationCanceledException) { return Json(503, new { error = "cancelled" }); }
        catch (Exception ex) { return Json(500, new { error = ex.Message }); }
    }

    private static SupervisorReply NotAllowed(string allow) => Json(405, new { error = $"use {allow}" }, new Dictionary<string, string> { ["Allow"] = allow });

    private static SupervisorReply Json(int status, object value, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, "application/json; charset=utf-8", SupervisorJson.Bytes(value), headers);

    private static JsonElement Root(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new FormatException("JSON body required");
        using var d = JsonDocument.Parse(body);
        if (d.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("JSON object required");
        return d.RootElement.Clone();
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? Scalar(v) : null;

    /// <summary>Strings as they are; numbers/bools as their JSON text; null/absent as null.</summary>
    private static string? Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => v.GetRawText(),
        _ => throw new FormatException("fields must be strings, numbers or booleans"),
    };

    private static string Who(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "admin";
        try { return Str(Root(body), "who") is { Length: > 0 } w ? w : "admin"; }
        catch (Exception ex) when (ex is JsonException or FormatException) { return "admin"; }
    }

    /// <summary><c>{"op","kind","id"?,"fields":{name: string|number|bool|null},"who","reason"?}</c> (id may be a number).</summary>
    internal static MemoryEdit ParseEdit(string? body)
    {
        var e = Root(body);
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (e.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
            foreach (var p in f.EnumerateObject()) fields[p.Name] = Scalar(p.Value);
        return new MemoryEdit(Str(e, "op") ?? "", Str(e, "kind") ?? "", Str(e, "id"), fields, Str(e, "who") ?? "admin", Str(e, "reason"));
    }

    /// <summary><c>{"shard":base64,"mode":"merge|replace|swap|add-mode|remove-mode","conflict":"newer|shard|mine","include_rules","include_world","who","max_usd"}</c>.</summary>
    internal static (byte[] Shard, ShardImportOptions Options) ParseShard(string? body)
    {
        var e = Root(body);
        var b64 = Str(e, "shard") ?? throw new FormatException("shard (base64 .mcshard) required");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { throw new FormatException("shard is not valid base64"); }
        static bool Flag(JsonElement e, string n) => e.TryGetProperty(n, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "true"));
        decimal max = Str(e, "max_usd") is { Length: > 0 } ms ? decimal.Parse(ms, NumberStyles.Float, CultureInfo.InvariantCulture) : 0.25m;
        return (bytes, new ShardImportOptions
        {
            Mode = ShardImportOptions.ParseMode(Str(e, "mode")), Conflict = ShardImportOptions.ParseConflict(Str(e, "conflict")),
            IncludeRules = Flag(e, "include_rules"), IncludeWorld = Flag(e, "include_world"), Who = Str(e, "who") ?? "admin", RebuildMaxUsd = max,
        });
    }

    /// <summary><c>{"scope":"node|kind","node"?:int,"kind"?:string,"max_usd"?:number (default 0.50),"who"?}</c>.</summary>
    internal static RebuildRequest ParseRebuild(string? body)
    {
        var e = Root(body);
        int? node = Str(e, "node") is { Length: > 0 } ns ? int.Parse(ns, NumberStyles.Integer, CultureInfo.InvariantCulture) : null;
        decimal max = Str(e, "max_usd") is { Length: > 0 } ms ? decimal.Parse(ms, NumberStyles.Float, CultureInfo.InvariantCulture) : 0.50m;
        return new RebuildRequest(Str(e, "scope") ?? "", node, Str(e, "kind"), max, Str(e, "who") ?? "admin");
    }
}
