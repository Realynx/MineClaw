using MineClaw.Agent;
using MineClaw.Agent.Rag;
using Microsoft.Extensions.Configuration;

namespace MineClaw.MemoryExplorer.Services;

/// <summary>
/// The bot's own configuration, layered like the bot (<c>AppConfig</c>): appsettings.json next to the exe → the gitignored
/// <c>src/MineClaw.App/appsettings.json</c> → an explicitly opened config file → plain env vars → <c>MINECLAW_</c>-prefixed env
/// vars. Only the <c>Agent</c> section is bound. Secrets stay inside <see cref="AgentOptions"/>: nothing here ever renders a key.
/// </summary>
public sealed class ExplorerConfig
{
    private ExplorerConfig(AgentOptions options, IReadOnlyList<string> sources, IConfiguration raw)
    {
        Agent = options;
        Sources = sources;
        Raw = raw;
    }

    public AgentOptions Agent { get; }
    /// <summary>Config files that existed and were layered in (paths only).</summary>
    public IReadOnlyList<string> Sources { get; }
    public IConfiguration Raw { get; }

    public bool EmbeddingsConfigured => Agent.Embeddings.IsConfigured || Agent.Embeddings.IsOllamaConfigured;

    /// <summary>"embeddings: configured (deployment X)" or why not. Never the key or the endpoint.</summary>
    public string EmbeddingsStatus => EmbeddingsConfigured
        ? Agent.Embeddings.IsOllama
            ? $"embeddings: configured (ollama model {Agent.Embeddings.Model})"
            : $"embeddings: configured (deployment {Agent.Embeddings.Deployment})"
        : "embeddings: not configured (lexical search only)";

    /// <summary>True when an Anthropic key is present (summary rebuilds possible). The key itself is never exposed.</summary>
    public bool LlmConfigured => !string.IsNullOrWhiteSpace(Agent.Llm.ApiKey) && !Agent.Llm.ApiKey.EndsWith("...", StringComparison.Ordinal);

    public string SummaryModel => Agent.Llm.CheapModel;

    /// <summary>Supervisor base URL (<c>Explorer:SupervisorUrl</c> or env <c>MINECLAW_Explorer__SupervisorUrl</c>).</summary>
    public string SupervisorUrl => Raw["Explorer:SupervisorUrl"] is { Length: > 0 } u ? u : "http://10.1.2.73:8765";

    /// <summary>A config from options alone (tests; no files, no env).</summary>
    public static ExplorerConfig FromOptions(AgentOptions options) => new(options, [], new ConfigurationBuilder().Build());

    public static ExplorerConfig Load(string? extraConfigFile = null, IDictionary<string, string?>? env = null)
    {
        var files = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            ExplorerPaths.BotConfig,
        };
        if (!string.IsNullOrWhiteSpace(extraConfigFile)) files.Add(Path.GetFullPath(extraConfigFile));
        var b = new ConfigurationBuilder();
        var used = new List<string>();
        foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool required = f == extraConfigFile;
            if (!File.Exists(f))
            {
                if (required) throw new FileNotFoundException($"config file not found: {f}", f);
                continue;
            }
            b.AddJsonFile(f, optional: !required);
            used.Add(f);
        }
        if (env == null) b.AddEnvironmentVariables().AddEnvironmentVariables("MINECLAW_");
        else
            b.AddInMemoryCollection(env.Where(kv => kv.Key.StartsWith("MINECLAW_", StringComparison.OrdinalIgnoreCase))
                .Select(kv => new KeyValuePair<string, string?>(kv.Key["MINECLAW_".Length..].Replace("__", ":"), kv.Value)));
        var raw = b.Build();
        var options = new AgentOptions();
        raw.GetSection("Agent").Bind(options);
        if (string.IsNullOrWhiteSpace(options.Llm.ApiKey) || options.Llm.ApiKey.EndsWith("...", StringComparison.Ordinal))
            options.Llm.ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? options.Llm.ApiKey;
        return new ExplorerConfig(options, used, raw);
    }

    /// <summary>The configured provider, or the null provider (lexical only). Calls only happen for query/edit embeddings.</summary>
    public IEmbeddingProvider CreateEmbeddings() => EmbeddingsConfigured ? EmbeddingProviders.Create(Agent.Embeddings, Agent.Llm.Ollama) : NullEmbeddingProvider.Instance;
}
