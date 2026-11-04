using Microsoft.Extensions.Logging;

namespace MineClaw.Hive.Story;

/// <summary>
/// Drops the pawns can't collect, kept by place (docs/web/story-mode.md "Hauling": "A collect that found nothing"). Live 11-04 story-1
/// (release 20261104-181056): "collect … failed: no reachable drops nearby" ×63 in 6 minutes all over the farm, each in about 0.2 s
/// (the pawn got there and its collect saw nothing it could take). The rest after a miss was keyed by the drop's entity id: a drop that
/// came back with a new id (a new egg or seed at the same spot, a merged stack, a re-drop) was a new target, a batch's other drops never
/// rested, and every drop took three pawns' misses before it rested for the colony, then rested only 30 s, 1 min, 10 min.
/// <list type="bullet">
/// <item>A collect that found nothing marks the spot of each of its drops still in view (any drop within a block of it counts there).
/// <see cref="DropSpotMisses"/> misses there (one when the pawn's own safety checks wrote them off: "; 1 drop(s) left near the void")
/// and the spot rests for everyone, whatever drops lie there: 5 min, then 15, then 30; after a rest one more miss rests it again.</item>
/// <item>Before a drop is offered at all: somewhere to stand by it (a cell beside it, at, over or under its level, with ground under it,
/// room for the body and the head out of water, at a height whose pickup box touches it). A drop in deep water, in a wall or over the
/// void has none and is never offered.</item>
/// <item>The idle audit says it: "waiting: drops at 6,64,6 unreachable" (the standing task), "no reachable target: haul …: drops there
/// unreachable" (a haul mark).</item>
/// </list>
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>Misses at one spot (by any pawns, of any drops) before it rests for everyone.</summary>
    public const int DropSpotMisses = 3;
    /// <summary>A spot nobody missed at for this long (and not resting) is forgotten.</summary>
    public static readonly TimeSpan DropSpotForget = TimeSpan.FromMinutes(30);

    /// <summary>The n-th rest of a spot: 5 min, 15 min, then 30 min.</summary>
    internal static TimeSpan DropSpotRest(int n) => n switch { <= 1 => TimeSpan.FromMinutes(5), 2 => TimeSpan.FromMinutes(15), _ => TimeSpan.FromMinutes(30) };

    private sealed class DropSpot
    {
        public P3 At;
        public int Misses, Rests;
        public DateTimeOffset Last, Until;
        public string Text = "";
    }

    private readonly List<DropSpot> _dropSpots = new();
    private readonly Dictionary<(P3 Cell, bool High), (bool Ok, DateTimeOffset At)> _standBy = new();

    /// <summary>A drop collect that found nothing: each of its drops still in view counts a miss at its spot.</summary>
    private void NoteDropMiss(PawnState p, ActiveJob j, string why)
    {
        if (!NoDropReached(j, why) || World is not { } w) return;
        _dropSpots.RemoveAll(s => s.Until <= Now && Now - s.Last > DropSpotForget);
        var ents = w.Entities();
        bool writtenOff = why.Contains(';'); // its own safety checks wrote them off: no use trying again soon
        if (DropOf(ents, j.Lease) is { } first) MissAt(p, first.Block, writtenOff);
        foreach (var more in j.MoreLeases)
            if (DropOf(ents, more) is { } e) MissAt(p, e.Block, writtenOff);
    }

    private static StoryEntity? DropOf(IReadOnlyList<StoryEntity> ents, string? lease)
    {
        if (lease == null || !lease.StartsWith("e:", StringComparison.Ordinal) || !int.TryParse(lease.AsSpan(2), out var id)) return null;
        foreach (var e in ents) if (e.Id == id) return e;
        return null;
    }

    private void MissAt(PawnState p, P3 at, bool writtenOff)
    {
        var spot = SpotNear(at, resting: false) ?? AddSpot(at);
        spot.Misses++;
        spot.Last = Now;
        if (spot.Until > Now || !writtenOff && spot.Misses < (spot.Rests == 0 ? DropSpotMisses : 1)) return;
        spot.Rests++;
        spot.Misses = 0;
        spot.Until = Now + DropSpotRest(spot.Rests);
        _log.LogInformation("story {Colony}: drops at {At} unreachable ({Pawn} the last to miss them): left for {Min} min", Id, spot.At, p.Name,
            (int)DropSpotRest(spot.Rests).TotalMinutes);
    }

    private DropSpot AddSpot(P3 at)
    {
        var s = new DropSpot { At = at, Text = $"drops at {at} unreachable" };
        if (_dropSpots.Count >= 256) _dropSpots.RemoveAt(0);
        _dropSpots.Add(s);
        return s;
    }

    /// <summary>The spot within a block of <paramref name="at"/> (only a resting one with <paramref name="resting"/>), or null.</summary>
    private DropSpot? SpotNear(P3 at, bool resting)
    {
        foreach (var s in _dropSpots)
            if ((!resting || s.Until > Now) && Math.Abs(s.At.X - at.X) <= 1 && Math.Abs(s.At.Y - at.Y) <= 1 && Math.Abs(s.At.Z - at.Z) <= 1) return s;
        return null;
    }

    /// <summary>
    /// Why a drop is left alone now: its spot rests after misses, or there is nowhere to stand by it (null = it may be offered).
    /// </summary>
    private string? DropUnreachable(IStoryWorld w, StoryEntity e)
    {
        if (_dropSpots.Count > 0 && SpotNear(e.Block, resting: true) is { } spot) return spot.Text;
        return StandBy(w, e) ? null : $"drops at {e.Block} unreachable";
    }

    /// <summary>Somewhere to pick the drop up from (cached a few seconds per cell): a cell beside it, at, or a level under or over it (over
    /// only when it lies high in its cell) whose pickup box touches it.</summary>
    private bool StandBy(IStoryWorld w, StoryEntity e)
    {
        var c = e.Block;
        if (!w.Loaded(c.X, c.Z)) return true; // can't tell: the pawn's collect will
        bool high = e.Y - c.Y > 0.875; // a pickup box from the level over it reaches it (mc-dev: the player box grown 0.5 down in 1.12.2)
        if (_standBy.TryGetValue((c, high), out var hit) && Now - hit.At < TimeSpan.FromSeconds(5)) return hit.Ok;
        if (_standBy.Count > 1024) _standBy.Clear();
        bool ok = false;
        for (int dy = -1; dy <= (high ? 1 : 0) && !ok; dy++)
            ok = StandRing(w, c.X, c.Y + dy, c.Z);
        _standBy[(c, high)] = (ok, Now);
        return ok;
    }

    private static bool StandRing(IStoryWorld w, int x, int y, int z)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                if (PickupStand(w, x + dx, y, z + dz)) return true;
        return false;
    }

    /// <summary>Ground under it, room for the body, the head out of liquid (wading is fine: the client's pickup spots allow it).</summary>
    private static bool PickupStand(IStoryWorld w, int x, int y, int z)
    {
        int feet = w.Block(x, y, z).Id, head = w.Block(x, y + 1, z).Id, floor = w.Block(x, y - 1, z).Id;
        return OpenCell(feet) && OpenCell(head) && !StoryBlocks.Liquids.Contains(head) && !OpenCell(floor) && !StoryBlocks.Liquids.Contains(floor);
    }
}
