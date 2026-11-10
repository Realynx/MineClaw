namespace MineClaw.Hive.Story;

/// <summary>
/// Full storage and the farm (docs/web/story-mode.md "Farming", "Storage full"). Live 11-06 (story-1): a 1,565-cell wheat field with a
/// "forever" bill filled every chest (wheat ×1,536–2,304 and seeds ×768–1,920 per chest, 25 chests); pawns walked their bags of
/// wheat from full chest to full chest ("no known chest has room"), their client placed overflow chests the colony never knew of, and
/// the owner couldn't tell why. Now a ripe crop whose produce no chest has room for is left standing (a ripe crop keeps in the field)
/// while tilling and sowing go on, the zone's status says so, and one alert (every 10 minutes at most) says storage is full and
/// which chests near home aren't storage yet.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>When a zone last left a ripe crop standing for want of room (its status says so for a minute).</summary>
    private readonly Dictionary<string, DateTimeOffset> _harvestHeld = new(StringComparer.Ordinal);
    private DateTimeOffset _storageFullSaid = DateTimeOffset.MinValue;

    /// <summary>The ripe crop stays: the colony has storage and none of it has room for what the harvest yields.</summary>
    private bool HarvestHeld(ZoneState z, StoryPlant crop)
    {
        if (Storages.Count == 0) return false; // no storage at all: pawns keep it or drop it at the drop spot, as before
        var (id, meta) = StoryPlants.Produce(crop);
        if (HaulDest(id, Math.Max(0, meta), z.Area.Center) != null) return false;
        _harvestHeld[z.Id] = Now;
        StorageFullAlert(z, crop, id, Math.Max(0, meta));
        return true;
    }

    /// <summary>The zone left ripe crops standing in the last minute.</summary>
    private bool HarvestHeldNow(ZoneState z) => _harvestHeld.TryGetValue(z.Id, out var at) && Now - at < TimeSpan.FromMinutes(1);

    private void StorageFullAlert(ZoneState z, StoryPlant crop, int id, int meta)
    {
        if (Now - _storageFullSaid < TimeSpan.FromMinutes(10)) return;
        SayStorageFull(id, meta, z.Area.Center,
            $"Growing zone {ZoneName(z)} leaves its ripe {crop.Label.ToLowerInvariant()} standing until one has room; tilling and sowing go on.");
    }

    /// <summary>The storage-full alert itself (its callers keep it to one every 10 minutes): the item, how many chests are full, what
    /// waits for room (<paramref name="waits"/>, a sentence), and the chests near home that aren't storage yet.</summary>
    private void SayStorageFull(int id, int meta, P3 near, string waits)
    {
        _storageFullSaid = Now;
        string item = World?.Data.DisplayName(id, meta) ?? ItemName(id, meta);
        int full = Storages.Count(s => s.FreeSlots == 0);
        var loose = UnregisteredChests(Home() ?? near, 24, 6);
        string more = loose.Count > 0
            ? $"Chests near home that aren't storage yet: {string.Join(", ", loose)} (set them as storage to use them)"
            : "Place more chests (a chest the colony builds is storage) or set more chests as storage";
        Alert("warn", $"Storage is full: no chest has room for {item} ({full} of {Storages.Count} storage chests full). {waits} {more}");
    }

    /// <summary>
    /// A miner with no free slot was about to get a dig (a quarry cell, the stair's head room, an ore vein): it gets none, and this says
    /// why (docs/web/story-mode.md "Quarries", full pockets). Its script's dig batch needs a free slot for every block and ends "0/12
    /// dug; inventory full" as a success, so the director handed the same batch out again at once: a full pick under the colony lock
    /// and a fresh quarry scan per round, several a second, for as long as storage stayed full. A pawn unloads before it works (the
    /// picker's delivery comes first), so full pockets here mean it could not: nothing it carries has a chest to go to (the
    /// storage-full alert names the item, like the farm's), its last delivery failed and rests, or it carries only what it keeps.
    /// </summary>
    private void PocketsFullNote(PawnState p, PawnLive live, string work, string halted = "digging")
    {
        Stack? stuck = null;
        bool room = false;
        foreach (var s in Deliverables(live))
        {
            if (HaulDest(s.Id, s.Meta, live.Block) is { Chest: not null }) { room = true; break; }
            stuck ??= s;
        }
        if (room) { Blocked($"{work}: pockets full, storing its load first"); return; }
        if (stuck == null) { Blocked($"{work}: pockets full of what it keeps (tools, food, its blocks)"); return; }
        Blocked($"{work}: pockets full, no storage has room");
        // No storage at all is the standing "No storage for: …" alert's (as for the farm): this one is for chests without room.
        if (Storages.Count == 0 || Now - _storageFullSaid < TimeSpan.FromMinutes(10)) return;
        SayStorageFull(stuck.Id, stuck.Meta, live.Block, $"{p.Display}'s pockets are full: no {halted} for it ({work}) until one has room.");
    }

    /// <summary>Chests (and trapped chests) within <paramref name="r"/> of <paramref name="around"/> that aren't storage, nearest first
    /// (a double chest once).</summary>
    private List<P3> UnregisteredChests(P3 around, int r, int max)
    {
        var found = new List<P3>();
        if (World is not { } w) return found;
        for (int x = around.X - r; x <= around.X + r; x++)
            for (int zz = around.Z - r; zz <= around.Z + r; zz++)
                if (w.Loaded(x, zz)) UnregisteredInColumn(w, x, zz, around.Y, found);
        return found.OrderBy(p => p.Dist(around)).Take(max).ToList();
    }

    private void UnregisteredInColumn(IStoryWorld w, int x, int z, int y0, List<P3> found)
    {
        for (int y = y0 - 6; y <= y0 + 6; y++)
        {
            if (w.Block(x, y, z).Id is not (54 or 146)) continue;
            var at = new P3(x, y, z);
            if (StorageAt(at) != null || DoubleHalf(at) is { } half && found.Contains(half)) continue;
            found.Add(at);
        }
    }
}
