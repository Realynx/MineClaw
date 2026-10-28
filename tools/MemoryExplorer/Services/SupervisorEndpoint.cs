using System.Diagnostics;
using System.Net.Http.Headers;

namespace MineClaw.MemoryExplorer.Services;

/// <summary>
/// Where the hive's Supervisor HTTP server is and how to authenticate. The token is read from
/// <c>~/.mineclaw-supervisor-token</c> (or env <c>MINECLAW_SUPERVISOR_TOKEN</c>) and only ever put into the
/// <c>X-Supervisor-Token</c> request header: it is never exposed as a property, logged, or shown.
/// </summary>
public sealed class SupervisorEndpoint
{
    public const string DefaultUrl = "http://10.1.2.73:8765";
    public const string TokenHeader = "X-Supervisor-Token";

    private string? _token;
    private bool _tokenLoaded;

    public SupervisorEndpoint(string? baseUrl = null, string? tokenFile = null)
    {
        BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultUrl : baseUrl.Trim();
        TokenFile = tokenFile ?? DefaultTokenFile;
    }

    public static string DefaultTokenFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mineclaw-supervisor-token");

    /// <summary>Base URL ("http://10.1.2.73:8765" or the tunnel's "http://localhost:8765"); editable at runtime.</summary>
    public string BaseUrl { get; set; }
    public string TokenFile { get; }

    /// <summary>True when a token is available (the value itself stays private).</summary>
    public bool HasToken => Token() != null;

    /// <summary>"token: ~/.mineclaw-supervisor-token" / "token: missing" — for the UI.</summary>
    public string TokenStatus => HasToken
        ? (Environment.GetEnvironmentVariable("MINECLAW_SUPERVISOR_TOKEN") is { Length: > 0 } ? "token: from env" : "token: ~/.mineclaw-supervisor-token")
        : "token: missing (~/.mineclaw-supervisor-token)";

    private string? Token()
    {
        if (_tokenLoaded) return _token;
        _tokenLoaded = true;
        var env = Environment.GetEnvironmentVariable("MINECLAW_SUPERVISOR_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return _token = env.Trim();
        try { _token = File.Exists(TokenFile) ? File.ReadAllText(TokenFile).Trim() : null; }
        catch (IOException) { _token = null; }
        catch (UnauthorizedAccessException) { _token = null; }
        if (_token is { Length: 0 }) _token = null;
        return _token;
    }

    /// <summary>Forget the cached token (re-read the file on next use).</summary>
    public void ReloadToken() { _tokenLoaded = false; _token = null; }

    public Uri Url(string pathAndQuery) => new(new Uri(BaseUrl.TrimEnd('/') + "/"), pathAndQuery.TrimStart('/'));

    /// <summary>A client whose every request carries the token header. Dispose it when done.</summary>
    public HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), MaxConnectionsPerServer = 8 })
        {
            BaseAddress = new Uri(BaseUrl.TrimEnd('/') + "/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };
        if (Token() is { } t) http.DefaultRequestHeaders.TryAddWithoutValidation(TokenHeader, t);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    /// <summary>Adds the token to one request (for a shared HttpClient without the default header).</summary>
    public void Authorize(HttpRequestMessage request)
    {
        if (Token() is { } t) request.Headers.TryAddWithoutValidation(TokenHeader, t);
    }

    /// <summary>
    /// Starts <c>ssh -N -L 8765:10.1.2.73:8765 alice</c> (a local tunnel to the CT) as a child process. Returns the process;
    /// the caller kills it on exit. Read-only: it only forwards a port.
    /// </summary>
    public static Process StartTunnel(int localPort = 8765, string target = "10.1.2.73:8765", string host = "alice")
    {
        var psi = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "-N", "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=30", "-L", $"{localPort}:{target}", host })
            psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new InvalidOperationException("could not start ssh");
    }
}
