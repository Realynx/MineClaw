using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Rag;
using MineClaw.World;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Bot.WorldIndex;

/// <summary>
/// The bot's mechanism memory: runs the <see cref="MechanismLearner"/> on the client's world (block changes + 0x36
/// piston actions), feeds it players' demonstrations ("watch, this button opens the wall") and answers to "what opens
/// this?", and persists every link as a <c>mechanism</c> observation (key = trigger position, per dimension; the link
/// itself in the fields JSON), loaded back into <see cref="WorldState.Mechanisms"/> on start. Zero LLM tokens.
/// </summary>
public sealed partial class MechanismWatcher : IDisposable
{
    public const string Kind = "mechanism";

    private readonly WorldState _world;
    private readonly ObservationStore? _store;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly List<Action> _unsub = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _loading;

    public MechanismWatcher(WorldState world, ObservationStore? store, TimeProvider? time = null, ILogger? logger = null,
        string? selfName = null, MechanismLearnerOptions? options = null)
    {
        _world = world;
        _store = store;
        _time = time ?? TimeProvider.System;
        _log = logger ?? NullLogger.Instance;
        Learner = new MechanismLearner(world, world.Mechanisms, _time, options) { SelfName = selfName };
        Learner.Observed += OnObserved;
        Action<MechanismLink> changed = Save;
        Action<MechanismLink> removed = Forget;
        Graph.Changed += changed;
        Graph.Removed += removed;
        _unsub.Add(() => { Graph.Changed -= changed; Graph.Removed -= removed; });
    }

    public MechanismLearner Learner { get; }
    public MechanismGraph Graph => _world.Mechanisms;

    /// <summary>Learned something (new link, reinforced, weakened).</summary>
    public event Action<MechanismObservation>? Learned;

    /// <summary>Subscribes to block changes and (optionally) 0x36 block actions.</summary>
    public MechanismWatcher Attach(IPlayerEventSource? events = null)
    {
        Learner.Attach();
        if (events != null)
        {
            Action<BlockPos, int, int> onAction = Learner.OnBlockAction;
            events.BlockAction += onAction;
            _unsub.Add(() => events.BlockAction -= onAction);
        }
        return this;
    }

    /// <summary>Subscribes to the game client's block changes and 0x36 block actions (pistons).</summary>
    public MechanismWatcher Attach(MineClaw.Bot.Game.IGameClient client)
    {
        Learner.Attach();
        Action<BlockPos, int, int> onAction = Learner.OnBlockAction;
        client.BlockAction += onAction;
        _unsub.Add(() => client.BlockAction -= onAction);
        return this;
    }

    /// <summary>Background pump (window closing, pulse watches).</summary>
    public void Start(TimeSpan? interval = null)
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var every = interval ?? TimeSpan.FromMilliseconds(100);
        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(every, _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                try { Learner.Pump(); }
                catch (Exception ex) { _log.LogDebug(ex, "mechanism pump failed"); }
            }
        }, ct);
    }

    public void Pump() => Learner.Pump();

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* cancelled */ }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        Learner.Dispose();
        foreach (var u in _unsub) u();
        _unsub.Clear();
    }

    // ------------------------------------------------------------------ players

    [GeneratedRegex(@"\b(watch|look|see|lemme show|let me show|like this|check (this|it) out)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DemoWords();

    [GeneratedRegex(@"\b(button|lever|plate|pressure plate|switch)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TriggerWords();

    [GeneratedRegex(@"(-?\d+)[ ,]+(-?\d+)[ ,]+(-?\d+)")]
    private static partial Regex Coords();

    /// <summary>
    /// Chat from a player: "watch, this button opens the wall" marks their next activation a demonstration; an answer to
    /// the pending "how do I open this?" (<see cref="MechanismGraph.Question"/>) naming a button/lever/plate (by
    /// coordinates, or the nearest one of that kind to the door) teaches the link. Returns the taught link, if any.
    /// </summary>
    public MechanismLink? NoteChat(string? player, string text)
    {
        if (string.IsNullOrWhiteSpace(player) || string.IsNullOrWhiteSpace(text)) return null;
        bool trigger = TriggerWords().IsMatch(text);
        if (DemoWords().IsMatch(text) || (trigger && Regex.IsMatch(text, @"\b(opens?|works?|controls?)\b", RegexOptions.IgnoreCase)))
            Learner.NoteDemonstration(player);
        if (!trigger || Graph.Question is not { } q || q.Dim != _world.Dimension || _time.GetUtcNow() - q.At > TimeSpan.FromMinutes(5)) return null;
        var at = TriggerNamed(text, q.Subject);
        if (at is null) return null;
        var link = Graph.Teach(_world, at.Value, [q.Subject], _world.Dimension, player, _time.GetUtcNow());
        Graph.Question = null;
        _log.LogInformation("mechanism taught by {Player}: {Link}", player, link.Describe());
        return link;
    }

    private BlockPos? TriggerNamed(string text, BlockPos subject)
    {
        var m = Coords().Match(text);
        if (m.Success)
        {
            var p = new BlockPos(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            if (Mechanisms.KindOf(_world.GetBlock(p)) is not null) return p;
        }
        int? want = text.Contains("lever", StringComparison.OrdinalIgnoreCase) ? Openables.Lever
            : text.Contains("plate", StringComparison.OrdinalIgnoreCase) ? null : Openables.StoneButton;
        bool plate = text.Contains("plate", StringComparison.OrdinalIgnoreCase);
        BlockPos? best = null;
        double bestD = double.MaxValue;
        for (int dx = -8; dx <= 8; dx++)
        for (int dy = -3; dy <= 3; dy++)
        for (int dz = -8; dz <= 8; dz++)
        {
            var p = subject.Offset(dx, dy, dz);
            int id = _world.GetBlock(p);
            if (plate ? !Openables.IsPlate(id) : id != want) continue;
            double d = dx * dx + dy * dy + dz * dz;
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    private void OnObserved(MechanismObservation o)
    {
        if (o.IsNew || o.Demonstrated)
            _log.LogInformation("mechanism {What}{By}: {Link}", o.IsNew ? "learned" : "demonstrated", o.By is null ? "" : $" (by {o.By})", o.Link.Describe());
        else if (o.Miss) _log.LogDebug("mechanism miss: {Link}", o.Link.Describe());
        Learned?.Invoke(o);
    }

    // ------------------------------------------------------------------ persistence

    /// <summary>Loads persisted links (all dimensions) into the graph. Returns how many.</summary>
    public int Load()
    {
        if (_store is null) return 0;
        var links = new List<MechanismLink>();
        foreach (var row in _store.OfKind(Kind, WorldDim.Any, 1000))
            if (row.FieldsJson is { } json && Parse(json, row.Dimension) is { } l) links.Add(l);
        _loading = true;
        try { Graph.Load(links); }
        finally { _loading = false; }
        return links.Count;
    }

    private void Save(MechanismLink link)
    {
        if (_store is null || _loading) return;
        try
        {
            var all = link.Effects.Select(e => e.Pos).Append(link.Trigger).ToList();
            _store.Upsert(new Observation(Kind, link.Key, link.Describe())
            {
                Bounds = new Box(all.Min(p => p.X), all.Min(p => p.Y), all.Min(p => p.Z), all.Max(p => p.X) + 1, all.Max(p => p.Y) + 1, all.Max(p => p.Z) + 1),
                FieldsJson = Serialize(link),
                Source = link.Taught ? ObservationSource.Taught : ObservationSource.Auto,
                Confidence = link.Confidence,
                Dimension = link.Dimension,
                SeenAt = link.LastSeen == default ? null : link.LastSeen,
            });
        }
        catch (Exception ex) { _log.LogWarning(ex, "saving mechanism {Key} failed", link.Key); }
    }

    private void Forget(MechanismLink link)
    {
        if (_store is null) return;
        try { _store.Remove(Kind, link.Key, tombstone: false, by: ObservationSource.Taught, dimension: link.Dimension); }
        catch (Exception ex) { _log.LogWarning(ex, "forgetting mechanism {Key} failed", link.Key); }
    }

    private sealed record EffectDto(int[] P, int[] Off, int[] On, int S);
    private sealed record LinkDto(int[] T, string K, EffectDto[] E, int? Pulse, int Lag, double C, int N, int M, bool Taught, string? By, bool D, long Seen);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Serialize(MechanismLink l) => JsonSerializer.Serialize(new LinkDto(
        [l.Trigger.X, l.Trigger.Y, l.Trigger.Z], l.Kind.ToString(),
        l.Effects.Select(e => new EffectDto([e.Pos.X, e.Pos.Y, e.Pos.Z], [e.Off.Id, e.Off.Meta], [e.On.Id, e.On.Meta], e.Seen)).ToArray(),
        l.PulseTicks, l.LagTicks, Math.Round(l.Confidence, 3), l.Observations, l.Misses, l.Taught, l.By, l.Dangerous,
        l.LastSeen.ToUnixTimeMilliseconds()), Json);

    public static MechanismLink? Parse(string json, int dimension)
    {
        try
        {
            var d = JsonSerializer.Deserialize<LinkDto>(json, Json);
            if (d is null || d.T.Length != 3 || !Enum.TryParse<TriggerKind>(d.K, out var kind)) return null;
            var effects = d.E.Where(e => e.P.Length == 3 && e.Off.Length == 2 && e.On.Length == 2)
                .Select(e => new MechanismEffect(new BlockPos(e.P[0], e.P[1], e.P[2]), new BlockState(e.Off[0], e.Off[1]), new BlockState(e.On[0], e.On[1]), e.S))
                .ToArray();
            return new MechanismLink(new BlockPos(d.T[0], d.T[1], d.T[2]), kind, effects)
            {
                Dimension = dimension, PulseTicks = d.Pulse, LagTicks = d.Lag, Confidence = d.C, Observations = d.N, Misses = d.M,
                Taught = d.Taught, By = d.By, Dangerous = d.D, LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(d.Seen),
            };
        }
        catch (JsonException) { return null; }
    }
}
