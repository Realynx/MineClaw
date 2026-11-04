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
    internal sealed record GrowCell(P3 Soil, string State, P3 At, bool Clear = false, string? Plant = null);

    // State: till | sow | dry (dirt with no water near: not tilled) | growing | ripe (harvest at At) | blocked
    private readonly Dictionary<string, (DateTimeOffset At, string Plant, List<GrowCell> Cells)> _growScan = new();
    private readonly Dictionary<string, DateTimeOffset> _growAlert = new();

    private static readonly HashSet<int> PlantBlocks = [59, 141, 142, 207, 104, 105, 83, 81, 115, 6, 86, 103, 17, 162, 18, 161];
    public static readonly int[] Hoes = [290, 291, 292, 293, 294];

    /// <summary>
    /// A growing zone's cells (cached 5 s, and while the active bill's plant stays; a finished job clears it): classified for the
    /// active bill's plant; a crop of another bill's plant already in the ground is harvested as that plant, never ripped out. With
    /// no active bill (all met) only the crops are listed: the zone keeps harvesting and sows nothing.
    /// </summary>
    internal List<GrowCell> GrowCells(ZoneState z)
    {
        var w = World;
        var active = w == null ? null : ActivePlant(w, z);
        if (_growScan.TryGetValue(z.Id, out var c) && c.Plant == (active?.Id ?? "") && Now - c.At < TimeSpan.FromSeconds(5)) return c.Cells;
        var cells = w == null ? new List<GrowCell>() : ScanGrow(w, z, active, BillPlants(w, z));
        _growScan[z.Id] = (Now, active?.Id ?? "", cells);
        return cells;
    }

    private List<GrowCell> ScanGrow(IStoryWorld w, ZoneState z, StoryPlant? active, IReadOnlyList<StoryPlant> grown)
    {
        var cells = new List<GrowCell>();
        int n = 0;
        foreach (var (x, zz) in z.Cells.Sorted())
        {
            if (++n > 4096) break;
            if (!w.Loaded(x, zz) || SoilIn(w, x, zz, z.Area.Y1) is not { } soil) continue;
            int above = w.Block(soil.X, soil.Y + 1, soil.Z).Id;
            var other = grown.FirstOrDefault(p => p.Id != active?.Id && GrowsHere(p, above));
            if ((other ?? active) is not { } plant) continue;
            var cell = Classify(w, plant, z, soil);
            if ((active == null || other != null) && cell.State is not ("ripe" or "growing")) continue;
            cells.Add(cell);
        }
        return cells;
    }

    /// <summary>Is <paramref name="above"/> (the block on the soil) this plant's crop, stem or grown tree?</summary>
    private static bool GrowsHere(StoryPlant p, int above) =>
        above == p.Block || p.Fruit != 0 && above == p.Fruit || p.Harvest == "tree" && StoryBlocks.Logs.Contains(above);

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

    internal GrowCell Classify(IStoryWorld w, StoryPlant plant, ZoneState z, P3 soil)
    {
        var g = new GrowSpot(w, plant, z, soil);
        return plant.Harvest switch
        {
            "crop" or "fruit" when plant.Soil == "farmland" => g.FarmlandCell(),
            "crop" => g.SoulSandCell(), // nether wart on soul sand
            "stalk" => g.StalkCell(),
            "tree" => g.TreeCell(),
            _ => g.Cell("blocked"),
        };
    }

    private static readonly (int Dx, int Dz)[] GrowSides = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>One soil cell of a growing zone as <see cref="Classify"/> reads it: the soil, the block on it, the plant and the zone grid.</summary>
    private readonly struct GrowSpot
    {
        private readonly IStoryWorld _w;
        private readonly StoryPlant _plant;
        private readonly ZoneState _z;
        private readonly P3 _soil, _at;
        private readonly int _soilId, _above, _meta;
        private readonly bool _open;

        public GrowSpot(IStoryWorld w, StoryPlant plant, ZoneState z, P3 soil)
        {
            _w = w; _plant = plant; _z = z; _soil = soil;
            _at = soil.Add(0, 1, 0);
            _soilId = w.Block(soil.X, soil.Y, soil.Z).Id;
            (_above, _meta) = w.Block(_at.X, _at.Y, _at.Z);
            _open = StoryWander.Passable(_above) && !StoryBlocks.Liquids.Contains(_above);
        }

        public GrowCell Cell(string state, P3? where = null) => new(_soil, state, where ?? _at, _open && _above != 0, _plant.Id);

        private GrowCell Blocked => Cell("blocked");

        /// <summary>Every other row (a fruit plant's): room for the fruit.</summary>
        private bool FruitGap => _plant.Harvest == "fruit" && (_soil.X - _z.Area.X1) % 2 == 1;

        private GrowCell CropCell() => Cell(_meta >= _plant.Ripe ? "ripe" : "growing");

        /// <summary>A crop or a fruit plant on farmland: ripe, growing, sow, till, dry (no water near: not tilled) or blocked.</summary>
        public GrowCell FarmlandCell()
        {
            if (_plant.Fruit != 0 && _above == _plant.Fruit) return Cell("ripe");
            if (_soilId == StoryPlants.Farmland) return OnFarmland();
            if (_soilId is StoryPlants.Grass or StoryPlants.Dirt && _open)
                return FruitGap ? Blocked : StoryPlants.WaterNear(_w, _soil) ? Cell("till", _soil) : Cell("dry", _soil);
            return Blocked;
        }

        private GrowCell OnFarmland()
        {
            if (_above == _plant.Block) return _plant.Harvest == "crop" ? CropCell() : StemCell();
            if (FruitGap) return Blocked;
            return _open ? Cell("sow") : Blocked;
        }

        /// <summary>A stem: ripe when its fruit stands beside it (harvested there).</summary>
        private GrowCell StemCell()
        {
            foreach (var (dx, dz) in GrowSides)
                if (_w.Block(_at.X + dx, _at.Y, _at.Z + dz).Id == _plant.Fruit) return Cell("ripe", _at.Add(dx, 0, dz));
            return Cell("growing");
        }

        public GrowCell SoulSandCell()
        {
            if (_soilId != StoryPlants.SoulSand) return Blocked;
            if (_above == _plant.Block) return CropCell();
            return _open ? Cell("sow") : Blocked;
        }

        /// <summary>Sugar cane and cactus: ripe when two tall (cut above the bottom block); cactus on a checkerboard, clear beside.</summary>
        public GrowCell StalkCell()
        {
            bool cactus = _plant.Block == StoryPlants.Cactus;
            if (_above == _plant.Block)
                return _w.Block(_at.X, _at.Y + 1, _at.Z).Id == _plant.Block ? Cell("ripe", _at.Add(0, 1, 0)) : Cell("growing");
            if (!_open || !StoryPlants.SoilFits(_w, _plant, _soil) || cactus && !StoryPlants.ClearBeside(_w, _at)) return Blocked;
            if (cactus && (_soil.X - _z.Area.X1 + _soil.Z - _z.Area.Z1) % 2 == 1) return Blocked; // a checkerboard
            return Cell("sow");
        }

        /// <summary>Saplings on a 3-block grid: room for the trees to grow.</summary>
        public GrowCell TreeCell()
        {
            if (StoryBlocks.Logs.Contains(_above)) return Cell("ripe");
            if (_above == _plant.Block) return Cell("growing");
            if ((_soil.X - _z.Area.X1) % 3 != 0 || (_soil.Z - _z.Area.Z1) % 3 != 0) return Blocked;
            return _open && StoryPlants.SoilFits(_w, _plant, _soil) ? Cell("sow") : Blocked;
        }
    }

    /// <summary>How many of a plant's seed items the colony has (storage and pawns), minus what pending sow jobs already took.</summary>
    private int SeedsFree(StoryPlant plant, string? except = null)
    {
        int have = CountItem(plant.SeedId, plant.SeedId == 6 ? plant.SeedMeta : -1);
        int reserved = _current.Where(kv => !kv.Key.Equals(except, StringComparison.OrdinalIgnoreCase) && kv.Value.Source == "sow" && kv.Value.Kind == "place"
            && kv.Value.SourceId is { } zid && Zones.FirstOrDefault(z => z.Id == zid) is { } sz && World is { } w && ActivePlant(w, sz)?.Id == plant.Id).Count();
        return have - reserved;
    }

    /// <summary>The next growing-zone job for this pawn in zone <paramref name="z"/>: harvest first, then sow, then till.</summary>
    private ActiveJob? GrowJob(PawnState p, PawnLive live, ZoneState z)
    {
        if (World is not { } w) return null;
        var plant = ActivePlant(w, z);
        var from = live.Block;
        var cells = GrowCells(z).Where(c => Open("b:" + c.At, p.Name, c.At)).OrderBy(c => c.At.Dist(from)).ToList();
        if (z.Harvest && cells.FirstOrDefault(c => c.State == "ripe") is { } r && StoryPlants.Get(w.Data, r.Plant) is { } crop)
        {
            _cropHarvest["b:" + r.At] = (z.Id, crop.Id); // counts toward the zone's bills when done
            if (crop.Harvest == "tree")
                return Job(WorkTypes.Farm, "chop", $"chopping a grown {crop.Label.ToLowerInvariant()}", r.At, "b:" + r.At, "crop", z.Id,
                    fill: j => ChopFill(j, StoryBlocks.TreeFrom(w, r.At)), xp: 30);
            // Sown again in the same go only when it is still what the zone sows (a met bill's crop is harvested, not replanted).
            bool replant = crop.Harvest == "crop" && crop.Id == plant?.Id;
            string again = StoryPlants.SeedName(w.Data, crop);
            return Job(WorkTypes.Farm, crop.Harvest == "crop" ? "harvest" : "dig", $"harvesting {crop.Label.ToLowerInvariant()}", r.At, "b:" + r.At, "crop", z.Id, "working",
                j => { if (replant) j["replant"] = again; }, xp: 15);
        }
        if (!z.Sow || plant == null) return null;
        string seed = StoryPlants.SeedName(w.Data, plant);
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
    private (string? Error, List<string> Warnings) ValidateGrow(ZoneState z, string? plantId = null)
    {
        var warn = new List<string>();
        if ((plantId ?? z.Plant) is not { } pid || World is not { } w || StoryPlants.Get(w.Data, pid) is not { } plant) return (null, warn);
        _growScan.Remove(z.Id);
        var cells = ScanGrow(w, z, plant, []); // the zone's soil for this plant (whatever its bills sow now)
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
                if (zone.Plant != old.Plant) SyncPlantBill(zone); // plant = the first bill's plant (StoryColony.GrowBills.cs)
                zone.Rev = Touch();
                _growScan.Remove(zone.Id);
                WakeIdle();
                return (new JsonObject { ["id"] = zone.Id, ["warnings"] = new JsonArray(warn.Select(x => (JsonNode)x!).ToArray()) }, null);
            }
            // seek_seeds: explicit, never automatic.
            if (World is not { } w) return (null, "no world view yet");
            if ((ActivePlant(w, zone) ?? StoryPlants.Get(w.Data, zone.Plant)) is not { } plant) return (null, "the zone grows nothing: set its plant first");
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

    /// <summary>
    /// The state's zone object: its cells as row runs <c>[z, x1, x2]</c> and their count (docs/web/story-mode.md "Zones"; area is their
    /// bounding box and level range). A growing zone adds plant (its type: the first bill's), active_plant, bills, sow, harvest, seeds
    /// (for what it sows now) and status.
    /// </summary>
    internal JsonObject ZoneJson(ZoneState z)
    {
        var o = new JsonObject
        {
            ["id"] = z.Id, ["kind"] = z.Kind, ["area"] = z.Area.Json(), ["name"] = z.Name, ["cells"] = z.Cells.Count,
            ["rows"] = new JsonArray((z.Rows ?? z.Cells.Rows()).Select(r => (JsonNode)new JsonArray(r[0], r[1], r[2])).ToArray()),
        };
        if (z.Kind == "fish") { FishJson(z, o); return o; } // StoryColony.Fishing.cs
        if (z.Kind == "mine") { QuarryJson(z, o); return o; } // StoryColony.Quarry.cs
        if (z.Kind != "farm" || World is not { } w) return o;
        if (z.Bills == null && StoryPlants.Get(w.Data, z.Plant) == null) return o; // a plain farm zone
        var plant = ActivePlant(w, z);
        var cells = GrowCells(z);
        int Count(string s) => cells.Count(c => c.State == s);
        int planted = Count("growing") + Count("ripe"), room = Count("sow") + Count("till"), total = planted + room;
        o["plant"] = z.Plant;
        o["active_plant"] = plant?.Id;
        o["sow"] = z.Sow;
        o["harvest"] = z.Harvest;
        if (z.Bills != null) BillsJson(w, z, o, room); // planting bills (StoryColony.GrowBills.cs)
        if (plant == null)
        {
            o["status"] = (z.Bills is { Count: 0 } ? "no planting bills" : "all bills met") + $": sowing nothing, {planted} growing, {Count("ripe")} ripe";
            return o;
        }
        int have = CountItem(plant.SeedId, plant.SeedId == 6 ? plant.SeedMeta : -1);
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
