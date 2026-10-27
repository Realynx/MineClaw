namespace MineClaw.Raptor;

/// <summary>Batch text embedder (production: Azure text-embedding-3-large @ 1024 dims). Vectors are normalized by the library.</summary>
public interface IEmbedder
{
    /// <summary>Model id recorded in the tree metadata; a mismatch on load means the tree must be re-embedded.</summary>
    string Model { get; }
    int Dimensions { get; }
    /// <summary>One vector per input, same order. May throw; callers surface the failure.</summary>
    Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}

public sealed record EmbeddingResult(IReadOnlyList<float[]> Vectors, long Tokens);

/// <summary>Cluster summarizer (production: Claude Haiku). Receives the paper's prompt pre-rendered.</summary>
public interface ISummarizer
{
    Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken ct);
}

/// <param name="Context">The raw cluster text (children joined as in utils.get_text).</param>
/// <param name="SystemPrompt">Paper Appendix D system message.</param>
/// <param name="UserPrompt">Paper Appendix D user message with {context} substituted.</param>
/// <param name="MaxTokens">summarization_length (max output tokens).</param>
public sealed record SummaryRequest(string Context, string SystemPrompt, string UserPrompt, int MaxTokens);

/// <summary>Summary text plus the usage the provider reported (used for budget accounting).</summary>
public sealed record SummaryResult(string Text, int InputTokens, int OutputTokens, double? CostUsd = null);

/// <summary>Token counter used for chunking, cluster caps and the retrieval budget (reference: tiktoken cl100k_base).</summary>
public interface ITokenCounter
{
    int Count(string text);
}
