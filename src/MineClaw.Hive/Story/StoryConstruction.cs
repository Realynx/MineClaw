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
        ["wall", "floor", "roof", "door", "window", "stairs", "fence", "block", "remove", "mine", "water", "crafting_table", "furnace", "chest", "bed", "torch"];

    public static readonly IReadOnlyList<string> Furniture = ["crafting_table", "furnace", "chest", "bed", "torch"];

    /// <summary>"oak planks"/"planks"/"cobblestone" or [id, meta] → the material for this version; null = unknown.</summary>
    public static Material? Resolve(IGameData d, JsonNode? m)
    {
        if (m is JsonArray a && a.Count >= 1) return ResolveState(d, a);
        if (m is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) return null;
        return ResolveName(d, s);
    }

    /// <summary>An explicit block state (a placed design's cell): the item that places exactly it (its variant: red wool, not wool).</summary>
    private static Material? ResolveState(IGameData d, JsonArray a)
    {
        int id = a[0]!.GetValue<int>(), meta = a.Count > 1 ? a[1]!.GetValue<int>() : 0;
        if (!d.ItemExists(id) && !d.BlockExists(id)) return null;
        var (item, _) = PlacingItem(d, id, Math.Max(0, meta));
        return new Material(id, Math.Max(0, meta), item ?? ItemOf(d, id, meta), ItemOf(d, id, meta));
    }

    /// <summary>A material by block name, else by item name (with its damage: "red_wool"), placeable items only.</summary>
    private static Material? ResolveName(IGameData d, string s)
    {
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

    private const string Poured = "liquids are poured from a bucket", Planted = "crops are planted, not placed",
        Cocoa = "cocoa grows on jungle logs", Sign = "a sign asks for its text", EntityLook = "its look is block-entity data";

    /// <summary>Blocks a pawn can't place yet (special placement), and why.</summary>
    private static readonly Dictionary<int, string> SpecialPlacements = new()
    {
        [8] = Poured, [9] = Poured, [10] = Poured, [11] = Poured,
        [59] = Planted, [104] = Planted, [105] = Planted, [115] = Planted, [141] = Planted, [142] = Planted, [207] = Planted,
        [127] = Cocoa,
        [63] = Sign, [68] = Sign,
        [144] = EntityLook, [176] = EntityLook, [177] = EntityLook,
    };

    private static string? SpecialPlacement(int id) => SpecialPlacements.GetValueOrDefault(id);

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
        return VariantItem(d, item, VariantDamage(id, meta));
    }

    /// <summary>The item's damage is the block's variant bits (an anvil's damage is meta >> 2).</summary>
    private static int VariantDamage(int id, int meta) => id == 145 ? (meta & 12) >> 2 : meta & MineClaw.Bot.Blueprints.DesignFacing.VariantMask(id);

    /// <summary>The item's name for this variant: only a name that reads back as this exact variant (else the plain item: never a
    /// different colour by accident).</summary>
    private static (string? Item, string? Why) VariantItem(IGameData d, int item, int damage)
    {
        if (damage == 0) return (d.Item(item).Name, null);
        var name = d.VariantName(item, damage);
        return d.ItemByNameWithDamage(name) is { } back && back.Item.Id == item && back.Damage == damage ? (name, null) : (null, $"no item name for {d.DisplayName(item, damage)}");
    }

    private static Material M(IGameData d, int id, int meta, int? item = null) =>
        new(id, meta, item is { } i && d.ItemExists(i) ? d.Item(i).Name : ItemOf(d, id, meta), ItemOf(d, id, meta));

    private static bool Has(IGameData d, int id) => d.BlockExists(id);

    /// <summary>Material block → its stairs (cobblestone, bricks, stone bricks, sandstone, quartz, nether brick…); planks go by meta.</summary>
    private static readonly Dictionary<int, int> StairsOfBlock = new()
    {
        [4] = 67, [1] = 67, [48] = 67, [45] = 108, [98] = 109, [112] = 114, [24] = 128, [155] = 156, [179] = 180, [201] = 203,
    };

    /// <summary>Planks meta 0..5 → oak, spruce, birch, jungle, acacia, dark oak stairs.</summary>
    private static readonly int[] PlankStairs = [53, 134, 135, 136, 163, 164];

    /// <summary>Stairs blocks: a stairs material is its own stairs.</summary>
    private static readonly HashSet<int> StairBlocks = [53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203];

    /// <summary>The stairs block for a material (oak, spruce… cobblestone, bricks, stone bricks, sandstone, quartz, nether brick).</summary>
    public static int StairsFor(IGameData d, Material m)
    {
        int s = StairsId(m);
        return Has(d, s) ? s : 53;
    }

    private static int StairsId(Material m)
    {
        if (m.Id == 5) return m.Meta is >= 0 and <= 5 ? PlankStairs[m.Meta] : 53;
        return StairBlocks.Contains(m.Id) ? m.Id : StairsOfBlock.GetValueOrDefault(m.Id, 53);
    }

    /// <summary>The half slab for a material (b1.8.1: stone slab metas 0 stone, 1 sandstone, 2 wood, 3 cobble, 4 brick, 5 stone brick).</summary>
    public static (int Id, int Meta) SlabFor(IGameData d, Material m)
    {
        bool modern = Has(d, 126);
        if (m.Id is 44 or 126) return (m.Id, m.Meta);
        if (m.Id == 5) return modern ? (126, m.Meta) : (44, 2);
        return (44, StoneSlabMeta(m.Id, modern));
    }

    /// <summary>A material's stone-slab meta (quartz and nether brick only where the modern slab exists); 0 = plain stone.</summary>
    private static int StoneSlabMeta(int id, bool modern) => id switch
    {
        4 => 3, 24 => 1, 45 => 4, 98 => 5,
        112 when modern => 6, 155 when modern => 7,
        _ => 0,
    };

    /// <summary>Stairs meta that ascends toward a direction (both versions): east 0, west 1, south 2, north 3.</summary>
    public static int StairsMeta(string facing) => facing switch { "e" => 0, "w" => 1, "s" => 2, _ => 3 };
    /// <summary>A door's lower-half facing meta (east 0, south 1, west 2, north 3); the upper half is meta 8.</summary>
    public static int DoorMeta(string facing) => facing switch { "e" => 0, "s" => 1, "w" => 2, _ => 3 };

    /// <summary>One build item as read: its kind, footprint (area and/or explicit blocks), height, facing (n|e|s|w or "") and level.</summary>
    private sealed record ExpandItem(string Kind, JsonObject Json, Area? Area, List<P3>? Blocks, int Height, string Facing, int? Level)
    {
        public IEnumerable<P3> Positions() => Blocks ?? Area!.Value.Blocks();
    }

    /// <summary>The ghosts an item expands to: a later block at a cell replaces an earlier one; nothing below y 0.</summary>
    private sealed class GhostSink
    {
        public readonly List<GhostState> G = new();

        public void Add(int x, int y, int z, int id, int meta, string? itemName, string? face = null, bool job = true)
        {
            if (y < 0) return;
            G.RemoveAll(o => o.At.X == x && o.At.Y == y && o.At.Z == z);
            G.Add(new GhostState { At = new P3(x, y, z), Id = id, Meta = meta, Item = itemName, Facing = face, Job = job });
        }

        /// <summary>The ghost just added at <paramref name="p"/> can't be placed by a pawn yet (alerted, never a job).</summary>
        public void MarkUnsupported(P3 p, string? why)
        {
            if (G.Count > 0 && G[^1].At == p) { G[^1].State = "unsupported"; G[^1].Reason = why; }
        }

        /// <summary>Ghosts to place without an item yet get their block's own item.</summary>
        public void NameItems(IGameData d)
        {
            foreach (var gh in G.Where(x => x.Item == null && !x.Remove)) gh.Item = ItemOf(d, gh.Id, gh.Meta);
        }
    }

    /// <summary>The expanded blocks of one item, or why it can't be expanded.</summary>
    public static (List<GhostState> Ghosts, Material? Mat, string? Error) Expand(IGameData d, JsonObject item, IStoryWorld? world = null)
    {
        var (x, err) = ParseItem(item);
        if (x == null) return ([], null, err);
        // Liquid sources poured from a bucket (docs/web/story-mode.md "Water"): each cell a still source of the item's liquid. A design's
        // water (or lava) source cells are poured like a water item (a flowing cell stays an unsupported ghost).
        if (x.Kind == "water" || IsDesignLiquid(x.Kind, item)) return Liquid(item, x.Positions());
        var (mat, merr) = MaterialFor(d, x.Kind, item);
        if (mat == null) return ([], null, merr);
        var sink = new GhostSink();
        // A placed mine design (docs/web/api.md "Mine designs"): its cells to dig at their absolute y, then the blocks that go in them.
        if (x.Kind == "mine") return Mine(d, item, x.Blocks ?? x.Area?.Blocks() ?? [], world, sink);
        var shapeErr = Shape(d, x, mat, world, sink);
        if (shapeErr != null) return ([], null, shapeErr);
        sink.NameItems(d);
        return (sink.G, mat, sink.G.Count == 0 ? "nothing to build there" : null);
    }

    /// <summary>The item's kind, footprint and shape fields, or why it has none.</summary>
    private static (ExpandItem? Item, string? Error) ParseItem(JsonObject item)
    {
        var kind = (item["kind"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
        if (!Kinds.Contains(kind)) return (null, $"kind must be {string.Join("|", Kinds)}");
        var area = Area.Parse(item["area"]);
        var blocks = item["blocks"] is JsonArray ba ? ba.Select(P3.Parse).Where(p => p != null).Select(p => p!.Value).ToList() : null;
        if (area == null && (blocks == null || blocks.Count == 0) && !IsPlaceOnlyMine(kind, item)) return (null, "area or blocks required");
        int height = Math.Clamp(item["height"]?.GetValue<int>() ?? (kind == "wall" ? 3 : 1), 1, 12);
        string facing = FacingOf(item);
        int? level = item["level"]?.GetValue<int>();
        return (new ExpandItem(kind, item, area, blocks, height, facing, level), null);
    }

    /// <summary>A mine item may carry only blocks to put in (<c>place</c>: the last of a big mine's items).</summary>
    private static bool IsPlaceOnlyMine(string kind, JsonObject item) => kind == "mine" && item["place"] is JsonArray { Count: > 0 };

    private static string FacingOf(JsonObject item) =>
        (item["facing"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant() is { Length: 1 } f && "nesw".Contains(f) ? f : "";

    /// <summary>A design's block item whose material is a liquid source state (id 8–11, meta 0 or none).</summary>
    private static bool IsDesignLiquid(string kind, JsonObject item) =>
        kind == "block" && item["material"] is JsonArray la && IntAt(la, 0) is >= 8 and <= 11 && (la.Count < 2 || IntAt(la, 1) == 0);

    private static int? IntAt(JsonArray a, int i) => i < a.Count && a[i] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;

    /// <summary>The item's material (its kind's default when it names none: a door, a window pane…), or "unknown material".</summary>
    private static (Material? Mat, string? Error) MaterialFor(IGameData d, string kind, JsonObject item)
    {
        var mat = item["material"] is { } mn ? Resolve(d, mn) : null;
        if (item["material"] != null && mat == null && kind != "remove") return (null, "unknown material");
        return (KindMaterial(d, kind, mat ?? DefaultMaterial(d, kind)), null);
    }

    private static Material DefaultMaterial(IGameData d, string kind) => kind switch
    {
        "door" => M(d, 64, 0, 324), "window" => M(d, 102, 0), "fence" => M(d, 85, 0), "torch" => M(d, 50, 0),
        "crafting_table" => M(d, 58, 0), "furnace" => M(d, 61, 0), "chest" => M(d, 54, 0), "bed" => M(d, 26, 0, 355),
        _ => M(d, 5, 0),
    };

    /// <summary>A door is always a door (iron for iron materials, else wood); a bed is always a bed.</summary>
    private static Material KindMaterial(IGameData d, string kind, Material mat)
    {
        if (kind == "door" && mat.Id is not (64 or 71) && !StoryBlocks.IsDoor(mat.Id)) mat = mat.Id == 42 || mat.Name.Contains("iron") ? M(d, 71, 0, 330) : M(d, 64, 0, 324);
        if (kind == "door" && mat.Id == 64) mat = M(d, 64, 0, 324);
        if (kind == "bed") mat = M(d, 26, 0, 355);
        return mat;
    }

    /// <summary>The item's blocks by kind; an error ends the item.</summary>
    private static string? Shape(IGameData d, ExpandItem x, Material mat, IStoryWorld? world, GhostSink g) => x.Kind switch
    {
        "wall" or "fence" => Walls(d, x, mat, g),
        "floor" => Floor(x, mat, g),
        "roof" => RoofItem(d, x, mat, g),
        "door" => Doors(x, mat, g),
        "bed" => Beds(x, mat, g),
        "stairs" => Stairs(d, x, mat, g),
        "remove" => Removals(x, world, g),
        _ => EachBlock(d, x, mat, g), // window, block and furniture: each block
    };

    /// <summary>A wall or fence: the area's perimeter × height (corners included), a wall's roof on top; explicit blocks: columns.</summary>
    private static string? Walls(IGameData d, ExpandItem x, Material mat, GhostSink g)
    {
        if (x.Area is not { } a)
        {
            foreach (var p in x.Positions()) for (int h = 0; h < x.Height; h++) g.Add(p.X, p.Y + h, p.Z, mat.Id, mat.Meta, mat.Item);
            return null;
        }
        Perimeter(a, x.Height, mat, g);
        if (x.Kind == "wall" && x.Json["roof"] is JsonObject roof)
            return Roof(d, g, Area.Of(a.X1, a.Y1 + x.Height, a.Z1, a.X2, a.Y1 + x.Height, a.Z2), roof, mat);
        return null;
    }

    private static void Perimeter(Area a, int height, Material mat, GhostSink g)
    {
        for (int h = 0; h < height; h++)
            for (int x = a.X1; x <= a.X2; x++)
                for (int z = a.Z1; z <= a.Z2; z++)
                    if (x == a.X1 || x == a.X2 || z == a.Z1 || z == a.Z2) g.Add(x, a.Y1 + h, z, mat.Id, mat.Meta, mat.Item);
    }

    private static string? Floor(ExpandItem x, Material mat, GhostSink g)
    {
        foreach (var p in x.Positions()) g.Add(p.X, x.Level ?? p.Y, p.Z, mat.Id, mat.Meta, mat.Item);
        return null;
    }

    private static string? RoofItem(IGameData d, ExpandItem x, Material mat, GhostSink g)
    {
        if (x.Area is not { } a) return "a roof needs an area (its footprint)";
        var spec = x.Json["roof"] as JsonObject ?? new JsonObject { ["style"] = "flat" };
        return Roof(d, g, Area.Of(a.X1, x.Level ?? a.Y2, a.Z1, a.X2, x.Level ?? a.Y2, a.Z2), spec, mat, roofKind: true);
    }

    /// <summary>A door per column (its lowest cell), the upper half with it (never a job of its own).</summary>
    private static string? Doors(ExpandItem x, Material mat, GhostSink g)
    {
        foreach (var p in x.Positions().GroupBy(p => (p.X, p.Z)).Select(c => c.OrderBy(p => p.Y).First()))
        {
            var fc = x.Facing.Length > 0 ? x.Facing : "n";
            g.Add(p.X, p.Y, p.Z, mat.Id, DoorMeta(fc), mat.Item, fc);
            g.Add(p.X, p.Y + 1, p.Z, mat.Id, 8, mat.Item, fc, job: false); // the upper half comes with the door
        }
        return null;
    }

    /// <summary>Up to 8 beds: the foot at each cell, the head one step toward the facing (south by default).</summary>
    private static string? Beds(ExpandItem x, Material mat, GhostSink g)
    {
        foreach (var p in x.Positions().Take(8))
        {
            var fc = x.Facing.Length > 0 ? x.Facing : "s";
            var (dx, dz) = fc switch { "n" => (0, -1), "e" => (1, 0), "w" => (-1, 0), _ => (0, 1) };
            int bm = fc switch { "s" => 0, "w" => 1, "n" => 2, _ => 3 };
            g.Add(p.X, p.Y, p.Z, 26, bm, mat.Item, fc);
            g.Add(p.X + dx, p.Y, p.Z + dz, 26, bm | 8, mat.Item, fc, job: false);
        }
        return null;
    }

    /// <summary>A staircase rising toward its facing (along the area's longer axis by default), one row per step; blocks: as given.</summary>
    private static string? Stairs(IGameData d, ExpandItem x, Material mat, GhostSink g)
    {
        if (x.Area is not { } a)
        {
            foreach (var p in x.Positions()) g.Add(p.X, p.Y, p.Z, StairsFor(d, mat), StairsMeta(x.Facing.Length > 0 ? x.Facing : "n"), null, x.Facing);
            return null;
        }
        var fc = x.Facing.Length > 0 ? x.Facing : (a.X2 - a.X1 >= a.Z2 - a.Z1 ? "e" : "s");
        int steps = Math.Max(1, a.Y2 - a.Y1);
        int sid = StairsFor(d, mat), sm = StairsMeta(fc);
        for (int k = 0; k < steps; k++) StairRow(a, fc, k, sid, sm, g);
        return null;
    }

    /// <summary>Step <paramref name="k"/>: a row across the area, k blocks along and up from where the stairs start.</summary>
    private static void StairRow(Area a, string fc, int k, int sid, int sm, GhostSink g)
    {
        int y = a.Y1 + k;
        if (fc is "e" or "w")
        {
            int x = fc == "e" ? a.X1 + k : a.X2 - k;
            for (int z = a.Z1; z <= a.Z2; z++) g.Add(x, y, z, sid, sm, null, fc);
        }
        else
        {
            int z = fc == "s" ? a.Z1 + k : a.Z2 - k;
            for (int x = a.X1; x <= a.X2; x++) g.Add(x, y, z, sid, sm, null, fc);
        }
    }

    /// <summary>Dig ghosts for what is there now (undiggable blocks left out).</summary>
    private static string? Removals(ExpandItem x, IStoryWorld? world, GhostSink g)
    {
        if (world == null) return "remove needs the world view";
        foreach (var p in x.Positions())
        {
            var (id, meta) = world.Block(p.X, p.Y, p.Z);
            if (StoryBlocks.Undiggable.Contains(id)) continue;
            g.G.Add(new GhostState { At = p, Id = id, Meta = meta, Remove = true });
        }
        return null;
    }

    /// <summary>
    /// A window, block or furniture: each block. A design's explicit state: a second half (a door's upper, a bed's head, a tall plant's
    /// top) comes with its first; a block pawns can't place yet is kept as an unsupported ghost (alerted, never a job).
    /// </summary>
    private static string? EachBlock(IGameData d, ExpandItem x, Material mat, GhostSink g)
    {
        bool second = x.Kind == "block" && MineClaw.Bot.Blueprints.DesignFacing.SecondHalf(mat.Id, mat.Meta);
        var (placing, why) = x.Kind == "block" ? PlacingItem(d, mat.Id, mat.Meta) : (mat.Item, null);
        // The Architect's turn (furniture, a single block): the meta faces that way, so the hologram and the pawn agree.
        int meta = Faced(mat.Id, mat.Meta, x.Facing);
        foreach (var p in x.Positions())
        {
            g.Add(p.X, p.Y, p.Z, mat.Id, meta, placing ?? mat.Item, x.Facing.Length > 0 ? x.Facing : null, job: !second && placing != null);
            if (!second && placing == null) g.MarkUnsupported(p, why);
        }
        return null;
    }

    /// <summary>
    /// A block's meta turned to an item's <c>facing</c> word (n|e|s|w; empty = as it is), in the family's own sense (DesignFacing): a
    /// furnace's or chest's front, the way stairs rise, a bed's foot → head, a pumpkin's face. A piston, dispenser or hopper pointing
    /// up or down (meta 0/1) turns to face that way too. A floor torch, an upright log, a block with no facing: unchanged.
    /// </summary>
    public static int Faced(int id, int meta, string facing)
    {
        if (MineClaw.Bot.Blueprints.Dirs.Parse(facing) is not { } dir) return meta;
        var fam = MineClaw.Bot.Blueprints.DesignFacing.FamilyOf(id);
        if (fam is MineClaw.Bot.Blueprints.DesignFamily.Facing6 or MineClaw.Bot.Blueprints.DesignFamily.Hopper && (meta & 7) is 0 or 1)
            return (meta & 8) | (dir switch { MineClaw.Bot.Blueprints.Dir.North => 2, MineClaw.Bot.Blueprints.Dir.South => 3, MineClaw.Bot.Blueprints.Dir.West => 4, _ => 5 });
        return MineClaw.Bot.Blueprints.DesignFacing.HasFacing(fam) ? MineClaw.Bot.Blueprints.DesignFacing.WithFacing(id, meta, dir) : meta;
    }

    /// <summary>A liquid's names (block, flowing, still, bucket) → the liquid.</summary>
    private static readonly Dictionary<string, string> LiquidNames = new()
    {
        ["water"] = "water", ["still_water"] = "water", ["flowing_water"] = "water", ["water_bucket"] = "water",
        ["lava"] = "lava", ["still_lava"] = "lava", ["flowing_lava"] = "lava", ["lava_bucket"] = "lava",
    };

    /// <summary>The liquid a water item pours: its <c>material</c> "water" (default) or "lava" (also by block id 8–11); null = neither.</summary>
    public static string? LiquidOf(JsonObject item) => item["material"] switch
    {
        null => "water",
        JsonArray a => IntAt(a, 0) is { } id ? LiquidById(id) : null,
        JsonValue v when v.TryGetValue<string>(out var s) => LiquidNames.GetValueOrDefault(s.Trim().ToLowerInvariant()),
        _ => null,
    };

    private static string? LiquidById(int id) => id is 8 or 9 ? "water" : id is 10 or 11 ? "lava" : null;

    /// <summary>Is this ghost a liquid source to pour (water 8/9, lava 10/11)?</summary>
    public static bool IsLiquid(GhostState g) => !g.Remove && g.Id is >= 8 and <= 11;

    /// <summary>A water item: each cell a still source (water 9 / lava 11, meta 0) poured from a full bucket (its item).</summary>
    private static (List<GhostState>, Material?, string?) Liquid(JsonObject item, IEnumerable<P3> cells)
    {
        var liquid = LiquidOf(item);
        if (liquid == null) return ([], null, "a water item's material is water|lava");
        int id = liquid == "lava" ? 11 : 9;
        string bucket = liquid + "_bucket";
        var g = new List<GhostState>();
        var seen = new HashSet<P3>();
        foreach (var p in cells)
        {
            if (p.Y < 1) return ([], null, $"{p} is below y 1");
            if (seen.Add(p)) g.Add(new GhostState { At = p, Id = id, Meta = 0, Item = bucket });
        }
        return (g, new Material(id, 0, bucket, liquid), g.Count == 0 ? "nothing to pour there" : null);
    }

    /// <summary>
    /// A mine job group's item (<c>kind: "mine"</c>): <c>blocks</c> = the cells to dig, each a dig ghost carrying the live world's
    /// block there (its expected material, for the tools that dig it; a column not in view yet: 0 = unknown); <c>place</c> =
    /// <c>[[x,y,z,id,meta]]</c>, the blocks put in once their cell is open (torches, ladders, rails…). Refused when a cell is below y 1
    /// or would cut bedrock. Air (a cave) needs nothing; liquids, fire and portals are left alone.
    /// </summary>
    private static (List<GhostState>, Material?, string?) Mine(IGameData d, JsonObject item, IEnumerable<P3> dig, IStoryWorld? world, GhostSink g)
    {
        if (world == null) return ([], null, "a mine needs the world view");
        var err = MineDig(dig, world, g) ?? MinePlace(d, item, g);
        if (err != null) return ([], null, err);
        return (g.G, null, g.G.Count == 0 ? "nothing to dig there (already open)" : null);
    }

    private static string? MineDig(IEnumerable<P3> dig, IStoryWorld world, GhostSink g)
    {
        foreach (var p in dig)
        {
            if (p.Y < 1) return $"the mine reaches y {p.Y} at {p}: a mine can't dig below y 1";
            bool known = world.Loaded(p.X, p.Z);
            var (id, meta) = known ? world.Block(p.X, p.Y, p.Z) : (0, 0);
            if (known && id == 7) return $"the mine would cut bedrock at {p}: place it higher";
            if (known && StoryBlocks.Undiggable.Contains(id)) continue;
            g.G.RemoveAll(o => o.At == p);
            g.G.Add(new GhostState { At = p, Id = id, Meta = meta, Remove = true });
        }
        return null;
    }

    private static string? MinePlace(IGameData d, JsonObject item, GhostSink g)
    {
        if (item["place"] is not JsonArray place) return null;
        foreach (var n in place)
            if (MinePlaceOne(d, n, g) is { } err) return err;
        return null;
    }

    /// <summary>One <c>[x,y,z,id,meta]</c> block put in once its cell is open.</summary>
    private static string? MinePlaceOne(IGameData d, JsonNode? n, GhostSink g)
    {
        if (n is not JsonArray c || c.Count < 4) return "place is [[x,y,z,id,meta]]";
        int x = c[0]!.GetValue<int>(), y = c[1]!.GetValue<int>(), z = c[2]!.GetValue<int>(), id = c[3]!.GetValue<int>(), meta = c.Count > 4 ? c[4]!.GetValue<int>() : 0;
        if (y < 1) return $"the mine places a block at y {y}: a mine can't go below y 1";
        if (!d.BlockExists(id)) return $"no block {id} in {d.VersionId}";
        bool second = MineClaw.Bot.Blueprints.DesignFacing.SecondHalf(id, meta);
        var (placing, why) = PlacingItem(d, id, meta);
        g.Add(x, y, z, id, meta, placing ?? ItemOf(d, id, meta), null, !second && placing != null);
        if (!second && placing == null) g.MarkUnsupported(new P3(x, y, z), why);
        return null;
    }

    /// <summary>A roof over <paramref name="top"/> (its y = the roof's base level): flat | slab | gable | none, overhang 0|1.</summary>
    private static string? Roof(IGameData d, GhostSink g, Area top, JsonObject spec, Material wall, bool roofKind = false)
    {
        var style = (spec["style"]?.GetValue<string>() ?? "flat").Trim().ToLowerInvariant();
        if (style == "none") return null;
        var mat = spec["material"] is { } rm ? Resolve(d, rm) : wall;
        if (mat == null) return "unknown roof material";
        int ov = Math.Clamp(spec["overhang"]?.GetValue<int>() ?? 0, 0, 1);
        var a = Area.Of(top.X1 - ov, top.Y1, top.Z1 - ov, top.X2 + ov, top.Y1, top.Z2 + ov);
        switch (style)
        {
            case "flat":
                Layer(a, top.Y1, mat.Id, mat.Meta, mat.Item, g);
                return null;
            case "slab":
                var (sid, sm) = SlabFor(d, mat);
                Layer(a, top.Y1, sid, sm, null, g);
                return null;
            case "gable":
                Gable(d, g, a, top, mat, wall);
                return null;
            default:
                return "roof style is flat|slab|gable|none";
        }
    }

    /// <summary>One block over the whole area at <paramref name="y"/>.</summary>
    private static void Layer(Area a, int y, int id, int meta, string? item, GhostSink g)
    {
        for (int x = a.X1; x <= a.X2; x++) for (int z = a.Z1; z <= a.Z2; z++) g.Add(x, y, z, id, meta, item, null, true);
    }

    /// <summary>A gable's axes: the ridge runs along the longer axis (z when the area is no wider in x), the slopes cross the shorter one.
    /// <c>Lo..Hi</c> across, <c>L1..L2</c> along, the gable ends at the wall's own ends.</summary>
    private readonly record struct GableAxes(bool AlongZ, int Lo, int Hi, int L1, int L2, int End1, int End2)
    {
        public static GableAxes Of(Area a, Area top) => a.X2 - a.X1 <= a.Z2 - a.Z1
            ? new(true, a.X1, a.X2, a.Z1, a.Z2, top.Z1, top.Z2)
            : new(false, a.Z1, a.Z2, a.X1, a.X2, top.X1, top.X2);

        /// <summary>The way the low-side slope rises (toward the ridge), and the high side's.</summary>
        public string LowFace => AlongZ ? "e" : "s";
        public string HighFace => AlongZ ? "w" : "n";

        public void Put(GhostSink g, int across, int y, int along, int id, int meta, string? face) =>
            g.Add(AlongZ ? across : along, y, AlongZ ? along : across, id, meta, null, face, true);
    }

    /// <summary>Stair rows meeting at a ridge (a slab when the width is odd), the gable ends filled with the wall material.</summary>
    private static void Gable(IGameData d, GhostSink g, Area a, Area top, Material mat, Material wall)
    {
        int sid = StairsFor(d, mat);
        var (slab, slabMeta) = SlabFor(d, mat);
        var ax = GableAxes.Of(a, top);
        for (int i = 0; ax.Lo + i <= ax.Hi - i; i++)
        {
            int y = top.Y1 + i, left = ax.Lo + i, right = ax.Hi - i;
            for (int s = ax.L1; s <= ax.L2; s++)
            {
                if (left == right) ax.Put(g, left, y, s, slab, slabMeta, null); // the ridge (odd width)
                else
                {
                    ax.Put(g, left, y, s, sid, StairsMeta(ax.LowFace), ax.LowFace);
                    ax.Put(g, right, y, s, sid, StairsMeta(ax.HighFace), ax.HighFace);
                }
            }
            GableEnds(g, ax, left, right, y, wall);
        }
    }

    /// <summary>Gable ends: wall material under the slopes, between them.</summary>
    private static void GableEnds(GhostSink g, GableAxes ax, int left, int right, int y, Material wall)
    {
        for (int c = left + 1; c <= right - 1; c++)
            foreach (var e in new[] { ax.End1, ax.End2 }.Distinct())
                ax.Put(g, c, y, e, wall.Id, wall.Meta, null);
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
