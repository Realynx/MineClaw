using System.Globalization;
using System.Text;
using MineClaw.Bot.Blueprints.Mine;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Designs;

namespace MineClaw.Bot.Blueprints;

/// <summary>One cell change on the Design Studio canvas: the new state (id 0 = air, below ground: dug out; id
/// <see cref="DesignerCanvas.Earth"/> = undisturbed ground again, below ground only).</summary>
public readonly record struct CanvasCell(int X, int Y, int Z, int Id, int Meta)
{
    public int[] Wire => [X, Y, Z, Id, Meta];
}

/// <summary>
/// The Design Studio's sandbox canvas (docs/web/api.md "Design Studio"): the AI's head, never the live world. A 64×64 plot,
/// local coordinates x, z 0..63 (+x east, +z south) and y -16..47: y 0 is the first layer above the plot's grass, y -1 the
/// grass layer itself and y -16..-1 the ground under it (solid earth until it is dug: a basement, a cellar, a foundation). It
/// holds blocks in the designs system's own format (<see cref="DesignSnapshot"/>: its <see cref="Version"/>'s ids and metas) and the
/// dug-out cells below ground (air the build must dig), so a save is an ordinary design (<see cref="DesignerSave"/>: the dug
/// cells drawn '.', the depth as <c>ground N</c>). Every state of the version's design palette is accepted (<see cref="DesignPalette"/>);
/// two-cell blocks are kept whole (<see cref="DesignFacing.Partner"/>): a door or a double plant as its two halves (upper = lower
/// | 8, as the normalizer draws a door), a bed as foot + head (head = foot | 8, one step along its facing); a chest placed beside
/// a single chest takes its facing (they pair, as the game pairs them).
/// <para>A <b>mine</b> canvas (<see cref="Kind"/> "mine", docs/web/api.md "Mine designs") is the same 64×64 plot over generated
/// terrain (<see cref="MineTerrain"/> of its <see cref="Seed"/> and version) at absolute world y 0..127: a cell is undisturbed
/// ground where that terrain is solid; air there digs it (never bedrock), earth fills it back; blocks go anywhere.</para>
/// </summary>
public sealed class DesignerCanvas
{
    public const string KindBuild = "build", KindMine = "mine";

    /// <summary>"build" (a grassland plot, y relative to the grass) or "mine" (generated terrain, absolute y).</summary>
    public string Kind { get; set; } = KindBuild;

    public bool Mine => Kind == KindMine;

    /// <summary>A mine canvas's terrain seed (text; a whole number as it is).</summary>
    public string Seed { get; set; } = "0";

    /// <summary>A mine canvas's height (absolute y 0..127).</summary>
    public const int MineHeight = MineTerrain.Height;

    /// <summary>A mine canvas's generated terrain (null on a build canvas).</summary>
    public MineTerrain? Terrain => Mine ? MineTerrain.For(Seed, Version) : null;

    /// <summary>The y range of this canvas's kind.</summary>
    public (int Min, int Max) YRange => Mine ? (0, MineHeight - 1) : (-Depth, Height - 1);

    /// <summary>Inside the plot for this canvas's kind.</summary>
    public bool Contains(int x, int y, int z) => Mine ? x is >= 0 and < Size && z is >= 0 and < Size && y >= 0 && y < MineHeight : Inside(x, y, z);

    /// <summary>Ground that is earth until dug: below the grass (build), solid generated terrain (mine).</summary>
    public bool Ground(int x, int y, int z) => Mine ? Terrain!.Solid(x, y, z) : Underground(y);

    /// <summary>Generated bedrock (a mine canvas never digs it).</summary>
    private bool Bedrock(int x, int y, int z) => Mine && Terrain!.Block(x, y, z).Id == MineTerrain.Bedrock;
    /// <summary>The game version the canvas is drawn for ("b1.8.1" / "1.12.2"; default: the server's).</summary>
    public string Version { get; set; } = DesignVersions.Server;

    /// <summary>The design palette of <see cref="Version"/>.</summary>
    public DesignPalette Palette => DesignPalette.For(Version);

    public const int Size = 64, Height = 48;
    /// <summary>Layers of ground under the grass the canvas reaches (y -Depth..-1).</summary>
    public const int Depth = 16;
    /// <summary>The wire id of undisturbed ground (a dug cell filled back in); below ground a missing cell is earth, above it air.</summary>
    public const int Earth = -1;
    /// <summary>Cells one op may change (a full plot fill is refused: a design holds at most 32³).</summary>
    public const int MaxCellsPerOp = 40_000;

    private readonly Dictionary<BlockPos, (int Id, int Meta)> _blocks = new();
    private readonly HashSet<BlockPos> _dug = new();

    public IReadOnlyDictionary<BlockPos, (int Id, int Meta)> Blocks => _blocks;
    /// <summary>Cells below ground dug out to air (a block standing there is in <see cref="Blocks"/>, never here).</summary>
    public IReadOnlyCollection<BlockPos> Dug => _dug;
    public int Count => _blocks.Count;
    /// <summary>Bumped by every applied change set (the browser's optimistic edits carry it).</summary>
    public int Rev { get; set; }

    public static bool Inside(int x, int y, int z) => x is >= 0 and < Size && z is >= 0 and < Size && y >= -Depth && y < Height;

    /// <summary>Below the grass's top: the ground (earth unless dug).</summary>
    public static bool Underground(int y) => y < 0;

    public (int Id, int Meta) Get(int x, int y, int z) => _blocks.TryGetValue(new BlockPos(x, y, z), out var s) ? s : (0, 0);

    /// <summary>Undisturbed ground: below the grass's top, no block and not dug.</summary>
    public bool IsEarth(int x, int y, int z)
    {
        var p = new BlockPos(x, y, z);
        return Ground(x, y, z) && !_blocks.ContainsKey(p) && !_dug.Contains(p);
    }

    /// <summary>Why (id, meta) can't stand on a canvas of the version in this flow (null = it can): air and earth always can, else a
    /// design-palette state.</summary>
    public static string? Refusal(int id, int meta) => id is 0 or Earth ? null : DesignPalette.Current.Refusal(id, meta);

    /// <summary>Why (id, meta) can't stand on this canvas (its <see cref="Version"/>).</summary>
    public string? RefusalOf(int id, int meta) => id is 0 or Earth ? null : Palette.Refusal(id, meta);

    /// <summary>
    /// Applies cell states in order (later wins) and returns the cells that really changed, with door halves kept paired. Air
    /// below ground digs the cell out; <see cref="Earth"/> fills it back (above ground it is air). Cells outside the plot or not
    /// in the design palette are skipped and counted in <paramref name="rejected"/>.
    /// </summary>
    public List<CanvasCell> Apply(IEnumerable<CanvasCell> cells, out int rejected)
    {
        rejected = 0;
        var changed = new Dictionary<BlockPos, CanvasCell>();
        int n = 0;
        foreach (var c0 in cells)
        {
            if (++n > MaxCellsPerOp || Accepted(c0) is not { } c) { rejected++; continue; }
            Set(c, changed);
            // A drawn first half brings its second (a door's upper half, a bed's head); a second half alone is accepted as drawn
            // (the browser sends both).
            if (c.Id > 0 && DesignFacing.Partner(c.Id, c.Meta) is { } part && Contains(c.X + part.Dx, c.Y + part.Dy, c.Z + part.Dz))
                Set(new CanvasCell(c.X + part.Dx, c.Y + part.Dy, c.Z + part.Dz, c.Id, part.Meta), changed);
        }
        // Wall torches and ladders hang on their wall: the changed ones and the ones next to a changed cell (a wall placed after
        // its torch) are turned to the solid side when theirs is open.
        LastFixed = FixAttachments(Near(changed.Keys), changed);
        if (changed.Count > 0) Rev++;
        return changed.Values.ToList();
    }

    /// <summary>A cell as it is set (air or earth by the ground there, a chest paired with its neighbour), or null when it is refused
    /// (outside the plot, not in the design palette, a mine's bedrock).</summary>
    private CanvasCell? Accepted(CanvasCell c0)
    {
        if (!Contains(c0.X, c0.Y, c0.Z)) return null;
        var c = c0.Id is 0 or Earth ? Grounded(c0) : c0;
        if (RefusalOf(c.Id, c.Meta) != null) return null;
        // A mine never digs bedrock.
        if (c.Id == 0 && Bedrock(c.X, c.Y, c.Z) && !_blocks.ContainsKey(new BlockPos(c.X, c.Y, c.Z))) return null;
        return c.Id > 0 && Pair(c) is { } paired ? paired : c;
    }

    /// <summary>Air or earth: earth only where there is ground (above it, air).</summary>
    private CanvasCell Grounded(CanvasCell c) => c with { Id = c.Id == Earth && Ground(c.X, c.Y, c.Z) ? Earth : 0, Meta = 0 };

    /// <summary>The cells and their neighbours (above and the four sides): where an attached block may have lost or gained its wall.</summary>
    private static HashSet<BlockPos> Near(IEnumerable<BlockPos> cells)
    {
        var near = new HashSet<BlockPos>();
        foreach (var p in cells)
        {
            near.Add(p);
            near.Add(p.Up());
            foreach (var (dx, dz) in Sides) near.Add(new BlockPos(p.X + dx, p.Y, p.Z + dz));
        }
        return near;
    }

    public List<CanvasCell> Apply(IEnumerable<CanvasCell> cells) => Apply(cells, out _);

    /// <summary>The attached blocks the last <see cref="Apply"/> turned to their wall (or put on the floor), as their new states.</summary>
    public List<CanvasCell> LastFixed { get; private set; } = [];

    private static readonly (int Dx, int Dz)[] Sides = [(-1, 0), (1, 0), (0, -1), (0, 1)];

    /// <summary>The side (dx, dz) of the block a wall torch (1..4) or a ladder (2..5) hangs on; null for anything else (a floor torch).</summary>
    public static (int Dx, int Dz)? WallOf(int id, int meta) =>
        IsTorch(id) ? TorchWall(meta) : id == MineClaw.Data.Blocks.Ladder ? LadderWall(meta) : null;

    private static (int Dx, int Dz)? TorchWall(int meta) => meta switch { 1 => (-1, 0), 2 => (1, 0), 3 => (0, -1), 4 => (0, 1), _ => null };

    private static (int Dx, int Dz)? LadderWall(int meta) => meta switch { 2 => (0, 1), 3 => (0, -1), 4 => (1, 0), 5 => (-1, 0), _ => null };

    /// <summary>A torch or a redstone torch (the same attachment rule).</summary>
    private static bool IsTorch(int id) => id is MineClaw.Data.Blocks.Torch or 76;

    /// <summary>The meta that hangs a torch or a ladder on the block at side (dx, dz).</summary>
    public static int MetaOn(int id, (int Dx, int Dz) wall) => IsTorch(id)
        ? wall switch { (-1, 0) => 1, (1, 0) => 2, (0, -1) => 3, _ => 4 }
        : wall switch { (0, 1) => 2, (0, -1) => 3, (1, 0) => 4, _ => 5 };

    /// <summary>A full block a torch or a ladder can hang on (a block on the canvas, or undisturbed ground below the grass).</summary>
    public bool Holds(int x, int y, int z)
    {
        if (y < YRange.Min) return true;
        if (!Contains(x, y, z)) return false;
        if (IsEarth(x, y, z)) return true;
        var (id, _) = Get(x, y, z);
        return BuildBlocks.NormalCube(id);
    }

    /// <summary>
    /// The attachment rule (mc-dev BlockTorch/BlockLadder canPlaceBlockAt): a wall torch or a ladder whose wall side is open but
    /// whose opposite side is solid is turned around (the usual mistake: the side named as the torch's, not its wall's); a wall
    /// torch with neither but a solid block under it stands on the floor (meta 5); anything else is left for the design checks
    /// ("torch … has no wall or floor"). Returns the corrected cells (also recorded in <paramref name="changed"/>).
    /// </summary>
    private List<CanvasCell> FixAttachments(IEnumerable<BlockPos> cells, Dictionary<BlockPos, CanvasCell>? changed)
    {
        var fixedCells = new List<CanvasCell>();
        foreach (var p in cells)
        {
            if (!_blocks.TryGetValue(p, out var st) || WallOf(st.Id, st.Meta) is not { } wall) continue;
            if (Holds(p.X + wall.Dx, p.Y, p.Z + wall.Dz)) continue;
            int meta;
            if (Holds(p.X - wall.Dx, p.Y, p.Z - wall.Dz)) meta = MetaOn(st.Id, (-wall.Dx, -wall.Dz));
            else if (IsTorch(st.Id) && Holds(p.X, p.Y - 1, p.Z)) meta = 5;
            else continue;
            _blocks[p] = (st.Id, meta);
            var c = new CanvasCell(p.X, p.Y, p.Z, st.Id, meta);
            if (changed != null) changed[p] = c;
            fixedCells.Add(c);
        }
        return fixedCells;
    }

    /// <summary>
    /// Every attached block on the canvas checked against the rule (a session saved before it existed, opened again): the
    /// corrected cells, the canvas revision bumped when any. Not an edit of the user's: it is how the build would stand.
    /// </summary>
    public List<CanvasCell> FixAllAttachments()
    {
        var fixedCells = FixAttachments(_blocks.Keys.ToList(), null);
        if (fixedCells.Count > 0) Rev++;
        return fixedCells;
    }

    /// <summary>"turned 3 wall torches to face their wall, put 1 on the floor" (empty when nothing was fixed).</summary>
    public static string FixText(IReadOnlyCollection<CanvasCell> fixedCells)
    {
        if (fixedCells.Count == 0) return "";
        int floor = fixedCells.Count(c => IsTorch(c.Id) && c.Meta == 5);
        int torches = fixedCells.Count(c => IsTorch(c.Id) && c.Meta != 5), ladders = fixedCells.Count(c => c.Id == MineClaw.Data.Blocks.Ladder);
        var parts = new List<string>();
        if (torches > 0) parts.Add($"turned {torches} wall torch{(torches == 1 ? "" : "es")} around to hang on the solid side");
        if (ladders > 0) parts.Add($"turned {ladders} ladder{(ladders == 1 ? "" : "s")} around to the solid side");
        if (floor > 0) parts.Add($"stood {floor} torch{(floor == 1 ? "" : "es")} with no wall on the floor");
        return string.Join(", ", parts);
    }

    /// <summary>A cell's state as a wire id: a block, air (0) or, below ground, undisturbed earth.</summary>
    private (int Id, int Meta) State(BlockPos p) =>
        _blocks.TryGetValue(p, out var o) ? o : Ground(p.X, p.Y, p.Z) && !_dug.Contains(p) ? (Earth, 0) : (0, 0);

    /// <summary>
    /// A chest placed beside a single chest of the same kind takes its facing when theirs is across the pair (the game pairs them
    /// that way: ItemBlock/BlockChest.onBlockPlacedBy); null = nothing to pair.
    /// </summary>
    private CanvasCell? Pair(CanvasCell c)
    {
        if (c.Id is not (54 or 146)) return null;
        foreach (var (dx, dz) in Sides)
        {
            var n = new BlockPos(c.X + dx, c.Y, c.Z + dz);
            if (!_blocks.TryGetValue(n, out var o) || o.Id != c.Id || DesignFacing.FacingOf(o.Id, o.Meta) is not { } f) continue;
            // Already paired with another chest: a third never joins.
            if (PairedElsewhere(n, dx, dz, c.Id)) continue;
            return Across(dx, f) && c.Meta != o.Meta ? c with { Meta = o.Meta } : null;
        }
        return null;
    }

    /// <summary>The chest at <paramref name="n"/> already has a partner on another side than the one at (−dx, −dz).</summary>
    private bool PairedElsewhere(BlockPos n, int dx, int dz, int id) =>
        Sides.Any(s => (s.Dx, s.Dz) != (-dx, -dz) && _blocks.TryGetValue(new BlockPos(n.X + s.Dx, n.Y, n.Z + s.Dz), out var t) && t.Id == id);

    /// <summary>A chest's facing runs across a pair joined along x (dx ≠ 0) when it faces north or south, along z when east or west.</summary>
    private static bool Across(int dx, Dir f) => dx != 0 ? f is Dir.North or Dir.South : f is Dir.East or Dir.West;

    private void Set(CanvasCell c, Dictionary<BlockPos, CanvasCell> changed)
    {
        var p = new BlockPos(c.X, c.Y, c.Z);
        var old = State(p);
        if (old == (c.Id, c.Meta)) return;
        // Replacing one half of a two-cell block (a door, a double plant, a bed) drops the other half (never left half standing).
        if (old.Id > 0 && DesignFacing.TwoCell(old.Item1) && c.Id != old.Item1) DropOtherHalf(p, old, changed);
        _blocks.Remove(p);
        _dug.Remove(p);
        if (c.Id > 0) _blocks[p] = (c.Id, c.Meta);
        else if (c.Id == 0 && Diggable(p)) _dug.Add(p);
        changed[p] = c;
    }

    /// <summary>The other half of the two-cell block at <paramref name="p"/> (state <paramref name="old"/>) removed with it.</summary>
    private void DropOtherHalf(BlockPos p, (int Id, int Meta) old, Dictionary<BlockPos, CanvasCell> changed)
    {
        BlockPos? other = DesignFacing.FirstHalf(old.Id, old.Meta) is { } f ? new BlockPos(p.X + f.Dx, p.Y + f.Dy, p.Z + f.Dz)
            : DesignFacing.Partner(old.Id, old.Meta) is { } s ? new BlockPos(p.X + s.Dx, p.Y + s.Dy, p.Z + s.Dz) : null;
        if (other is not { } o || !_blocks.TryGetValue(o, out var os) || os.Id != old.Id) return;
        _blocks.Remove(o);
        if (Diggable(o)) _dug.Add(o);
        changed[o] = new CanvasCell(o.X, o.Y, o.Z, 0, 0);
    }

    /// <summary>Ground that air there digs out (never a mine's bedrock).</summary>
    private bool Diggable(BlockPos p) => Ground(p.X, p.Y, p.Z) && !Bedrock(p.X, p.Y, p.Z);

    /// <summary>Blocks and dug cells set as they are (a converted canvas: already whole, never re-paired or re-checked).</summary>
    public void Load(IEnumerable<KeyValuePair<BlockPos, (int Id, int Meta)>> blocks, IEnumerable<BlockPos> dug)
    {
        foreach (var (p, s) in blocks)
            if (Contains(p.X, p.Y, p.Z) && s.Id > 0) _blocks[p] = s;
        // A mine keeps its dug cells as they are (its design data, whatever the terrain under them now).
        foreach (var p in dug)
            if ((Mine || Underground(p.Y)) && Contains(p.X, p.Y, p.Z) && !_blocks.ContainsKey(p)) _dug.Add(p);
    }

    /// <summary>Every block removed and every dug cell filled back (the canvas cleared: a flat grassland again).</summary>
    public List<CanvasCell> Clear()
    {
        var changed = Apply(_blocks.Keys.Concat(_dug).ToList().Select(p => new CanvasCell(p.X, p.Y, p.Z, Ground(p.X, p.Y, p.Z) ? Earth : 0, 0)));
        // A mine's dug cells kept over what is no longer ground (another seed) go too.
        if (_dug.Count > 0)
        {
            changed.AddRange(_dug.Select(p => new CanvasCell(p.X, p.Y, p.Z, Earth, 0)));
            _dug.Clear();
            Rev++;
        }
        return changed;
    }

    /// <summary>The box of the blocks and the dug cells, or null when the canvas is empty.</summary>
    public (BlockPos Min, BlockPos Max)? Bounds
    {
        get
        {
            if (_blocks.Count == 0 && _dug.Count == 0) return null;
            int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
            foreach (var p in _blocks.Keys.Concat(_dug))
            {
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
            }
            return (new BlockPos(x0, y0, z0), new BlockPos(x1, y1, z1));
        }
    }

    /// <summary>Every block as wire cells [x, y, z, id, meta] (y, z, x order: stable).</summary>
    public List<int[]> Wire() =>
        _blocks.OrderBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.Z).ThenBy(kv => kv.Key.X).Select(kv => new[] { kv.Key.X, kv.Key.Y, kv.Key.Z, kv.Value.Id, kv.Value.Meta }).ToList();

    /// <summary>The dug cells as [x, y, z] (y, z, x order).</summary>
    public List<int[]> DugWire() => _dug.OrderBy(p => p.Y).ThenBy(p => p.Z).ThenBy(p => p.X).Select(p => new[] { p.X, p.Y, p.Z }).ToList();

    // ------------------------------------------------------------------ persistence (the designs system's own encoding)

    /// <summary>The blocks as state runs, the dug cells as the air state "0:0" (a line no block set ever writes).</summary>
    public string Encode()
    {
        if (_dug.Count == 0) return DesignSnapshot.EncodeStates(_blocks);
        var all = new Dictionary<BlockPos, (int Id, int Meta)>(_blocks);
        foreach (var p in _dug) all[p] = (0, 0);
        return DesignSnapshot.EncodeStates(all);
    }

    public static DesignerCanvas Decode(string? states, int rev = 0, string? kind = null, string? seed = null, string? version = null)
    {
        var c = new DesignerCanvas { Rev = rev, Kind = kind == KindMine ? KindMine : KindBuild, Seed = MineTerrain.NormalizeSeed(seed) };
        if (DesignVersions.Normalize(version) is { } v) c.Version = v;
        foreach (var kv in DesignSnapshot.Decode(states, null).Blocks)
        {
            if (!c.Contains(kv.Key.X, kv.Key.Y, kv.Key.Z)) continue;
            if (kv.Value.Id > 0) c._blocks[kv.Key] = kv.Value;
            // Only dug cells are stored as air.
            else if (kv.Value.Id == 0 && (c.Mine || Underground(kv.Key.Y))) c._dug.Add(kv.Key);
        }
        return c;
    }

    /// <summary>The canvas as a design block set (local coordinates as they are; the dug cells as its air).</summary>
    public DesignSnapshot ToSnapshot()
    {
        var s = new DesignSnapshot();
        foreach (var kv in _blocks) s.Blocks[kv.Key] = kv.Value;
        s.Air.UnionWith(_dug);
        return s;
    }

    /// <summary>
    /// A design revision's block set on a fresh canvas: its box centred on the plot, its <paramref name="gradeY"/> (the y of its
    /// first layer above the ground: <c>ground N</c> layers over its bottom) on y = 0, else its lowest block standing on the
    /// grass; its air below that y is the dug ground. Returns the offset added to every position (design → canvas), so a save
    /// can put it back where the design stands.
    /// </summary>
    public static (DesignerCanvas Canvas, BlockPos Offset) FromSnapshot(DesignSnapshot snap, int? gradeY = null)
    {
        var c = new DesignerCanvas();
        if (snap.Box is not { } box) return (c, default);
        int w = box.Max.X - box.Min.X + 1, d = box.Max.Z - box.Min.Z + 1;
        var off = new BlockPos(Size / 2 - w / 2 - box.Min.X, -(gradeY ?? box.Min.Y), Size / 2 - d / 2 - box.Min.Z);
        foreach (var kv in snap.Blocks)
        {
            var p = new BlockPos(kv.Key.X + off.X, kv.Key.Y + off.Y, kv.Key.Z + off.Z);
            if (Inside(p.X, p.Y, p.Z)) c._blocks[p] = kv.Value;
        }
        if (gradeY != null)
            foreach (var a in snap.Air)
            {
                var p = new BlockPos(a.X + off.X, a.Y + off.Y, a.Z + off.Z);
                if (Underground(p.Y) && Inside(p.X, p.Y, p.Z) && !c._blocks.ContainsKey(p)) c._dug.Add(p);
            }
        return (c, off);
    }

    // ------------------------------------------------------------------ what the AI reads

    /// <summary>The palette spelling of a state in the version of this flow ("planks", "wool:red", "oak_stairs:2"), as the AI's
    /// tools accept it (a second half is spelled as its first: the tools add the second).</summary>
    public static string Spec(int id, int meta) => DesignPalette.Current.Spec(id, SecondFirst(id, meta));

    /// <summary>The first half's meta of a second half (a door's upper → its lower), else the meta.</summary>
    private static int SecondFirst(int id, int meta) => DesignFacing.SecondHalf(id, meta) ? meta & (DesignFacing.FamilyOf(id) == DesignFamily.Bed ? 3 : 7) : meta;

    /// <summary>A state's display name in the version of this flow.</summary>
    private static string NameOf(int id, int meta) => DesignPalette.Current.Name(id, meta).ToLowerInvariant();

    /// <summary>
    /// The compact summary sent with every message: "r12 214 blocks, box x28..36 y-4..5 z29..35 (9×10×7); planks 120,
    /// cobblestone 60…; dug 96 below ground (y-4..-1); y-4 49 cobblestone · y-3--1 28 cobblestone, 21 dug · y0 49 cobblestone · …"
    /// (layers with the same counts merged).
    /// </summary>
    public string Describe(int maxNames = 8)
    {
        if (Mine && Terrain is { } t && Bounds == null)
        {
            var (lo, hi) = t.SurfaceRange();
            return $"r{Rev} empty mine (plot x,z 0..63, absolute y 1..127 over generated terrain: surface y{lo}..{hi}, bedrock y0-4)";
        }
        if (Bounds is not { } b) return $"r{Rev} empty (plot x,z 0..63, y -16..47; y0 stands on the grass, below is solid ground)";
        var sb = new StringBuilder();
        int w = b.Max.X - b.Min.X + 1, h = b.Max.Y - b.Min.Y + 1, d = b.Max.Z - b.Min.Z + 1;
        sb.Append(CultureInfo.InvariantCulture, $"r{Rev} {_blocks.Count} blocks, box x{b.Min.X}..{b.Max.X} y{b.Min.Y}..{b.Max.Y} z{b.Min.Z}..{b.Max.Z} ({w}×{h}×{d})");
        sb.Append(OverLimit(w, h, d));
        var names = _blocks.Values.Where(s => !DesignFacing.SecondHalf(s.Id, s.Meta))
            .GroupBy(s => NameOf(s.Id, s.Meta)).OrderByDescending(g => g.Count()).ToList();
        sb.Append("; ").Append(string.Join(", ", names.Take(maxNames).Select(g => $"{g.Key} {g.Count()}"))).Append(names.Count > maxNames ? ", …" : "");
        if (_dug.Count > 0) sb.Append(CultureInfo.InvariantCulture, $"; dug {_dug.Count} {(Mine ? "to mine out" : "below ground")} (y{_dug.Min(p => p.Y)}..{_dug.Max(p => p.Y)})");
        sb.Append("; ").Append(LayerSummary(b.Min.Y, b.Max.Y));
        return sb.ToString();
    }

    /// <summary>" OVER the 32 limit" (a mine: 64 wide / 128 high) when the box is bigger than a design holds, else "".</summary>
    private string OverLimit(int w, int h, int d)
    {
        if (w <= BlueprintParser.MaxSide && d <= BlueprintParser.MaxSide && h <= (Mine ? BlueprintParser.MaxMineHeight : BlueprintParser.MaxHeight)) return "";
        return Mine ? " OVER the 64 wide / 128 high limit" : " OVER the 32 limit";
    }

    /// <summary>"y-4 49 cobblestone · y-3--1 28 cobblestone, 21 dug · y0 49 cobblestone" (layers with the same text merged).</summary>
    private string LayerSummary(int y0, int y1)
    {
        var layers = new List<(int Y0, int Y1, string Text)>();
        for (int y = y0; y <= y1; y++)
        {
            var text = LayerText(y);
            if (layers.Count > 0 && layers[^1].Text == text) layers[^1] = (layers[^1].Y0, y, text);
            else layers.Add((y, y, text));
        }
        static string Group((int Y0, int Y1, string Text) l) => (l.Y0 == l.Y1 ? $"y{l.Y0}" : $"y{l.Y0}-{l.Y1}") + " " + l.Text;
        if (layers.Count <= MaxLayerGroups) return string.Join(" · ", layers.Select(Group));
        // A tall, varied canvas (a deep mine): the bottom and the top, the middle left to describe y (it rides in every message).
        int head = MaxLayerGroups * 2 / 3, tail = MaxLayerGroups - head;
        return string.Join(" · ", layers.Take(head).Select(Group)) + $" · … {layers.Count - head - tail} more layer groups (describe y for one) … · "
            + string.Join(" · ", layers.TakeLast(tail).Select(Group));
    }

    /// <summary>Layer groups the summary lists before it folds the middle ones.</summary>
    private const int MaxLayerGroups = 36;

    /// <summary>One layer's count and two most common names, and its dug cells: "28 cobblestone+planks, 21 dug".</summary>
    private string LayerText(int y)
    {
        var at = _blocks.Where(kv => kv.Key.Y == y).ToList();
        int dug = Mine || Underground(y) ? _dug.Count(p => p.Y == y) : 0;
        var text = at.Count == 0 ? "0" : $"{at.Count} " + string.Join("+", at.GroupBy(kv => NameOf(kv.Value.Id, kv.Value.Meta)).OrderByDescending(g => g.Count()).Take(2).Select(g => g.Key));
        if (dug > 0) text = (at.Count == 0 ? "" : text + ", ") + $"{dug} dug";
        return text;
    }

    private const string LayerSymbols = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>One layer as rows (north row first, west char first) over the canvas box, with a legend; '.' = air (below ground:
    /// dug out), '#' = undisturbed ground.</summary>
    public string Layer(int y)
    {
        if (Bounds is not { } b) return "empty";
        var legend = new Dictionary<(int, int), char>();
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"y{y}{(Mine ? "" : Underground(y) ? " (below ground)" : "")}, rows z{b.Min.Z}..{b.Max.Z}, cols x{b.Min.X}..{b.Max.X}:\n");
        for (int z = b.Min.Z; z <= b.Max.Z; z++)
        {
            for (int x = b.Min.X; x <= b.Max.X; x++) sb.Append(LayerChar(x, y, z, legend));
            sb.Append('\n');
        }
        sb.Append(string.Join(" ", legend.Select(kv => $"{kv.Value}={Spec(kv.Key.Item1, kv.Key.Item2)}")));
        if (Mine || Underground(y)) sb.Append(legend.Count > 0 ? " " : "").Append("#=ground .=dug");
        return sb.ToString();
    }

    /// <summary>A cell's character on a layer: its block's legend symbol (a new one added), '#' undisturbed ground, '.' air.</summary>
    private char LayerChar(int x, int y, int z, Dictionary<(int, int), char> legend)
    {
        if (!_blocks.TryGetValue(new BlockPos(x, y, z), out var s)) return IsEarth(x, y, z) ? '#' : '.';
        if (legend.TryGetValue(s, out var ch)) return ch;
        ch = legend.Count < LayerSymbols.Length ? LayerSymbols[legend.Count] : '?';
        legend[s] = ch;
        return ch;
    }

    /// <summary>"placed 12 planks, removed 3 glass; dug 20": what a change set did, for the AI's next turn and the transcript.</summary>
    public static string Summarize(IReadOnlyCollection<CanvasCell> changes, int max = 4, DesignerCanvas? on = null)
    {
        if (changes.Count == 0) return "nothing changed";
        var placed = changes.Where(c => c.Id > 0 && !DesignFacing.SecondHalf(c.Id, c.Meta))
            .GroupBy(c => NameOf(c.Id, c.Meta)).OrderByDescending(g => g.Count()).ToList();
        bool Below(CanvasCell c) => on?.Ground(c.X, c.Y, c.Z) ?? Underground(c.Y);
        int removed = changes.Count(c => c.Id == 0 && !Below(c)), dug = changes.Count(c => c.Id == 0 && Below(c)), filled = changes.Count(c => c.Id == Earth);
        var parts = new List<string>();
        if (placed.Count > 0) parts.Add("placed " + string.Join(", ", placed.Take(max).Select(g => $"{g.Count()} {g.Key}")) + (placed.Count > max ? ", …" : ""));
        if (removed > 0) parts.Add($"removed {removed}");
        if (dug > 0) parts.Add(on?.Mine == true ? $"dug {dug}" : $"dug {dug} below ground");
        if (filled > 0) parts.Add($"filled {filled} back with ground");
        return parts.Count > 0 ? string.Join("; ", parts) : "nothing changed";
    }

    /// <summary>The design palette of the version in this flow: every placeable state worth a palette button (<see cref="DesignPalette"/>).</summary>
    public static IReadOnlyList<DesignBlock> PaletteEntries() => DesignPalette.Current.Entries;
}
