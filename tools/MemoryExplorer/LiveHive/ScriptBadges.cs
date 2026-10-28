namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>
/// The tiles' "script" badge: polls <c>/api/bodies/scripts</c> (who runs a script; clones from their telemetry, no IPC) every
/// <see cref="Interval"/> while the Live Hive tab is shown, through the same <see cref="HiveApi"/> (timeouts, back-off).
/// </summary>
public sealed class ScriptBadgePoller(HiveApi api, Action<LiveScriptsDto> onRows) : IDisposable
{
    private CancellationTokenSource? _cts;

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1.5);
    /// <summary>An older server without the script routes answers 404: the poller then retries rarely (HiveApi's 404 back-off).</summary>
    public string? LastError { get; private set; }

    public void Start()
    {
        if (_cts != null) return;
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!api.BackingOff("scripts"))
                {
                    var rows = await api.GetJsonAsync<LiveScriptsDto>("scripts", "api/bodies/scripts", ct).ConfigureAwait(false);
                    LastError = null;
                    onRows(rows);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { LastError = ex.Message; }
            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Sets each tile's badge from the rows (UI thread): matched by login or short name ("maid_chan_67" / "67", main).</summary>
    public static void Apply(IEnumerable<BotTileViewModel> tiles, IReadOnlyList<LiveScriptRowDto> rows)
    {
        foreach (var t in tiles)
        {
            var row = rows.FirstOrDefault(r => r.Body.Equals(t.Name, StringComparison.OrdinalIgnoreCase) || r.Short.Equals(t.Key, StringComparison.OrdinalIgnoreCase)
                || (t.Bot?.Main == true && r.Short.Equals("main", StringComparison.OrdinalIgnoreCase)));
            t.SetScript(row?.Script, row?.Paused == true, row?.Line ?? 0);
        }
    }

    public void Dispose() => Stop();
}
