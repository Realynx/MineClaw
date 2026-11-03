using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Routines;
using MineClaw.ViewerBridge.Data;
using MineClaw.ViewerBridge.Graph;
using MineClaw.ViewerBridge.Live;
using MineClaw.ViewerBridge.Security;
using MineClaw.ViewerBridge.Supervisor;

namespace MineClaw.ViewerBridge.Endpoints;

/// <summary>Query parsing with the contract's defaults and bounds (Nest validates too; the bridge never trusts it blindly).</summary>
internal static class Q
{
    public static string? Str(HttpContext c, string k, int max = 1000)
    {
        var v = c.Request.Query[k].ToString();
        if (v.Length == 0) return null;
        if (v.Length > max) throw new ArgumentException($"{k} too long");
        return v;
    }

    public static int Int(HttpContext c, string k, int def, int min, int max)
    {
        var v = c.Request.Query[k].ToString();
        if (v.Length == 0) return def;
        if (!int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i) || i < min || i > max) throw new ArgumentException($"{k} must be an integer in {min}..{max}");
        return i;
    }

    public static int? IntOpt(HttpContext c, string k, int min, int max) =>
        c.Request.Query[k].ToString().Length == 0 ? null : Int(c, k, 0, min, max);

    public static string Enum(HttpContext c, string k, string def, params string[] allowed)
    {
        var v = c.Request.Query[k].ToString();
        if (v.Length == 0) return def;
        if (!allowed.Contains(v, StringComparer.Ordinal)) throw new ArgumentException($"{k} must be one of {string.Join("|", allowed)}");
        return v;
    }

    public static bool Bool(HttpContext c, string k) => c.Request.Query[k].ToString() is "1" or "true";
}

public static class BridgeEndpoints
{
    public const string Version = "1.0.0";

    /// <summary>Strict request bodies: unknown keys are a 400 (a client cannot smuggle who/player/allow_commands).</summary>
    public static readonly JsonSerializerOptions Strict = new(BridgeJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private static IResult Json<T>(T value, int status = 200) => Results.Json(value, BridgeJson.Options, "application/json; charset=utf-8", status);

    private static T Body<T>(HttpContext ctx) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(ctx.BodyBytes(), Strict) ?? throw new ArgumentException("body required"); }
        catch (JsonException ex) { throw new ArgumentException("invalid body: " + (ex.Path is { Length: > 0 } p ? p : "malformed JSON")); }
    }

    private static IResult RequireRole(HttpContext ctx, string role) => ApiErrors.Forbidden($"{role} role required");

    public static void Map(WebApplication app)
    {
        var v1 = app.MapGroup("/v1");

        v1.MapGet("/health", async (HttpContext ctx, BankHost banks, SupervisorClient sup) =>
        {
            string db;
            try { await banks.GetAsync(ctx.RequestAborted); db = "up"; }
            catch (BankUnavailableException) { db = "down"; }
            // "up" | "down" (Nest's status indicator): a supervisor answer in the last 10 s counts, else one quick probe.
            bool up = sup.LastOk is { } ok && (sup.LastFailure is not { } f || ok > f) && DateTimeOffset.UtcNow - ok < TimeSpan.FromSeconds(10);
            if (!up)
            {
                try { up = (await sup.GetAsync("api/bots", TimeSpan.FromMilliseconds(1500), ctx.RequestAborted)).Status == 200; }
                catch (SupervisorUnavailableException) { up = false; }
            }
            string bot = up ? "up" : "down";
            return Json(new { ok = true, version = Version, db, supervisor = bot, latency_ms = sup.LatencyMs >= 0 ? Math.Round(sup.LatencyMs, 1) : (double?)null });
        });

        // ---------------- db reads (bridge-native) ----------------
        v1.MapGet("/overview", async (HttpContext ctx, BankHost banks, BridgeConfig cfg) => Json(DbViews.Overview(await banks.GetAsync(ctx.RequestAborted), cfg)));

        v1.MapGet("/memory/kinds", async (HttpContext ctx, BankHost banks) => Json(new MemoryBrowser(await banks.GetAsync(ctx.RequestAborted)).KindsInfo()));

        v1.MapGet("/memory/{kind}", async (HttpContext ctx, string kind, BankHost banks) =>
        {
            if (MemoryBrowser.Kind(kind) == null) return ApiErrors.NotFound("unknown memory kind");
            var q = new MemoryListQuery(Q.Str(ctx, "q", 500), Q.Enum(ctx, "sort", "updated", "updated", "name", "type", "state", "dim", "conf", "id"),
                Q.Enum(ctx, "dir", "desc", "asc", "desc"), Q.Enum(ctx, "dim", "any", "any", "overworld", "nether", "none"), Q.Str(ctx, "state", 64),
                Q.Int(ctx, "offset", 0, 0, int.MaxValue), Q.Int(ctx, "limit", 100, 1, 500));
            var bank = await banks.GetAsync(ctx.RequestAborted);
            return Json(await Task.Run(() => new MemoryBrowser(bank).Page(kind, q), ctx.RequestAborted));
        });

        v1.MapGet("/memory/{kind}/{id}", async (HttpContext ctx, string kind, string id, BankHost banks) =>
        {
            if (MemoryBrowser.Kind(kind) == null || id.Length is 0 or > 256) return ApiErrors.NotFound("unknown memory kind");
            var d = new MemoryBrowser(await banks.GetAsync(ctx.RequestAborted)).Detail(kind, id);
            return d == null ? ApiErrors.NotFound("no such record") : Json(d);
        });

        v1.MapGet("/search", async (HttpContext ctx, BankHost banks) =>
        {
            var text = Q.Str(ctx, "q", 1000) ?? throw new ArgumentException("q required");
            bool lexical = Q.Enum(ctx, "mode", "auto", "auto", "lexical") == "lexical";
            int k = Q.Int(ctx, "k", 30, 1, 200);
            var kind = Q.Enum(ctx, "kind", "all", "all", "episode", "knowledge", "gamedata", "structure", "note", "player", "world", "claim", "skill", "routine", "summary", "selfnote");
            var dim = Q.Enum(ctx, "dim", "any", "any", "overworld", "nether");
            var bank = await banks.GetAsync(ctx.RequestAborted);
            return Json(await SearchService.SearchAsync(bank, text, lexical, kind == "all" ? null : [kind], dim switch { "overworld" => 0, "nether" => -1, _ => null }, k,
                ctx.RequestAborted));
        });

        v1.MapGet("/raptor/tree", async (HttpContext ctx, BankHost banks) =>
            Json(RaptorViews.Tree(await banks.GetAsync(ctx.RequestAborted), Q.IntOpt(ctx, "root", 0, int.MaxValue), Q.Int(ctx, "depth", 1, 0, 8))));

        v1.MapGet("/raptor/graph", async (HttpContext ctx, BankHost banks) =>
        {
            var layout = Q.Enum(ctx, "layout", "icicle", "icicle", "radial", "nodelink");
            var colorBy = Q.Enum(ctx, "color_by", "kind", "kind", "time", "cluster");
            var focus = Q.IntOpt(ctx, "focus", 0, int.MaxValue);
            var bank = await banks.GetAsync(ctx.RequestAborted);
            var g = await Task.Run(bank.Graph, ctx.RequestAborted);
            if (g == null) return Json(new RaptorGraphDto(layout, colorBy, bank.StateKey, null, [], [], [], []));
            return Json(GraphLayoutService.Layout(g, layout, colorBy, focus, bank.StateKey));
        });

        v1.MapGet("/raptor/node/{id:int}/leaves", async (HttpContext ctx, int id, BankHost banks) =>
            RaptorViews.Leaves(await banks.GetAsync(ctx.RequestAborted), id) is { } r ? Json(r) : ApiErrors.NotFound("no such node"));

        v1.MapGet("/raptor/node/{id:int}", async (HttpContext ctx, int id, BankHost banks) =>
            RaptorViews.Node(await banks.GetAsync(ctx.RequestAborted), id) is { } r ? Json(r) : ApiErrors.NotFound("no such node"));

        v1.MapGet("/personality", async (HttpContext ctx, BankHost banks) =>
            Json(DbViews.Personality(await banks.GetAsync(ctx.RequestAborted), Q.Str(ctx, "scope", 128) ?? "main")));

        // ---------------- shards ----------------
        v1.MapGet("/shards/packs", async (HttpContext ctx, BankHost banks, BridgeConfig cfg) => Json(ShardViews.Packs(await banks.GetAsync(ctx.RequestAborted), cfg.PacksDir)));

        v1.MapPost("/shards/export/preview", async (HttpContext ctx, BankHost banks, BridgeConfig cfg) =>
        {
            var body = Body<ShardExportBody>(ctx);
            Validate(body);
            var bank = await banks.GetAsync(ctx.RequestAborted);
            return Json(await ShardViews.PreviewAsync(bank, ShardViews.Request(body, ctx.Identity().Who, cfg.Raw["Minecraft:Username"]), ctx.RequestAborted));
        });

        v1.MapPost("/shards/export", async (HttpContext ctx, BankHost banks, BridgeConfig cfg) =>
        {
            var body = Body<ShardExportBody>(ctx);
            Validate(body);
            var bank = await banks.GetAsync(ctx.RequestAborted);
            var (bytes, _) = await ShardViews.ExportAsync(bank, ShardViews.Request(body, ctx.Identity().Who, cfg.Raw["Minecraft:Username"]), ctx.RequestAborted);
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{ShardViews.FileName(body.Name, DateTimeOffset.UtcNow)}\"";
            return Results.Bytes(bytes, "application/zip");
        });

        v1.MapPost("/shards/import/preview", async (HttpContext ctx, BridgeConfig cfg) =>
        {
            var id = ctx.Identity();
            if (!id.IsAdmin) return RequireRole(ctx, "admin");
            var (shardBytes, options) = await ReadUploadAsync(ctx);
            Shard shard;
            try { shard = ShardFile.Read(shardBytes); }
            catch (Exception ex) when (ex is ShardFormatException or InvalidDataException) { return ApiErrors.BadRequest("shard refused: " + ex.Message); }
            var scratch = Path.Combine(Path.GetTempPath(), "mineclaw-bridge");
            var r = await Task.Run(() => ShardViews.ImportPreview(cfg, shard, ShardViews.Options(options, id.Who), scratch), ctx.RequestAborted);
            return Json(r);
        });

        // ---------------- scripts ----------------
        v1.MapGet("/scripts", async (HttpContext ctx, BankHost banks, SupervisorClient sup) =>
        {
            if (Q.Enum(ctx, "source", "live", "live", "db") == "live")
            {
                await SupervisorProxy.ForwardAsync(ctx, sup, HttpMethod.Get, "scripts", "api/scripts", null);
                return Results.Empty;
            }
            var bank = await banks.GetAsync(ctx.RequestAborted);
            return Json(new ScriptLibraryResponse(DateTimeOffset.UtcNow, await Task.Run(() => new DbScripts(bank.Db).List(), ctx.RequestAborted), "db"));
        });

        v1.MapGet("/scripts/{name}/diff", async (HttpContext ctx, string name, BankHost banks, SupervisorClient sup) =>
        {
            var source = Q.Enum(ctx, "source", "db", "live", "db");
            int a = Q.Int(ctx, "a", 0, 1, int.MaxValue), b = Q.Int(ctx, "b", 0, 1, int.MaxValue), context = Q.Int(ctx, "context", 3, 0, 20);
            if (a == 0 || b == 0) throw new ArgumentException("a and b are required");
            var va = await ScriptVersionAsync(ctx, banks, sup, source, name, a);
            var vb = await ScriptVersionAsync(ctx, banks, sup, source, name, b);
            if (va == null || vb == null) return ApiErrors.NotFound("no such script version");
            return Json(ScriptDiff.Build(va.Script.Name, a, b, va.Code, vb.Code, context));
        });

        v1.MapGet("/scripts/{name}/lint", async (HttpContext ctx, string name, BankHost banks, SupervisorClient sup) =>
        {
            var source = Q.Enum(ctx, "source", "db", "live", "db");
            var d = await ScriptVersionAsync(ctx, banks, sup, source, name, Q.IntOpt(ctx, "v", 1, int.MaxValue));
            return d == null ? ApiErrors.NotFound("no such script") : Json(new ScriptLintResponse(d.Script.Name, d.Version.V, d.Lint, d.Params, source == "db"));
        });

        v1.MapGet("/scripts/{name}", async (HttpContext ctx, string name, BankHost banks, SupervisorClient sup) =>
        {
            if (name.Length is 0 or > 128) return ApiErrors.NotFound("no such script");
            var source = Q.Enum(ctx, "source", "live", "live", "db");
            var v = Q.IntOpt(ctx, "v", 1, int.MaxValue);
            if (source == "live")
            {
                var rel = $"api/scripts/{Uri.EscapeDataString(name)}" + (v is { } x ? "/" + x.ToString(CultureInfo.InvariantCulture) : "");
                await SupervisorProxy.ForwardAsync(ctx, sup, HttpMethod.Get, "scripts/" + name, rel, null);
                return Results.Empty;
            }
            var bank = await banks.GetAsync(ctx.RequestAborted);
            return new DbScripts(bank.Db).Version(name, v) is { } d ? Json(d) : ApiErrors.NotFound("no such script");
        });

        // ---------------- designs ----------------
        v1.MapGet("/designs", async (HttpContext ctx, BankHost banks, BridgeConfig cfg) =>
        {
            var bank = await banks.GetAsync(ctx.RequestAborted);
            return Json(new DesignViews(bank.Db, cfg.ServerVersion).List(Q.Str(ctx, "q", 200), Q.Int(ctx, "offset", 0, 0, int.MaxValue), Q.Int(ctx, "limit", 100, 1, 500)));
        });

        v1.MapGet("/designs/{id:long}/picture.png", async (HttpContext ctx, long id, BankHost banks, BridgeConfig cfg) =>
        {
            var png = new DesignViews((await banks.GetAsync(ctx.RequestAborted)).Db, cfg.ServerVersion).Picture(id);
            return png is { Length: > 0 } ? Results.Bytes(png, "image/png") : ApiErrors.NotFound("no picture");
        });

        v1.MapGet("/designs/{id:long}/diff", async (HttpContext ctx, long id, BankHost banks, BridgeConfig cfg) =>
        {
            string? Side(string k)
            {
                var s = Q.Str(ctx, k, 12);
                if (s == null || s == "world" || s.All(char.IsAsciiDigit)) return s;
                throw new ArgumentException($"{k} must be a revision number or \"world\"");
            }
            var d = new DesignViews((await banks.GetAsync(ctx.RequestAborted)).Db, cfg.ServerVersion).Diff(id, Side("a"), Side("b"), Q.Int(ctx, "max", 500, 1, 5000));
            return d == null ? ApiErrors.NotFound("no such design or revision") : Json(d);
        });

        v1.MapGet("/designs/{id:long}", async (HttpContext ctx, long id, BankHost banks, BridgeConfig cfg) =>
            new DesignViews((await banks.GetAsync(ctx.RequestAborted)).Db, cfg.ServerVersion).Detail(id) is { } d ? Json(d) : ApiErrors.NotFound("no such design"));

        // ---------------- memory activity heat ----------------
        v1.MapGet("/memory-activity/heat", async (HttpContext ctx, BankHost banks, MemoryActivityStore store, BridgeConfig cfg, TimeProvider time) =>
        {
            var brain = Q.Str(ctx, "brain", 64);
            int window = Q.Int(ctx, "window_s", 900, 10, 24 * 3600);
            var bank = await banks.GetAsync(ctx.RequestAborted);
            await store.RefreshAsync(ctx.RequestAborted, TimeSpan.FromMilliseconds(500));
            var (events, inCtx) = store.Snapshot();
            double half = Math.Clamp(cfg.Bridge.HeatHalfLifeS, 1, window);
            var at = time.GetUtcNow();
            var r = bank.ReadTree(t => MemoryHeat.Compute(events, inCtx, t, bank.NodeOfDoc, MemoryBank.StateKeyOf(t), at, window, half, brain),
                MemoryHeat.Compute(events, inCtx, null, bank.NodeOfDoc, "none", at, window, half, brain));
            return Json(r);
        });

        // ---------------- admin (bridge-native) ----------------
        v1.MapGet("/admin/schema", (HttpContext ctx) => ctx.Identity().IsAdmin ? Json(AdminViews.Schema()) : RequireRole(ctx, "admin"));

        v1.MapPost("/groom/preview", async (HttpContext ctx, BankHost banks, BridgeConfig cfg) =>
        {
            if (!ctx.Identity().IsAdmin) return RequireRole(ctx, "admin");
            var body = Body<GroomPreviewBody>(ctx);
            if (!AdminViews.GroomSources.Contains(body.Source)) throw new ArgumentException("unknown source");
            if (body.Text is { Length: > 200_000 }) throw new ArgumentException("text too long");
            return Json(await AdminViews.GroomAsync(await banks.GetAsync(ctx.RequestAborted), cfg, body, ctx.RequestAborted));
        });

        // ---------------- supervisor writes with identity ----------------
        v1.MapPost("/sup/viewer/chat", async (HttpContext ctx, SupervisorClient sup) =>
        {
            var id = ctx.Identity();
            if (!id.Has("chat")) return RequireRole(ctx, "chat");
            if (id.Player.Length == 0) return ApiErrors.Forbidden("no linked player");
            var obj = SupervisorProxy.ParseObject(ctx.BodyBytes()) ?? throw new ArgumentException("body must be a JSON object");
            if (obj.ContainsKey("player") || obj.ContainsKey("allow_commands") || obj.ContainsKey("who")) return ApiErrors.BadRequest("player and allow_commands are set by the bridge");
            if (obj.Any(kv => kv.Key is not ("bot" or "text" or "request_id"))) return ApiErrors.BadRequest("unknown body keys");
            string? bot = (string?)obj["bot"], text = (string?)obj["text"], requestId = (string?)obj["request_id"];
            if (string.IsNullOrWhiteSpace(bot) || bot.Length > 64) return ApiErrors.BadRequest("bot required");
            if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > 256) return ApiErrors.BadRequest("text: 1-256 characters");
            if (requestId is not { Length: >= 8 and <= 64 } || !requestId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return ApiErrors.BadRequest("request_id: 8-64 of A-Z a-z 0-9 _ -");
            var up = new JsonObject
            {
                ["bot"] = bot.Trim(), ["text"] = text.Trim(), ["player"] = id.Player, ["allow_commands"] = id.Has("commands"), ["request_id"] = requestId,
            };
            HttpResponseMessage res;
            try { res = await sup.SendAsync(HttpMethod.Post, "api/viewer/chat", new StringContent(up.ToJsonString(), Encoding.UTF8, "application/json"), TimeSpan.FromSeconds(8), false, ctx.RequestAborted); }
            catch (SupervisorUnavailableException ex) { return ApiErrors.Result(503, "upstream_unavailable", ex.Message); }
            using (res)
            {
                if (!res.IsSuccessStatusCode) { await SupervisorProxy.MapErrorAsync(ctx, res, "viewer/chat", true); return Results.Empty; }
                var reply = JsonNode.Parse(await res.Content.ReadAsByteArrayAsync(ctx.RequestAborted)) as JsonObject;
                bool accepted = reply?["accepted"]?.GetValue<bool>() == true;
                long? seq = reply?["id"] is JsonValue sv && sv.TryGetValue<long>(out var s) ? s : null;
                return Json(new ChatAccepted(accepted, requestId, seq is > 0 ? seq : null, accepted ? null : "the bot did not take the line"), 202);
            }
        });

        v1.MapPost("/sup/admin/shard/import", async (HttpContext ctx, SupervisorClient sup) =>
        {
            var id = ctx.Identity();
            if (!id.IsAdmin) return RequireRole(ctx, "admin");
            var (bytes, o) = await ReadUploadAsync(ctx);
            var up = new JsonObject
            {
                ["shard"] = Convert.ToBase64String(bytes), ["mode"] = o.Mode, ["conflict"] = o.Conflict, ["include_rules"] = o.IncludeRules,
                ["include_world"] = o.IncludeWorld, ["max_usd"] = o.MaxUsd, ["who"] = id.Who,
            };
            await SupervisorProxy.ForwardAsync(ctx, sup, HttpMethod.Post, "admin/shard/import", "api/admin/shard/import",
                new StringContent(up.ToJsonString(), Encoding.UTF8, "application/json"));
            return Results.Empty;
        });

        v1.MapGet("/sup/world/chunks", async (HttpContext ctx, SupervisorClient sup, ChunkCache cache) =>
        {
            await ChunkProxy.HandleAsync(ctx, sup, cache);
            return Results.Empty;
        });

        v1.MapMethods("/sup/{**rest}", ["GET", "HEAD", "POST"], async (HttpContext ctx, SupervisorClient sup, ChunkCache cache) =>
        {
            await SupervisorProxy.HandleAsync(ctx, sup, cache);
            return Results.Empty;
        });

        // ---------------- renders ----------------
        v1.MapGet("/render/tile/{bot}.png", async (HttpContext ctx, string bot, SupervisorClient sup) =>
        {
            if (bot.Length is 0 or > 64) return ApiErrors.NotFound();
            var mode = Q.Enum(ctx, "mode", "iso", "sheet", "brain", "eye", "iso", "shoulder", "orbit", "high", "top", "side", "layers");
            var radius = Q.IntOpt(ctx, "radius", 3, 12);
            var xray = Q.Enum(ctx, "xray", "auto", "on", "off", "auto");
            var rel = $"view/{Uri.EscapeDataString(bot)}?mode={mode}" + (radius is { } r ? $"&radius={r}" : "") + (xray != "auto" ? $"&xray={xray}" : "");
            await SupervisorProxy.ForwardAsync(ctx, sup, HttpMethod.Get, "view.png", rel, null);
            return Results.Empty;
        });

        // ---------------- live ----------------
        v1.MapGet("/live/stream", async (HttpContext ctx, LiveHub hub) =>
        {
            await hub.StreamAsync(ctx);
            return Results.Empty;
        });
    }

    private static void Validate(ShardExportBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Length > 128) throw new ArgumentException("name: 1-128 characters");
        if (b.Description is { Length: > 2000 }) throw new ArgumentException("description too long");
        if (b.Selection.Mode is not ("subtree" or "filter" or "person" or "pack")) throw new ArgumentException("selection.mode");
    }

    /// <summary>multipart/form-data: a <c>shard</c> file part (≤ 48 MB) and an <c>options</c> part (ShardImportOptions JSON, strict).</summary>
    private static async Task<(byte[] Shard, ShardImportOptionsBody Options)> ReadUploadAsync(HttpContext ctx)
    {
        if (!ctx.Request.HasFormContentType) throw new ArgumentException("multipart/form-data with shard and options parts required");
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var file = form.Files.GetFile("shard") ?? throw new ArgumentException("shard part required");
        if (file.Length > 48L * 1024 * 1024) throw new InvalidDataException("shard too large");
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ctx.RequestAborted);
        var optionsText = form["options"].ToString();
        ShardImportOptionsBody options;
        try { options = optionsText.Length == 0 ? new ShardImportOptionsBody() : JsonSerializer.Deserialize<ShardImportOptionsBody>(optionsText, Strict) ?? new ShardImportOptionsBody(); }
        catch (JsonException) { throw new ArgumentException("options: invalid ShardImportOptions JSON"); }
        if (options.Mode is not ("merge" or "replace-section" or "swap-pack" or "add-mode" or "remove-mode")) throw new ArgumentException("options.mode");
        if (options.Conflict is not ("keep-newer" or "prefer-shard" or "keep-mine")) throw new ArgumentException("options.conflict");
        if (options.MaxUsd is < 0 or > 5) throw new ArgumentException("options.max_usd: 0..5");
        return (ms.ToArray(), options);
    }

    private static async Task<ScriptVersionDetail?> ScriptVersionAsync(HttpContext ctx, BankHost banks, SupervisorClient sup, string source, string name, int? v)
    {
        if (name.Length is 0 or > 128) return null;
        if (source == "db") return new DbScripts((await banks.GetAsync(ctx.RequestAborted)).Db).Version(name, v);
        var rel = $"api/scripts/{Uri.EscapeDataString(name)}" + (v is { } x ? "/" + x.ToString(CultureInfo.InvariantCulture) : "");
        var (status, body, _, _) = await sup.GetAsync(rel, TimeSpan.FromSeconds(8), ctx.RequestAborted);
        if (status == 404) return null;
        if (status != 200) throw new SupervisorUnavailableException("the bot's script library answered " + status);
        return JsonSerializer.Deserialize<ScriptVersionDetail>(body, BridgeJson.Options);
    }
}

/// <summary>Contracts ChatAccepted: <c>id</c> echoes the request id (the viewer feed line carries it), <c>feed_seq</c> = that line's seq.</summary>
public sealed record ChatAccepted(bool Accepted, string Id, long? FeedSeq, string? Note);
