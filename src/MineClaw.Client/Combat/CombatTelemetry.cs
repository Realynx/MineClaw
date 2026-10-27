using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using MineClaw.World;

namespace MineClaw.Client.Combat;

/// <summary>
/// One JSON object per line for every combat event (logs/combat-yyyyMMdd.jsonl): tick, event type, positions, distance,
/// plus event fields (swings, hits, damage, estimated hp, knockback vectors, EdgeGuard interventions). Lines are
/// queued and written by a background task, so the tick thread never touches the disk. <see cref="Sink"/> sees
/// every line synchronously (tests, debugging).
/// </summary>
public sealed class CombatTelemetry : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Channel<string>? _queue;
    private readonly Task? _writer;
    private readonly Func<DateTime> _clock;

    public CombatTelemetry(string? directory, Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory = directory;
        _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        _writer = Task.Run(WriteLoopAsync);
    }

    public string? Directory { get; }

    /// <summary>Synchronous observer of every line (in addition to the file).</summary>
    public Action<string>? Sink { get; set; }

    public long Lines => Interlocked.Read(ref _lines);
    private long _lines;

    public string FilePath(DateTime when) => Path.Combine(Directory ?? ".", $"combat-{when:yyyyMMdd}.jsonl");

    /// <summary>Writes one event. <paramref name="fields"/> are flattened into the object after the common fields.</summary>
    public void Log(long tick, string type, Vec3d? me = null, Vec3d? target = null, IReadOnlyDictionary<string, object?>? fields = null)
    {
        var o = new Dictionary<string, object?>
        {
            ["t"] = _clock().ToString("O"),
            ["tick"] = tick,
            ["ev"] = type,
        };
        if (me is { } m) o["me"] = V(m);
        if (target is { } t) o["tgt"] = V(t);
        if (me is { } a && target is { } b) o["dist"] = Math.Round(a.DistanceTo(b), 2);
        if (fields is not null) foreach (var (k, v) in fields) o[k] = v is Vec3d vv ? V(vv) : v is double d ? Math.Round(d, 3) : v;
        string line = JsonSerializer.Serialize(o, Json);
        Interlocked.Increment(ref _lines);
        Sink?.Invoke(line);
        _queue?.Writer.TryWrite(line);
    }

    private static double[] V(Vec3d v) => [Math.Round(v.X, 2), Math.Round(v.Y, 2), Math.Round(v.Z, 2)];

    private async Task WriteLoopAsync()
    {
        try
        {
            bool created = false;
            await foreach (var line in _queue!.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (!created) { System.IO.Directory.CreateDirectory(Directory!); created = true; }
                try { await File.AppendAllTextAsync(FilePath(_clock()), line + "\n").ConfigureAwait(false); }
                catch (IOException) { /* log file busy: drop the line rather than stall combat */ }
            }
        }
        catch (Exception) { /* telemetry must never take the bot down */ }
    }

    public async ValueTask DisposeAsync()
    {
        _queue?.Writer.TryComplete();
        if (_writer is not null)
            try { await _writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* ignore */ }
    }
}
