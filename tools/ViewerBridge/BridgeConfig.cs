using System.Text.Json;
using MineClaw.Agent;

namespace MineClaw.ViewerBridge;

/// <summary>
/// The bridge's settings. Layered (lowest first): the bot's own config (<c>Bridge:BotConfig</c>, default
/// <c>/etc/mineclaw/appsettings.json</c>: db location, <c>Supervisor:Token</c>/<c>Port</c>, the <c>Agent</c> section search needs) →
/// the bridge's <c>appsettings.json</c> / <c>appsettings.{Environment}.json</c> beside the exe (dev: gitignored Development file) →
/// the key file (<c>Bridge:KeyFile</c>, default <c>/etc/mineclaw-bridge/bridge.json</c>) → env <c>MINECLAW_Section__Key</c> →
/// env <c>MINECLAW_BRIDGE_KEY</c> / <c>MINECLAW_SUPERVISOR_TOKEN</c>. Secrets are never logged: <see cref="Describe"/> names only
/// where each came from.
/// </summary>
public sealed class BridgeOptions
{
    public const string DefaultBotConfig = "/etc/mineclaw/appsettings.json";
    public const string DefaultKeyFile = "/etc/mineclaw-bridge/bridge.json";

    public string Bind { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8766;
    /// <summary>HMAC secret shared with Nest (≥ 32 chars). Secret.</summary>
    public string? Key { get; set; }
    /// <summary>The agent db; default <c>&lt;DataDir&gt;/&lt;Agent:Rag:DbFile&gt;</c>.</summary>
    public string? DbPath { get; set; }
    /// <summary>The bot's data dir (the bot runs with <c>--data-dir /var/lib/mineclaw</c>, which its config file doesn't carry).</summary>
    public string? DataDir { get; set; }
    /// <summary>Default knowledge packs (index.json + .mcshard).</summary>
    public string? PacksDir { get; set; }
    /// <summary>JSONL audit of every write the bridge forwards or serves.</summary>
    public string? AuditLog { get; set; }
    /// <summary>"b1.8.1" | "1.12.2"; default: <c>server-version.txt</c> beside the db, else b1.8.1.</summary>
    public string? ServerVersion { get; set; }
    /// <summary>Supervisor base URL; default <c>http://127.0.0.1:{Supervisor:Port}</c>.</summary>
    public string? SupervisorUrl { get; set; }
    /// <summary>Total connections to the supervisor (its MaxConnections is 8; 1..7 taken). Two of four or more are reserved for live motion
    /// and status; smaller pools reserve one, except a single connection remains shared. 6 adds an interactive lane of two (story
    /// commands, one chest, a body's inventory, recipes) and leaves two ordinary; 7 also gives the story reads that wait on the colony
    /// lock one of their own. A total of 5 or less has no interactive lane.</summary>
    public int SupervisorConnections { get; set; } = 6;
    /// <summary>Memory heat decay half-life (seconds).</summary>
    public double HeatHalfLifeS { get; set; } = 120;
    /// <summary>How often the db-side caches (vectors, RAPTOR tree) look for changes the bot made.</summary>
    public int ReloadCheckSeconds { get; set; } = 60;
    /// <summary>Threads the memory cloud's background UMAP may use (its kNN is the only parallel step).</summary>
    public int CloudThreads { get; set; } = 1;
    /// <summary>Above this many build leaves the cloud's UMAP runs on an even sample and places the rest from their neighbours (100..2000).</summary>
    public int CloudMaxUmapPoints { get; set; } = 2000;
    /// <summary>
    /// Above this many build leaves the cloud's distance layout (metric MDS, all-pairs SMACOF) runs on an even sample and places the
    /// rest by minimising their own stress against it (100..3000; 3000 leaves = a 36 MB distance matrix while it runs).
    /// </summary>
    public int CloudMaxMdsPoints { get; set; } = 3000;
}

public sealed class BridgeConfig
{
    private BridgeConfig(IConfiguration raw, BridgeOptions bridge, AgentOptions agent, string? token, int supPort, IReadOnlyList<string> sources, string keySource, string tokenSource)
    {
        Raw = raw;
        Bridge = bridge;
        Agent = agent;
        SupervisorToken = token;
        SupervisorPort = supPort;
        Sources = sources;
        KeySource = keySource;
        TokenSource = tokenSource;
    }

    public IConfiguration Raw { get; }
    public BridgeOptions Bridge { get; }
    public AgentOptions Agent { get; }
    public string? SupervisorToken { get; }
    public int SupervisorPort { get; }
    public IReadOnlyList<string> Sources { get; }
    public string KeySource { get; }
    public string TokenSource { get; }

    public string SupervisorUrl => (Bridge.SupervisorUrl is { Length: > 0 } u ? u : $"http://127.0.0.1:{SupervisorPort}").TrimEnd('/');

    public string DbPath
    {
        get
        {
            if (Bridge.DbPath is { Length: > 0 } p) return Path.GetFullPath(p);
            return Path.GetFullPath(Path.Combine(DataDir, string.IsNullOrWhiteSpace(Agent.Rag.DbFile) ? "mineclaw.db" : Agent.Rag.DbFile));
        }
    }

    public string DataDir
    {
        get
        {
            if (Bridge.DataDir is { Length: > 0 } d) return Path.GetFullPath(d);
            if (Path.IsPathRooted(Agent.DataDirectory)) return Agent.DataDirectory;
            if (OperatingSystem.IsLinux() && Directory.Exists("/var/lib/mineclaw")) return "/var/lib/mineclaw";
            return Path.GetFullPath(Path.Combine(RepoRoot() ?? AppContext.BaseDirectory, Agent.DataDirectory));
        }
    }

    public string PacksDir
    {
        get
        {
            if (Bridge.PacksDir is { Length: > 0 } d) return Path.GetFullPath(d);
            foreach (var c in new[] { "/opt/mineclaw/current/shards/defaults", Path.Combine(AppContext.BaseDirectory, "shards", "defaults") })
                if (Directory.Exists(c)) return c;
            return Path.Combine(RepoRoot() ?? AppContext.BaseDirectory, "shards", "defaults");
        }
    }

    public string AuditLog => Bridge.AuditLog is { Length: > 0 } a ? Path.GetFullPath(a)
        : OperatingSystem.IsLinux() && Directory.Exists("/var/log/mineclaw-bridge") ? "/var/log/mineclaw-bridge/audit.jsonl"
        : Path.Combine(AppContext.BaseDirectory, "logs", "bridge-audit.jsonl");

    public string ServerVersion
    {
        get
        {
            if (Bridge.ServerVersion is "1.12.2" or "b1.8.1") return Bridge.ServerVersion;
            try
            {
                var f = Path.Combine(Path.GetDirectoryName(DbPath) ?? DataDir, "server-version.txt");
                if (File.Exists(f) && File.ReadAllText(f).Contains("1.12.2", StringComparison.Ordinal)) return "1.12.2";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return "b1.8.1";
        }
    }

    public bool EmbeddingsConfigured => Agent.Embeddings.IsConfigured || Agent.Embeddings.IsOllamaConfigured;

    /// <summary>"embeddings: configured (deployment X)" or why not; never a key or an endpoint.</summary>
    public string EmbeddingsStatus => EmbeddingsConfigured
        ? Agent.Embeddings.IsOllama ? $"embeddings: configured (ollama model {Agent.Embeddings.Model})" : $"embeddings: configured (deployment {Agent.Embeddings.Deployment})"
        : "embeddings: not configured (lexical search only)";

    public bool LlmConfigured => !string.IsNullOrWhiteSpace(Agent.Llm.ApiKey) && !Agent.Llm.ApiKey.EndsWith("...", StringComparison.Ordinal);

    public MineClaw.Agent.Rag.IEmbeddingProvider CreateEmbeddings() =>
        EmbeddingsConfigured ? MineClaw.Agent.Rag.EmbeddingProviders.Create(Agent.Embeddings, Agent.Llm.Ollama) : MineClaw.Agent.Rag.NullEmbeddingProvider.Instance;

    /// <summary>One line per setting source, without values.</summary>
    public string Describe() =>
        $"config: {string.Join(", ", Sources)}; key: {KeySource}; supervisor token: {TokenSource}; {EmbeddingsStatus}";

    /// <summary>For tests: options and settings without any file or env.</summary>
    public static BridgeConfig ForTests(BridgeOptions bridge, AgentOptions? agent = null, string? token = "test-token", int supPort = 8765) =>
        new(new ConfigurationBuilder().Build(), bridge, agent ?? new AgentOptions(), token, supPort, ["tests"], "tests", "tests");

    public static BridgeConfig Load(string[] args, string environment, IDictionary<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.OrdinalIgnoreCase);
        var envPairs = env.Where(kv => kv.Key.StartsWith("MINECLAW_", StringComparison.OrdinalIgnoreCase))
            .Select(kv => new KeyValuePair<string, string?>(kv.Key["MINECLAW_".Length..].Replace("__", ":"), kv.Value)).ToList();
        var own = new[] { Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(AppContext.BaseDirectory, $"appsettings.{environment}.json") }
            .Concat(new[] { Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"), Path.Combine(Directory.GetCurrentDirectory(), $"appsettings.{environment}.json") })
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();
        var (botConfig, keyFile) = Locate(args, own, envPairs);
        var (raw, sources, keyFileUsed) = BuildConfig(args, own, envPairs, botConfig, keyFile);
        var bridge = new BridgeOptions();
        raw.GetSection("Bridge").Bind(bridge);
        var keySource = KeySourceOf(raw, bridge, env, keyFileUsed);
        var agent = new AgentOptions();
        raw.GetSection("Agent").Bind(agent);
        var (token, tokenSource) = TokenOf(raw, env);
        int port = int.TryParse(raw["Supervisor:Port"], out var sp) ? sp : 8765;
        return new BridgeConfig(raw, bridge, agent, token, port, sources, keySource, tokenSource);
    }

    /// <summary>Pass 1: where the bot config and the key file are.</summary>
    private static (string BotConfig, string KeyFile) Locate(string[] args, List<string> own, List<KeyValuePair<string, string?>> envPairs)
    {
        var pre = new ConfigurationBuilder();
        foreach (var f in own) pre.AddJsonFile(f, optional: true);
        pre.AddInMemoryCollection(envPairs).AddCommandLine(args);
        var p = pre.Build();
        var botConfig = p["Bridge:BotConfig"] is { Length: > 0 } bc ? bc : BridgeOptions.DefaultBotConfig;
        var keyFile = p["Bridge:KeyFile"] is { Length: > 0 } kf ? kf : BridgeOptions.DefaultKeyFile;
        return (botConfig, keyFile);
    }

    /// <summary>Pass 2: the bot config, the bridge's own files, the key file, env and the command line (later wins).</summary>
    private static (IConfigurationRoot Raw, List<string> Sources, bool KeyFileUsed) BuildConfig(string[] args, List<string> own, List<KeyValuePair<string, string?>> envPairs,
        string botConfig, string keyFile)
    {
        var b = new ConfigurationBuilder();
        var sources = new List<string>();
        if (File.Exists(botConfig)) { b.AddJsonFile(Path.GetFullPath(botConfig), optional: true); sources.Add("bot config " + botConfig); }
        foreach (var f in own) { b.AddJsonFile(f, optional: true); sources.Add(Path.GetFileName(f)); }
        bool keyFileUsed = false;
        if (File.Exists(keyFile))
        {
            try { b.AddJsonFile(Path.GetFullPath(keyFile), optional: true); keyFileUsed = true; sources.Add("key file " + keyFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        b.AddInMemoryCollection(envPairs).AddCommandLine(args);
        return (b.Build(), sources, keyFileUsed);
    }

    /// <summary>Where the bridge key came from (MINECLAW_BRIDGE_KEY in the environment wins and sets it).</summary>
    private static string KeySourceOf(IConfiguration raw, BridgeOptions bridge, IDictionary<string, string?> env, bool keyFileUsed)
    {
        string keySource = keyFileUsed && raw["Bridge:Key"] is { Length: > 0 } ? "key file" : bridge.Key is { Length: > 0 } ? "config" : "missing";
        if (env.TryGetValue("MINECLAW_BRIDGE_KEY", out var envKey) && !string.IsNullOrWhiteSpace(envKey)) { bridge.Key = envKey.Trim(); return "env MINECLAW_BRIDGE_KEY"; }
        return env.ContainsKey("MINECLAW_Bridge__Key") ? "env MINECLAW_Bridge__Key" : keySource;
    }

    /// <summary>The supervisor token and where it came from (MINECLAW_SUPERVISOR_TOKEN in the environment wins).</summary>
    private static (string? Token, string Source) TokenOf(IConfiguration raw, IDictionary<string, string?> env)
    {
        string? token = raw["Supervisor:Token"];
        string tokenSource = string.IsNullOrEmpty(token) ? "missing" : "config";
        if (env.TryGetValue("MINECLAW_SUPERVISOR_TOKEN", out var envTok) && !string.IsNullOrWhiteSpace(envTok)) { token = envTok.Trim(); tokenSource = "env MINECLAW_SUPERVISOR_TOKEN"; }
        return (token, tokenSource);
    }

    private static string? RepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "MineClaw.sln"))) return d.FullName;
        return null;
    }
}

/// <summary>The bridge's JSON: snake_case, nulls omitted, NaN/±Infinity as named literals (the supervisor's convention).</summary>
public static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PropertyNameCaseInsensitive = true,
    };

    public static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    public static string Text<T>(T value) => JsonSerializer.Serialize(value, Options);
}
