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
        try { Task.WaitAll([_fast ?? Task.CompletedTask, _world ?? Task.CompletedTask], TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        cts.Dispose();
        SetState(LinkState.Disconnected, "not connected");
    }

    private void SetState(LinkState s, string text)
    {
        if (State == s && StateText == text) return;
        State = s;
        StateText = text;
        StateChanged?.Invoke();
    }

    private async Task FastLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var wait = TimeSpan.FromMilliseconds(200);
            try
            {
                var e = await _api.EntitiesAsync(ct).ConfigureAwait(false);
                Entities = e;
                EntitiesAt = DateTime.UtcNow;
                SetState(LinkState.Connected, "connected");
                EntitiesChanged?.Invoke(e);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (HiveApiException ex) when (ex.Status is 401 or 403) { SetState(LinkState.Unauthorized, ex.Message); wait = TimeSpan.FromSeconds(3); }
            catch (HiveApiException ex) when (ex.Status is 429 or 503) { wait = ex.RetryAfter ?? TimeSpan.FromSeconds(1); }
            catch (HiveApiException ex) when (ex.Status == 0) { SetState(LinkState.Disconnected, "no answer: " + ex.Message); wait = TimeSpan.FromSeconds(2); }
            catch (Exception ex) { SetState(LinkState.Error, ex.Message); wait = TimeSpan.FromSeconds(2); }
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task WorldLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (State == LinkState.Connected)
            {
                try { await WorldPass(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception) { /* next pass; the fast loop reports the link */ }
            }
            try { await Task.Delay(150, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
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
            if (_api.BackingOff("index")) break;
            IndexDto index;
            try { index = await _api.IndexAsync(it.Dim, it.X, it.Z, r, ct).ConfigureAwait(false); }
            catch (HiveApiException) { lock (_lock) _indexed.Remove(key); continue; }
            int cx0 = (it.X - r) >> 4, cx1 = (it.X + r) >> 4, cz0 = (it.Z - r) >> 4, cz1 = (it.Z + r) >> 4;
            var want = _cache.Reconcile(it.Dim, cx0, cz0, cx1, cz1, index.Columns);
            if (want.Count == 0) continue;
            int ccx = it.X >> 4, ccz = it.Z >> 4;
            want.Sort((a, b) => ((a.Cx - ccx) * (a.Cx - ccx) + (a.Cz - ccz) * (a.Cz - ccz)).CompareTo((b.Cx - ccx) * (b.Cx - ccx) + (b.Cz - ccz) * (b.Cz - ccz)));
            for (int i = 0; i < want.Count && i < BatchColumns * 8; i += BatchColumns)
            {
                var batch = want.Skip(i).Take(BatchColumns).ToList();
                try
                {
                    var payload = await _api.ChunksAsync(it.Dim, batch, ct).ConfigureAwait(false);
                    _cache.Apply(payload);
                }
                catch (HiveApiException) { lock (_lock) _indexed.Remove(key); break; } // retried on the next index
                catch (InvalidDataException) { lock (_lock) _indexed.Remove(key); break; }
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
            catch (HiveApiException) { }
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
            try { ViewPng?.Invoke(bot, mode, await _api.ViewPngAsync(bot, mode, ct).ConfigureAwait(false)); }
            catch (HiveApiException) { }
        }
    }

    public void Dispose() => Stop();
}
