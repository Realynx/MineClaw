using System.Globalization;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Rag;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.Protocol.Recording;
using MineClaw.World;

namespace MineClaw.Replay;

/// <summary>Recording time as the clock: every timestamp the pipeline takes is the frame's time, so replays are exact.</summary>
public sealed class ReplayClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Start { get; } = start;
    public long OffsetMs { get; private set; }
    public void Set(long offsetMs) => OffsetMs = Math.Max(OffsetMs, offsetMs);
    public override DateTimeOffset GetUtcNow() => Start + TimeSpan.FromMilliseconds(OffsetMs);
    public override long GetTimestamp() => OffsetMs * (Frequency / 1000);
    public override long TimestampFrequency => Frequency;
    private const long Frequency = 1_000_000;
}

/// <summary>One detected thing on the replay timeline.</summary>
/// <param name="Source">activity (attributed player event), wake (would have woken the brain), self (the bot's own
/// outbound action), index (a world-index observation written).</param>
public sealed record TimelineEvent(long OffsetMs, string Source, string? Player, string Kind, string Text)
{
    public double Confidence { get; init; } = 1;
    public int? BlockId { get; init; }
    public int? ItemId { get; init; }
    public int Count { get; init; } = 1;
    public BlockPos? Pos { get; init; }
    public string? Priority { get; init; }

    public string Format() =>
        $"{ReplayResult.Clock(OffsetMs)} {Source,-8} {Player ?? "-",-12} {Kind,-12} {Text}"
        + (Source == "activity" ? $" ({Confidence:0.00})" : "") + (Priority is { } p ? $" [{p}]" : "");
}

public sealed class ReplayOptions
{
    /// <summary>The bot's login (default: from the recording's meta or its own 0x01).</summary>
    public string? SelfName { get; init; }
    /// <summary>Indexer/tracker pass every N ms of recording time (live: 250 ms activity, 1000 ms index).</summary>
    public int PassMs { get; init; } = 250;
    /// <summary>Include index observation writes in the timeline (noisy; off by default).</summary>
    public bool IncludeIndex { get; init; }
    /// <summary>0 = as fast as possible (fast-forward); 1 = real time; 10 = ten times faster.</summary>
    public double Speed { get; init; }
}

public sealed class ReplayResult
{
    public required DateTimeOffset Start { get; init; }
    public required long DurationMs { get; init; }
    public required int Frames { get; init; }
    public required bool Truncated { get; init; }
    public required string? Self { get; init; }
    public required IReadOnlyList<TimelineEvent> Timeline { get; init; }
    public required IReadOnlyDictionary<string, int> IndexCensus { get; init; }
    public long SelfEditsSkipped { get; init; }

    public static string Clock(long ms) => $"+{ms / 60000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}";

    public string RenderTimeline()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"replay {Start:u}  {Clock(DurationMs)}  {Frames} frames{(Truncated ? " (truncated)" : "")}  self={Self}");
        foreach (var e in Timeline) sb.AppendLine(e.Format());
        return sb.ToString();
    }

    /// <summary>Stable JSON for diffing two runs (no wall-clock values, sorted keys).</summary>
    public string ToJson()
    {
        var counts = Timeline.Where(e => e.Source is "activity" or "self")
            .GroupBy(e => (e.Source == "self" ? Self ?? "self" : e.Player ?? "?"))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.GroupBy(e => e.Kind).OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Sum(e => e.Count)));
        var doc = new
        {
            start = Start.ToString("o", CultureInfo.InvariantCulture),
            durationMs = DurationMs,
            frames = Frames,
            truncated = Truncated,
            self = Self,
            selfEditsSkipped = SelfEditsSkipped,
            counts,
            wakes = Timeline.Count(e => e.Source == "wake"),
            index = IndexCensus.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
            events = Timeline.Select(e => new
            {
                t = e.OffsetMs, src = e.Source, player = e.Player, kind = e.Kind, text = e.Text, conf = Math.Round(e.Confidence, 2),
                block = e.BlockId is { } b ? Blocks.Get(b).Name : null, item = e.ItemId, count = e.Count,
                pos = e.Pos is { } p ? new[] { p.X, p.Y, p.Z } : null, priority = e.Priority,
            }),
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }
}

/// <summary>
/// Offline replay: a recording → fresh <see cref="WorldState"/> + <see cref="EntityTracker"/> + <see cref="WorldIndexer"/> +
/// <see cref="PlayerActivityTracker"/> (with its <see cref="DangerMonitor"/> death/danger rules), on the recording's clock.
/// No network, no LLM, no SQLite. Deterministic: the same recording always yields the same timeline.
/// </summary>
public static class ReplayHarness
{
    public static ReplayResult Run(string path, ReplayOptions? o = null) => Run(PacketRecording.Load(path), o);

    public static ReplayResult Run(PacketRecording rec, ReplayOptions? o = null)
    {
        o ??= new ReplayOptions();
        var clock = new ReplayClock(rec.Start);
        var world = new WorldState();
        world.SelfEdits.Clock = () => clock.OffsetMs;
        var events = new ManualPlayerEvents();
        var feed = new ManualIndexFeed();
        var sink = new MemorySink();
        string? self = o.SelfName ?? rec.MetaValue("user");
        var applier = new WorldApplier(world, events, feed) { SelfName = self };
        var timeline = new List<TimelineEvent>();

        var indexOptions = new WorldIndexOptions { MaxColumnsPerPass = 100_000, ScanBudgetMs = double.MaxValue, ClusterMinWriteSeconds = 0, ContainerMinWriteSeconds = 0 };
        // The self name is only known once the login is seen; create the pipeline lazily at the first server packet after it.
        WorldIndexer? indexer = null;
        PlayerActivityTracker? tracker = null;
        void Ensure()
        {
            if (indexer != null) return;
            self ??= applier.SelfName ?? "maid_chan";
            applier.SelfName = self;
            indexer = new WorldIndexer(world, sink, feed, indexOptions, self, clock).Attach();
            tracker = new PlayerActivityTracker(world, events, null, indexer, new ActivityOptions(), self, time: clock).Attach();
            tracker.EventRecorded += e => timeline.Add(new TimelineEvent(clock.OffsetMs, "activity", e.Player, e.Kind, e.Text)
            {
                Confidence = e.Confidence, BlockId = e.BlockId, ItemId = e.ItemId, Count = e.Count, Pos = e.Pos,
            });
            tracker.Wake += e => timeline.Add(new TimelineEvent(clock.OffsetMs, "wake", e.Player, e.Kind.ToString().ToLowerInvariant(), e.Text)
            {
                Priority = e.Priority.ToString().ToLowerInvariant(),
            });
            if (o.IncludeIndex) sink.Written += obs => timeline.Add(new TimelineEvent(clock.OffsetMs, "index", null, obs.Kind, obs.Text));
        }
        applier.Self += (kind, what, pos, id) =>
        {
            if (kind == "said") { timeline.Add(new TimelineEvent(clock.OffsetMs, "self", self, "said", what)); return; }
            timeline.Add(new TimelineEvent(clock.OffsetMs, "self", self, kind, pos is { } p ? $"{what} at {p.X},{p.Y},{p.Z}" : what)
                { BlockId = kind is "dug" or "placed" ? id : null, Pos = pos });
        };

        long nextPass = o.PassMs;
        var wall = System.Diagnostics.Stopwatch.StartNew();
        foreach (var f in rec.Frames)
        {
            while (indexer != null && f.OffsetMs >= nextPass)
            {
                clock.Set(nextPass);
                indexer.Pump();
                tracker!.Pump();
                nextPass += o.PassMs;
            }
            clock.Set(f.OffsetMs);
            Pace(f.OffsetMs);
            if (f.Direction == MineClaw.Protocol.PacketDirection.ClientBound && (applier.SelfName != null || self != null)) Ensure();
            applier.Apply(f);
        }
        Ensure();
        // Let pending sessions/danger windows settle a little after the last frame.
        long end = rec.Frames.Count == 0 ? 0 : rec.Frames[^1].OffsetMs;
        Settle(end);
        var census = indexer!.Census();

        // Replays at --speed: sleeps until the frame is due on the wall clock.
        void Pace(long offsetMs)
        {
            if (!(o.Speed > 0)) return;
            var due = TimeSpan.FromMilliseconds(offsetMs / o.Speed) - wall.Elapsed;
            if (due > TimeSpan.Zero) Thread.Sleep(due);
        }

        void Settle(long last)
        {
            for (long t = Math.Max(nextPass, last); t <= last + 5000; t += o.PassMs)
            {
                clock.Set(t);
                indexer!.Pump();
                tracker!.Pump();
            }
        }
        long skipped = tracker!.SelfEdits;
        tracker.Dispose();
        indexer.Dispose();
        var ordered = timeline.Select((e, i) => (e, i)).OrderBy(x => x.e.OffsetMs).ThenBy(x => x.i).Select(x => x.e).ToList();
        return new ReplayResult
        {
            Start = rec.Start, DurationMs = end, Frames = rec.Frames.Count, Truncated = rec.Truncated, Self = self, Timeline = ordered,
            IndexCensus = census, SelfEditsSkipped = skipped,
        };
    }

    /// <summary>In-memory observation sink (the replay never touches SQLite).</summary>
    private sealed class MemorySink : IObservationSink
    {
        private readonly Dictionary<(string, string), Observation> _rows = new();
        public event Action<Observation>? Written;
        public int UpsertMany(IReadOnlyList<Observation> batch)
        {
            foreach (var b in batch) { _rows[(b.Kind, b.Key)] = b; Written?.Invoke(b); }
            return batch.Count;
        }
        public bool Remove(string kind, string key) => _rows.Remove((kind, key));
        public IReadOnlyList<ObservationRow> Near(string? kind, double x, double y, double z, double radius, int limit) => [];
    }
}
