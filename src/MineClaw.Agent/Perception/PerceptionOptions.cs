namespace MineClaw.Agent.Perception;

/// <summary>
/// The perception tier ("Agent:Llm:Perception"): a separate model that looks at the <c>see</c> contact sheet and answers the
/// brain's spatial question in checked text, so Opus never pays for (or misreads) the image. Swapping the model is a config
/// change only: Provider = anthropic (default, reuses the Anthropic client and key) | azure-openai | openai | ollama.
/// </summary>
public sealed class PerceptionOptions
{
    /// <summary>"anthropic" (default) | "azure-openai" | "openai" (any OpenAI-compatible chat endpoint) | "ollama".</summary>
    public string Provider { get; set; } = PerceptionProviders.Anthropic;

    /// <summary>Model id (anthropic/openai/ollama) or the Azure model name for the ledger. Default Sonnet 5.5.</summary>
    public string Model { get; set; } = "claude-sonnet-5-5";

    /// <summary>azure-openai: https://&lt;resource&gt;.openai.azure.com; openai: the base URL; ollama: empty = Agent:Llm:Ollama:Endpoint.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>azure-openai "api-key" / openai or ollama bearer. Anthropic: empty = the shared Agent:Llm:ApiKey.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>azure-openai deployment name; empty = the v1 API (<c>/openai/v1/chat/completions</c> with <see cref="Model"/>).</summary>
    public string Deployment { get; set; } = "";

    /// <summary>azure-openai api-version (deployment URLs only).</summary>
    public string ApiVersion { get; set; } = "2024-10-21";

    /// <summary>"perception" (default: the perception model answers, the brain gets ~100 tokens of verified text) or "direct"
    /// (today's path: the picture goes to the caller's model).</summary>
    public string Mode { get; set; } = PerceptionModes.Perception;

    public int MaxTokens { get; set; } = 1000;
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>Anthropic only: output_config.effort ("" = omit) and thinking ("off" | "adaptive" | "omit").</summary>
    public string Effort { get; set; } = "low";
    public string Thinking { get; set; } = "off";

    /// <summary>Perception calls per rolling hour (per bot), auto-vision included; over it <c>see</c> answers with the text map.</summary>
    public int PerHour { get; set; } = 60;

    /// <summary>Answers cached by (view hash, question): asking again about an unchanged spot costs nothing.</summary>
    public int CacheEntries { get; set; } = 64;

    /// <summary>Price per MTok for a model the price table doesn't know (Azure/OpenAI/Ollama); 0 = price table / fallback.</summary>
    public decimal InputPerMTok { get; set; }
    public decimal OutputPerMTok { get; set; }

    /// <summary>Auto-vision: spatial jobs get a focused perception answer attached to the brain's / worker's next turn.</summary>
    public AutoVisionOptions Auto { get; set; } = new();

    public bool Direct => string.Equals(Mode?.Trim(), PerceptionModes.Direct, StringComparison.OrdinalIgnoreCase);
}

public sealed class AutoVisionOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Auto answers per rolling hour (inside <see cref="PerceptionOptions.PerHour"/>).</summary>
    public int PerHour { get; set; } = 20;
    /// <summary>The worker's next round waits this long for an answer already in flight (0 = never wait).</summary>
    public int WorkerWaitMs { get; set; } = 4000;
    /// <summary>Builds/repairs end with a perception check of the result ("floor patched, 1 gap at x,y,z").</summary>
    public bool VerifyBuilds { get; set; } = true;
    /// <summary>Radius of the auto focus area.</summary>
    public int Radius { get; set; } = 5;
}

public static class PerceptionModes
{
    public const string Perception = "perception";
    public const string Direct = "direct";
}

public static class PerceptionProviders
{
    public const string Anthropic = "anthropic";
    public const string AzureOpenAI = "azure-openai";
    public const string OpenAI = "openai";
    public const string Ollama = "ollama";

    public static string Normalize(string? p)
    {
        var n = string.IsNullOrWhiteSpace(p) ? Anthropic : p.Trim().ToLowerInvariant().Replace('_', '-');
        return n switch { "azure" or "azureopenai" or "azure-openai" or "aoai" => AzureOpenAI, "claude" => Anthropic, _ => n };
    }

    public static bool IsKnown(string p) => p is Anthropic or AzureOpenAI or OpenAI or Ollama;
}
