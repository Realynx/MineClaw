using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MineClaw.Agent.Llm;

namespace MineClaw.App.Supervisor.Admin;

public sealed record ProviderTestResult(bool Ok, string Message, double? LatencyMs = null, IReadOnlyList<string>? Models = null);

public sealed record OllamaModelInfoView(string Name, IReadOnlyList<string>? Capabilities);

public sealed record OllamaListResult(bool Ok, string? Error, IReadOnlyList<OllamaModelInfoView> Models, double LatencyMs);

/// <summary>Provider checks without saving anything (tests fake it: no network).</summary>
public interface IProviderTester
{
    /// <summary>Retrieves one model with the key (the free Models API): proves the key works and the 5.5-generation id exists.</summary>
    Task<ProviderTestResult> AnthropicAsync(string apiKey, string? baseUrl, string model, CancellationToken ct);
    /// <summary><c>GET /api/tags</c> (+ <c>/api/show</c> capabilities for the first models when <paramref name="details"/>).</summary>
    Task<OllamaListResult> OllamaAsync(string endpoint, string? apiKey, bool details, CancellationToken ct);
}

public sealed class LiveProviderTester : IProviderTester
{
    /// <summary>Capabilities are looked up for at most this many models (one /api/show each).</summary>
    public const int MaxShow = 24;

    public async Task<ProviderTestResult> AnthropicAsync(string apiKey, string? baseUrl, string model, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var client = string.IsNullOrWhiteSpace(baseUrl) ? new Anthropic.AnthropicClient { ApiKey = apiKey } : new Anthropic.AnthropicClient { ApiKey = apiKey, BaseUrl = baseUrl.Trim() };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var m = await client.Models.Retrieve(model, cancellationToken: cts.Token).ConfigureAwait(false);
            return new ProviderTestResult(true, $"key accepted; {m.ID} is available", sw.Elapsed.TotalMilliseconds, [m.ID]);
        }
        catch (Anthropic.Exceptions.AnthropicUnauthorizedException) { return new ProviderTestResult(false, "the key was rejected (401)", sw.Elapsed.TotalMilliseconds); }
        catch (Anthropic.Exceptions.AnthropicNotFoundException) { return new ProviderTestResult(false, $"the key works but model '{model}' was not found (404)", sw.Elapsed.TotalMilliseconds); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new ProviderTestResult(false, "no answer within 20 s", sw.Elapsed.TotalMilliseconds); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never echo the exception text: it may carry the request URL or headers.
            return new ProviderTestResult(false, $"the API could not be reached ({ex.GetType().Name})", sw.Elapsed.TotalMilliseconds);
        }
    }

    public async Task<OllamaListResult> OllamaAsync(string endpoint, string? apiKey, bool details, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var o = new OllamaOptions { Endpoint = endpoint, ApiKey = apiKey ?? "", TimeoutSeconds = 10 };
        using var http = OllamaLlmClient.CreateHttp(o);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(12));
            var catalog = new OllamaCatalog(o, http);
            var tags = await catalog.TagsAsync(cts.Token).ConfigureAwait(false);
            var models = tags.Select(t => new OllamaModelInfoView(t, null)).ToList();
            if (details && models.Count > 0) models = await WithCapabilitiesAsync(catalog, models, ct).ConfigureAwait(false);
            return new OllamaListResult(true, null, models, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException or OllamaException)
        {
            if (ct.IsCancellationRequested) throw;
            return new OllamaListResult(false, WhyUnreachable(ex), [], sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>The first <see cref="MaxShow"/> models with their capabilities (four <c>show</c> calls at a time, 8 s in all; one that
    /// fails stays as it was).</summary>
    private static async Task<List<OllamaModelInfoView>> WithCapabilitiesAsync(OllamaCatalog catalog, List<OllamaModelInfoView> models, CancellationToken ct)
    {
        using var showCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        showCts.CancelAfter(TimeSpan.FromSeconds(8));
        using var gate = new SemaphoreSlim(4);
        var shown = await Task.WhenAll(models.Take(MaxShow).Select(async m =>
        {
            await gate.WaitAsync(showCts.Token).ConfigureAwait(false);
            try { return m with { Capabilities = (await catalog.ShowAsync(m.Name, showCts.Token).ConfigureAwait(false))?.Capabilities }; }
            catch (Exception) { return m; }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        return [.. shown, .. models.Skip(MaxShow)];
    }

    private static string WhyUnreachable(Exception ex) => ex switch
    {
        OperationCanceledException => "no answer within 12 s",
        OllamaException oe => $"the server answered {(int?)oe.StatusCode ?? 0}",
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } => "connection refused or host unreachable",
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "the host name does not resolve",
        HttpRequestException { StatusCode: { } s } => $"the server answered {(int)s}",
        System.Text.Json.JsonException => "the answer is not Ollama's (not JSON)",
        _ => "the server could not be reached",
    };

    /// <summary>
    /// An endpoint the bot may be pointed at from the web: absolute http(s), no credentials in the URL, not a link-local or cloud
    /// metadata address. Null = fine; else why not.
    /// </summary>
    public static string? CheckEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return "an endpoint is required";
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            return "must be an absolute http:// or https:// URL";
        if (!string.IsNullOrEmpty(u.UserInfo)) return "credentials don't go in the URL";
        return ForbiddenHost(u);
    }

    /// <summary>A cloud metadata name or a link-local address (IPv4 169.254/16, IPv6 fe80::/10): why not; else null.</summary>
    private static string? ForbiddenHost(Uri u)
    {
        if (u.Host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase)) return "that address is not allowed";
        if (!IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip)) return null;
        return IsLinkLocal(ip) ? "link-local addresses are not allowed" : null;
    }

    private static bool IsLinkLocal(IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetwork ? ip.GetAddressBytes() is [169, 254, ..]
        : ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv6LinkLocal;
}
