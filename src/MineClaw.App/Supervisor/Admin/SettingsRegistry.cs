using System.Reflection;
using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;
using MineClaw.App.Hosting;
using MineClaw.Bot.Bodies;
using MineClaw.Hive;

namespace MineClaw.App.Supervisor.Admin;

public enum SettingKind { Bool, Int, Number, String, Text, Enum, Url, List, Secret, Model }

/// <summary>
/// One setting the web's Administration page shows: its config key (bound by the options classes), editor, whether a change is
/// applied live (the running options object is read per use) or at the next restart, and its limits. <see cref="ReadOnly"/> keeps a
/// setting visible but not editable (anything that would lock the admin out or break the bridge, or that only the deploy owns).
/// </summary>
public sealed record SettingDef(string Key, string Group, string Section, string Label, string Description, SettingKind Kind, bool Live = false)
{
    public bool Secret => Kind == SettingKind.Secret;
    /// <summary>Why it can't be edited here; null = editable.</summary>
    public string? ReadOnly { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public string? Unit { get; init; }
    /// <summary>For a <see cref="SettingKind.Model"/>: the provider setting that says whether this is a Claude id or an Ollama tag.</summary>
    public string? ProviderKey { get; init; }
    /// <summary>For a model whose provider is fixed (not a setting): "anthropic" or "ollama".</summary>
    public string? FixedProvider { get; init; }
    /// <summary>null is a real value ("inherit"), e.g. Viewer:AllowStory.</summary>
    public bool Nullable { get; init; }
}

public sealed record SettingGroup(string Id, string Title, string Description);

/// <summary>
/// The typed registry of everything the Administration page can show or change (docs/web/deploy.md "Bot settings from the web").
/// Every key binds to a real property of the options classes under <see cref="Roots"/> (a test walks them). Live settings are
/// the ones whose running options object is read on each use (the per-tier output budgets, the budget caps, the viewer's gates,
/// the Design Studio's limits, the console log level); everything else is saved and runs after a restart.
/// </summary>
public static class SettingsRegistry
{
    /// <summary>Top-level config section → the options class it binds (as <see cref="AppSettings.Bind"/> does).</summary>
    public static readonly IReadOnlyDictionary<string, Type> Roots = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["Agent"] = typeof(AgentOptions),
        ["Supervisor"] = typeof(SupervisorOptions),
        ["Hive"] = typeof(HiveConfig),
        ["Host"] = typeof(HostSettings),
        ["Telemetry"] = typeof(TelemetryOptions),
        ["Minecraft"] = typeof(MinecraftSettings),
        ["Bodies"] = typeof(BodiesOptions),
    };

    public static readonly IReadOnlyList<SettingGroup> Groups =
    [
        new("models", "Models & providers", "Which models the bot thinks with, where it reaches them, and what it may spend."),
        new("bot", "Bot & chat", "The bot's name in chat, who it trusts, and the Minecraft server it joins."),
        new("viewer", "Viewer access", "What the web explorer's users may do through the bot."),
        new("hive", "Hive & accounts", "How many helper bodies the bot may run and what they may spend."),
        new("story", "Story & designer", "Story Mode and the Design Studio's limits."),
        new("logging", "Logging", "How much the bot writes to its journal and telemetry."),
        new("system", "System", "Set by the deployment; shown here for reference, never edited from the web."),
    ];

    private static readonly string[] Providers = [LlmProviders.Anthropic, LlmProviders.Ollama];
    private static readonly string[] Efforts = ["", "low", "medium", "high"];
    private static readonly string[] Thinking = ["off", "adaptive", "omit"];
    private const string Lockout = "would lock the web explorer out of the bot; change it in /etc/mineclaw/appsettings.json";
    private const string DeployOwned = "set by the deployment (deploy/deploy-web.ps1 -BotConfig)";

    private static IEnumerable<SettingDef> Tier(string tier, string name, string role)
    {
        var p = $"Agent:Llm:{tier}";
        yield return new($"{p}Provider", "models", "Tiers", $"{name} provider", $"Where the {role} runs: Anthropic (Claude) or the Ollama server.", SettingKind.Enum)
            { Options = Providers };
        yield return new($"{p}Model", "models", "Tiers", $"{name} model", $"The {role}'s model. Claude ids are limited to the 5.5 generation; on Ollama, a model tag from the server.",
            SettingKind.Model) { ProviderKey = $"{p}Provider" };
        yield return new($"{p}MaxTokens", "models", "Tiers", $"{name} max output tokens", $"Output ceiling of one {role} call.", SettingKind.Int, Live: true)
            { Min = 64, Max = LlmOptions.ModelMaxOutputTokens, Unit = "tokens" };
        yield return new($"{p}Effort", "models", "Tiers", $"{name} effort", "Claude output effort (empty = not sent). Lower keeps replies terse and cheap.", SettingKind.Enum, Live: true)
            { Options = Efforts };
        yield return new($"{p}Thinking", "models", "Tiers", $"{name} thinking", "off = the cheapest setting the model accepts; adaptive = the model decides; omit = send nothing.",
            SettingKind.Enum, Live: true) { Options = Thinking };
    }

    public static readonly IReadOnlyList<SettingDef> All = Build();

    private static List<SettingDef> Build()
    {
        var l = new List<SettingDef>
        {
            // ---------------------------------------------------------------- models: Anthropic
            new("Agent:Llm:ApiKey", "models", "Anthropic", "Anthropic API key", "The key every Claude call uses (the startup check proves it).", SettingKind.Secret),
            new("Agent:Llm:BaseUrl", "models", "Anthropic", "Anthropic base URL", "A gateway or proxy in front of the Anthropic API; empty = api.anthropic.com.", SettingKind.Url),
            new("Agent:Llm:MaxRetries", "models", "Anthropic", "Retries", "How often a failed Claude call is retried.", SettingKind.Int) { Min = 0, Max = 10 },
            new("Agent:Llm:TimeoutSeconds", "models", "Anthropic", "Call timeout", "Whole-call timeout of an ordinary Claude call.", SettingKind.Int) { Min = 5, Max = 600, Unit = "s" },
            new("Agent:Llm:PrefixCacheTtl", "models", "Anthropic", "Prompt cache lifetime", "How long the brain's stable prompt prefix stays cached (1h survives idle gaps; 5m is cheaper to write).",
                SettingKind.Enum) { Options = ["1h", "5m"] },
        };
        // ---------------------------------------------------------------- models: tiers
        l.AddRange(Tier("Main", "Brain", "brain (planning, decisions, conversation)"));
        l.AddRange(Tier("Worker", "Worker", "worker (delegated multi-step tasks)"));
        l.AddRange(Tier("Cheap", "Memory", "memory tier (compaction, reflection, recall)"));
        l.AddRange(
        [
            new("Agent:Llm:AuthorMaxTokens", "models", "Authoring", "Author output budget", "Output budget of one script or blueprint authoring call (Opus 5.5; thinking included).",
                SettingKind.Int, Live: true) { Min = 1024, Max = LlmOptions.ModelMaxOutputTokens, Unit = "tokens" },
            new("Agent:Llm:AuthorEffort", "models", "Authoring", "Author effort", "Effort of authoring calls.", SettingKind.Enum, Live: true) { Options = Efforts },
            new("Agent:Llm:AuthorMaxUsd", "models", "Authoring", "Author cost cap", "USD cap of one authoring job (all its versions).", SettingKind.Number, Live: true)
                { Min = 0.05, Max = 50, Unit = "USD" },
            // ---------------------------------------------------------------- models: Ollama
            new("Agent:Llm:Ollama:Endpoint", "models", "Ollama", "Ollama endpoint", "The Ollama server (it also proxies its cloud models); empty = no Ollama anywhere.", SettingKind.Url),
            new("Agent:Llm:Ollama:ApiKey", "models", "Ollama", "Ollama bearer token", "Only for a proxy in front of Ollama.", SettingKind.Secret),
            new("Agent:Llm:Ollama:DefaultNumCtx", "models", "Ollama", "Context size", "options.num_ctx (0 = the server's default; too small silently cuts the prompt's head).", SettingKind.Int)
                { Min = 0, Max = 1_048_576, Unit = "tokens" },
            new("Agent:Llm:Ollama:KeepAlive", "models", "Ollama", "Keep alive", "How long Ollama keeps the model loaded (\"30m\", \"-1\"; empty = server default).", SettingKind.String),
            new("Agent:Llm:Ollama:TimeoutSeconds", "models", "Ollama", "Call timeout", "Per call, streams included (0 = 120 s).", SettingKind.Int) { Min = 0, Max = 3600, Unit = "s" },
            new("Agent:Llm:Ollama:MaxRetries", "models", "Ollama", "Retries", "How often a failed Ollama call is retried.", SettingKind.Int) { Min = 0, Max = 10 },
            new("Agent:Llm:Ollama:VisionModels", "models", "Ollama", "Vision models", "Ollama models that accept images (besides those whose capabilities say vision).", SettingKind.List),
            // ---------------------------------------------------------------- models: vision (perception)
            new("Agent:Llm:Perception:Provider", "models", "Vision", "Vision provider", "Who looks at the bot's pictures for the brain.", SettingKind.Enum)
                { Options = ["anthropic", "azure-openai", "openai", "ollama"] },
            new("Agent:Llm:Perception:Model", "models", "Vision", "Vision model", "The perception model (Claude: 5.5 generation only).", SettingKind.Model) { ProviderKey = "Agent:Llm:Perception:Provider" },
            new("Agent:Llm:Perception:Endpoint", "models", "Vision", "Vision endpoint", "Azure/OpenAI base URL; Ollama: empty = the Ollama endpoint above.", SettingKind.Url),
            new("Agent:Llm:Perception:ApiKey", "models", "Vision", "Vision API key", "Azure/OpenAI key or Ollama bearer; Anthropic: empty = the Anthropic key above.", SettingKind.Secret),
            new("Agent:Llm:Perception:Mode", "models", "Vision", "Vision mode", "perception = the vision model answers in a few words; direct = the picture goes to the caller's model.",
                SettingKind.Enum) { Options = ["perception", "direct"] },
            new("Agent:Llm:Perception:PerHour", "models", "Vision", "Vision calls per hour", "Per bot; over it the bot falls back to the text map.", SettingKind.Int) { Min = 0, Max = 1000 },
            // ---------------------------------------------------------------- models: budget
            new("Agent:Budget:DailyCapUsd", "models", "Budget", "Daily cap", "Model spend per day before the bot pauses its models; 0 = none.",
                SettingKind.Number, Live: true) { Min = 0, Max = 10_000, Unit = "USD" },
            new("Agent:Budget:MonthlyCapUsd", "models", "Budget", "Monthly cap", "Model spend per month before the bot pauses its models; 0 = none.", SettingKind.Number, Live: true)
                { Min = 0, Max = 100_000, Unit = "USD" },
            new("Agent:Embeddings:ApiKey", "models", "Embeddings", "Embeddings key", "Vector recall's key.", SettingKind.Secret)
                { ReadOnly = "the bridge reads it too (memory search); change it in /etc/mineclaw/mineclaw.env" },
            new("Agent:Embeddings:Endpoint", "models", "Embeddings", "Embeddings endpoint", "Vector recall's endpoint.", SettingKind.Url)
                { ReadOnly = "the bridge reads it too (memory search); change it in /etc/mineclaw/appsettings.json" },

            // ---------------------------------------------------------------- bot & chat
            new("Agent:Chat:DisplayName", "bot", "Identity", "Display name", "The name the bot answers to in chat (a name players give it in game wins).", SettingKind.String),
            new("Agent:Chat:Aliases", "bot", "Identity", "Aliases", "Other names it answers to.", SettingKind.List),
            new("Agent:Chat:ReplyWindowSeconds", "bot", "Identity", "Reply window", "A player's line this soon after talking with the bot counts as addressed to it.", SettingKind.Int)
                { Min = 0, Max = 900, Unit = "s" },
            new("Agent:Persona", "bot", "Persona", "Persona", "The bot's base character.", SettingKind.Text),
            new("Agent:PersonaExtra", "bot", "Persona", "About the players", "What the bot knows about its regular players.", SettingKind.Text),
            new("Agent:Self:TrustedPlayers", "bot", "Trust", "Trusted players", "Players whose requests the bot treats as its owners' (op commands, viewer chat). Changes who may command it.",
                SettingKind.List),
            new("Agent:Worker:MaxConcurrent", "bot", "Work", "Parallel delegated tasks", "Worker loops at once.", SettingKind.Int) { Min = 0, Max = 8 },
            new("Agent:Worker:MaxCostUsd", "bot", "Work", "Cost per delegated task", "USD cap of one worker loop.", SettingKind.Number) { Min = 0.01, Max = 20, Unit = "USD" },
            new("Minecraft:Host", "bot", "Server", "Server host", "The Minecraft server the bot joins.", SettingKind.String),
            new("Minecraft:Port", "bot", "Server", "Server port", "The Minecraft server's port.", SettingKind.Int) { Min = 1, Max = 65535 },
            new("Minecraft:Username", "bot", "Server", "Bot account", "The bot's login.", SettingKind.String)
                { ReadOnly = "the account ties the bot to its memory and identity; change it in /etc/mineclaw/appsettings.json" },

            // ---------------------------------------------------------------- viewer access
            new("Supervisor:Viewer:Enabled", "viewer", "Web viewer", "Web viewer routes", "Chat, the live feed, Story Mode and the Design Studio for the explorer. Off = those pages go dark (admin settings stay).",
                SettingKind.Bool),
            new("Supervisor:Viewer:AllowCommands", "viewer", "Web viewer", "Viewer commands", "A web user with the commands role may arm the bot's command grant from chat.", SettingKind.Bool, Live: true),
            new("Supervisor:Viewer:PerMinute", "viewer", "Web viewer", "Chat lines per minute", "Per player, from the web; more are refused (429).", SettingKind.Int, Live: true) { Min = 1, Max = 120 },
            new("Supervisor:Viewer:AllowStory", "viewer", "Web viewer", "Story Mode writes", "Empty = on whenever the viewer is.", SettingKind.Bool, Live: true) { Nullable = true },
            new("Supervisor:Viewer:OpAccounts", "viewer", "Web viewer", "Op accounts", "Accounts the bot treats as op on b1.8.1 (1.12.2 servers say it themselves).", SettingKind.List, Live: true),

            // ---------------------------------------------------------------- story & designer
            new("Supervisor:Viewer:Designer:Enabled", "story", "Design Studio", "Design Studio", "The sandbox canvas co-designed with a model.", SettingKind.Bool),
            new("Supervisor:Viewer:Designer:MaxRounds", "story", "Design Studio", "Model rounds per message", "Model calls one studio message may make.", SettingKind.Int, Live: true)
                { Min = 1, Max = 40 },
            new("Supervisor:Viewer:Designer:MaxTokens", "story", "Design Studio", "Output tokens per call", "Output ceiling of one studio call.", SettingKind.Int, Live: true)
                { Min = 256, Max = 32_000, Unit = "tokens" },
            new("Supervisor:Viewer:Designer:MaxUsdPerMessage", "story", "Design Studio", "Cost per message", "A message stops calling the model past this.", SettingKind.Number, Live: true)
                { Min = 0.01, Max = 20, Unit = "USD" },
            new("Supervisor:Viewer:Designer:HistoryTurns", "story", "Design Studio", "History turns", "Earlier exchanges sent with each message.", SettingKind.Int, Live: true) { Min = 0, Max = 40 },
            new("Supervisor:Viewer:Designer:Effort", "story", "Design Studio", "Studio effort", "Effort of studio calls (empty = the tier's).", SettingKind.Enum, Live: true) { Options = Efforts },

            // ---------------------------------------------------------------- hive & accounts
            new("Hive:Enabled", "hive", "Hive", "Hive", "Helper bodies at all.", SettingKind.Bool),
            new("Hive:MaxClones", "hive", "Hive", "Max bodies", "Hard cap on helper accounts out at once; 0 = the memory- and player-cap-aware ceiling.", SettingKind.Int)
                { Min = 0, Max = 500 },
            new("Hive:Isolation", "hive", "Hive", "Hive isolation", "inproc = hives as threads in the bot's process; process = each in its own process.", SettingKind.Enum)
                { Options = ["inproc", "process"] },
            new("Hive:Resources:LimitMb", "hive", "Hive", "Memory ceiling", "Memory the hive plans bodies into; 0 = the container's limit. Leave room for the web stack.", SettingKind.Number)
                { Min = 0, Max = 1_048_576, Unit = "MB" },
            new("Hive:Subhives:Enabled", "hive", "Hive", "Subhive bodies", "Hives may run script-only bodies of their own.", SettingKind.Bool),
            new("Hive:Subhives:MaxPerSpawn", "hive", "Hive", "Bodies per spawn", "Subhive bodies one spawn may ask for.", SettingKind.Int) { Min = 1, Max = 200 },
            new("Hive:RegularPlayers", "hive", "Accounts", "Regular players", "People who play here (kept out of the account count).", SettingKind.List),
            new("Hive:Accounts:ChatLinesPerMinute", "hive", "Accounts", "Body chat lines per minute", "How chatty a helper body may be.", SettingKind.Number) { Min = 0, Max = 60 },
            new("Hive:Accounts:Prefix", "hive", "Accounts", "Account prefix", "Helper accounts are named with this.", SettingKind.String)
                { ReadOnly = "renaming would orphan the existing helper accounts; change it in /etc/mineclaw/appsettings.json" },
            new("Hive:Budget:CloneDailyCapUsd", "hive", "Budget", "Per-body daily cap", "Model spend per helper per day; 0 = the shared caps only.", SettingKind.Number)
                { Min = 0, Max = 10_000, Unit = "USD" },
            new("Hive:Budget:CloneMonthlyCapUsd", "hive", "Budget", "Per-body monthly cap", "Model spend per helper per month; 0 = the shared caps only.", SettingKind.Number)
                { Min = 0, Max = 100_000, Unit = "USD" },

            // ---------------------------------------------------------------- logging
            new("Host:ConsoleLogLevel", "logging", "Journal", "Journal level", "Lowest level written to the journal (the file log keeps Debug).", SettingKind.Enum, Live: true)
                { Options = ["Debug", "Information", "Warning", "Error"] },
            new("Host:LogFormat", "logging", "Journal", "Journal format", "auto = systemd under journald.", SettingKind.Enum) { Options = ["auto", "simple", "systemd", "json"] },
            new("Telemetry:Enabled", "logging", "Telemetry", "Prompt telemetry", "Per-call usage and context captures.", SettingKind.Bool),
            new("Telemetry:KeepDays", "logging", "Telemetry", "Keep telemetry", "Days of telemetry files kept.", SettingKind.Int) { Min = 1, Max = 365, Unit = "days" },

            // ---------------------------------------------------------------- system (read-only)
            new("Supervisor:Enabled", "system", "Supervisor", "Supervisor", "The bot's HTTP endpoint the bridge talks to.", SettingKind.Bool) { ReadOnly = Lockout },
            new("Supervisor:AllowAdminWrites", "system", "Supervisor", "Admin writes", "Memory edits, rebuilds and these settings.", SettingKind.Bool) { ReadOnly = Lockout },
            new("Supervisor:Token", "system", "Supervisor", "Supervisor token", "The bridge's shared secret.", SettingKind.Secret) { ReadOnly = Lockout },
            new("Supervisor:Bind", "system", "Supervisor", "Listen address", "Where the supervisor listens.", SettingKind.String) { ReadOnly = Lockout },
            new("Supervisor:Port", "system", "Supervisor", "Listen port", "The supervisor's port.", SettingKind.Int) { ReadOnly = Lockout },
            new("Agent:DataDirectory", "system", "Paths", "Data directory", "The bot's database, usage and this override file.", SettingKind.String) { ReadOnly = DeployOwned },
        ]);
        return l;
    }

    public static SettingDef? Find(string key) => All.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ property paths

    /// <summary>The property chain a key binds to (root section's type first); null = it binds to nothing.</summary>
    public static IReadOnlyList<PropertyInfo>? PathOf(string key)
    {
        var segs = key.Split(':');
        if (segs.Length < 2 || !Roots.TryGetValue(segs[0], out var type)) return null;
        var chain = new List<PropertyInfo>();
        foreach (var s in segs.Skip(1))
        {
            var p = type.GetProperty(s, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p == null || p.GetIndexParameters().Length > 0) return null;
            chain.Add(p);
            type = p.PropertyType;
        }
        return chain;
    }

    /// <summary>The value at the key's path under a root options object (null when a parent is null).</summary>
    public static object? Read(object root, IReadOnlyList<PropertyInfo> path)
    {
        object? o = root;
        foreach (var p in path)
        {
            if (o == null) return null;
            o = p.GetValue(o);
        }
        return o;
    }

    /// <summary>Sets the value at the key's path under a live root options object; false when a parent is null or it is read-only.</summary>
    public static bool Write(object root, IReadOnlyList<PropertyInfo> path, object? value)
    {
        object? o = root;
        for (int i = 0; i < path.Count - 1; i++)
        {
            o = path[i].GetValue(o);
            if (o == null) return false;
        }
        var last = path[^1];
        if (!last.CanWrite) return false;
        last.SetValue(o, value);
        return true;
    }

    /// <summary>Whether a property type can hold this kind of setting (the coverage test's check).</summary>
    public static bool Fits(SettingKind kind, Type t) => kind switch
    {
        SettingKind.Bool => t == typeof(bool) || t == typeof(bool?),
        SettingKind.Int => t == typeof(int) || t == typeof(int?) || t == typeof(long),
        SettingKind.Number => t == typeof(double) || t == typeof(decimal) || t == typeof(double?) || t == typeof(decimal?) || t == typeof(float),
        SettingKind.List => t == typeof(string[]) || t == typeof(List<string>),
        _ => t == typeof(string),
    };
}
