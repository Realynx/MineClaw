using MineClaw.Hive.Ipc;
using MineClaw.World.Claims;

namespace MineClaw.Hive.Claims;

/// <summary>A live lease on the board.</summary>
public sealed record Lease(ClaimKey Key, string Owner, DateTimeOffset Expires, string? Note)
{
    public LeaseMsg ToMsg() => new(Key.Id, Owner, Expires.ToUnixTimeMilliseconds());
}

/// <summary>
/// The authoritative claim board, hosted by the main process: leases with TTLs on blocks, areas, chests, furnaces and
/// tasks. Re-claiming something you hold renews it; expired leases are dropped lazily. A dead clone's leases go with
/// <see cref="ReleaseAll"/> the moment its pipe closes. Thread-safe.
/// </summary>
public sealed class ClaimBoard(TimeSpan? defaultTtl = null, TimeProvider? time = null)
{
    private readonly object _lock = new();
    private readonly List<Lease> _leases = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public TimeSpan DefaultTtl { get; } = defaultTtl ?? TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(30);

    /// <summary>Raised (outside the lock) after any grant, renewal or release, so the mirror can be pushed to clones.</summary>
    public event Action? Changed;

    private DateTimeOffset Now => _time.GetUtcNow();

    private void Purge(DateTimeOffset now) => _leases.RemoveAll(l => l.Expires <= now);

    public ClaimResult TryClaim(string owner, ClaimKey key, TimeSpan? ttl = null, string? note = null)
    {
        var t = ttl is { } x && x > TimeSpan.Zero ? (x > MaxTtl ? MaxTtl : x) : DefaultTtl;
        ClaimResult r;
        lock (_lock)
        {
            var now = Now;
            Purge(now);
            var other = _leases.FirstOrDefault(l => !Same(l.Owner, owner) && l.Key.ConflictsWith(key));
            if (other != null) return ClaimResult.Refused(other.Owner, other.Expires);
            _leases.RemoveAll(l => Same(l.Owner, owner) && l.Key == key);
            var lease = new Lease(key, owner, now + t, note);
            _leases.Add(lease);
            r = ClaimResult.Granted(owner, lease.Expires);
        }
        Changed?.Invoke();
        return r;
    }

    public bool Release(string owner, ClaimKey key)
    {
        int n;
        lock (_lock) n = _leases.RemoveAll(l => Same(l.Owner, owner) && l.Key == key);
        if (n > 0) Changed?.Invoke();
        return n > 0;
    }

    /// <summary>Drops every lease of <paramref name="owner"/> (a clone died or logged out). Returns how many.</summary>
    public int ReleaseAll(string owner)
    {
        int n;
        lock (_lock) n = _leases.RemoveAll(l => Same(l.Owner, owner));
        if (n > 0) Changed?.Invoke();
        return n;
    }

    public bool IsFree(string owner, ClaimKey key)
    {
        lock (_lock)
        {
            var now = Now;
            return !_leases.Any(l => l.Expires > now && !Same(l.Owner, owner) && l.Key.ConflictsWith(key));
        }
    }

    public string? HolderOf(ClaimKey key)
    {
        lock (_lock)
        {
            var now = Now;
            return _leases.FirstOrDefault(l => l.Expires > now && l.Key.ConflictsWith(key))?.Owner;
        }
    }

    public IReadOnlyList<Lease> Leases()
    {
        lock (_lock)
        {
            Purge(Now);
            return _leases.ToArray();
        }
    }

    public int CountOf(string owner) { lock (_lock) return _leases.Count(l => Same(l.Owner, owner) && l.Expires > Now); }

    public ClaimsMsg Mirror() => new(Leases().Select(l => l.ToMsg()).ToList());

    /// <summary>The main bot's own view of the board (in process, no IPC).</summary>
    public IClaimBoard ViewFor(string owner) => new LocalView(this, owner);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed class LocalView(ClaimBoard board, string owner) : IClaimBoard
    {
        public string Owner => owner;
        public bool IsFree(ClaimKey key) => board.IsFree(owner, key);
        public Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default) =>
            Task.FromResult(board.TryClaim(owner, key, ttl, note));
        public Task ReleaseAsync(ClaimKey key, CancellationToken ct = default) { board.Release(owner, key); return Task.CompletedTask; }
    }
}

/// <summary>
/// A clone's view of the main process's board: claims/releases go over IPC (main decides), <see cref="IsFree"/> reads the
/// mirror main pushes after every change (plus our own grants), so it's a fast local check. With the pipe gone (emergency
/// logout) claims are granted locally — the clone is only depositing on its way out.
/// </summary>
public sealed class RemoteClaimBoard(string owner, Func<HiveLink?> link, TimeProvider? time = null) : IClaimBoard
{
    private readonly object _lock = new();
    private List<Lease> _mirror = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Owner => owner;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public void SetMirror(ClaimsMsg m)
    {
        var list = new List<Lease>();
        foreach (var l in m.Leases)
            if (ClaimKey.TryParse(l.Key, out var k)) list.Add(new Lease(k, l.Owner, DateTimeOffset.FromUnixTimeMilliseconds(l.ExpiresMs), null));
        lock (_lock) _mirror = list;
    }

    public bool IsFree(ClaimKey key)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
            return !_mirror.Any(l => l.Expires > now && !string.Equals(l.Owner, owner, StringComparison.OrdinalIgnoreCase) && l.Key.ConflictsWith(key));
    }

    public async Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default)
    {
        var l = link();
        if (l is not { IsOpen: true }) return ClaimResult.Granted(owner, _time.GetUtcNow() + (ttl ?? TimeSpan.FromSeconds(60)));
        try
        {
            var r = await l.RequestAsync<ClaimReplyMsg>(Msg.Claim, new ClaimMsg(key.Id, (ttl ?? TimeSpan.Zero).TotalSeconds, note), RequestTimeout, ct).ConfigureAwait(false);
            if (r == null) return new ClaimResult(false, null, default, "no answer from main");
            var exp = DateTimeOffset.FromUnixTimeMilliseconds(r.ExpiresMs);
            if (r.Ok) lock (_lock) { _mirror.RemoveAll(x => x.Key == key && string.Equals(x.Owner, owner, StringComparison.OrdinalIgnoreCase)); _mirror.Add(new Lease(key, owner, exp, note)); }
            return r.Ok ? ClaimResult.Granted(owner, exp) : ClaimResult.Refused(r.Holder ?? "?", exp);
        }
        catch (HiveIpcException ex) { return new ClaimResult(false, null, default, ex.Message); }
    }

    public async Task ReleaseAsync(ClaimKey key, CancellationToken ct = default)
    {
        lock (_lock) _mirror.RemoveAll(x => x.Key == key && string.Equals(x.Owner, owner, StringComparison.OrdinalIgnoreCase));
        if (link() is { IsOpen: true } l) await l.SendAsync(Msg.Release, new ReleaseMsg(key.Id), ct).ConfigureAwait(false);
    }
}
