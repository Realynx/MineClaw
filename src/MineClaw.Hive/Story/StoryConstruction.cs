using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

/// <summary>A block material: what goes in the world (id, meta) and the item a pawn places for it.</summary>
public sealed record Material(int Id, int Meta, string Item, string Name);

/// <summary>
/// The Architect's build items expanded to blocks (docs/web/story-mode.md "Construction"): wall = the rectangle's perimeter × height,
/// corners included; floor = the filled area at its level; roof flat|slab|gable over the footprint one above the wall top (gable: stair
/// rows meeting at a ridge along the longer axis, ridge as a slab or block, gable ends filled with the wall material); door = 2 tall;
/// window = glass panes in the wall; stairs = a staircase rising toward its facing; block/furniture = each block; remove = dig.
/// Pure: the world is read only for "remove" (what is there now).
/// </summary>
public static class StoryConstruction
{
    public static readonly IReadOnlyList<string> Kinds =
        ["wall", "floor", "roof", "door", "window", "stairs", "fence", "block", "remove", "mine", "crafting_table", "furnace", "chest", "bed", "torch"];

    public static readonly IReadOnlyList<string> Furniture = ["crafting_table", "furnace", "chest", "bed", "torch"];

    /// <summary>"oak planks"/"planks"/"cobblestone" or [id, meta] → the material for this version; null = unknown.</summary>
    public static Material? Resolve(IGameData d, JsonNode? m)
    {
        if (m is JsonArray a && a.Count >= 1)
        {
            int id = a[0]!.GetValue<int>(), meta = a.Count > 1 ? a[1]!.GetValue<int>() : 0;
            if (!d.ItemExists(id) && !d.BlockExists(id)) return null;
            // An explicit block state (a placed design's cell): the item that places exactly it (its variant: red wool, not wool).
            var (item, _) = PlacingItem(d, id, Math.Max(0, meta));
            return new Material(id, Math.Max(0, meta), item ?? ItemOf(d, id, meta), d.ItemExists(id) ? d.Item(id).Name : d.Block(id).Name);
        }
        if (m is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) return null;
        var name = s.Trim().ToLowerInvariant().Replace(' ', '_');
        if (d.BlockByName(name) is { } b && b.Id != 0) return new Material(b.Id, 0, ItemOf(d, b.Id, 0), b.Name);
        if (d.ItemByNameWithDamage(name) is { } it && it.Item.Id is > 0 and < 256)
            return new Material(it.Item.Id, Math.Max(0, it.Damage), ItemOf(d, it.Item.Id, it.Damage), it.Item.Name);
        return null;
    }

    private static string ItemOf(IGameData d, int id, int meta) => d.ItemExists(id) ? d.Item(id).Name : d.Block(id).Name;

    /// <summary>Block → the item that places it, where they differ (doors, beds, wire, cane, cake, repeaters, string…).</summary>
    private static readonly Dictionary<int, int> PlacedBy = new()
    {
        [26] = 355, [55] = 331, [64] = 324, [71] = 330, [83] = 338, [92] = 354, [93] = 356, [94] = 356, [117] = 379, [118] = 380,
        [132] = 287, [140] = 390, [149] = 404, [150] = 404, [193] = 427, [194] = 428, [195] = 429, [196] = 430, [197] = 431,
    };

    /// <summary>Blocks a pawn can't place yet (special placement), and why.</summary>
    private static string? SpecialPlacement(int id) => id switch
    {
        8 or 9 or 10 or 11 => "liquids are poured from a bucket",
        59 or 104 or 105 or 115 or 141 or 142 or 207 => "crops are planted, not placed",
        127 => "cocoa grows on jungle logs",
        63 or 68 => "a sign asks for its text",
        144 or 176 or 177 => "its look is block-entity data",
        _ => null,
    };

    /// <summary>
    /// The item a pawn places for (id, meta) in this version, with its variant ("red_wool", "spruce_planks"; a door's item for a
    /// door), or (null, why) when pawns can't place it yet: those cells are kept as <c>unsupported</c> ghosts (shown, alerted,
    /// never a job) instead of a wrong block or a build blocked forever on an item that doesn't exist.
    /// </summary>
    public static (string? Item, string? Why) PlacingItem(IGameData d, int id, int meta)
    {
        if (SpecialPlacement(id) is { } why) return (null, why);
        int item = PlacedBy.TryGetValue(id, out var by) ? by : id;
        if (!d.ItemExists(item)) return (null, $"no item places {(d.BlockExists(id) ? d.Block(id).DisplayName : $"block {id}")} in {d.VersionId}");
        if (item != id) return (d.Item(item).Name, null);
        // The item's damage is the block's variant bits (an anvil's damage is meta >> 2).
        int damage = id == 145 ? (meta & 12) >> 2 : meta & MineClaw.Bot.Blueprints.DesignFacing.VariantMask(id);
        if (damage == 0) return (d.Item(item).Name, null);
        var name = d.VariantName(item, damage);
        // Only a name that reads back as this exact variant (else the plain item: never a different colour by accident).
        return d.ItemByNameWithDamage(name) is { } back && back.Item.Id == item && back.Damage == damage ? (name, null) : (null, $"no item name for {d.DisplayName(item, damage)}");
    }

    private static Material M(IGameData d, int id, int meta, int? item = null) =>
        new(id, meta, item is { } i && d.ItemExists(i) ? d.Item(i).Name : ItemOf(d, id, meta), ItemOf(d, id, meta));

    private static bool Has(IGameData d, int id) => d.BlockExists(id);

    /// <summary>The stairs block for a material (oak, spruce… cobblestone, bricks, stone bricks, sandstone, quartz, nether brick).</summary>
    public static int StairsFor(IGameData d, Material m)
    {
        int s = m.Id switch
        {
            5 => m.Meta switch { 1 => 134, 2 => 135, 3 => 136, 4 => 163, 5 => 164, _ => 53 },
            4 or 1 or 48 => 67,
            45 => 108,
            98 => 109,
            112 => 114,
            24 => 128,
            155 => 156,
            179 => 180,
            201 => 203,
            53 or 67 or 108 or 109 or 114 or 128 or 134 or 135 or 136 or 156 or 163 or 164 or 180 or 203 => m.Id,
            _ => 53,
        };
        return Has(d, s) ? s : 53;
    }

    /// <summary>The half slab for a material (b1.8.1: stone slab metas 0 stone, 1 sandstone, 2 wood, 3 cobble, 4 brick, 5 stone brick).</summary>
    public static (int Id, int Meta) SlabFor(IGameData d, Material m)
    {
        bool modern = Has(d, 126);
        return m.Id switch
        {
            5 => modern ? (126, m.Meta) : (44, 2),
            4 => (44, 3), 24 => (44, 1), 45 => (44, 4), 98 => (44, 5),
            112 when modern => (44, 6), 155 when modern => (44, 7),
            44 or 126 => (m.Id, m.Meta),
            _ => (44, 0),
        };
    }

    /// <summary>Stairs meta that ascends toward a direction (both versions): east 0, west 1, south 2, north 3.</summary>
    public static int StairsMeta(string facing) => facing switch { "e" => 0, "w" => 1, "s" => 2, _ => 3 };
    /// <summary>A door's lower-half facing meta (east 0, south 1, west 2, north 3); the upper half is meta 8.</summary>
    public static int DoorMeta(string facing) => facing switch { "e" => 0, "s" => 1, "w" => 2, _ => 3 };

    /// <summary>The expanded blocks of one item, or why it can't be expanded.</summary>
    public static (List<GhostState> Ghosts, Material? Mat, string? Error) Expand(IGameData d, JsonObject item, IStoryWorld? world = null)
    {
        var kind = (item["kind"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
        if (!Kinds.Contains(kind)) return ([], null, $"kind must be {string.Join("|", Kinds)}");
        var area = Area.Parse(item["area"]);
        var blocks = item["blocks"] is JsonArray ba ? ba.Select(P3.Parse).Where(p => p != null).Select(p => p!.Value).ToList() : null;
        // A mine item may carry only blocks to put in (`place`: the last of a big mine's items).
        if (area == null && (blocks == null || blocks.Count == 0) && !(kind == "mine" && item["place"] is JsonArray { Count: > 0 }))
            return ([], null, "area or blocks required");
        int height = Math.Clamp(item["height"]?.GetValue<int>() ?? (kind == "wall" ? 3 : 1), 1, 12);
        string facing = (item["facing"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant() is { Length: 1 } f && "nesw".Contains(f) ? f : "";
        int? level = item["level"]?.GetValue<int>();
        var mat = item["material"] is { } mn ? Resolve(d, mn) : null;
        if (item["material"] != null && mat == null && kind != "remove") return ([], null, "unknown material");
        mat ??= kind switch
        {
            "door" => M(d, 64, 0, 324), "window" => M(d, 102, 0), "fence" => M(d, 85, 0), "torch" => M(d, 50, 0),
            "crafting_table" => M(d, 58, 0), "furnace" => M(d, 61, 0), "chest" => M(d, 54, 0), "bed" => M(d, 26, 0, 355),
            _ => M(d, 5, 0),
        };
        if (kind == "door" && mat.Id is not (64 or 71) && !StoryBlocks.IsDoor(mat.Id)) mat = mat.Id == 42 || mat.Name.Contains("iron") ? M(d, 71, 0, 330) : M(d, 64, 0, 324);
        if (kind == "door" && mat.Id == 64) mat = M(d, 64, 0, 324);
        if (kind == "bed") mat = M(d, 26, 0, 355);
        var g = new List<GhostState>();
        void Add(int x, int y, int z, int id, int meta, string? itemName, string? face = null, bool job = true)
        {
            if (y < 0) return;
            g.RemoveAll(o => o.At.X == x && o.At.Y == y && o.At.Z == z);
            g.Add(new GhostState { At = new P3(x, y, z), Id = id, Meta = meta, Item = itemName, Facing = face, Job = job });
        }
        IEnumerable<P3> Positions() => blocks ?? area!.Value.Blocks();

        // A placed mine design (docs/web/api.md "Mine designs"): its cells to dig at their absolute y, then the blocks that go in them.
        if (kind == "mine") return Mine(d, item, blocks ?? area?.Blocks() ?? [], world, Add, g);

        switch (kind)
        {
            case "wall":
            case "fence":
            {
                if (area is not { } a) { foreach (var p in Positions()) for (int h = 0; h < height; h++) Add(p.X, p.Y + h, p.Z, mat.Id, mat.Meta, mat.Item); break; }
                for (int h = 0; h < height; h++)
                    for (int x = a.X1; x <= a.X2; x++)
                        for (int z = a.Z1; z <= a.Z2; z++)
                            if (x == a.X1 || x == a.X2 || z == a.Z1 || z == a.Z2) Add(x, a.Y1 + h, z, mat.Id, mat.Meta, mat.Item);
                if (kind == "wall" && item["roof"] is JsonObject roof)
                {
                    var err = Roof(d, g, Area.Of(a.X1, a.Y1 + height, a.Z1, a.X2, a.Y1 + height, a.Z2), roof, mat, Add);
                    if (err != null) return ([], null, err);
                }
                break;
            }
            case "floor":
                foreach (var p in Positions()) Add(p.X, level ?? p.Y, p.Z, mat.Id, mat.Meta, mat.Item);
                break;
            case "roof":
            {
                if (area is not { } a) return ([], null, "a roof needs an area (its footprint)");
                var spec = item["roof"] as JsonObject ?? new JsonObject { ["style"] = "flat" };
                var err = Roof(d, g, Area.Of(a.X1, level ?? a.Y2, a.Z1, a.X2, level ?? a.Y2, a.Z2), spec, mat, Add, roofKind: true);
                if (err != null) return ([], null, err);
                break;
            }
            case "door":
                foreach (var p in Positions().GroupBy(p => (p.X, p.Z)).Select(c => c.OrderBy(p => p.Y).First()))
                {
                    var fc = facing.Length > 0 ? facing : "n";
                    Add(p.X, p.Y, p.Z, mat.Id, DoorMeta(fc), mat.Item, fc);
                    Add(p.X, p.Y + 1, p.Z, mat.Id, 8, mat.Item, fc, job: false); // the upper half comes with the door
                }
                break;
            case "bed":
                foreach (var p in Positions().Take(8))
                {
                    var fc = facing.Length > 0 ? facing : "s";
                    var (dx, dz) = fc switch { "n" => (0, -1), "e" => (1, 0), "w" => (-1, 0), _ => (0, 1) };
                    int bm = fc switch { "s" => 0, "w" => 1, "n" => 2, _ => 3 };
                    Add(p.X, p.Y, p.Z, 26, bm, mat.Item, fc);
                    Add(p.X + dx, p.Y, p.Z + dz, 26, bm | 8, mat.Item, fc, job: false);
                }
                break;
            case "stairs":
            {
                if (area is not { } a) { foreach (var p in Positions()) Add(p.X, p.Y, p.Z, StairsFor(d, mat), StairsMeta(facing.Length > 0 ? facing : "n"), null, facing); break; }
                var fc = facing.Length > 0 ? facing : (a.X2 - a.X1 >= a.Z2 - a.Z1 ? "e" : "s");
                int steps = Math.Max(1, a.Y2 - a.Y1);
                int sid = StairsFor(d, mat), sm = StairsMeta(fc);
                for (int k = 0; k < steps; k++)
                {
                    int y = a.Y1 + k;
                    if (fc is "e" or "w")
                    {
                        int x = fc == "e" ? a.X1 + k : a.X2 - k;
                        for (int z = a.Z1; z <= a.Z2; z++) Add(x, y, z, sid, sm, null, fc);
                    }
                    else
                    {
                        int z = fc == "s" ? a.Z1 + k : a.Z2 - k;
                        for (int x = a.X1; x <= a.X2; x++) Add(x, y, z, sid, sm, null, fc);
                    }
                }
                break;
            }
            case "remove":
                if (world == null) return ([], null, "remove needs the world view");
                foreach (var p in Positions())
                {
                    var (id, meta) = world.Block(p.X, p.Y, p.Z);
                    if (StoryBlocks.Undiggable.Contains(id)) continue;
                    g.Add(new GhostState { At = p, Id = id, Meta = meta, Remove = true });
                }
                break;
            default: // window, block and furniture: each block
                {
                    // A design's explicit state: a second half (a door's upper, a bed's head, a tall plant's top) comes with its first;
                    // a block pawns can't place yet is kept as an unsupported ghost (alerted, never a job).
                    bool second = kind == "block" && MineClaw.Bot.Blueprints.DesignFacing.SecondHalf(mat.Id, mat.Meta);
                    var (placing, why) = kind == "block" ? PlacingItem(d, mat.Id, mat.Meta) : (mat.Item, null);
                    foreach (var p in Positions())
                    {
                        Add(p.X, p.Y, p.Z, mat.Id, mat.Meta, placing ?? mat.Item, facing.Length > 0 ? facing : null, job: !second && placing != null);
                        if (!second && placing == null && g.Count > 0 && g[^1].At == p) { g[^1].State = "unsupported"; g[^1].Reason = why; }
                    }
                    break;
                }
        }
        foreach (var gh in g.Where(x => x.Item == null && !x.Remove)) gh.Item = d.ItemExists(gh.Id) ? d.Item(gh.Id).Name : d.Block(gh.Id).Name;
        return (g, mat, g.Count == 0 ? "nothing to build there" : null);
    }

    /// <summary>
    /// A mine job group's item (<c>kind: "mine"</c>): <c>blocks</c> = the cells to dig, each a dig ghost carrying the live world's
    /// block there (its expected material, for the tools that dig it; a column not in view yet: 0 = unknown); <c>place</c> =
    /// <c>[[x,y,z,id,meta]]</c>, the blocks put in once their cell is open (torches, ladders, rails…). Refused when a cell is below y 1
    /// or would cut bedrock. Air (a cave) needs nothing; liquids, fire and portals are left alone.
    /// </summary>
    private static (List<GhostState>, Material?, string?) Mine(IGameData d, JsonObject item, IEnumerable<P3> dig, IStoryWorld? world,
        Action<int, int, int, int, int, string?, string?, bool> add, List<GhostState> g)
    {
        if (world == null) return ([], null, "a mine needs the world view");
        foreach (var p in dig)
        {
            if (p.Y < 1) return ([], null, $"the mine reaches y {p.Y} at {p}: a mine can't dig below y 1");
            bool known = world.Loaded(p.X, p.Z);
            var (id, meta) = known ? world.Block(p.X, p.Y, p.Z) : (0, 0);
            if (known && id == 7) return ([], null, $"the mine would cut bedrock at {p}: place it higher");
            if (known && StoryBlocks.Undiggable.Contains(id)) continue;
            g.RemoveAll(o => o.At == p);
            g.Add(new GhostState { At = p, Id = id, Meta = meta, Remove = true });
        }
        if (item["place"] is JsonArray place)
            foreach (var n in place)
            {
                if (n is not JsonArray c || c.Count < 4) return ([], null, "place is [[x,y,z,id,meta]]");
                int x = c[0]!.GetValue<int>(), y = c[1]!.GetValue<int>(), z = c[2]!.GetValue<int>(), id = c[3]!.GetValue<int>(), meta = c.Count > 4 ? c[4]!.GetValue<int>() : 0;
                if (y < 1) return ([], null, $"the mine places a block at y {y}: a mine can't go below y 1");
                if (!d.BlockExists(id)) return ([], null, $"no block {id} in {d.VersionId}");
                bool second = MineClaw.Bot.Blueprints.DesignFacing.SecondHalf(id, meta);
                var (placing, why) = PlacingItem(d, id, meta);
                add(x, y, z, id, meta, placing ?? (d.ItemExists(id) ? d.Item(id).Name : d.Block(id).Name), null, !second && placing != null);
                if (!second && placing == null && g.Count > 0 && g[^1].At == new P3(x, y, z)) { g[^1].State = "unsupported"; g[^1].Reason = why; }
            }
        return (g, null, g.Count == 0 ? "nothing to dig there (already open)" : null);
    }

    /// <summary>A roof over <paramref name="top"/> (its y = the roof's base level): flat | slab | gable | none, overhang 0|1.</summary>
    private static string? Roof(IGameData d, List<GhostState> g, Area top, JsonObject spec, Material wall,
        Action<int, int, int, int, int, string?, string?, bool> add, bool roofKind = false)
    {
        var style = (spec["style"]?.GetValue<string>() ?? "flat").Trim().ToLowerInvariant();
        if (style == "none") return null;
        var mat = spec["material"] is { } rm ? Resolve(d, rm) : wall;
        if (mat == null) return "unknown roof material";
        int ov = Math.Clamp(spec["overhang"]?.GetValue<int>() ?? 0, 0, 1);
        var a = Area.Of(top.X1 - ov, top.Y1, top.Z1 - ov, top.X2 + ov, top.Y1, top.Z2 + ov);
        int y0 = top.Y1;
        switch (style)
        {
            case "flat":
                for (int x = a.X1; x <= a.X2; x++) for (int z = a.Z1; z <= a.Z2; z++) add(x, y0, z, mat.Id, mat.Meta, mat.Item, null, true);
                return null;
            case "slab":
            {
                var (sid, sm) = SlabFor(d, mat);
                for (int x = a.X1; x <= a.X2; x++) for (int z = a.Z1; z <= a.Z2; z++) add(x, y0, z, sid, sm, null, null, true);
                return null;
            }
            case "gable":
            {
                int sid = StairsFor(d, mat);
                var (slab, slabMeta) = SlabFor(d, mat);
                bool alongZ = a.X2 - a.X1 <= a.Z2 - a.Z1; // the ridge runs along the longer axis; the slopes cross the shorter one
                int lo = alongZ ? a.X1 : a.Z1, hi = alongZ ? a.X2 : a.Z2;
                int l1 = alongZ ? a.Z1 : a.X1, l2 = alongZ ? a.Z2 : a.X2;
                int end1 = alongZ ? top.Z1 : top.X1, end2 = alongZ ? top.Z2 : top.X2; // gable ends: the wall's own ends
                void Put(int across, int y, int along, int id, int meta, string? face) =>
                    add(alongZ ? across : along, y, alongZ ? along : across, id, meta, null, face, true);
                for (int i = 0; lo + i <= hi - i; i++)
                {
                    int y = y0 + i, left = lo + i, right = hi - i;
                    for (int s = l1; s <= l2; s++)
                    {
                        if (left == right) Put(left, y, s, slab, slabMeta, null); // the ridge (odd width)
                        else
                        {
                            Put(left, y, s, sid, alongZ ? StairsMeta("e") : StairsMeta("s"), alongZ ? "e" : "s");
                            Put(right, y, s, sid, alongZ ? StairsMeta("w") : StairsMeta("n"), alongZ ? "w" : "n");
                        }
                    }
                    // Gable ends: wall material under the slopes, between them.
                    for (int c = left + 1; c <= right - 1; c++)
                        foreach (var e in new[] { end1, end2 }.Distinct())
                            Put(c, y, e, wall.Id, wall.Meta, null);
                }
                return null;
            }
            default:
                return "roof style is flat|slab|gable|none";
        }
    }

    /// <summary>
    /// The placement order inside one build: bottom-up (support first), then blocks touching something solid (or an already
    /// placed block) first, then nearest to the pawn.
    /// </summary>
    public static IEnumerable<GhostState> Ordered(IEnumerable<GhostState> todo, Func<P3, bool> solid, P3 from) =>
        todo.OrderBy(t => t.At.Y)
            .ThenBy(t => Supported(t.At, solid) ? 0 : 1)
            .ThenBy(t => t.At.Dist(from));

    public static bool Supported(P3 p, Func<P3, bool> solid) =>
        solid(p.Add(0, -1, 0)) || solid(p.Add(1, 0, 0)) || solid(p.Add(-1, 0, 0)) || solid(p.Add(0, 0, 1)) || solid(p.Add(0, 0, -1)) || solid(p.Add(0, 1, 0));
}
