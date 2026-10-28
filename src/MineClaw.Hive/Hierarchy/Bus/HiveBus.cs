using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Hierarchy.Bus;

/// <summary>
/// Main's router for the off-channel hive bus. Authenticated and local only: every endpoint (main itself, a hive process over its
/// current-user-only pipe, an in-process body host) is registered by main and gets a random token; a message is accepted only
/// with its sender's token and, over IPC, only from the link that endpoint owns. Main routes everything: requests to its own
/// handler, notifications to one hive or all of them. The bus has no chat dependency at all: internal coordination can't leak.
/// </summary>
public sealed class HiveBus
{
    /// <summary>Delivers an envelope to one endpoint (an IPC send, or an in-process call). The reply goes back to the sender.</summary>
    public delegate Task<BusReply> Deliver(BusEnvelope e, CancellationToken ct);

    private sealed record Endpoint(string Id, string Token, Deliver Deliver);

    private readonly string _main;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, Endpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private long _seq, _routed, _rejected;

    public HiveBus(string main, ILogger? log = null)
    {
        _main = main;
        _log = log ?? NullLogger.Instance;
        MainToken = NewToken();
    }

    /// <summary>Main's own token (main-originated messages).</summary>
    public string MainToken { get; }

    /// <summary>Main's handler for requests and notifications addressed to "main".</summary>
    public Deliver? MainHandler { get; set; }

    public long Routed => Interlocked.Read(ref _routed);
    public long Rejected => Interlocked.Read(ref _rejected);
    public IReadOnlyCollection<string> Endpoints => _endpoints.Keys.ToList();

    /// <summary>Raised for every routed envelope (telemetry, tests). Never wired to chat.</summary>
    public event Action<BusEnvelope>? MessageRouted;

    /// <summary>Registers (or re-registers, with a fresh token) a hive endpoint; returns its token.</summary>
    public string Register(string id, Deliver deliver)
    {
        var ep = new Endpoint(id, NewToken(), deliver);
        _endpoints[id] = ep;
        return ep.Token;
    }

    public void Unregister(string id) => _endpoints.TryRemove(id, out _);

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private bool Authentic(BusEnvelope e, string? linkIdentity)
    {
        if (e.Token is not { Length: > 0 } token) return false;
        string? expected = IsMain(e.From) ? MainToken : _endpoints.TryGetValue(e.From, out var ep) ? ep.Token : null;
        if (expected == null) return false;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(expected))) return false;
        // Over IPC the link itself is the identity: a hive can't speak as another (or as main) on its own pipe.
        return linkIdentity == null || e.From.Equals(linkIdentity, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsMain(string id) => id.Equals(HiveTree.Root, StringComparison.OrdinalIgnoreCase) || id.Equals(_main, StringComparison.OrdinalIgnoreCase);

    /// <summary>Routes one envelope. <paramref name="linkIdentity"/>: the IPC link it arrived on (null = in process).</summary>
    public async Task<BusReply> RouteAsync(BusEnvelope e, string? linkIdentity = null, CancellationToken ct = default)
    {
        if (!BusTypes.All.Contains(e.Type)) return Reject(e, "unknown type");
        if (!Authentic(e, linkIdentity)) return Reject(e, "not authenticated");
        if (BusTypes.MainOnly.Contains(e.Type) && !IsMain(e.From)) return Reject(e, "main only");
        var routed = e with { Seq = Interlocked.Increment(ref _seq), Token = null }; // tokens never travel onward
        Interlocked.Increment(ref _routed);
        try { MessageRouted?.Invoke(routed); } catch (Exception) { }
        try
        {
            if (IsMain(e.To))
                return MainHandler is { } h ? await h(routed, ct).ConfigureAwait(false) : BusReply.Fail("main has no bus handler");
            if (e.To == "*")
            {
                var tasks = _endpoints.Values.Where(x => !x.Id.Equals(e.From, StringComparison.OrdinalIgnoreCase))
                    .Select(x => SafeDeliver(x, routed, ct)).ToList();
                if (!IsMain(e.From) && MainHandler is { } mh) tasks.Add(mh(routed, ct));
                var replies = await Task.WhenAll(tasks).ConfigureAwait(false);
                return new BusReply(true, Ipc.HiveJson.ToElement(new { delivered = replies.Count(r => r.Ok) }));
            }
            if (_endpoints.TryGetValue(e.To, out var to)) return await SafeDeliver(to, routed, ct).ConfigureAwait(false);
            return BusReply.Fail($"no hive '{e.To}' on the bus");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "hive bus: {Type} {From}→{To} failed", e.Type, e.From, e.To);
            return BusReply.Fail(ex.Message);
        }
    }

    private async Task<BusReply> SafeDeliver(Endpoint ep, BusEnvelope e, CancellationToken ct)
    {
        try { return await ep.Deliver(e, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "hive bus: delivery to {To} failed", ep.Id);
            return BusReply.Fail("delivery failed: " + ex.Message);
        }
    }

    private BusReply Reject(BusEnvelope e, string why)
    {
        Interlocked.Increment(ref _rejected);
        _log.LogWarning("hive bus: rejected {Type} from {From} to {To}: {Why}", e.Type, e.From, e.To, why);
        return BusReply.Fail(why);
    }

    /// <summary>Main-originated message (e.g. <see cref="BusTypes.MemChanged"/> to "*"); <paramref name="except"/> skips one hive
    /// (the writer of a memory change already has it).</summary>
    public async Task<BusReply> PublishAsync(string type, object? body, string to = "*", CancellationToken ct = default, string? except = null)
    {
        if (to != "*" || except == null) return await RouteAsync(BusEnvelope.Make(type, HiveTree.Root, to, body, MainToken), null, ct).ConfigureAwait(false);
        var targets = _endpoints.Keys.Where(k => !k.Equals(except, StringComparison.OrdinalIgnoreCase)).ToList();
        var replies = await Task.WhenAll(targets.Select(t => RouteAsync(BusEnvelope.Make(type, HiveTree.Root, t, body, MainToken), null, ct))).ConfigureAwait(false);
        return new BusReply(true, Ipc.HiveJson.ToElement(new { delivered = replies.Count(r => r.Ok) }));
    }
}
