using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.App.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.App.Supervisor.Admin;

public sealed record SettingError(string Key, string Message);

public sealed record SaveOutcome(bool Ok, IReadOnlyList<string> Applied, IReadOnlyList<string> RestartRequired, string? Backup, IReadOnlyList<SettingError> Errors);

public sealed record SettingsAuditLine(DateTimeOffset At, string Who, string Action, IReadOnlyList<string> Keys, string? Reason, bool Ok, string? Note);

/// <summary>This bot instance as the Administration page names it (the bridge sets the node id and its URL).</summary>
public sealed record AdminNodeInfo(string Id, string Name, string? Version, DateTimeOffset? StartedAt, string? GameVersion);

/// <summary>
/// The bot's settings as the web's Administration page sees and changes them: the <see cref="SettingsRegistry"/> with the running
/// values (from the layered config: built-in default → base config → env → the override file), validation (all-or-nothing,
/// including the startup rules: Claude models of the 5.5 generation only, an Ollama tier needs an endpoint…), writes to the
/// override file (<see cref="SettingsOverrideStore"/>), live application of the live settings onto the running options objects,
/// and an audit trail of keys (never values). Secret values never leave this class: only "set" and the last four characters.
/// </summary>
public sealed class AdminSettingsService
{
    public const int MaxText = 4000, MaxString = 200, MaxSecret = 400, MaxListItems = 64, MaxListItem = 100;

    private readonly IConfigurationRoot _config;
    private readonly SettingsOverrideProvider _layer;
    private readonly SettingsOverrideStore _store;
    private readonly IReadOnlyDictionary<string, object> _live;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Func<string, string?> _env;
    private readonly object _lock = new();
    /// <summary>Key → fingerprint of the value the process started with (what a restart setting runs until the next restart).</summary>
    private readonly Dictionary<string, string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Action<object?>> _hooks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _auditPath;

    /// <param name="live">Section → the running options object live settings are written to ("Agent" → main's AgentOptions…).</param>
    public AdminSettingsService(IConfigurationRoot config, SettingsOverrideStore store, IReadOnlyDictionary<string, object> live, AdminNodeInfo node,
        IRestartSafetyProbe safety, IProviderTester tester, Action<string>? restart = null, ILogger? log = null, TimeProvider? time = null,
        Func<string, string?>? env = null)
    {
        _config = config;
        _layer = SettingsOverrideFile.Of(config) ?? throw new InvalidOperationException("the config has no settings override layer");
        _store = store;
        _live = live;
        Node = node;
        Safety = safety;
        Tester = tester;
        RequestRestart = restart;
        _log = log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _env = env ?? Environment.GetEnvironmentVariable;
        _auditPath = Path.Combine(Path.GetDirectoryName(store.Path)!, "settings-audit.jsonl");
        _hooks["Host:ConsoleLogLevel"] = v => AppLogging.SetConsoleLevel(v as string);
        var bound = Bind(_config);
        foreach (var d in SettingsRegistry.All) _running[d.Key] = Fingerprint(ValueOf(d, bound));
    }

    public AdminNodeInfo Node { get; }
    public IRestartSafetyProbe Safety { get; }
    public IProviderTester Tester { get; }
    public Action<string>? RequestRestart { get; }
    public string AuditPath => _auditPath;

    // ------------------------------------------------------------------ reading

    /// <summary>Fresh options objects bound from a config (one per registry root), as <see cref="AppSettings.Bind"/> binds them.</summary>
    public static Dictionary<string, object> Bind(IConfiguration config)
    {
        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (section, type) in SettingsRegistry.Roots)
        {
            var o = Activator.CreateInstance(type)!;
            config.GetSection(section).Bind(o);
            d[section] = o;
        }
        return d;
    }

    private static object? ValueOf(SettingDef d, IReadOnlyDictionary<string, object> roots) =>
        SettingsRegistry.PathOf(d.Key) is { } path && roots.TryGetValue(d.Key.Split(':')[0], out var root) ? SettingsRegistry.Read(root, path) : null;

    /// <summary>One fresh (unbound) options object per root: the built-in defaults.</summary>
    private static readonly Lazy<Dictionary<string, object>> Defaults = new(() =>
        SettingsRegistry.Roots.ToDictionary(kv => kv.Key, kv => Activator.CreateInstance(kv.Value)!, StringComparer.OrdinalIgnoreCase));

    private static object? DefaultOf(SettingDef d) => ValueOf(d, Defaults.Value);

    /// <summary>A JSON-friendly copy of an options value (string arrays as lists, decimals as numbers).</summary>
    public static object? Plain(object? v) => v switch
    {
        null => null,
        string[] a => a.ToArray(),
        List<string> l => l.ToArray(),
        decimal m => (double)m,
        _ => v,
    };

    private static string Fingerprint(object? v)
    {
        var json = JsonSerializer.Serialize(Plain(v));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>"…ab12" for a secret at least 12 long (shorter ones would give too much away); null otherwise.</summary>
    public static string? Hint(string? secret) => secret is { Length: >= 12 } s ? "…" + s[^4..] : null;

    /// <summary>The layer a key's value comes from: the highest config provider that holds it, else the built-in default.</summary>
    public string SourceOf(SettingDef d)
    {
        var providers = _config.Providers.ToList();
        int overrideAt = providers.IndexOf(_layer);
        bool list = d.Kind == SettingKind.List;
        for (int i = providers.Count - 1; i >= 0; i--)
        {
            var p = providers[i];
            bool has = list ? p.TryGet(d.Key + ":0", out _) || (p == _layer && _layer.Lists.Contains(d.Key)) : p.TryGet(d.Key, out var v) && v != null;
            if (!has) continue;
            return p switch
            {
                SettingsOverrideProvider => "override",
                EnvironmentVariablesConfigurationProvider => "env",
                JsonConfigurationProvider => "config",
                MemoryConfigurationProvider when i > overrideAt => "cli",
                MemoryConfigurationProvider => "env",
                _ => "config",
            };
        }
        return "default";
    }

    /// <summary>GET /api/admin/settings.</summary>
    public object View()
    {
        lock (_lock)
        {
            var bound = Bind(_config);
            JsonObject file;
            try { file = _store.Read(); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException) { file = []; }
            var pending = new List<string>();
            var rows = new List<object>();
            foreach (var d in SettingsRegistry.All)
            {
                var value = ValueOf(d, bound);
                var source = SourceOf(d);
                bool isPending = !d.Live && _running.TryGetValue(d.Key, out var fp) && fp != Fingerprint(value);
                if (isPending) pending.Add(d.Key);
                object? secretState = null;
                if (d.Secret)
                {
                    var s = value as string;
                    if (string.IsNullOrEmpty(s) && d.Key.Equals("Agent:Llm:ApiKey", StringComparison.OrdinalIgnoreCase) && _env("ANTHROPIC_API_KEY") is { Length: > 0 } envKey)
                    {
                        s = envKey;
                        source = "env";
                    }
                    secretState = new { set = !string.IsNullOrEmpty(s), hint = Hint(s) };
                }
                rows.Add(new
                {
                    key = d.Key, group = d.Group, section = d.Section, label = d.Label, description = d.Description, type = KindName(d.Kind),
                    apply = d.Live ? "live" : "restart", secret = d.Secret, editable = d.ReadOnly == null, read_only_reason = d.ReadOnly,
                    @default = d.Secret ? null : Plain(DefaultOf(d)), value = d.Secret ? null : Plain(value), secret_state = secretState,
                    source, overridden = SettingsOverrideStore.Get(file, d.Key) != null, pending_restart = isPending,
                    nullable = d.Nullable ? true : (bool?)null,
                    options = d.Kind == SettingKind.Model ? ModelProfiles.CurrentGeneration : d.Options,
                    min = d.Min, max = d.Max, unit = d.Unit, provider_key = d.ProviderKey,
                });
            }
            return new
            {
                node = NodeView(),
                groups = SettingsRegistry.Groups.Select(g => new { id = g.Id, title = g.Title, description = g.Description }),
                settings = rows,
                allowed_claude_models = ModelProfiles.CurrentGeneration,
                restart_pending = pending,
                @override = new { path = _store.Path, exists = _store.Exists, updated_at = _store.UpdatedAt, backups = _store.BackupCount },
            };
        }
    }

    public object NodeView() => new { id = Node.Id, name = Node.Name, version = Node.Version, started_at = Node.StartedAt, game_version = Node.GameVersion };

    public static string KindName(SettingKind k) => k.ToString().ToLowerInvariant();

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// PUT /api/admin/settings: validates every change (types, limits, read-only, then the startup rules on the whole candidate
    /// config: only problems this save would add count), writes the override file once, applies the live ones. All or nothing.
    /// </summary>
    public SaveOutcome Save(IReadOnlyList<(string Key, JsonElement Value)> changes, string reason, string who)
    {
        lock (_lock)
        {
            var errors = new List<SettingError>();
            var normalized = new List<(SettingDef Def, JsonNode? Node)>();
            if (changes.Count == 0) errors.Add(new SettingError("", "no changes"));
            foreach (var group in changes.GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
                if (group.Count() > 1) errors.Add(new SettingError(group.Key, "changed twice in one save"));
            foreach (var (key, value) in changes)
            {
                var d = SettingsRegistry.Find(key);
                if (d == null) { errors.Add(new SettingError(key, "not a setting this page manages")); continue; }
                if (d.ReadOnly != null) { errors.Add(new SettingError(d.Key, "read-only: " + d.ReadOnly)); continue; }
                if (Normalize(d, value, out var node) is { } err) { errors.Add(new SettingError(d.Key, err)); continue; }
                normalized.Add((d, node));
            }
            JsonObject current;
            try { current = _store.Read(); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                errors.Add(new SettingError("", "the override file on the bot can't be read; fix or remove it by hand"));
                current = [];
            }
            if (errors.Count > 0) return Refused(errors, who, changes, reason);

            // The startup rules on the whole candidate config (the override with these changes), against today's.
            var candidate = (JsonObject)current.DeepClone();
            foreach (var (d, node) in normalized) SettingsOverrideStore.Set(candidate, d.Key, node?.DeepClone());
            var candidateConfig = WithOverride(candidate);
            Dictionary<string, object> before, after;
            try
            {
                before = Bind(_config);
                after = Bind(candidateConfig);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(new SettingError("", "the result would not bind: " + ex.Message));
                return Refused(errors, who, changes, reason);
            }
            foreach (var (d, _) in normalized)
                if (d.Kind == SettingKind.Model && ModelProblem(d, after) is { } mp) errors.Add(new SettingError(d.Key, mp));
            var was = Problems(before).ToHashSet(StringComparer.Ordinal);
            foreach (var p in Problems(after).Where(p => !was.Contains(p)))
            {
                var owner = SettingsRegistry.All.FirstOrDefault(d => p.Contains(d.Key + " ", StringComparison.OrdinalIgnoreCase) || p.Contains(d.Key + "'", StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith(d.Key, StringComparison.OrdinalIgnoreCase) || p.Contains(d.Key + ":", StringComparison.OrdinalIgnoreCase) || p.Contains(d.Key + " is", StringComparison.OrdinalIgnoreCase));
                if (errors.All(e => e.Message != p)) errors.Add(new SettingError(owner?.Key ?? "", p));
            }
            if (errors.Count > 0) return Refused(errors, who, changes, reason);

            var backup = _store.Write(normalized.Select(n => (n.Def.Key, n.Node)).ToList());
            _layer.Load();
            var now = Bind(_config);
            var applied = new List<string>();
            var restart = new List<string>();
            foreach (var (d, _) in normalized)
            {
                var v = ValueOf(d, now);
                if (d.Live && ApplyLive(d, v))
                {
                    applied.Add(d.Key);
                    _running[d.Key] = Fingerprint(v);
                }
                else if (_running.TryGetValue(d.Key, out var fp) && fp != Fingerprint(v)) restart.Add(d.Key);
                else applied.Add(d.Key); // back to what runs
            }
            var keys = normalized.Select(n => n.Def.Key).ToList();
            Audit(new SettingsAuditLine(_time.GetUtcNow(), who, "update", keys, Clip(reason), true,
                restart.Count > 0 ? $"{applied.Count} live, {restart.Count} after a restart" : $"{applied.Count} live"));
            _log.LogInformation("admin settings: {Who} changed {Keys} ({Restart} need a restart)", who, string.Join(", ", keys), restart.Count);
            return new SaveOutcome(true, applied, restart, backup, []);
        }
    }

    private SaveOutcome Refused(List<SettingError> errors, string who, IReadOnlyList<(string Key, JsonElement Value)> changes, string reason)
    {
        Audit(new SettingsAuditLine(_time.GetUtcNow(), who, "update", changes.Select(c => c.Key).Take(100).ToList(), Clip(reason), false,
            $"refused: {errors.Count} problem{(errors.Count == 1 ? "" : "s")}"));
        return new SaveOutcome(false, [], [], null, errors);
    }

    /// <summary>Writes a live setting's new value onto the running options object (and runs its hook).</summary>
    private bool ApplyLive(SettingDef d, object? value)
    {
        if (SettingsRegistry.PathOf(d.Key) is not { } path || !_live.TryGetValue(d.Key.Split(':')[0], out var root)) return false;
        try
        {
            if (!SettingsRegistry.Write(root, path, value)) return false;
            if (_hooks.TryGetValue(d.Key, out var hook)) hook(value);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or TargetException or TargetInvocationException)
        {
            _log.LogWarning("admin settings: {Key} could not be applied live ({Error}); it runs after a restart", d.Key, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>The config as it would be with this override object instead of the file's (same layers, same order).</summary>
    public IConfigurationRoot WithOverride(JsonObject candidate)
    {
        var providers = new List<IConfigurationProvider>();
        foreach (var p in _config.Providers)
            providers.Add(p == _layer ? new FixedOverrideProvider(candidate) : new ReadThrough(p));
        return new ConfigurationRoot(providers);
    }

    /// <summary>A live layer read as it is: never reloaded, no change subscription (the candidate root is thrown away).</summary>
    private sealed class ReadThrough(IConfigurationProvider inner) : IConfigurationProvider
    {
        public bool TryGet(string key, out string? value) => inner.TryGet(key, out value);
        public void Set(string key, string? value) { }
        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() => new Microsoft.Extensions.Primitives.CancellationChangeToken(CancellationToken.None);
        public void Load() { }
        public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath) => inner.GetChildKeys(earlierKeys, parentPath);
    }

    /// <summary>The override layer from an in-memory object (same list semantics as the file's).</summary>
    private sealed class FixedOverrideProvider(JsonObject o) : ConfigurationProvider
    {
        private readonly HashSet<string> _lists = new(StringComparer.OrdinalIgnoreCase);

        public override void Load()
        {
            var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            _lists.Clear();
            using var doc = JsonDocument.Parse(o.ToJsonString());
            SettingsOverrideProvider.Flatten(doc.RootElement, null, data, _lists);
            Data = data;
        }

        public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath) =>
            parentPath != null && _lists.Contains(parentPath) ? base.GetChildKeys([], parentPath) : base.GetChildKeys(earlierKeys, parentPath);
    }

    /// <summary>The startup config check's complaints for a bound config (<see cref="ConfigCheck"/>).</summary>
    private static List<string> Problems(IReadOnlyDictionary<string, object> roots)
    {
        try { return ConfigCheck.Validate((MinecraftSettings)roots["Minecraft"], (AgentOptions)roots["Agent"]); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return ["the config check failed: " + ex.GetType().Name]; }
    }

    /// <summary>A model id against its provider in the candidate config: Claude ids of the 5.5 generation only.</summary>
    private static string? ModelProblem(SettingDef d, IReadOnlyDictionary<string, object> roots)
    {
        var model = ValueOf(d, roots) as string ?? "";
        var provider = d.FixedProvider ?? (d.ProviderKey is { } pk && SettingsRegistry.Find(pk) is { } pd ? ValueOf(pd, roots) as string : null);
        provider = LlmProviders.Normalize(provider);
        if (string.IsNullOrWhiteSpace(model)) return provider == LlmProviders.Anthropic ? "pick a Claude model" : null;
        if (provider == LlmProviders.Anthropic && !ModelProfiles.IsCurrentGeneration(model))
            return $"'{model}' is not allowed: Claude models must be one of {string.Join(", ", ModelProfiles.CurrentGeneration)}";
        return null;
    }

    /// <summary>A request value as the override file stores it (null = drop the key), or why it can't be.</summary>
    public static string? Normalize(SettingDef d, JsonElement v, out JsonNode? node)
    {
        node = null;
        if (v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        switch (d.Kind)
        {
            case SettingKind.Bool:
                bool? b = v.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String when bool.TryParse(v.GetString(), out var p) => p,
                    _ => null,
                };
                if (b is not { } bv) return "must be true or false";
                node = JsonValue.Create(bv);
                return null;
            case SettingKind.Int:
                {
                    if (!TryNumber(v, out var n) || n != Math.Floor(n) || Math.Abs(n) > int.MaxValue) return "must be a whole number";
                    if (Range(d, n) is { } re) return re;
                    node = JsonValue.Create((long)n);
                    return null;
                }
            case SettingKind.Number:
                {
                    if (!TryNumber(v, out var n) || !double.IsFinite(n)) return "must be a number";
                    if (Range(d, n) is { } re) return re;
                    node = JsonValue.Create(decimal.Parse(n.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture));
                    return null;
                }
            case SettingKind.List:
                {
                    if (v.ValueKind != JsonValueKind.Array) return "must be a list of names";
                    var items = new JsonArray();
                    foreach (var e in v.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.String) return "every item must be text";
                        var s = e.GetString()!.Trim();
                        if (s.Length == 0 || s.Length > MaxListItem || s.Any(char.IsControl)) return $"items are 1-{MaxListItem} characters, no control characters";
                        items.Add(s);
                    }
                    if (items.Count > MaxListItems) return $"at most {MaxListItems} items";
                    node = items;
                    return null;
                }
        }
        if (v.ValueKind != JsonValueKind.String) return "must be text";
        var text = v.GetString()!;
        switch (d.Kind)
        {
            case SettingKind.Secret:
                text = text.Trim();
                if (text.Length > MaxSecret) return $"at most {MaxSecret} characters";
                if (text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return "no spaces or control characters";
                break;
            case SettingKind.Text:
                if (text.Length > MaxText) return $"at most {MaxText} characters";
                if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) return "no control characters";
                break;
            case SettingKind.Enum:
                {
                    var match = d.Options?.FirstOrDefault(o => o.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (match == null) return "must be one of " + string.Join(", ", (d.Options ?? []).Select(o => o.Length == 0 ? "(empty)" : o));
                    text = match;
                    break;
                }
            case SettingKind.Url:
                text = text.Trim();
                if (text.Length > MaxSecret) return $"at most {MaxSecret} characters";
                if (text.Length > 0 && LiveProviderTester.CheckEndpoint(text) is { } ue) return ue;
                break;
            case SettingKind.Model:
                text = text.Trim();
                if (text.Length > 128 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '/' or '-')) return "not a model id";
                break;
            default:
                text = text.Trim();
                if (text.Length > MaxString) return $"at most {MaxString} characters";
                if (text.Any(char.IsControl)) return "no control characters";
                break;
        }
        node = JsonValue.Create(text);
        return null;
    }

    private static bool TryNumber(JsonElement v, out double n)
    {
        n = 0;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetDouble(out n);
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n);
    }

    private static string? Range(SettingDef d, double n) =>
        d.Min is { } min && n < min ? $"at least {min.ToString(CultureInfo.InvariantCulture)}"
        : d.Max is { } max && n > max ? $"at most {max.ToString(CultureInfo.InvariantCulture)}"
        : null;

    private static string Clip(string s)
    {
        s = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length > 300 ? s[..300] : s;
    }

    // ------------------------------------------------------------------ the running values a probe needs

    /// <summary>The running (saved) value of a non-secret or secret string setting, for provider tests (never returned).</summary>
    public string? Current(string key)
    {
        var d = SettingsRegistry.Find(key);
        if (d == null) return null;
        var v = ValueOf(d, Bind(_config)) as string;
        if (string.IsNullOrEmpty(v) && key.Equals("Agent:Llm:ApiKey", StringComparison.OrdinalIgnoreCase)) v = _env("ANTHROPIC_API_KEY");
        return v;
    }

    // ------------------------------------------------------------------ audit

    public void Audit(SettingsAuditLine line)
    {
        try
        {
            var dir = Path.GetDirectoryName(_auditPath)!;
            Directory.CreateDirectory(dir);
            bool fresh = !File.Exists(_auditPath);
            File.AppendAllText(_auditPath, JsonSerializer.Serialize(line, SupervisorJson.Options) + "\n");
            if (fresh && !OperatingSystem.IsWindows()) File.SetUnixFileMode(_auditPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("admin settings: audit not writable ({Error})", ex.GetType().Name);
        }
    }

    /// <summary>The newest <paramref name="max"/> audit lines, newest first.</summary>
    public IReadOnlyList<SettingsAuditLine> AuditTail(int max)
    {
        if (!File.Exists(_auditPath)) return [];
        var lines = File.ReadAllLines(_auditPath);
        var list = new List<SettingsAuditLine>();
        for (int i = lines.Length - 1; i >= 0 && list.Count < max; i--)
        {
            try
            {
                if (JsonSerializer.Deserialize<SettingsAuditLine>(lines[i], SupervisorJson.Options) is { } l) list.Add(l);
            }
            catch (JsonException) { }
        }
        return list;
    }
}
