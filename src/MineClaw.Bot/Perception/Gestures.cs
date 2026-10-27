using BlockPos = MineClaw.World.BlockPos;
using MineClaw.Bot.Demo;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Perception;

/// <summary>One observed pointing gesture: a player swung their arm (0x12) while looking at a block.</summary>
public sealed record Gesture(string Player, BlockPos Pos, int BlockId, DateTimeOffset At, string Description)
{
    public string Text(DateTimeOffset now)
    {
        int ago = (int)Math.Max(0, (now - At).TotalSeconds);
        return $"{Player} punched {Description} [{Pos.X},{Pos.Y},{Pos.Z}] {ago}s ago";
    }
}

/// <summary>Swing classification shared by the demo recorder and the gesture tracker.</summary>
public static class Gestures
{
    /// <summary><see cref="Agent.Human.DemoAction.Detail"/> of a swing that is a punch (pointing / digging), not an item use.</summary>
    public const string Punched = "punched";

    /// <summary>True when a swing with this held item is (likely) a right-click use: placing a block, a bucket, flint, seeds,
    /// bone meal, a hoe, a sign/door/bed... Empty hand, tools and swords punch.</summary>
    public static bool IsUseItem(int held)
    {
        if (held <= 0) return false;
        if (held < 256) return true; // a block in hand: the swing places it
        return held is Items.Bucket or Items.WaterBucket or Items.LavaBucket or Items.MilkBucket or Items.FlintAndSteel or Items.Seeds
            or Items.PumpkinSeeds or Items.MelonSeeds or Items.WoodenDoor or Items.IronDoor or Items.Redstone or Items.Minecart
            or Items.Boat or Items.Bed or Items.Sign or Items.Dye or Items.SugarCane or Items.Painting or Items.Cake or Items.Repeater
            || (held >= Items.WoodenHoe && held <= Items.GoldenHoe);
    }
}

/// <summary>
/// Gesture perception: a player's arm swing while looking at a block is pointing ("this one" + a punch). Raycasts from the
/// player's eye along their look (entity look packets) with vanilla's punch ray (liquids skipped) and keeps the last few
/// gestures for the turn context ("[pointing] PoofimaFox punched the west cell of the gen ..."), outside demos too.
/// Repeated swings at the same block (digging) collapse into one gesture.
/// </summary>
public sealed class GestureTracker : IDisposable
{
    private readonly IGameClient _client;
    private readonly Func<string, bool> _isBot;
    private readonly TimeProvider _time;
    private readonly Func<WorldState, BlockPos, int, string> _describe;
    private readonly List<Gesture> _recent = new();
    private readonly object _lock = new();

    /// <summary>Gestures this old or newer go in the turn's [pointing] line.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(20);

    public int MaxGestures { get; init; } = 3;

    /// <summary>Raised for each NEW gesture (not for repeated swings at the same block).</summary>
    public event Action<Gesture>? Pointed;

    public GestureTracker(IGameClient client, Func<string, bool>? isBot = null, TimeProvider? time = null,
        Func<WorldState, BlockPos, int, string>? describe = null)
    {
        _client = client;
        _isBot = isBot ?? (_ => false);
        _time = time ?? TimeProvider.System;
        _describe = describe ?? ((w, p, id) => BlockDescriber.Describe(w, p, null));
        _client.EntityAnimated += OnAnimated;
    }

    public void Dispose() => _client.EntityAnimated -= OnAnimated;

    private void OnAnimated(int entityId, int animation)
    {
        if (animation != 1) return;
        try { Observe(entityId); } catch (Exception) { /* perception is best-effort */ }
    }

    /// <summary>Handles one swing of entity <paramref name="entityId"/> (tests call it directly).</summary>
    public Gesture? Observe(int entityId)
    {
        var w = _client.World;
        var e = w.Entities.Get(entityId);
        if (e == null || e.Kind != EntityKind.Player || e.Name is not { Length: > 0 } name) return null;
        if (name.Equals(_client.Username, StringComparison.OrdinalIgnoreCase) || _isBot(name)) return null;
        if (Gestures.IsUseItem(e.HeldItem)) return null; // a right-click use is an action, not pointing
        if (DemonstrationRecorder.LookTarget(w, e.Position, e.Yaw, e.Pitch, solidOnly: true) is not { } t) return null;
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            int i = _recent.FindLastIndex(g => g.Player.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && _recent[i].Pos == t.Pos && now - _recent[i].At < Window)
            {
                _recent[i] = _recent[i] with { At = now }; // still punching the same block
                return null;
            }
        }
        var g = new Gesture(name, t.Pos, t.Id, now, _describe(w, t.Pos, t.Id));
        lock (_lock)
        {
            _recent.Add(g);
            while (_recent.Count > 16) _recent.RemoveAt(0);
        }
        try { Pointed?.Invoke(g); } catch (Exception) { }
        return g;
    }

    /// <summary>The latest gestures (at most <see cref="MaxGestures"/>, within <see cref="Window"/>), newest last.</summary>
    public IReadOnlyList<Gesture> Recent()
    {
        var now = _time.GetUtcNow();
        lock (_lock) return _recent.Where(g => now - g.At <= Window).TakeLast(MaxGestures).ToList();
    }

    /// <summary>"[pointing] PoofimaFox punched ... 3s ago; ..." or null (turn context, uncached tail).</summary>
    public string? Line()
    {
        var list = Recent();
        if (list.Count == 0) return null;
        var now = _time.GetUtcNow();
        return "[pointing] " + string.Join("; ", list.Select(g => g.Text(now)));
    }

    /// <summary>Gestures since <paramref name="since"/> that already left the [pointing] window (for the activity digest).</summary>
    public string Older(DateTimeOffset since)
    {
        var now = _time.GetUtcNow();
        List<Gesture> list;
        lock (_lock) list = _recent.Where(g => g.At >= since && now - g.At > Window).TakeLast(MaxGestures).ToList();
        return string.Join("; ", list.Select(g => $"{g.Player} punched {g.Description}"));
    }
}

/// <summary>
/// Player-style descriptions of a block for gestures and questions: "the west cell of the gen, next to the lava",
/// "the cobblestone under the west gen cell", else "cobblestone 2 E of the house".
/// </summary>
public static class BlockDescriber
{
    private static readonly BlockPos[] FlowIn = [new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1), new(0, 1, 0)];
    private static readonly BlockPos[] Six = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];

    /// <summary>
    /// A cobblestone-generator dig cell by geometry: cobblestone/stone (or the air/flowing lava of a just-dug cell) with lava
    /// flowing in from a side/above and water touching a side/above.
    /// </summary>
    public static bool IsGenCell(WorldState w, BlockPos p)
    {
        if ((uint)p.Y >= WorldState.Height) return false;
        int id = w.GetBlock(p);
        if (!(id is Blocks.Cobblestone or Blocks.Stone or 0 || id == Blocks.FlowingLava)) return false;
        bool lava = false, water = false;
        foreach (var o in FlowIn)
        {
            int n = w.GetBlock(p + o);
            lava |= Blocks.IsLava(n);
            water |= Blocks.IsWater(n);
        }
        return lava && water;
    }

    /// <summary>Generator cells within <paramref name="radius"/> of <paramref name="p"/> (geometry), nearest first.</summary>
    public static List<BlockPos> GenCellsNear(WorldState w, BlockPos p, int radius = 4)
    {
        var list = new List<BlockPos>();
        for (int dx = -radius; dx <= radius; dx++)
        for (int dy = -2; dy <= 2; dy++)
        for (int dz = -radius; dz <= radius; dz++)
        {
            var q = p.Offset(dx, dy, dz);
            if (IsGenCell(w, q) && w.GetBlock(q) is Blocks.Cobblestone or Blocks.Stone) list.Add(q);
        }
        if (IsGenCell(w, p) && !list.Contains(p)) list.Add(p);
        return list.OrderBy(q => q.DistanceSquaredTo(p)).ToList();
    }

    /// <summary>For questions: a relational name ("the west cell of the gen", "the cobblestone 2 E of the house"), or null
    /// when there's nothing better than the bare block name (the coordinates then stay).</summary>
    public static string? Landmark(WorldState w, BlockPos p, Func<int, int, int, string?>? landmark)
    {
        int id = w.GetBlock(p);
        var d = Describe(w, p, landmark);
        if (d == Names.Block(id) || d == "the empty spot") return null;
        return d.StartsWith("the ", StringComparison.Ordinal) ? d : "the " + d;
    }

    public static string Describe(WorldState w, BlockPos p, Func<int, int, int, string?>? landmark)
    {
        int id = w.GetBlock(p);
        string name = id == 0 ? "the empty spot" : Names.Block(id);
        if (GenCellName(w, p) is { } cell) return cell;
        foreach (var o in Six)
            if (GenCellName(w, p + o) is { } near)
            {
                string rel = o.Y > 0 ? "under" : o.Y < 0 ? "on top of" : "next to";
                return $"the {name} {rel} {near}";
            }
        var lm = landmark?.Invoke(p.X, p.Y, p.Z);
        return lm != null ? $"{name} {lm}" : name;
    }

    /// <summary>"the west cell of the gen, next to the lava" for a generator cell, else null.</summary>
    public static string? GenCellName(WorldState w, BlockPos p)
    {
        if (!IsGenCell(w, p)) return null;
        var cells = GenCellsNear(w, p, 3);
        bool lavaNext = Six.Any(o => Blocks.IsLava(w.GetBlock(p + o)));
        string tail = lavaNext ? ", right next to the lava" : "";
        if (cells.Count < 2) return "the gen's dig cell" + tail;
        double cx = cells.Average(c => c.X), cz = cells.Average(c => c.Z);
        double dx = p.X - cx, dz = p.Z - cz;
        string side = Math.Abs(dx) >= Math.Abs(dz) && Math.Abs(dx) > 0.01 ? (dx < 0 ? "west" : "east")
            : Math.Abs(dz) > 0.01 ? (dz < 0 ? "north" : "south") : "middle";
        return $"the {side} cell of the gen" + tail;
    }
}
