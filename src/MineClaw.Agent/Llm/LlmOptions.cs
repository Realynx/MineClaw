namespace MineClaw.Agent.Llm;

/// <summary>Model/tier configuration. Bindable from appsettings ("Agent:Llm").</summary>
public sealed class LlmOptions
{
    /// <summary>Haiku-class "memory" model: compaction, reflection, ask_memory, budget-fallback chat.</summary>
    public string CheapModel { get; set; } = "claude-haiku-5-5";

    /// <summary>Opus-class "frontal lobe": planning, decisions, all player conversation.</summary>
    public string MainModel { get; set; } = "claude-opus-5-5";

    /// <summary>Sonnet-class worker/builder for delegated multi-step tasks (and the brain's first budget fallback).</summary>
    public string WorkerModel { get; set; } = "claude-sonnet-5-5";

    public int CheapMaxTokens { get; set; } = 400;
    public int MainMaxTokens { get; set; } = 1200;
    public int WorkerMaxTokens { get; set; } = 800;

    /// <summary>Hard output ceiling of the 5.5 models (max_tokens above this is a 400).</summary>
    public const int ModelMaxOutputTokens = 128_000;

    /// <summary>Default <see cref="AuthorMaxTokens"/> (also the blueprint author's default).</summary>
    public const int DefaultAuthorMaxTokens = 100_000;

    /// <summary>
    /// Output budget of an authoring call (Lua scripts, blueprints, build plans): the separate Opus author context, never the
    /// brain's turns (those keep <see cref="MainMaxTokens"/>; the brain's context limit and compaction are unchanged). Opus 5.5
    /// can't turn thinking off, so this covers its thinking AND the artifact. Clamped to <see cref="ModelMaxOutputTokens"/>.
    /// </summary>
    public int AuthorMaxTokens { get; set; } = DefaultAuthorMaxTokens;

    /// <summary><see cref="AuthorMaxTokens"/> as sent: 1024..128K.</summary>
    public int AuthorMaxTokensClamped => Math.Clamp(AuthorMaxTokens <= 0 ? DefaultAuthorMaxTokens : AuthorMaxTokens, 1024, ModelMaxOutputTokens);

    /// <summary>output_config.effort of authoring calls (with thinking always on for Opus 5.5, effort + budget are the levers).</summary>
    public string AuthorEffort { get; set; } = "medium";

    /// <summary>USD cap of ONE authoring job (all its versions): a streaming call is aborted once its running cost passes the rest.</summary>
    public decimal AuthorMaxUsd { get; set; } = 3.00m;

    /// <summary>Author versions per job (v1 + revisions after lint/check failures) before it gives up.</summary>
    public int AuthorMaxVersions { get; set; } = 3;

    /// <summary>
    /// The interrupt patch turn (<c>Authoring/PatchAgent</c>): one small Opus 5.5 call that returns a few lines of JS for a paused
    /// script. Output budget (thinking + the tool call) and effort; a tiny cached system prompt keeps it fast.
    /// </summary>
    public int PatchMaxTokens { get; set; } = 600;
    public string PatchEffort { get; set; } = "low";

    /// <summary>Calls with max_tokens above this stream (a long non-streaming request risks HTTP timeouts).</summary>
    public int StreamAboveTokens { get; set; } = 16_000;

    /// <summary>Whole-call timeout of a large streamed call (100K output tokens take many minutes; <see cref="TimeoutSeconds"/> is for small calls).</summary>
    public int LongCallTimeoutMinutes { get; set; } = 45;

    /// <summary>output_config.effort per tier ("low"|"medium"|"high"|""=omit). Low keeps replies terse.</summary>
    public string CheapEffort { get; set; } = "low";
    public string MainEffort { get; set; } = "medium";
    public string WorkerEffort { get; set; } = "low";

    /// <summary>
    /// "off" (default: the cheapest setting the model accepts — disabled, or between_tools on Sonnet 5.5),
    /// "adaptive", or "omit" (send nothing).
    /// </summary>
    public string CheapThinking { get; set; } = "off";
    public string MainThinking { get; set; } = "off";
    public string WorkerThinking { get; set; } = "off";

    /// <summary>
    /// Cache TTL of the brain's stable prefix: tools + static system + core knowledge (breakpoint 1) and the identity/self
    /// block (breakpoint 2). "1h" (default; 2x base input per write, survives idle gaps: live, 4-15 min between brain calls
    /// re-wrote ~31-36K prefix tokens each time on a 5m cache) or "5m" (1.25x write, refreshed free by every hit). The history
    /// breakpoint always stays 5m, after the prefix (the API wants longer TTLs first).
    /// </summary>
    public string PrefixCacheTtl { get; set; } = "1h";

    /// <summary>
    /// Legacy override of breakpoint 1 only (the static block): "5m", "1h", or "auto" = 1h while a session is active (chat
    /// within <see cref="ActiveSessionMinutes"/>), else 5m. Null/empty = <see cref="PrefixCacheTtl"/>. Breakpoint 2 never
    /// outlives breakpoint 1.
    /// </summary>
    public string? StaticPrefixTtl { get; set; }

    /// <summary>For StaticPrefixTtl "auto": a session counts as active this long after the last chat to the bot.</summary>
    public int ActiveSessionMinutes { get; set; } = 30;

    /// <summary><see cref="PrefixCacheTtl"/> as sent: "5m" when configured so, else "1h".</summary>
    public string PrefixTtl => NormalizeTtl(PrefixCacheTtl) ?? "1h";

    /// <summary>"1h" / "5m" (case-insensitive, trimmed), else null.</summary>
    public static string? NormalizeTtl(string? ttl) => ttl?.Trim().ToLowerInvariant() switch
    {
        "1h" => "1h",
        "5m" => "5m",
        _ => null,
    };

    /// <summary>API key; null means the SDK default (ANTHROPIC_API_KEY env var).</summary>
    public string? ApiKey { get; set; }

    public int MaxRetries { get; set; } = 2;
    public int TimeoutSeconds { get; set; } = 60;
    /// <summary>
    /// Stream interruptible calls (the brain's first call of a turn) so a restart for a new chat line aborts generation
    /// server-side and reports partial usage. Off = plain requests (cancel still drops the HTTP call client-side).
    /// </summary>
    public bool StreamInterruptible { get; set; } = true;

    // ------------------------------------------------------------------ providers (per tier) and named profiles

    /// <summary>Provider per tier: "anthropic" (default) or "ollama" (then the tier's *Model is an Ollama model name).</summary>
    public string MainProvider { get; set; } = LlmProviders.Anthropic;
    public string WorkerProvider { get; set; } = LlmProviders.Anthropic;
    public string CheapProvider { get; set; } = LlmProviders.Anthropic;

    /// <summary>The local Ollama server ("Agent:Llm:Ollama"). Empty endpoint = no Ollama anywhere (behaviour unchanged).</summary>
    public OllamaOptions Ollama { get; set; } = new();

    /// <summary>The perception tier ("Agent:Llm:Perception"): the model that looks at <c>see</c> pictures for the brain.</summary>
    public Perception.PerceptionOptions Perception { get; set; } = new();

    /// <summary>
    /// Named model profiles ("Agent:Llm:Profiles") the brain can pick for a delegated task or a clone's brain. Built-ins:
    /// opus, sonnet, haiku. A profile without a model, or on an unconfigured provider, is never offered or accepted.
    /// </summary>
    public Dictionary<string, LlmProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["opus"] = new() { Model = "claude-opus-5-5", Notes = "planning, tricky building", CostHint = "$$$" },
        ["sonnet"] = new() { Model = "claude-sonnet-5-5", Notes = "normal multi-step work", CostHint = "$$" },
        ["haiku"] = new() { Model = "claude-haiku-5-5", Notes = "simple repetitive chores", CostHint = "$" },
    };

    /// <summary>Profile the brain tier runs on (set by <see cref="ApplyBrainProfile"/> for a clone; telemetry tag). Null = config.</summary>
    public string? MainProfile { get; private set; }

    /// <summary>
    /// Whether the brain tier may write Lua scripts. True for the configured brain (keyed to the tier; a warning is logged when
    /// it isn't Opus 5.5); false once a non-Opus profile was applied to this process's brain (a hive clone on haiku...).
    /// </summary>
    public bool BrainAuthorsScripts { get; private set; } = true;

    /// <summary>The brain model the user requires for script authorship.</summary>
    public const string ScriptAuthorModel = "claude-opus-5-5";

    /// <summary>An Opus-class model: never a worker unless a profile's <see cref="LlmProfile.Worker"/> says so.</summary>
    public static bool IsBrainClass(string? model) => model != null && model.Contains("opus", StringComparison.OrdinalIgnoreCase);

    public static bool IsOpus55(string? model) => model != null && model.StartsWith(ScriptAuthorModel, StringComparison.OrdinalIgnoreCase);

    public string ProviderFor(ModelTier tier) => LlmProviders.Normalize(tier switch { ModelTier.Cheap => CheapProvider, ModelTier.Worker => WorkerProvider, _ => MainProvider });

    /// <summary>Any tier configured for Ollama.</summary>
    public bool TiersUseOllama => new[] { ModelTier.Main, ModelTier.Worker, ModelTier.Cheap }.Any(t => ProviderFor(t) == LlmProviders.Ollama);

    /// <summary>Any tier configured for Anthropic (the API key is required only then).</summary>
    public bool TiersUseAnthropic => new[] { ModelTier.Main, ModelTier.Worker, ModelTier.Cheap }.Any(t => ProviderFor(t) == LlmProviders.Anthropic);

    /// <summary>Ollama is reachable from some call: a tier uses it, or the endpoint is set (profiles may use it).</summary>
    public bool OllamaInUse => TiersUseOllama || Ollama.IsConfigured;

    /// <summary>A provider counts as configured: anthropic = an API key is set; ollama = the endpoint is set.</summary>
    public bool ProviderConfigured(string provider) => LlmProviders.Normalize(provider) switch
    {
        LlmProviders.Anthropic => !string.IsNullOrWhiteSpace(ApiKey),
        LlmProviders.Ollama => Ollama.IsConfigured,
        _ => false,
    };

    /// <summary>Provider of a model id: the tier that names it, else a profile that names it, else anthropic.</summary>
    public string ProviderOfModel(string? model)
    {
        if (string.IsNullOrEmpty(model)) return ProviderFor(ModelTier.Main);
        if (TierOfModel(model) is { } t) return ProviderFor(t);
        foreach (var p in Profiles.Values)
            if (string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase)) return LlmProviders.Normalize(p.Provider);
        return LlmProviders.Anthropic;
    }

    /// <summary>Profile name of a model id (telemetry / budget tag): the tier's applied profile, else the first profile naming it.</summary>
    public string? ProfileOfModel(string? model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        if (MainProfile != null && string.Equals(model, MainModel, StringComparison.OrdinalIgnoreCase)) return MainProfile;
        foreach (var (name, p) in Profiles)
            if (string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase) && string.Equals(LlmProviders.Normalize(p.Provider), ProviderOfModel(model), StringComparison.Ordinal))
                return name;
        return null;
    }

    /// <summary>Which provider serves a request: its profile, else the provider of its model override, else the tier's.</summary>
    public string ProviderFor(LlmRequest request)
    {
        if (request.Profile != null && Profiles.TryGetValue(request.Profile, out var p)) return LlmProviders.Normalize(p.Provider);
        return request.Model != null ? ProviderOfModel(request.Model) : ProviderFor(request.Tier);
    }

    /// <summary>Profiles that can be used right now (model set, provider configured), cheapest-first as configured.</summary>
    public IEnumerable<string> UsableProfiles() => Profiles.Where(kv => CheckProfile(kv.Key) == null).Select(kv => kv.Key);

    /// <summary>Null when <paramref name="name"/> can be used; else a clear refusal ("no profile 'x' (have ...)").</summary>
    public string? CheckProfile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "no profile given";
        name = name.Trim();
        if (!Profiles.TryGetValue(name, out var p))
            return $"no profile '{name}' (have {string.Join(", ", Profiles.Keys.Where(k => CheckProfile(k) == null))})";
        if (_disabled.TryGetValue(name, out var why)) return $"profile '{name}' is unavailable: {why}";
        if (string.IsNullOrWhiteSpace(p.Model)) return $"profile '{name}' isn't configured (no model)";
        var provider = LlmProviders.Normalize(p.Provider);
        if (!LlmProviders.IsKnown(provider)) return $"profile '{name}' has an invalid provider '{p.Provider}' (anthropic|ollama)";
        if (!ProviderConfigured(provider))
            return provider == LlmProviders.Ollama ? $"profile '{name}' needs Agent:Llm:Ollama:Endpoint" : $"profile '{name}' needs an Anthropic API key";
        foreach (var sub in new[] { p.Worker, p.Memory })
            if (!string.IsNullOrWhiteSpace(sub) && !string.Equals(sub, name, StringComparison.OrdinalIgnoreCase) && CheckProfile(sub) is { } e)
                return $"profile '{name}': {e}";
        return null;
    }

    /// <summary>
    /// The profile a <c>delegate</c> worker loop really runs on. Workers use <see cref="WorkerModel"/> unless the profile
    /// explicitly says otherwise: a profile naming <see cref="LlmProfile.Worker"/> runs the worker on that profile; a cheaper
    /// profile (haiku, local) runs as asked; a brain-class profile (the brain model / Opus) without an explicit
    /// <c>Worker</c> stays on the worker tier (live 2026-10-27: a water task ran 12 Opus worker calls for $0.107).
    /// Null = the tier's model.
    /// </summary>
    public string? WorkerProfileFor(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        var name = requested.Trim();
        if (Profile(name) is not { } p) return name; // unknown: CheckProfile refuses it
        if (!string.IsNullOrWhiteSpace(p.Worker)) return p.Worker.Trim();
        var model = p.Model.Trim();
        if (IsBrainClass(model)) return null;
        return name;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _disabled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Startup found the profile's model missing or tool-less: refuse it with <paramref name="why"/>.</summary>
    public void DisableProfile(string name, string why) => _disabled[name.Trim()] = why;

    public LlmProfile? Profile(string? name) => name != null && Profiles.TryGetValue(name.Trim(), out var p) ? p : null;

    /// <summary>
    /// Hive clone spawned with a profile: its brain tier (and worker/memory when the profile names them) runs on that profile.
    /// A non-Opus brain loses script authorship. Returns an error (profile unusable) or null.
    /// </summary>
    public string? ApplyBrainProfile(string name)
    {
        if (CheckProfile(name) is { } err) return err;
        var p = Profiles[name.Trim()];
        MainModel = p.Model.Trim();
        MainProvider = LlmProviders.Normalize(p.Provider);
        if (p.MaxTokens is { } mt and > 0) MainMaxTokens = mt;
        if (!string.IsNullOrWhiteSpace(p.Thinking)) MainThinking = p.Thinking!;
        MainProfile = name.Trim();
        BrainAuthorsScripts = IsOpus55(MainModel) && MainProvider == LlmProviders.Anthropic;
        if (Profile(p.Worker) is { } w)
        {
            WorkerModel = w.Model.Trim();
            WorkerProvider = LlmProviders.Normalize(w.Provider);
            if (w.MaxTokens is { } wmt and > 0) WorkerMaxTokens = wmt;
        }
        if (Profile(p.Memory) is { } c)
        {
            CheapModel = c.Model.Trim();
            CheapProvider = LlmProviders.Normalize(c.Provider);
            if (c.MaxTokens is { } cmt and > 0) CheapMaxTokens = cmt;
        }
        return null;
    }

    /// <summary>Config problems of the provider settings (empty = fine). With nothing set to ollama this is always empty.</summary>
    public List<string> ValidateProviders()
    {
        var p = new List<string>();
        foreach (var (key, value) in new[] { ("MainProvider", MainProvider), ("WorkerProvider", WorkerProvider), ("CheapProvider", CheapProvider) })
            if (!LlmProviders.IsKnown(LlmProviders.Normalize(value))) p.Add($"Agent:Llm:{key} '{value}' is not a provider (anthropic|ollama)");
        foreach (var (tier, key) in new[] { (ModelTier.Main, "MainProvider"), (ModelTier.Worker, "WorkerProvider"), (ModelTier.Cheap, "CheapProvider") })
            if (ProviderFor(tier) == LlmProviders.Ollama && !Ollama.IsConfigured)
                p.Add($"Agent:Llm:{key} is ollama but Agent:Llm:Ollama:Endpoint is empty");
        foreach (var (name, prof) in Profiles)
            if (!string.IsNullOrWhiteSpace(prof.Model) && !LlmProviders.IsKnown(LlmProviders.Normalize(prof.Provider)))
                p.Add($"Agent:Llm:Profiles:{name}:Provider '{prof.Provider}' is not a provider (anthropic|ollama)");
        if (Ollama.IsConfigured && !Uri.TryCreate(Ollama.Endpoint.Trim(), UriKind.Absolute, out _))
            p.Add($"Agent:Llm:Ollama:Endpoint '{Ollama.Endpoint}' is not an absolute URL");
        // Anthropic tiers and profiles run only on the 5.5 generation (user rule, 2026-10-28).
        foreach (var (tier, key) in new[] { (ModelTier.Main, "MainModel"), (ModelTier.Worker, "WorkerModel"), (ModelTier.Cheap, "CheapModel") })
            if (ProviderFor(tier) == LlmProviders.Anthropic && !ModelProfiles.IsCurrentGeneration(ModelFor(tier)))
                p.Add($"Agent:Llm:{key} '{ModelFor(tier)}' is older than the 5.5 generation (use one of {string.Join(", ", ModelProfiles.CurrentGeneration)})");
        foreach (var (name, prof) in Profiles)
            if (!string.IsNullOrWhiteSpace(prof.Model) && LlmProviders.Normalize(prof.Provider) == LlmProviders.Anthropic
                && !ModelProfiles.IsCurrentGeneration(prof.Model))
                p.Add($"Agent:Llm:Profiles:{name}:Model '{prof.Model}' is older than the 5.5 generation");
        return p;
    }

    public string ModelFor(ModelTier tier) => tier switch { ModelTier.Cheap => CheapModel, ModelTier.Worker => WorkerModel, _ => MainModel };
    public int MaxTokensFor(ModelTier tier) => tier switch { ModelTier.Cheap => CheapMaxTokens, ModelTier.Worker => WorkerMaxTokens, _ => MainMaxTokens };
    public string EffortFor(ModelTier tier) => tier switch { ModelTier.Cheap => CheapEffort, ModelTier.Worker => WorkerEffort, _ => MainEffort };
    public string ThinkingFor(ModelTier tier) => tier switch { ModelTier.Cheap => CheapThinking, ModelTier.Worker => WorkerThinking, _ => MainThinking };

    /// <summary>Which tier a model id belongs to (for per-tier accounting); null for an unknown model.</summary>
    public ModelTier? TierOfModel(string model) =>
        string.Equals(model, MainModel, StringComparison.OrdinalIgnoreCase) ? ModelTier.Main
        : string.Equals(model, WorkerModel, StringComparison.OrdinalIgnoreCase) ? ModelTier.Worker
        : string.Equals(model, CheapModel, StringComparison.OrdinalIgnoreCase) ? ModelTier.Cheap
        : null;
}

public static class LlmProviders
{
    public const string Anthropic = "anthropic";
    public const string Ollama = "ollama";

    /// <summary>"Ollama " → "ollama"; empty → anthropic (the default).</summary>
    public static string Normalize(string? provider) => string.IsNullOrWhiteSpace(provider) ? Anthropic : provider.Trim().ToLowerInvariant();

    public static bool IsKnown(string provider) => provider is Anthropic or Ollama;
}

/// <summary>A named model choice ("Agent:Llm:Profiles:&lt;name&gt;").</summary>
public sealed class LlmProfile
{
    public string Provider { get; set; } = LlmProviders.Anthropic;
    public string Model { get; set; } = "";
    /// <summary>When to pick it (shown to the brain in the tool description).</summary>
    public string? Notes { get; set; }
    public int? MaxTokens { get; set; }
    /// <summary>Thinking override ("off" | "adaptive" | "omit").</summary>
    public string? Thinking { get; set; }
    /// <summary>"$", "$$", "free"... for the brain's choice.</summary>
    public string? CostHint { get; set; }
    /// <summary>
    /// For a clone's brain: the profile its worker tier uses (null = the clone's config). For <c>delegate {profile}</c>: an
    /// Opus-class profile runs the worker on this profile only when it is set (e.g. "opus" = "opus"); otherwise on WorkerModel.
    /// </summary>
    public string? Worker { get; set; }
    /// <summary>For a clone's brain: the profile its memory tier uses (null = the clone's config).</summary>
    public string? Memory { get; set; }
}

/// <summary>The local Ollama server. It proxies cloud models itself, so one endpoint covers both.</summary>
public sealed class OllamaOptions
{
    /// <summary>"http://localhost:11434". Empty = Ollama unused.</summary>
    public string Endpoint { get; set; } = "";
    /// <summary>Optional bearer token (for a proxy in front of Ollama).</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>options.num_ctx (0 = server default). Too small silently truncates the prompt's head.</summary>
    public int DefaultNumCtx { get; set; }
    /// <summary>keep_alive ("30m", "-1"...; empty = server default). Keeping the model loaded keeps the KV-cache prefix warm.</summary>
    public string KeepAlive { get; set; } = "";
    /// <summary>Per call, streams included (0 = 120 s).</summary>
    public int TimeoutSeconds { get; set; } = 120;
    public int EffectiveTimeoutSeconds => TimeoutSeconds > 0 ? TimeoutSeconds : 120;
    public int MaxRetries { get; set; } = 1;
    /// <summary>options.temperature (null = model default).</summary>
    public double? Temperature { get; set; }
    /// <summary>Models that accept images (besides those whose /api/show capabilities say "vision").</summary>
    public string[] VisionModels { get; set; } = [];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);

    public Uri BaseUri => new(Endpoint.Trim().TrimEnd('/') + "/");
}

/// <summary>Per-model request-shape quirks (which thinking-off form is accepted, whether effort is supported).</summary>
public static class ModelProfiles
{
    public enum ThinkingOffStyle { Omit, Disabled, BetweenTools }

    /// <summary>Claude models this bot may run on: the 5.5 generation (and the Fable/Mythos 5.1 tier) only.</summary>
    public static readonly string[] CurrentGeneration =
        ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-1", "claude-mythos-5-1"];

    public static bool IsCurrentGeneration(string model) =>
        CurrentGeneration.Any(g => model.Trim().StartsWith(g, StringComparison.OrdinalIgnoreCase));

    public static ThinkingOffStyle OffStyle(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.StartsWith("claude-sonnet-5-5")) return ThinkingOffStyle.BetweenTools; // disabled → 400
        if (m.StartsWith("claude-haiku-5-5")) return ThinkingOffStyle.Disabled;      // accepted at effort high or below
        return ThinkingOffStyle.Omit; // Opus 5.5, Fable/Mythos 5.1: thinking can't be disabled; low effort is the lever
    }

    public static bool SupportsEffort(string model) => model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase);
}
