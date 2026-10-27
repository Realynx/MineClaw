namespace MineClaw.Agent.Llm;

/// <summary>
/// Per-call provider routing: each request goes to the provider of its profile, its model override, or its tier
/// (<see cref="LlmOptions.ProviderFor(LlmRequest)"/>). Clients are created on first use, so an all-Ollama setup never builds
/// the Anthropic SDK client and vice versa.
/// </summary>
public sealed class RoutingLlmClient : ILlmClient, IDisposable
{
    private readonly LlmOptions _options;
    private readonly Lazy<ILlmClient> _anthropic;
    private readonly Lazy<ILlmClient> _ollama;

    public RoutingLlmClient(LlmOptions options, Func<ILlmClient>? anthropic = null, Func<ILlmClient>? ollama = null)
    {
        _options = options;
        _anthropic = new(anthropic ?? (() => new AnthropicLlmClient(options)));
        _ollama = new(ollama ?? (() => new OllamaLlmClient(options)));
    }

    public LlmOptions Options => _options;

    /// <summary>The Ollama client (startup check / tests); created on demand.</summary>
    public ILlmClient Ollama => _ollama.Value;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) =>
        (_options.ProviderFor(request) == LlmProviders.Ollama ? _ollama.Value : _anthropic.Value).CompleteAsync(request, ct);

    public void Dispose()
    {
        if (_ollama.IsValueCreated && _ollama.Value is IDisposable d) d.Dispose();
    }
}

public static class LlmClientFactory
{
    /// <summary>
    /// The provider client for these options. Nothing on Ollama (no tier, no endpoint) → exactly the old
    /// <see cref="AnthropicLlmClient"/>, unwrapped; otherwise a <see cref="RoutingLlmClient"/>.
    /// </summary>
    public static ILlmClient Create(LlmOptions options) =>
        options.OllamaInUse ? new RoutingLlmClient(options) : new AnthropicLlmClient(options);
}
