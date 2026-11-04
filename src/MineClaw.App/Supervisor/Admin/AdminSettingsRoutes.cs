using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.App.Supervisor.Admin;

/// <summary>
/// The Administration page's routes on the supervisor (behind the bridge's node proxy, never the browser directly):
/// <list type="bullet">
/// <item><c>GET /api/admin/settings</c> → the registry with running values (secrets: set + last four only)</item>
/// <item><c>PUT /api/admin/settings</c> <c>{changes:[{key,value}], reason}</c> → <c>{applied, restart_required, backup}</c>; 400
/// <c>{error, errors:[{key,message}]}</c> (nothing written)</item>
/// <item><c>GET /api/admin/settings/node</c> → this bot instance; <c>GET /api/admin/settings/audit?max=</c> → <c>{entries}</c></item>
/// <item><c>POST /api/admin/settings/test</c> <c>{kind:"anthropic"|"ollama", api_key?, base_url?, model?, endpoint?, ollama_api_key?}</c>
/// → <c>{ok, message, latency_ms, models}</c> (nothing saved)</item>
/// <item><c>GET /api/admin/ollama/models?endpoint=</c> → <c>{endpoint, ok, error, models:[{name, capabilities}]}</c></item>
/// <item><c>GET /api/admin/restart/safety</c>; <c>POST /api/admin/restart</c> <c>{reason, force?}</c> → 202, or 409 with the safety check</item>
/// </list>
/// Every route needs <see cref="SupervisorOptions.AllowAdminWrites"/>, the token in the <c>X-Supervisor-Token</c> header and the
/// bridge's signed identity: <c>X-MCX-Roles</c> holding <c>admin</c> (403 otherwise); <c>X-MCX-User</c> is who the audit names.
/// Writes and probes are rate limited (429).
/// </summary>
public sealed class AdminSettingsRoutes
{
    public const int MaxBodyBytes = 64 * 1024;
    public const string UserHeader = "X-MCX-User", RolesHeader = "X-MCX-Roles";
    private static readonly string[] Prefixes = ["/api/admin/settings", "/api/admin/ollama", "/api/admin/restart"];

    private readonly SupervisorOptions _o;
    private readonly AdminSettingsService _svc;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly SlidingWindow _writes;
    private readonly SlidingWindow _probes;
    private int _restarting;

    public AdminSettingsRoutes(SupervisorOptions o, AdminSettingsService svc, ILogger? log = null, TimeProvider? time = null, int writesPerMinute = 12, int probesPerMinute = 30)
    {
        _o = o;
        _svc = svc;
        _log = log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _writes = new SlidingWindow(writesPerMinute, _time);
        _probes = new SlidingWindow(probesPerMinute, _time);
    }

    public AdminSettingsService Service => _svc;
    /// <summary>The restart is requested this long after the 202 goes out (so the answer reaches the browser first).</summary>
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(1.5);

    public static bool IsSettingsPath(string path)
    {
        var p = path.TrimEnd('/');
        return Prefixes.Any(x => p.Equals(x, StringComparison.OrdinalIgnoreCase) || p.StartsWith(x + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The body of a PUT/POST to a settings route (null otherwise), read up to the cap, only with the token header and the flag.</summary>
    public static async Task<string?> ReadBodyAsync(HttpListenerRequest req, SupervisorOptions o, CancellationToken ct)
    {
        var m = req.HttpMethod;
        if (!(m.Equals("PUT", StringComparison.OrdinalIgnoreCase) || m.Equals("POST", StringComparison.OrdinalIgnoreCase)) || !IsSettingsPath(req.Url?.AbsolutePath ?? "/") || !req.HasEntityBody)
            return null;
        if (!o.AllowAdminWrites || !TokenOk(req.Headers["X-Supervisor-Token"], o.Token)) return null;
        return await SupervisorAdminRoutes.ReadCappedAsync(req, MaxBodyBytes, 16384, ct).ConfigureAwait(false);
    }

    private static bool TokenOk(string? given, string? token)
    {
        if (string.IsNullOrEmpty(given) || string.IsNullOrEmpty(token)) return false;
        var a = Encoding.UTF8.GetBytes(given);
        var b = Encoding.UTF8.GetBytes(token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    public async Task<SupervisorReply> HandleAsync(SupervisorRequest r, CancellationToken ct)
    {
        if (Denied(r) is { } denied) return denied;
        var who = Clip(Header(r, UserHeader) ?? "", 64) + " (web)";
        var path = r.Path.TrimEnd('/').ToLowerInvariant();
        try
        {
            return await RouteAsync(r, path, r.Method.ToUpperInvariant(), who, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return Json(400, new { error = "bad request body" });
        }
        catch (OperationCanceledException) { return Json(503, new { error = "cancelled" }); }
        catch (Exception ex)
        {
            _log.LogWarning("admin settings: {Path} failed ({Error})", path, ex.GetType().Name); // never the body: it may hold a key
            return Json(500, new { error = "internal error" });
        }
    }

    /// <summary>The gate, in order: the flag, a configured token, the token header, the bridge's admin role and user, the body size.</summary>
    private SupervisorReply? Denied(SupervisorRequest r)
    {
        if (!_o.AllowAdminWrites) return Json(403, new { error = "admin writes are off (Supervisor:AllowAdminWrites)" });
        if (string.IsNullOrEmpty(_o.Token)) return Json(403, new { error = "admin writes need Supervisor:Token" });
        if (!TokenOk(r.TokenHeader, _o.Token)) return Json(403, new { error = "X-Supervisor-Token header required" });
        var roles = Header(r, RolesHeader) ?? "";
        var user = Header(r, UserHeader) ?? "";
        if (!roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains("admin", StringComparer.OrdinalIgnoreCase) || user.Length == 0)
            return Json(403, new { error = "admin role required" });
        if (r.Body == SupervisorAdminRoutes.TooLarge) return Json(413, new { error = $"body over {MaxBodyBytes} bytes" });
        return null;
    }

    private static bool IsGet(string m) => m is "GET" or "HEAD";

    /// <summary>By lower-cased path; <paramref name="m"/> is the upper-cased method. Writes and probes take their rate limit first.</summary>
    private async Task<SupervisorReply> RouteAsync(SupervisorRequest r, string path, string m, string who, CancellationToken ct)
    {
        switch (path)
        {
            case "/api/admin/settings":
                return SettingsRoute(r, m, who);
            case "/api/admin/settings/node":
                return GetOnly(m, () => Json(200, _svc.NodeView()));
            case "/api/admin/settings/audit":
                return GetOnly(m, () => AuditTail(r));
            case "/api/admin/settings/test":
                return await PostProbeAsync(m, () => TestAsync(r.Body, who, ct)).ConfigureAwait(false);
            case "/api/admin/ollama/models":
                return IsGet(m) ? await ProbeAsync(() => OllamaModelsAsync(r, ct)).ConfigureAwait(false) : NotAllowed("GET");
            case "/api/admin/restart/safety":
                return GetOnly(m, () => Json(200, _svc.Safety.Check()));
            case "/api/admin/restart":
                return RestartRoute(r, m, who);
            default:
                return Json(404, new { error = "not found" });
        }
    }

    private static SupervisorReply GetOnly(string m, Func<SupervisorReply> reply) => IsGet(m) ? reply() : NotAllowed("GET");

    /// <summary>A probe (key test, model list) within the probes' rate limit.</summary>
    private async Task<SupervisorReply> ProbeAsync(Func<Task<SupervisorReply>> probe) =>
        _probes.Take() is { } wait ? Limited(wait) : await probe().ConfigureAwait(false);

    private async Task<SupervisorReply> PostProbeAsync(string m, Func<Task<SupervisorReply>> probe) =>
        m != "POST" ? NotAllowed("POST") : await ProbeAsync(probe).ConfigureAwait(false);

    private SupervisorReply RestartRoute(SupervisorRequest r, string m, string who)
    {
        if (m != "POST") return NotAllowed("POST");
        return _writes.Take() is { } rw ? Limited(rw) : Restart(r.Body, who);
    }

    private SupervisorReply SettingsRoute(SupervisorRequest r, string m, string who)
    {
        if (IsGet(m)) return Json(200, _svc.View());
        if (m != "PUT") return NotAllowed("GET, PUT");
        if (_writes.Take() is { } wait) return Limited(wait);
        return Save(r.Body, who);
    }

    private SupervisorReply AuditTail(SupervisorRequest r)
    {
        int max = int.TryParse(Q(r, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mv) ? Math.Clamp(mv, 1, 500) : 100;
        return Json(200, new { entries = _svc.AuditTail(max) });
    }

    private const string OllamaEndpointKey = "Agent:Llm:Ollama:Endpoint";

    /// <summary>Whether <paramref name="endpoint"/> is the configured Ollama endpoint (then its saved key may go with a probe).</summary>
    private bool IsConfiguredOllama(string endpoint) => endpoint.Equals(_svc.Current(OllamaEndpointKey)?.Trim(), StringComparison.OrdinalIgnoreCase);

    private async Task<SupervisorReply> OllamaModelsAsync(SupervisorRequest r, CancellationToken ct)
    {
        var endpoint = Q(r, "endpoint") is { Length: > 0 } e ? e.Trim() : _svc.Current(OllamaEndpointKey) ?? "";
        if (endpoint.Length == 0) return Json(200, new { endpoint, ok = false, error = "no Ollama endpoint is set", models = Array.Empty<object>() });
        if (LiveProviderTester.CheckEndpoint(endpoint) is { } bad) return Json(400, new { error = "endpoint " + bad });
        var res = await _svc.Tester.OllamaAsync(endpoint, IsConfiguredOllama(endpoint) ? _svc.Current("Agent:Llm:Ollama:ApiKey") : null, details: true, ct).ConfigureAwait(false);
        return Json(200, new { endpoint, ok = res.Ok, error = res.Error, models = res.Models.Select(x => new { name = x.Name, capabilities = x.Capabilities }) });
    }

    private SupervisorReply Save(string? body, string who)
    {
        var root = Root(body);
        if (!root.TryGetProperty("changes", out var ch) || ch.ValueKind != JsonValueKind.Array) return Json(400, new { error = "changes (a list) required" });
        var reason = root.TryGetProperty("reason", out var rv) && rv.ValueKind == JsonValueKind.String ? rv.GetString()!.Trim() : "";
        if (reason.Length < 3) return Json(400, new { error = "a reason (at least 3 characters) is required", errors = new[] { new { key = "reason", message = "say why" } } });
        if (ChangesOf(ch) is not { } changes) return Json(400, new { error = "every change needs a key" });
        if (changes.Count > 100) return Json(400, new { error = "at most 100 changes at once" });
        var res = _svc.Save(changes, reason, who);
        if (!res.Ok)
            return Json(400, new
            {
                error = res.Errors.Count == 1 ? Describe(res.Errors[0]) : $"{res.Errors.Count} settings need attention; nothing was saved",
                errors = res.Errors.Select(e => new { key = e.Key, message = e.Message }),
            });
        return Json(200, new { applied = res.Applied, restart_required = res.RestartRequired, backup = res.Backup });
    }

    /// <summary>The <c>changes</c> list as (key, value) pairs; null when one has no string key.</summary>
    private static List<(string, JsonElement)>? ChangesOf(JsonElement ch)
    {
        var changes = new List<(string, JsonElement)>();
        foreach (var c in ch.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String) return null;
            changes.Add((k.GetString()!, c.TryGetProperty("value", out var v) ? v.Clone() : default));
        }
        return changes;
    }

    private static string Describe(SettingError e) => e.Key.Length > 0 ? $"{e.Key}: {e.Message}" : e.Message;

    private async Task<SupervisorReply> TestAsync(string? body, string who, CancellationToken ct)
    {
        var root = Root(body);
        var kind = Str(root, "kind");
        var (result, refused) = kind switch
        {
            "anthropic" => await TestAnthropicAsync(root, ct).ConfigureAwait(false),
            "ollama" => await TestOllamaAsync(root, ct).ConfigureAwait(false),
            _ => (null, Json(400, new { error = "kind must be anthropic or ollama" })),
        };
        if (result == null) return refused!;
        _svc.Audit(new SettingsAuditLine(_time.GetUtcNow(), who, "test", [kind == "anthropic" ? "Agent:Llm:ApiKey" : OllamaEndpointKey], null, result.Ok, Clip(result.Message, 200)));
        return Json(200, new { ok = result.Ok, message = result.Message, latency_ms = result.LatencyMs is { } l ? Math.Round(l) : (double?)null, models = result.Models });
    }

    /// <summary>A key check (the given key, else the saved one) with a 5.5-generation model; a refusal reply when it can't run.</summary>
    private async Task<(ProviderTestResult? Result, SupervisorReply? Refused)> TestAnthropicAsync(JsonElement root, CancellationToken ct)
    {
        var key = Str(root, "api_key") is { Length: > 0 } k ? k.Trim() : _svc.Current("Agent:Llm:ApiKey");
        if (string.IsNullOrWhiteSpace(key)) return (null, Json(200, new { ok = false, message = "no Anthropic key is set" }));
        var model = Str(root, "model") is { Length: > 0 } mm ? mm.Trim() : "claude-haiku-5-5";
        if (!ModelProfiles.IsCurrentGeneration(model)) return (null, Json(400, new { error = $"test with a 5.5-generation model ({string.Join(", ", ModelProfiles.CurrentGeneration)})" }));
        var baseUrl = root.TryGetProperty("base_url", out _) ? Str(root, "base_url") : _svc.Current("Agent:Llm:BaseUrl");
        if (!string.IsNullOrWhiteSpace(baseUrl) && LiveProviderTester.CheckEndpoint(baseUrl) is { } bu) return (null, Json(400, new { error = "base URL " + bu }));
        return (await _svc.Tester.AnthropicAsync(key, baseUrl, model, ct).ConfigureAwait(false), null);
    }

    /// <summary>The endpoint's model list (the given endpoint, else the saved one), and whether the wanted model is on it.</summary>
    private async Task<(ProviderTestResult? Result, SupervisorReply? Refused)> TestOllamaAsync(JsonElement root, CancellationToken ct)
    {
        var endpoint = Str(root, "endpoint") is { Length: > 0 } e ? e.Trim() : _svc.Current(OllamaEndpointKey) ?? "";
        if (LiveProviderTester.CheckEndpoint(endpoint) is { } bad) return (null, Json(400, new { error = "endpoint " + bad }));
        var token = Str(root, "ollama_api_key") is { Length: > 0 } t ? t.Trim()
            : IsConfiguredOllama(endpoint) ? _svc.Current("Agent:Llm:Ollama:ApiKey") : null;
        var list = await _svc.Tester.OllamaAsync(endpoint, token, details: false, ct).ConfigureAwait(false);
        var want = Str(root, "model");
        var names = list.Models.Select(x => x.Name).ToList();
        bool wanted = want is not { Length: > 0 } || names.Any(n => OllamaCatalog.SameModel(n, want));
        return (new ProviderTestResult(list.Ok && wanted, OllamaMessage(list, want, names.Count, wanted), list.LatencyMs, names), null);
    }

    private static string OllamaMessage(OllamaListResult list, string? want, int count, bool wanted)
    {
        if (!list.Ok) return list.Error ?? "unreachable";
        return !wanted ? $"reachable ({count} models), but '{want}' is not one of them" : $"reachable: {count} model{(count == 1 ? "" : "s")}";
    }

    private SupervisorReply Restart(string? body, string who)
    {
        var root = Root(body);
        var reason = Str(root, "reason")?.Trim() ?? "";
        if (reason.Length < 3) return Json(400, new { error = "a reason (at least 3 characters) is required" });
        bool force = root.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True;
        if (_svc.RequestRestart is not { } restart) return Json(409, new { error = "this process can't restart itself" });
        var safety = _svc.Safety.Check();
        if (!safety.Safe && !force) return RestartRefused(safety, reason, who);
        if (Interlocked.Exchange(ref _restarting, 1) == 1) return Json(409, new { error = "a restart is already under way" });
        return RestartAccepted(restart, safety, force && !safety.Safe, reason, who);
    }

    /// <summary>Audited and logged, then requested after <see cref="RestartDelay"/> (so the 202 reaches the browser first).</summary>
    private SupervisorReply RestartAccepted(Action<string> restart, RestartSafetyResult safety, bool forced, string reason, string who)
    {
        _svc.Audit(new SettingsAuditLine(_time.GetUtcNow(), who, "restart", [], Clip(reason, 300), true, forced ? "forced: " + Clip(string.Join("; ", safety.Reasons), 280) : null));
        _log.LogWarning("admin settings: restart requested by {Who}{Forced}", who, forced ? " (forced while unsafe)" : "");
        var why = $"restart requested from the web by {who}";
        _ = Task.Delay(RestartDelay).ContinueWith(_ => restart(why), TaskScheduler.Default);
        return Json(202, new { accepted = true, forced, safety, message = "restarting: the bot stops gracefully and systemd starts it again" });
    }

    private SupervisorReply RestartRefused(RestartSafetyResult safety, string reason, string who)
    {
        _svc.Audit(new SettingsAuditLine(_time.GetUtcNow(), who, "restart_refused", [], Clip(reason, 300), false, Clip(string.Join("; ", safety.Reasons), 300)));
        return Json(409, new { error = "not safe to restart now: " + string.Join("; ", safety.Reasons), safety });
    }

    private static JsonElement Root(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new FormatException("JSON body required");
        using var d = JsonDocument.Parse(body);
        if (d.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("JSON object required");
        return d.RootElement.Clone();
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string? Q(SupervisorRequest r, string k) => r.Query.TryGetValue(k, out var v) ? v : null;
    private static string? Header(SupervisorRequest r, string name) => r.Headers != null && r.Headers.TryGetValue(name, out var v) ? v : null;

    private static string Clip(string s, int max)
    {
        s = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length > max ? s[..max] : s;
    }

    private static SupervisorReply Limited(TimeSpan wait) =>
        Json(429, new { error = "too many admin requests; slow down", retry_after_ms = (long)wait.TotalMilliseconds },
            new Dictionary<string, string> { ["Retry-After"] = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture) });

    private static SupervisorReply NotAllowed(string allow) => Json(405, new { error = $"use {allow}" }, new Dictionary<string, string> { ["Allow"] = allow });

    private static SupervisorReply Json(int status, object value, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, "application/json; charset=utf-8", SupervisorJson.Bytes(value), headers);

    /// <summary>At most N per rolling minute.</summary>
    private sealed class SlidingWindow(int perMinute, TimeProvider time)
    {
        private readonly Queue<DateTimeOffset> _q = new();

        public TimeSpan? Take()
        {
            lock (_q)
            {
                var now = time.GetUtcNow();
                while (_q.Count > 0 && now - _q.Peek() >= TimeSpan.FromMinutes(1)) _q.Dequeue();
                if (_q.Count >= Math.Max(1, perMinute)) return _q.Peek() + TimeSpan.FromMinutes(1) - now;
                _q.Enqueue(now);
                return null;
            }
        }
    }
}
