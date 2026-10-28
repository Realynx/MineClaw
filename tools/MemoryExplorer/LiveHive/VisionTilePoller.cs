using Avalonia.Media.Imaging;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>
/// The tiles in "AI's last sheet" mode: every <see cref="Interval"/> while the Live Hive tab is shown, asks each such body for
/// sheets newer than the one on its tile (<c>/api/bodies/{name}/vision?since=</c>: usually an empty list), and fetches the PNG
/// of a new one (decoded here, off the UI thread). Through the same <see cref="IVisionSource"/> as the panel.
/// </summary>
public sealed class VisionTilePoller(IVisionSource source, Func<IReadOnlyList<(string Key, long Since)>> wanted, Action<string, VisionSheetDto, Bitmap?> onSheet)
    : IDisposable
{
    private CancellationTokenSource? _cts;

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2.5);
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
            foreach (var (key, since) in wanted())
            {
                try
                {
                    var list = await source.ListAsync(key, since, ct).ConfigureAwait(false);
                    if (list.Sheets.Where(s => s.Id > since).MaxBy(s => s.Id) is { } newest)
                    {
                        var png = await source.PngAsync(key, newest.Id, ct).ConfigureAwait(false);
                        Bitmap? bmp = null;
                        try { using var ms = new MemoryStream(png); bmp = new Bitmap(ms); } catch (Exception) { }
                        onSheet(key, newest, bmp);
                    }
                    LastError = null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { LastError = ex.Message; }
            }
            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose() => Stop();
}
