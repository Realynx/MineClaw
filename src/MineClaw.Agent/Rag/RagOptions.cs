namespace MineClaw.Agent.Rag;

/// <summary>Retrieval settings ("Agent:Rag"). Retrieval is local (SQLite FTS5 + cached vectors) and costs zero Claude tokens.</summary>
public sealed class RagOptions
{
    /// <summary>
    /// Off = old behaviour: whole knowledge directory in the cached prompt, no auto-retrieval, no embeddings.
    /// Memory still lives in SQLite and <c>recall</c> still uses the local full-text index.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Database file; relative paths are under <see cref="AgentOptions.DataDirectory"/>.</summary>
    public string DbFile { get; set; } = "mineclaw.db";

    /// <summary>Pre-generated "clean state" db copied in when <see cref="DbFile"/> doesn't exist yet (relative to DataDirectory).</summary>
    public string SeedFile { get; set; } = "seed/mineclaw-seed.db";

    /// <summary>Inject a compact <c>[memory]</c> block into each turn (newest user message only).</summary>
    public bool AutoRetrieve { get; set; } = true;
    public int AutoTopK { get; set; } = 4;
    /// <summary>Hard cap on the whole <c>[memory]</c> block (~250 tokens).</summary>
    public int AutoMaxChars { get; set; } = 1000;
    public int AutoLineChars { get; set; } = 220;
    /// <summary>Things within this many blocks of the bot (waypoints, structures, world objects) qualify spatially (0 = off).</summary>
    public int AutoNearRadius { get; set; } = 12;

    /// <summary>Cosine gate for vector-only hits.</summary>
    public double MinSimilarity { get; set; } = 0.35;
    /// <summary>Lexical relevance gate (0..1, idf-weighted term evidence) for auto-retrieval.</summary>
    public double MinLexical { get; set; } = 0.45;

    public int RecallLimit { get; set; } = 6;
    /// <summary>Keep below the tool-result cap so recall output is never cut mid-line.</summary>
    public int RecallMaxChars { get; set; } = 390;

    /// <summary>Knowledge chunk size bounds (estimated tokens).</summary>
    public int ChunkMinTokens { get; set; } = 150;
    public int ChunkMaxTokens { get; set; } = 300;
    /// <summary>Cap on the "core rules" knowledge kept in the cached system prompt.</summary>
    public int MaxCoreChars { get; set; } = 2500;

    /// <summary>Episodes get score × (1 + boost × 0.5^(age/halfLife)).</summary>
    public double RecencyBoost { get; set; } = 0.5;
    public double RecencyHalfLifeHours { get; set; } = 24;

    public int EpisodeSummaryChars { get; set; } = 600;
    public int QueryEmbedTimeoutMs { get; set; } = 1500;
    /// <summary>Reload the newest journal into context at startup (continuity across restarts).</summary>
    public bool RestoreJournal { get; set; } = true;
    public int ChatLogMaxRows { get; set; } = 50_000;
    /// <summary>World-observation kinds that churn (container snapshots...): FTS + structured columns only, never embedded.</summary>
    public string[] NoEmbedKinds { get; set; } = ["chest", "container", "furnace", "event", "player_activity"];
    /// <summary>Reciprocal-rank-fusion constant.</summary>
    public int RrfK { get; set; } = 60;
    /// <summary>RAPTOR tree over episodes/knowledge/notes ("Agent:Rag:Raptor"), fused with the flat lists by RRF.</summary>
    public Raptor.RaptorMemoryOptions Raptor { get; set; } = new();
}

/// <summary>Embedding provider settings ("Agent:Embeddings"). Empty key/endpoint = FTS-only.</summary>
public sealed class EmbeddingOptions
{
    /// <summary>"AzureOpenAI" or "None".</summary>
    public string Provider { get; set; } = "AzureOpenAI";
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Deployment { get; set; } = "text-embedding-3-large";
    public string ApiVersion { get; set; } = "2024-10-21";
    /// <summary>Sent as the <c>dimensions</c> parameter (text-embedding-3 supports shortening).</summary>
    public int Dimensions { get; set; } = 1024;
    public int MaxBatch { get; set; } = 32;
    public int TimeoutSeconds { get; set; } = 20;
    public int MaxRetries { get; set; } = 3;
    public int MaxInputChars { get; set; } = 4000;
    /// <summary>USD per million embedding tokens (text-embedding-3-large ≈ $0.13).</summary>
    public decimal PricePerMTok { get; set; } = 0.13m;

    public bool IsConfigured =>
        Provider.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey) &&
        !Endpoint.Contains("YOUR-RESOURCE", StringComparison.OrdinalIgnoreCase) && Dimensions > 0;
}
