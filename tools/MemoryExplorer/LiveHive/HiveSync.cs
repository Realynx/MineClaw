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
                var d = await _api.EntitiesSinceAsync(_entityRev, ct).ConfigureAwait(false);
                var e = Store.Apply(d);
                _entityRev = d.Rev;
                Entities = e;
                EntitiesAt = DateTime.UtcNow;
                SetState(LinkState.Connected, "connected");
                ok = true;
                EntitiesChanged?.Invoke(e);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (HiveApiException ex) when (ex.Status is 401 or 403) { Problem(ex); SetState(LinkState.Unauthorized, ex.Message); wait = TimeSpan.FromSeconds(3); }
            catch (HiveApiException ex) when (ex.Status is 429 or 503) { wait = ex.RetryAfter ?? Backoff(failures); }
            catch (HiveApiException ex) when (ex.Status == 0)
            {
                Problem(ex);
                // One missed poll isn't a dropped link: say "reconnecting" only after a few in a row.
                if (failures >= 2) SetState(LinkState.Disconnected, "reconnecting… (" + ex.Message + ")");
                wait = Backoff(failures);
            }
            catch (Exception ex)
            {
                // Anything else (a bad reply, a handler bug): log once, keep the loop alive.
                Problem(ex);
                if (failures >= 2) SetState(LinkState.Error, ex.Message);
                wait = Backoff(failures);
            }
            failures = ok ? 0 : failures + 1;
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
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
        var interests = Interests();
        foreach (var it in interests)
        {
            int r = Math.Clamp(it.Radius, 16, 256);
            var key = (it.Dim, it.X >> 5, it.Z >> 5, r); // moving within 32 blocks reuses the schedule
            lock (_lock)
            {
                if (_indexed.TryGetValue(key, out var due) && now < due) continue;
                if (_indexed.Count > 64) _indexed.Clear();
                _indexed[key] = now + TimeSpan.FromMilliseconds(it.PeriodMs);
            }
            if (_api.BackingOff("index")) { lock (_lock) _indexed.Remove(key); break; }
            IndexDto index;
            try { index = await _api.IndexAsync(it.Dim, it.X, it.Z, r, ct).ConfigureAwait(false); }
            catch (HiveApiException ex) { Problem(ex); lock (_lock) _indexed.Remove(key); continue; } // retried after the route's back-off
            _cache.SetWorldInfo(it.Dim, index.Version, index.Time);
            int cx0 = (it.X - r) >> 4, cx1 = (it.X + r) >> 4, cz0 = (it.Z - r) >> 4, cz1 = (it.Z + r) >> 4;
            var want = _cache.Reconcile(it.Dim, cx0, cz0, cx1, cz1, index.Columns);
            if (want.Count == 0) continue;
            int ccx = it.X >> 4, ccz = it.Z >> 4;
            want.Sort((a, b) => ((a.Cx - ccx) * (a.Cx - ccx) + (a.Cz - ccz) * (a.Cz - ccz)).CompareTo((b.Cx - ccx) * (b.Cx - ccx) + (b.Cz - ccz) * (b.Cz - ccz)));
            for (int i = 0; i < want.Count && i < BatchColumns * 8; i += BatchColumns)
            {
                if (_api.BackingOff("chunks")) { lock (_lock) _indexed.Remove(key); break; }
                var batch = want.Skip(i).Take(BatchColumns).ToList();
                try
                {
                    double f0 = FreecamStats.NowMs;
                    var payload = await _api.ChunksAsync(it.Dim, batch, ct).ConfigureAwait(false);
                    double a0 = FreecamStats.NowMs;
                    if (FreecamStats.Shared.OnUiThread) Interlocked.Increment(ref FreecamStats.Shared.SyncOnUiThread);
                    Interlocked.Increment(ref FreecamStats.Shared.SyncCalls);
                    _cache.Apply(payload);
                    FreecamStats.Shared.FetchMs.Add(a0 - f0);
                    FreecamStats.Shared.SyncMs.Add(FreecamStats.NowMs - a0);
                }
                catch (HiveApiException ex) { Problem(ex); lock (_lock) _indexed.Remove(key); break; } // retried on the next index
                catch (InvalidDataException ex) { Problem(ex); lock (_lock) _indexed.Remove(key); break; }
            }
            _cache.Trim(it.Dim, ccx, ccz, MaxColumnsPerDim);
        }

        if (OverlayDim() is { } od && (od != _overlaysDim || now - _overlaysAt > TimeSpan.FromSeconds(4)) && !_api.BackingOff("overlays"))
        {
            _overlaysAt = now;
            _overlaysDim = od;
            try
            {
                var ov = await _api.OverlaysAsync(od, ct).ConfigureAwait(false);
                lock (_lock) _overlays[od] = ov;
            }
            catch (HiveApiException ex) { Problem(ex); }
        }

        foreach (var (bot, mode) in MapViews())
        {
            var key = (bot, mode);
            lock (_lock)
            {
                if (_viewed.TryGetValue(key, out var due) && now < due) continue;
                if (_viewed.Count > 64) _viewed.Clear();
                _viewed[key] = now + TimeSpan.FromMilliseconds(1500);
            }
            if (_api.BackingOff("view")) break;
            byte[] png;
            try { png = await _api.ViewPngAsync(bot, mode, ct).ConfigureAwait(false); }
            catch (HiveApiException ex) { Problem(ex); continue; }
            try { ViewPng?.Invoke(bot, mode, png); } // the handler decodes here, on the sync worker, not on the UI thread
            catch (Exception ex) { Problem(ex); }
        }
    }

    public void Dispose() => Stop();
}
