namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>An area the viewer needs blocks for (the free camera, an eye tile's bot), re-indexed every <paramref name="PeriodMs"/>.</summary>
public readonly record struct Interest(int Dim, int X, int Z, int Radius, int PeriodMs = 1200);

/// <summary>
/// Keeps the viewer in step with the hive (all GET, all off the UI thread):
/// <list type="bullet">
/// <item>fast loop: bots + entities at 5 Hz (also the connection state and latency)</item>
/// <item>world loop: per interest an index of its columns, then only the new/changed ones fetched as binary payloads (hash
/// deltas, nearest first, 48 per request); overlays every 4 s for the camera's dimension; server-rendered view PNGs for the
/// tiles that show iso/top/side</item>
/// </list>
/// 429/503 back off by Retry-After; a dropped connection retries every 2 s.
/// </summary>
public sealed class HiveSync : IDisposable
{
    private readonly HiveApi _api;
    private readonly HiveWorldCache _cache;
    private readonly Dictionary<(int, int, int, int), DateTime> _indexed = new();
    private readonly Dictionary<(string, string), DateTime> _viewed = new();
    private readonly Dictionary<int, OverlaysDto> _overlays = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _fast, _world;
    private DateTime _overlaysAt = DateTime.MinValue;
    private int? _overlaysDim;

    public HiveSync(HiveApi api, HiveWorldCache cache)
    {
        _api = api;
        _cache = cache;
    }

    public const int BatchColumns = 48;
    /// <summary>Most columns kept per dimension (a column is ~64 KB decoded).</summary>
    public int MaxColumnsPerDim { get; init; } = 1600;

    /// <summary>Areas to keep loaded (called on the world loop; must be thread-safe).</summary>
    public Func<IReadOnlyList<Interest>> Interests { get; set; } = () => [];
    /// <summary>Server-rendered tiles wanted: (bot key, mode).</summary>
    public Func<IReadOnlyList<(string Bot, string Mode)>> MapViews { get; set; } = () => [];
    /// <summary>The x-ray toggle sent with the server pictures ("auto", "on", "off").</summary>
    public string XRay { get; set; } = "auto";
    /// <summary>The dimension whose overlays are wanted (null = none).</summary>
    public Func<int?> OverlayDim { get; set; } = () => null;

    public LinkState State { get; private set; } = LinkState.Disconnected;
    public string StateText { get; private set; } = "not connected";
    public EntitiesDto? Entities { get; private set; }
    public DateTime EntitiesAt { get; private set; }
    public long Frames;

    /// <summary>The merged entity set the delta stream keeps (thread-safe; renderers read interpolated positions from it).</summary>
    public EntityStore Store { get; init; } = new();
    private long _entityRev;

    public event Action? StateChanged;
    public event Action<EntitiesDto>? EntitiesChanged;
    public event Action<string, string, byte[]>? ViewPng;

    public OverlaysDto? Overlays(int dim) { lock (_lock) return _overlays.GetValueOrDefault(dim); }

    public bool Running => _cts != null;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetState(LinkState.Connecting, "connecting…");
        _fast = Task.Run(() => FastLoop(ct), ct);
        _world = Task.Run(() => WorldLoop(ct), ct);
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        if (cts == null) return;
        cts.Cancel();
        // Never block the caller (the UI thread, on every tab switch) on loops that may be mid-request: they see the cancelled
        // token and end on their own; the token source is disposed once both have.
        var loops = Task.WhenAll(_fast ?? Task.CompletedTask, _world ?? Task.CompletedTask);
        _ = loops.ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
        SetState(LinkState.Disconnected, "not connected");
    }

    private void SetState(LinkState s, string text)
    {
        if (State == s && StateText == text) return;
        State = s;
        StateText = text;
        StateChanged?.Invoke();
    }

    /// <summary>The last problem either loop hit (shown in the UI; written to the trace once per distinct message).</summary>
    public string? LastError { get; private set; }
    /// <summary>When the last world pass succeeded (for the "stale Ns" badges).</summary>
    public DateTime WorldOkAt { get; private set; }
    public long Errors;

    private void Problem(Exception ex)
    {
        Interlocked.Increment(ref Errors);
        var msg = ex is HiveApiException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
        if (msg == LastError) return;
        LastError = msg;
        System.Diagnostics.Trace.WriteLine("livehive sync: " + (ex is HiveApiException ? msg : ex.ToString()));
    }

    /// <summary>A loop's wait after a failure: grows with consecutive failures, ±25 % jitter (so viewers don't retry in step).</summary>
    private static TimeSpan Backoff(int failures, double baseS = 0.5, double maxS = 10) =>
        TimeSpan.FromSeconds(Math.Min(maxS, baseS * Math.Pow(2, Math.Min(10, failures))) * (0.75 + Random.Shared.NextDouble() * 0.5));

    private async Task FastLoop(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var wait = TimeSpan.FromMilliseconds(200);
            bool ok = false;
            try
            {
                var e = await PollEntitiesAsync(ct).ConfigureAwait(false);
                ok = true;
                EntitiesChanged?.Invoke(e);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { wait = FastFailure(ex, failures); }
            failures = ok ? 0 : failures + 1;
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One entity poll (the delta since the last revision), merged into the store; the link is connected.</summary>
    private async Task<EntitiesDto> PollEntitiesAsync(CancellationToken ct)
    {
        var d = await _api.EntitiesSinceAsync(_entityRev, ct).ConfigureAwait(false);
        var e = Store.Apply(d);
        _entityRev = d.Rev;
        Entities = e;
        EntitiesAt = DateTime.UtcNow;
        SetState(LinkState.Connected, "connected");
        return e;
    }

    /// <summary>The fast loop's reaction to a failed poll (after <paramref name="failures"/> in a row before it): the wait before the next.</summary>
    private TimeSpan FastFailure(Exception ex, int failures)
    {
        switch (ex)
        {
            case HiveApiException { Status: 401 or 403 }:
                Problem(ex);
                SetState(LinkState.Unauthorized, ex.Message);
                return TimeSpan.FromSeconds(3);
            case HiveApiException { Status: 429 or 503 } busy:
                return busy.RetryAfter ?? Backoff(failures);
            case HiveApiException { Status: 0 }:
                Problem(ex);
                // One missed poll isn't a dropped link: say "reconnecting" only after a few in a row.
                if (failures >= 2) SetState(LinkState.Disconnected, "reconnecting… (" + ex.Message + ")");
                return Backoff(failures);
            default:
                // Anything else (a bad reply, a handler bug): log once, keep the loop alive.
                Problem(ex);
                if (failures >= 2) SetState(LinkState.Error, ex.Message);
                return Backoff(failures);
        }
    }

    private async Task WorldLoop(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var wait = TimeSpan.FromMilliseconds(150);
            if (State is LinkState.Connected or LinkState.Disconnected or LinkState.Error)
            {
                try { await WorldPass(ct).ConfigureAwait(false); failures = 0; WorldOkAt = DateTime.UtcNow; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { Problem(ex); wait = Backoff(failures++, 0.25, 5); } // logged once; the loop never dies
            }
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task WorldPass(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var it in Interests())
            if (!await SyncInterestAsync(it, now, ct).ConfigureAwait(false)) break;
        await SyncOverlaysAsync(now, ct).ConfigureAwait(false);
        await SyncViewsAsync(now, ct).ConfigureAwait(false);
    }

    /// <summary>Claims <paramref name="key"/>'s next slot in a schedule: false = not due yet (a schedule over 64 keys starts over).</summary>
    private bool Claim<TKey>(Dictionary<TKey, DateTime> schedule, TKey key, DateTime now, int periodMs) where TKey : notnull
    {
        lock (_lock)
        {
            if (schedule.TryGetValue(key, out var due) && now < due) return false;
            if (schedule.Count > 64) schedule.Clear();
            schedule[key] = now + TimeSpan.FromMilliseconds(periodMs);
            return true;
        }
    }

    private void Unindex((int, int, int, int) key)
    {
        lock (_lock) _indexed.Remove(key);
    }

    /// <summary>One interest: its index (when due), then the new/changed columns nearest first. False = the index route is
    /// backing off (the rest of the pass skips indexing).</summary>
    private async Task<bool> SyncInterestAsync(Interest it, DateTime now, CancellationToken ct)
    {
        int r = Math.Clamp(it.Radius, 16, 256);
        var key = (it.Dim, it.X >> 5, it.Z >> 5, r); // moving within 32 blocks reuses the schedule
        if (!Claim(_indexed, key, now, it.PeriodMs)) return true;
        if (_api.BackingOff("index")) { Unindex(key); return false; }
        IndexDto index;
        try { index = await _api.IndexAsync(it.Dim, it.X, it.Z, r, ct).ConfigureAwait(false); }
        catch (HiveApiException ex) { Problem(ex); Unindex(key); return true; } // retried after the route's back-off
        _cache.SetWorldInfo(it.Dim, index.Version, index.Time);
        if (index.Rain is { } rain) _cache.SetWeather(it.Dim, rain, index.Thunder ?? 0f);
        int cx0 = (it.X - r) >> 4, cx1 = (it.X + r) >> 4, cz0 = (it.Z - r) >> 4, cz1 = (it.Z + r) >> 4;
        var want = _cache.Reconcile(it.Dim, cx0, cz0, cx1, cz1, index.Columns);
        if (want.Count == 0) return true;
        await FetchColumnsAsync(it, key, want, ct).ConfigureAwait(false);
        _cache.Trim(it.Dim, it.X >> 4, it.Z >> 4, MaxColumnsPerDim);
        return true;
    }

    /// <summary>The wanted columns nearest the interest's centre first, <see cref="BatchColumns"/> per request, at most 8 requests.</summary>
    private async Task FetchColumnsAsync(Interest it, (int, int, int, int) key, List<(int Cx, int Cz)> want, CancellationToken ct)
    {
        int ccx = it.X >> 4, ccz = it.Z >> 4;
        want.Sort((a, b) => ((a.Cx - ccx) * (a.Cx - ccx) + (a.Cz - ccz) * (a.Cz - ccz)).CompareTo((b.Cx - ccx) * (b.Cx - ccx) + (b.Cz - ccz) * (b.Cz - ccz)));
        for (int i = 0; i < want.Count && i < BatchColumns * 8; i += BatchColumns)
        {
            if (_api.BackingOff("chunks")) { Unindex(key); break; }
            if (!await FetchBatchAsync(it.Dim, want.Skip(i).Take(BatchColumns).ToList(), key, ct).ConfigureAwait(false)) break;
        }
    }

    /// <summary>One chunks request applied to the cache; false = it failed (the interest is re-indexed next pass).</summary>
    private async Task<bool> FetchBatchAsync(int dim, List<(int Cx, int Cz)> batch, (int, int, int, int) key, CancellationToken ct)
    {
        try
        {
            double f0 = FreecamStats.NowMs;
            var payload = await _api.ChunksAsync(dim, batch, ct).ConfigureAwait(false);
            double a0 = FreecamStats.NowMs;
            if (FreecamStats.Shared.OnUiThread) Interlocked.Increment(ref FreecamStats.Shared.SyncOnUiThread);
            Interlocked.Increment(ref FreecamStats.Shared.SyncCalls);
            _cache.Apply(payload);
            FreecamStats.Shared.FetchMs.Add(a0 - f0);
            FreecamStats.Shared.SyncMs.Add(FreecamStats.NowMs - a0);
            return true;
        }
        catch (Exception ex) when (ex is HiveApiException or InvalidDataException) { Problem(ex); Unindex(key); return false; } // retried on the next index
    }

    private bool OverlaysDue(int dim, DateTime now) => dim != _overlaysDim || now - _overlaysAt > TimeSpan.FromSeconds(4);

    /// <summary>The camera dimension's overlays, every 4 s (at once when the dimension changes).</summary>
    private async Task SyncOverlaysAsync(DateTime now, CancellationToken ct)
    {
        if (OverlayDim() is not { } od || !OverlaysDue(od, now) || _api.BackingOff("overlays")) return;
        _overlaysAt = now;
        _overlaysDim = od;
        try
        {
            var ov = await _api.OverlaysAsync(od, ct).ConfigureAwait(false);
            lock (_lock) _overlays[od] = ov;
        }
        catch (HiveApiException ex) { Problem(ex); }
    }

    /// <summary>The server-rendered tiles, each at most every 1.5 s.</summary>
    private async Task SyncViewsAsync(DateTime now, CancellationToken ct)
    {
        foreach (var (bot, mode) in MapViews())
        {
            if (!Claim(_viewed, (bot, mode), now, 1500)) continue;
            if (_api.BackingOff("view")) break;
            await FetchViewAsync(bot, mode, ct).ConfigureAwait(false);
        }
    }

    private async Task FetchViewAsync(string bot, string mode, CancellationToken ct)
    {
        byte[] png;
        try { png = await _api.ViewPngAsync(bot, mode, XRay, ct).ConfigureAwait(false); }
        catch (HiveApiException ex) { Problem(ex); return; }
        try { ViewPng?.Invoke(bot, mode, png); } // the handler decodes here, on the sync worker, not on the UI thread
        catch (Exception ex) { Problem(ex); }
    }

    public void Dispose() => Stop();
}
