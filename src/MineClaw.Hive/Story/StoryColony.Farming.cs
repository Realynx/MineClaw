using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// Growing zones (docs/web/story-mode.md "Farming"): a farm zone with a chosen plant (<see cref="ZoneState.Plant"/>). Pawns with Farm
/// work on till dirt and grass into farmland (a hoe: carried, fetched, or a bill for one at a crafting table), sow the plant's seed item
/// on every empty cell that suits it, harvest what is ripe and sow again, cut sugar cane and cactus above the bottom block, break
/// pumpkins and melons and leave their stems, and chop grown saplings. The zone's state says what it has (sown, ripe, empty), where its
/// seeds come from and what is missing; "seek seeds" designates tall grass to cut (wheat) or queues a bill (pumpkin, melon seeds).
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>One cell of a growing zone: its soil, what it needs now and where (till: the soil; the rest: the plant cell).</summary>
    internal sealed record GrowCell(P3 Soil, string State, P3 At, bool Clear = false);

    // State: till | sow | dry (dirt with no water near: not tilled) | growing | ripe (harvest at At) | blocked
    private readonly Dictionary<string, (DateTimeOffset At, List<GrowCell> Cells)> _growScan = new();
    private readonly Dictionary<string, DateTimeOffset> _growAlert = new();

    private static readonly HashSet<int> PlantBlocks = [59, 141, 142, 207, 104, 105, 83, 81, 115, 6, 86, 103, 17, 162, 18, 161];
    public static readonly int[] Hoes = [290, 291, 292, 293, 294];

    /// <summary>A growing zone's cells (cached 5 s; a finished job clears it).</summary>
    internal List<GrowCell> GrowCells(ZoneState z)
    {
        if (_growScan.TryGetValue(z.Id, out var c) && Now - c.At < TimeSpan.FromSeconds(5)) return c.Cells;
        var cells = new List<GrowCell>();
        if (World is { } w && StoryPlants.Get(w.Data, z.Plant) is { } plant)
        {
            var a = z.Area;
            int n = 0;
            for (int x = a.X1; x <= a.X2; x++)
                for (int zz = a.Z1; zz <= a.Z2; zz++)
                {
                    if (++n > 4096) goto done;
                    if (!w.Loaded(x, zz) || SoilIn(w, x, zz, a.Y1) is not { } soil) continue;
                    cells.Add(Classify(w, plant, z, soil));
                }
            done:;
        }
        _growScan[z.Id] = (Now, cells);
        return cells;
    }

    /// <summary>The ground block of a column near the zone's level: the first block down that isn't air, a small plant or the crop.</summary>
    private static P3? SoilIn(IStoryWorld w, int x, int z, int level)
    {
        for (int y = level + 4; y >= level - 3 && y > 0; y--)
        {
            int id = w.Block(x, y, z).Id;
            if (StoryWander.Passable(id) || PlantBlocks.Contains(id)) continue;
            return new P3(x, y, z);
        }
        return null;
    }

    private GrowCell Classify(IStoryWorld w, StoryPlant plant, ZoneState z, P3 soil)
    {
        var at = soil.Add(0, 1, 0);
        int s = w.Block(soil.X, soil.Y, soil.Z).Id;
        var (above, meta) = w.Block(at.X, at.Y, at.Z);
        bool open = StoryWander.Passable(above) && !StoryBlocks.Liquids.Contains(above);
        GrowCell Cell(string state, P3? where = null) => new(soil, state, where ?? at, open && above != 0);
        switch (plant.Harvest)
        {
            case "crop" or "fruit" when plant.Soil == "farmland":
                if (plant.Fruit != 0 && above == plant.Fruit) return Cell("ripe");
                if (s == StoryPlants.Farmland)
                {
                    if (above == plant.Block)
                    {
                        if (plant.Harvest == "crop") return Cell(meta >= plant.Ripe ? "ripe" : "growing");
                        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                            if (w.Block(at.X + dx, at.Y, at.Z + dz).Id == plant.Fruit) return Cell("ripe", at.Add(dx, 0, dz));
                        return Cell("growing");
                    }
                    if (plant.Harvest == "fruit" && (soil.X - z.Area.X1) % 2 == 1) return Cell("blocked"); // every other row: room for the fruit
                    return open ? Cell("sow") : Cell("blocked");
                }
                if (s is StoryPlants.Grass or StoryPlants.Dirt && open)
                    return plant.Harvest == "fruit" && (soil.X - z.Area.X1) % 2 == 1 ? Cell("blocked")
                        : StoryPlants.WaterNear(w, soil) ? Cell("till", soil) : Cell("dry", soil);
                return Cell("blocked");
            case "crop": // nether wart on soul sand
                if (s != StoryPlants.SoulSand) return Cell("blocked");
                if (above == plant.Block) return Cell(meta >= plant.Ripe ? "ripe" : "growing");
                return open ? Cell("sow") : Cell("blocked");
            case "stalk":
                if (above == plant.Block)
                    return w.Block(at.X, at.Y + 1, at.Z).Id == plant.Block ? Cell("ripe", at.Add(0, 1, 0)) : Cell("growing");
                if (!open || !StoryPlants.SoilFits(w, plant, soil) || plant.Block == StoryPlants.Cactus && !StoryPlants.ClearBeside(w, at))
                    return Cell("blocked");
                if (plant.Block == StoryPlants.Cactus && (soil.X - z.Area.X1 + soil.Z - z.Area.Z1) % 2 == 1) return Cell("blocked"); // a checkerboard
                return Cell("sow");
            case "tree":
                if (StoryBlocks.Logs.Contains(above)) return Cell("ripe");
                if (above == plant.Block) return Cell("growing");
                // Saplings on a 3-block grid: room for the trees to grow.
                if ((soil.X - z.Area.X1) % 3 != 0 || (soil.Z - z.Area.Z1) % 3 != 0) return Cell("blocked");
                return open && StoryPlants.SoilFits(w, plant, soil) ? Cell("sow") : Cell("blocked");
        }
        return Cell("blocked");
    }

    /// <summary>How many of a plant's seed items the colony has (storage and pawns), minus what pending sow jobs already took.</summary>
    private int SeedsFree(StoryPlant plant, string? except = null)
    {
        int have = CountItem(plant.SeedId, plant.SeedId == 6 ? plant.SeedMeta : -1);
        int reserved = _current.Where(kv => !kv.Key.Equals(except, StringComparison.OrdinalIgnoreCase) && kv.Value.Source == "sow" && kv.Value.Kind == "place"
            && kv.Value.SourceId is { } zid && Zones.FirstOrDefault(z => z.Id == zid)?.Plant == plant.Id).Count();
        return have - reserved;
    }

    /// <summary>The next growing-zone job for this pawn in zone <paramref name="z"/>: harvest first, then sow, then till.</summary>
    private ActiveJob? GrowJob(PawnState p, PawnLive live, ZoneState z)
    {
        if (World is not { } w || StoryPlants.Get(w.Data, z.Plant) is not { } plant) return null;
        var from = live.Block;
        var cells = GrowCells(z).Where(c => Open("b:" + c.At, p.Name, c.At)).OrderBy(c => c.At.Dist(from)).ToList();
        string seed = StoryPlants.SeedName(w.Data, plant);
        if (z.Harvest && cells.FirstOrDefault(c => c.State == "ripe") is { } r)
        {
            if (plant.Harvest == "tree")
                return Job(WorkTypes.Farm, "chop", $"chopping a grown {plant.Label.ToLowerInvariant()}", r.At, "b:" + r.At, "crop", z.Id,
                    fill: j => ChopFill(j, StoryBlocks.TreeFrom(w, r.At)), xp: 30);
            bool replant = plant.Harvest == "crop";
            return Job(WorkTypes.Farm, replant ? "harvest" : "dig", $"harvesting {plant.Label.ToLowerInvariant()}", r.At, "b:" + r.At, "crop", z.Id, "working",
                j => { if (replant) j["replant"] = seed; }, xp: 15);
        }
        if (!z.Sow) return null;
        if (cells.FirstOrDefault(c => c.State == "sow") is { } sow)
        {
            var mine = live.Inventory.FirstOrDefault(s => StoryPlants.IsSeed(plant, s));
            (StorageState Store, Stack Stack)? stored = mine == null && SeedsFree(plant, p.Name) > 0 ? FindStored(s => StoryPlants.IsSeed(plant, s), from) : null;
            if (mine != null || stored != null)
            {
                var j = Job(WorkTypes.Farm, "place", $"sowing {plant.Label.ToLowerInvariant()}", sow.At, "b:" + sow.At, "sow", z.Id, "working", f =>
                {
                    f["block"] = seed;
                    if (sow.Clear) f["clear"] = true;
                    if (stored is { } st) f["fetch"] = Fetch(st.Store.Chest, st.Stack, 16);
                }, xp: 10);
                if (stored is { } st2) j.Takes.Add((st2.Store.Chest, st2.Stack with { Count = Math.Min(16, st2.Stack.Count) }));
                return j;
            }
            NoSeeds(z, plant);
        }
        if (cells.FirstOrDefault(c => c.State == "till") is { } till)
        {
            var hoe = live.Inventory.FirstOrDefault(s => Hoes.Contains(s.Id));
            var stored = hoe == null ? FindStored(s => Hoes.Contains(s.Id), from) : null;
            if (hoe == null && stored == null) { NeedHoe(z); return null; }
            var item = hoe ?? stored!.Value.Stack;
            var j = Job(WorkTypes.Farm, "till", "tilling soil", till.At, "b:" + till.At, "sow", z.Id, "working", f =>
            {
                f["hoe"] = ItemName(item.Id, item.Meta);
                if (stored is { } st) f["fetch"] = Fetch(st.Store.Chest, st.Stack, 1);
            }, xp: 5);
            if (stored is { } st2) j.Takes.Add((st2.Store.Chest, st2.Stack with { Count = 1 }));
            return j;
        }
        return null;
    }

    /// <summary>No seeds for a zone that has room: said once in a while; pumpkin and melon seeds get a bill when the fruit is stored.</summary>
    private void NoSeeds(ZoneState z, StoryPlant plant)
    {
        if (plant.Harvest == "fruit" && World is { } w)
        {
            int fruit = plant.Fruit == 103 ? 360 : plant.Fruit; // a melon slice crafts melon seeds
            if (CountItem(fruit, -1) > 0) AutoBill(w, plant.SeedId, 4, $"growing zone {ZoneName(z)} needs {StoryPlants.SeedName(w.Data, plant)}");
        }
        if (_growAlert.TryGetValue("seeds:" + z.Id, out var last) && Now - last < TimeSpan.FromMinutes(10)) return;
        _growAlert["seeds:" + z.Id] = Now;
        Alert("warn", $"Growing zone {ZoneName(z)}: {SeedHint(plant)}");
    }

    private void NeedHoe(ZoneState z)
    {
        if (World is { } w && AutoBill(w, 290, 1, $"growing zone {ZoneName(z)} needs a hoe to till")) return;
        if (_growAlert.TryGetValue("hoe:" + z.Id, out var last) && Now - last < TimeSpan.FromMinutes(10)) return;
        _growAlert["hoe:" + z.Id] = Now;
        Alert("warn", $"Growing zone {ZoneName(z)} needs a hoe to till: store one, or add a crafting table for a hoe bill");
    }

    private static string ZoneName(ZoneState z) => z.Name is { Length: > 0 } n ? $"'{n}'" : z.Id;

    /// <summary>What is missing, said the way the colony can act on it.</summary>
    internal static string SeedHint(StoryPlant p) => p.Id switch
    {
        "wheat" => "No wheat seeds — cut tall grass to find some",
        "carrots" => "Needs carrots — none in storage (harvests, zombie drops, village farms)",
        "potatoes" => "Needs potatoes — none in storage (harvests, zombie drops, village farms)",
        "pumpkin" => "No pumpkin seeds — store a pumpkin and a crafting table makes them",
        "melon" => "No melon seeds — store a melon slice and a crafting table makes them",
        _ => $"No {p.Label.ToLowerInvariant()} to sow — {p.Source}",
    };

    /// <summary>
    /// A count bill for <paramref name="outputId"/> at the colony's crafting table, unless one is still open. False when there is no
    /// crafting table or no such recipe in this version.
    /// </summary>
    private bool AutoBill(IStoryWorld w, int outputId, int target, string why)
    {
        var recipe = StoryRecipes.For(w.Data, "crafting_table").FirstOrDefault(r => r.Output.Id == outputId);
        var bench = Benches.Where(b => b.Kind == "crafting_table").OrderBy(b => Home() is { } h ? b.Pos.Dist(h) : 0).FirstOrDefault();
        if (recipe == null || bench == null) return false;
        if (Benches.Any(b => b.Bills.Any(x => x.Recipe == recipe.Id && !x.Paused && (x.Mode != "count" || x.Done < x.Target)))) return true;
        bench.Bills.Add(new BillState { Id = NextId("bill"), Recipe = recipe.Id, Mode = "count", Target = target });
        bench.Rev = Touch();
        Alert("info", $"Added a bill for {target} {recipe.Label} at the crafting table {bench.Pos}: {why}");
        return true;
    }

    // ---------------------------------------------------------------- zone ops and state

    /// <summary>A growing zone's settings from a request body: plant (null keeps), sow, harvest. Error when the plant is unknown here.</summary>
    private string? ApplyGrow(ZoneState z, JsonObject? body)
    {
        if (body == null) return null;
        if (body["plant"] is { } pn)
        {
            var id = pn is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (string.IsNullOrWhiteSpace(id)) z.Plant = null;
            else if (World is not { } w) return "no world view yet";
            else if (StoryPlants.Get(w.Data, id) is not { } plant)
                return $"plant must be one of {string.Join("|", StoryPlants.For(w.Data).Select(x => x.Id))} on {w.Version}";
            else z.Plant = plant.Id;
        }
        if (Bool(body["sow"]) is { } sow) z.Sow = sow;
        if (Bool(body["harvest"]) is { } harvest) z.Harvest = harvest;
        return null;

        static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    }

    /// <summary>What a growing zone's soil allows: an error when nothing in it can grow the plant, warnings for what slows it.</summary>
    private (string? Error, List<string> Warnings) ValidateGrow(ZoneState z)
    {
        var warn = new List<string>();
        if (z.Plant == null || World is not { } w || StoryPlants.Get(w.Data, z.Plant) is not { } plant) return (null, warn);
        _growScan.Remove(z.Id);
        var cells = GrowCells(z);
        if (cells.Count == 0) return (null, warn); // not loaded yet: nothing to judge
        if (cells.All(c => c.State == "blocked"))
            return (plant.Soil switch
            {
                "sand" => $"{plant.Label} grows on sand: there is none free in the zone",
                "soul_sand" => $"{plant.Label} grows on soul sand: there is none in the zone",
                "cane" => $"{plant.Label} grows on sand, dirt or grass right beside water: there is none in the zone",
                _ => $"nothing in the zone can grow {plant.Label.ToLowerInvariant()} (it needs {plant.Soil} with room above)",
            }, warn);
        if (plant.Soil == "farmland" && cells.Any(c => c.State == "dry") && !cells.Any(c => c.State is "till" or "sow" or "growing" or "ripe"))
            warn.Add("no water within 4 blocks of the zone: dirt there isn't tilled (farmland dries out)");
        else if (cells.Any(c => c.State == "dry")) warn.Add($"{cells.Count(c => c.State == "dry")} cell(s) have no water within 4 blocks and aren't tilled");
        return (null, warn);
    }

    /// <summary>update {id, plant?, sow?, harvest?} | seek_seeds {id}.</summary>
    private (JsonNode? Result, string? Error) GrowZoneOp(string op, string? id, JsonObject? body)
    {
        lock (_lock)
        {
            if (Zones.FirstOrDefault(z => z.Id == id) is not { } zone) return (null, "id: no such zone");
            if (op == "update")
            {
                if (zone.Kind != "farm") return (null, "only farm (growing) zones take plant, sow and harvest");
                var old = (zone.Plant, zone.Sow, zone.Harvest);
                if (ApplyGrow(zone, body) is { } err) return (null, err);
                var (bad, warn) = ValidateGrow(zone);
                if (bad != null) { (zone.Plant, zone.Sow, zone.Harvest) = old; return (null, bad); }
                zone.Rev = Touch();
                WakeIdle();
                return (new JsonObject { ["id"] = zone.Id, ["warnings"] = new JsonArray(warn.Select(x => (JsonNode)x!).ToArray()) }, null);
            }
            // seek_seeds: explicit, never automatic.
            if (World is not { } w) return (null, "no world view yet");
            if (StoryPlants.Get(w.Data, zone.Plant) is not { } plant) return (null, "the zone grows nothing: set its plant first");
            if (plant.Id == "wheat")
            {
                var grass = TallGrassNear(w, zone, 24, 64);
                if (grass.Count == 0) return (null, "no tall grass or ferns within 24 blocks of the zone");
                var (res, derr) = Designate("add", "cut", null, grass, null);
                return derr != null ? (null, derr) : (new JsonObject { ["count"] = res?["count"]?.DeepClone(), ["kind"] = "cut" }, null);
            }
            if (plant.Harvest == "fruit")
            {
                int fruit = plant.Fruit == 103 ? 360 : plant.Fruit;
                if (CountItem(fruit, -1) == 0) return (null, $"store a {(fruit == 360 ? "melon slice" : "pumpkin")} first: the seeds are crafted from it");
                if (!AutoBill(w, plant.SeedId, 4, $"growing zone {ZoneName(zone)} needs seeds")) return (null, "no crafting table at home for the bill");
                return (new JsonObject { ["count"] = 4, ["kind"] = "bill" }, null);
            }
            return (null, SeedHint(plant));
        }
    }

    /// <summary>Tall grass and ferns (their lower half) near the zone, nearest its centre first.</summary>
    private List<P3> TallGrassNear(IStoryWorld w, ZoneState z, int r, int max)
    {
        var c = z.Area.Center;
        var found = new List<P3>();
        for (int x = c.X - r; x <= c.X + r; x++)
            for (int zz = c.Z - r; zz <= c.Z + r; zz++)
            {
                if (!w.Loaded(x, zz)) continue;
                for (int y = z.Area.Y1 - 6; y <= z.Area.Y1 + 6; y++)
                {
                    var (id, meta) = w.Block(x, y, zz);
                    if (id == 31 && meta is 1 or 2 || id == 175 && (meta & 8) == 0 && (meta & 7) is 2 or 3) found.Add(new P3(x, y, zz));
                }
            }
        return found.OrderBy(p => p.Dist(c)).Take(max).ToList();
    }

    /// <summary>The state's zone object: a growing zone adds plant, sow, harvest, seeds and status.</summary>
    internal JsonObject ZoneJson(ZoneState z)
    {
        var o = new JsonObject { ["id"] = z.Id, ["kind"] = z.Kind, ["area"] = z.Area.Json(), ["name"] = z.Name };
        if (z.Kind != "farm" || z.Plant == null || World is not { } w || StoryPlants.Get(w.Data, z.Plant) is not { } plant) return o;
        var cells = GrowCells(z);
        int Count(string s) => cells.Count(c => c.State == s);
        int planted = Count("growing") + Count("ripe"), room = Count("sow") + Count("till"), total = planted + room;
        int have = CountItem(plant.SeedId, plant.SeedId == 6 ? plant.SeedMeta : -1);
        o["plant"] = plant.Id;
        o["sow"] = z.Sow;
        o["harvest"] = z.Harvest;
        o["status"] = $"{planted}/{total} sown, {Count("ripe")} ripe" + (Count("dry") > 0 ? $", {Count("dry")} too far from water" : "");
        o["seeds"] = new JsonObject
        {
            ["item"] = StoryPlants.SeedName(w.Data, plant), ["have"] = have, ["needed_for_empty"] = room, ["source_hint"] = plant.Source,
            ["missing"] = have == 0 && room > 0 ? SeedHint(plant) : null,
            ["action"] = have == 0 && room > 0 && plant.Id == "wheat" ? "seek_seeds" : have == 0 && room > 0 && plant.Harvest == "fruit" ? "craft_seeds" : null,
        };
        o["needs_hoe"] = z.Sow && Count("till") > 0 && CountItem(290, -1) + CountItem(291, -1) + CountItem(292, -1) + CountItem(293, -1) + CountItem(294, -1) == 0;
        return o;
    }
}
