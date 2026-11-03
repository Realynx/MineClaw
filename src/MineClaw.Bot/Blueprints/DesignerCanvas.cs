using System.Globalization;
using System.Text;
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
/// holds blocks in the designs system's own format (<see cref="DesignSnapshot"/>: the server version's ids and metas) and the
/// dug-out cells below ground (air the build must dig), so a save is an ordinary design (<see cref="DesignerSave"/>: the dug
/// cells drawn '.', the depth as <c>ground N</c>). Only design-palette blocks are accepted (<see cref="BuildBlocks.Buildable"/>);
/// a door is kept as its two halves (lower = facing meta, upper = lower | 8, as the normalizer draws it).
/// </summary>
public sealed class DesignerCanvas
{
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
        return Underground(y) && !_blocks.ContainsKey(p) && !_dug.Contains(p);
    }

    /// <summary>Why (id, meta) can't stand on the canvas (null = it can): air and earth always can, else a design-palette block.</summary>
    public static string? Refusal(int id, int meta)
    {
        if (id is 0 or Earth) return null;
        if (id < 0) return $"no block id {id}";
        if (BuildBlocks.Refusal(id) is { } r) return r;
        return BuildBlocks.MetaProblem(id, meta);
    }

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
            if (++n > MaxCellsPerOp) { rejected++; continue; }
            var c = c0.Id is 0 or Earth ? c0 with { Id = c0.Id == Earth && Underground(c0.Y) ? Earth : 0, Meta = 0 } : c0;
            if (!Inside(c.X, c.Y, c.Z) || Refusal(c.Id, c.Meta) != null) { rejected++; continue; }
            Set(c, changed);
            if (c.Id > 0 && BuildBlocks.KindOf(c.Id) == BuildKind.Door)
            {
                // A drawn lower half brings its upper; an upper half alone is accepted as drawn (the browser sends both).
                if ((c.Meta & 8) == 0 && Inside(c.X, c.Y + 1, c.Z)) Set(new CanvasCell(c.X, c.Y + 1, c.Z, c.Id, (c.Meta & 7) | 8), changed);
            }
        }
        // Wall torches and ladders hang on their wall: the changed ones and the ones next to a changed cell (a wall placed after
        // its torch) are turned to the solid side when theirs is open.
        var near = new HashSet<BlockPos>();
        foreach (var p in changed.Keys)
        {
            near.Add(p);
            near.Add(p.Up());
            foreach (var (dx, dz) in Sides) near.Add(new BlockPos(p.X + dx, p.Y, p.Z + dz));
        }
        LastFixed = FixAttachments(near, changed);
        if (changed.Count > 0) Rev++;
        return changed.Values.ToList();
    }

    public List<CanvasCell> Apply(IEnumerable<CanvasCell> cells) => Apply(cells, out _);

    /// <summary>The attached blocks the last <see cref="Apply"/> turned to their wall (or put on the floor), as their new states.</summary>
    public List<CanvasCell> LastFixed { get; private set; } = [];

    private static readonly (int Dx, int Dz)[] Sides = [(-1, 0), (1, 0), (0, -1), (0, 1)];

    /// <summary>The side (dx, dz) of the block a wall torch (1..4) or a ladder (2..5) hangs on; null for anything else (a floor torch).</summary>
    public static (int Dx, int Dz)? WallOf(int id, int meta) =>
        id == MineClaw.Data.Blocks.Torch ? meta switch { 1 => (-1, 0), 2 => (1, 0), 3 => (0, -1), 4 => (0, 1), _ => null }
        : id == MineClaw.Data.Blocks.Ladder ? meta switch { 2 => (0, 1), 3 => (0, -1), 4 => (1, 0), 5 => (-1, 0), _ => null }
        : null;

    /// <summary>The meta that hangs a torch or a ladder on the block at side (dx, dz).</summary>
    public static int MetaOn(int id, (int Dx, int Dz) wall) => id == MineClaw.Data.Blocks.Torch
        ? wall switch { (-1, 0) => 1, (1, 0) => 2, (0, -1) => 3, _ => 4 }
        : wall switch { (0, 1) => 2, (0, -1) => 3, (1, 0) => 4, _ => 5 };

    /// <summary>A full block a torch or a ladder can hang on (a block on the canvas, or undisturbed ground below the grass).</summary>
    public bool Holds(int x, int y, int z)
    {
        if (y < -Depth) return true;
        if (x is < 0 or >= Size || z is < 0 or >= Size || y >= Height) return false;
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
            else if (st.Id == MineClaw.Data.Blocks.Torch && Holds(p.X, p.Y - 1, p.Z)) meta = 5;
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
        int floor = fixedCells.Count(c => c.Id == MineClaw.Data.Blocks.Torch && c.Meta == 5);
        int torches = fixedCells.Count(c => c.Id == MineClaw.Data.Blocks.Torch && c.Meta != 5), ladders = fixedCells.Count(c => c.Id == MineClaw.Data.Blocks.Ladder);
        var parts = new List<string>();
        if (torches > 0) parts.Add($"turned {torches} wall torch{(torches == 1 ? "" : "es")} around to hang on the solid side");
        if (ladders > 0) parts.Add($"turned {ladders} ladder{(ladders == 1 ? "" : "s")} around to the solid side");
        if (floor > 0) parts.Add($"stood {floor} torch{(floor == 1 ? "" : "es")} with no wall on the floor");
        return string.Join(", ", parts);
    }

    /// <summary>A cell's state as a wire id: a block, air (0) or, below ground, undisturbed earth.</summary>
    private (int Id, int Meta) State(BlockPos p) =>
        _blocks.TryGetValue(p, out var o) ? o : Underground(p.Y) && !_dug.Contains(p) ? (Earth, 0) : (0, 0);

    private void Set(CanvasCell c, Dictionary<BlockPos, CanvasCell> changed)
    {
        var p = new BlockPos(c.X, c.Y, c.Z);
        var old = State(p);
        if (old == (c.Id, c.Meta)) return;
        // Replacing one half of a door drops the other half (a door is never left half standing).
        if (old.Id > 0 && BuildBlocks.KindOf(old.Item1) == BuildKind.Door && c.Id != old.Item1)
        {
            var other = (old.Item2 & 8) != 0 ? p.Down() : p.Up();
            if (_blocks.TryGetValue(other, out var os) && os.Id == old.Item1)
            {
                _blocks.Remove(other);
                if (Underground(other.Y)) _dug.Add(other);
                changed[other] = new CanvasCell(other.X, other.Y, other.Z, 0, 0);
            }
        }
        _blocks.Remove(p);
        _dug.Remove(p);
        if (c.Id > 0) _blocks[p] = (c.Id, c.Meta);
        else if (c.Id == 0 && Underground(p.Y)) _dug.Add(p);
        changed[p] = c;
    }

    /// <summary>Every block removed and every dug cell filled back (the canvas cleared: a flat grassland again).</summary>
    public List<CanvasCell> Clear() => Apply(_blocks.Keys.Concat(_dug).ToList().Select(p => new CanvasCell(p.X, p.Y, p.Z, Underground(p.Y) ? Earth : 0, 0)));

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

    public static DesignerCanvas Decode(string? states, int rev = 0)
    {
        var c = new DesignerCanvas { Rev = rev };
        foreach (var kv in DesignSnapshot.Decode(states, null).Blocks)
        {
            if (!Inside(kv.Key.X, kv.Key.Y, kv.Key.Z)) continue;
            if (kv.Value.Id > 0) c._blocks[kv.Key] = kv.Value;
            else if (kv.Value.Id == 0 && Underground(kv.Key.Y)) c._dug.Add(kv.Key);
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

    /// <summary>The palette spelling of a state ("planks", "oak stairs" → "wooden_stairs:2"), as the AI's tools accept it.</summary>
    public static string Spec(int id, int meta) => StructureCapture.PaletteName(id, (BuildBlocks.KindOf(id) == BuildKind.Door ? meta & 7 : meta)) ?? BuildBlocks.Name(id, meta).Replace(' ', '_');

    /// <summary>
    /// The compact summary sent with every message: "r12 214 blocks, box x28..36 y-4..5 z29..35 (9×10×7); planks 120,
    /// cobblestone 60…; dug 96 below ground (y-4..-1); y-4 49 cobblestone · y-3--1 28 cobblestone, 21 dug · y0 49 cobblestone · …"
    /// (layers with the same counts merged).
    /// </summary>
    public string Describe(int maxNames = 8)
    {
        if (Bounds is not { } b) return $"r{Rev} empty (plot x,z 0..63, y -16..47; y0 stands on the grass, below is solid ground)";
        var sb = new StringBuilder();
        int w = b.Max.X - b.Min.X + 1, h = b.Max.Y - b.Min.Y + 1, d = b.Max.Z - b.Min.Z + 1;
        sb.Append(CultureInfo.InvariantCulture, $"r{Rev} {_blocks.Count} blocks, box x{b.Min.X}..{b.Max.X} y{b.Min.Y}..{b.Max.Y} z{b.Min.Z}..{b.Max.Z} ({w}×{h}×{d})");
        if (w > BlueprintParser.MaxSide || d > BlueprintParser.MaxSide || h > BlueprintParser.MaxHeight) sb.Append(" OVER the 32 limit");
        var names = _blocks.Values.Where(s => !(BuildBlocks.KindOf(s.Id) == BuildKind.Door && (s.Meta & 8) != 0))
            .GroupBy(s => BuildBlocks.Name(s.Id, s.Meta)).OrderByDescending(g => g.Count()).ToList();
        sb.Append("; ").Append(string.Join(", ", names.Take(maxNames).Select(g => $"{g.Key} {g.Count()}"))).Append(names.Count > maxNames ? ", …" : "");
        if (_dug.Count > 0) sb.Append(CultureInfo.InvariantCulture, $"; dug {_dug.Count} below ground (y{_dug.Min(p => p.Y)}..{_dug.Max(p => p.Y)})");
        var layers = new List<(int Y0, int Y1, string Text)>();
        for (int y = b.Min.Y; y <= b.Max.Y; y++)
        {
            var at = _blocks.Where(kv => kv.Key.Y == y).ToList();
            int dug = Underground(y) ? _dug.Count(p => p.Y == y) : 0;
            var text = at.Count == 0 ? "0" : $"{at.Count} " + string.Join("+", at.GroupBy(kv => BuildBlocks.Name(kv.Value.Id, kv.Value.Meta)).OrderByDescending(g => g.Count()).Take(2).Select(g => g.Key));
            if (dug > 0) text = (at.Count == 0 ? "" : text + ", ") + $"{dug} dug";
            if (layers.Count > 0 && layers[^1].Text == text) layers[^1] = (layers[^1].Y0, y, text);
            else layers.Add((y, y, text));
        }
        sb.Append("; ").Append(string.Join(" · ", layers.Select(l => (l.Y0 == l.Y1 ? $"y{l.Y0}" : $"y{l.Y0}-{l.Y1}") + " " + l.Text)));
        return sb.ToString();
    }

    /// <summary>One layer as rows (north row first, west char first) over the canvas box, with a legend; '.' = air (below ground:
    /// dug out), '#' = undisturbed ground.</summary>
    public string Layer(int y)
    {
        if (Bounds is not { } b) return "empty";
        var legend = new Dictionary<(int, int), char>();
        const string Symbols = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"y{y}{(Underground(y) ? " (below ground)" : "")}, rows z{b.Min.Z}..{b.Max.Z}, cols x{b.Min.X}..{b.Max.X}:\n");
        for (int z = b.Min.Z; z <= b.Max.Z; z++)
        {
            for (int x = b.Min.X; x <= b.Max.X; x++)
            {
                if (!_blocks.TryGetValue(new BlockPos(x, y, z), out var s)) { sb.Append(IsEarth(x, y, z) ? '#' : '.'); continue; }
                if (!legend.TryGetValue(s, out var ch))
                {
                    ch = legend.Count < Symbols.Length ? Symbols[legend.Count] : '?';
                    legend[s] = ch;
                }
                sb.Append(ch);
            }
            sb.Append('\n');
        }
        sb.Append(string.Join(" ", legend.Select(kv => $"{kv.Value}={Spec(kv.Key.Item1, kv.Key.Item2)}")));
        if (Underground(y)) sb.Append(legend.Count > 0 ? " " : "").Append("#=ground .=dug");
        return sb.ToString();
    }

    /// <summary>"placed 12 planks, removed 3 glass; dug 20": what a change set did, for the AI's next turn and the transcript.</summary>
    public static string Summarize(IReadOnlyCollection<CanvasCell> changes, int max = 4)
    {
        if (changes.Count == 0) return "nothing changed";
        var placed = changes.Where(c => c.Id > 0 && !(BuildBlocks.KindOf(c.Id) == BuildKind.Door && (c.Meta & 8) != 0))
            .GroupBy(c => BuildBlocks.Name(c.Id, c.Meta)).OrderByDescending(g => g.Count()).ToList();
        int removed = changes.Count(c => c.Id == 0 && !Underground(c.Y)), dug = changes.Count(c => c.Id == 0 && Underground(c.Y)), filled = changes.Count(c => c.Id == Earth);
        var parts = new List<string>();
        if (placed.Count > 0) parts.Add("placed " + string.Join(", ", placed.Take(max).Select(g => $"{g.Count()} {g.Key}")) + (placed.Count > max ? ", …" : ""));
        if (removed > 0) parts.Add($"removed {removed}");
        if (dug > 0) parts.Add($"dug {dug} below ground");
        if (filled > 0) parts.Add($"filled {filled} back with ground");
        return parts.Count > 0 ? string.Join("; ", parts) : "nothing changed";
    }

    /// <summary>The design palette of the running version: every buildable state worth a palette button (variants spelled out).</summary>
    public static List<(int Id, int Meta, string Name, bool Facing)> Palette()
    {
        var list = new List<(int, int, string, bool)>();
        for (int id = 1; id < 256; id++)
        {
            if (!MineClaw.Data.Blocks.Exists(id) || !BuildBlocks.Buildable(id)) continue;
            bool facing = BuildBlocks.Directional(id) || id is MineClaw.Data.Blocks.Torch or MineClaw.Data.Blocks.Trapdoor or MineClaw.Data.Blocks.Pumpkin or MineClaw.Data.Blocks.JackOLantern;
            int variants = id switch { MineClaw.Data.Blocks.Wool => 16, MineClaw.Data.Blocks.Log => 3, MineClaw.Data.Blocks.Slab => 6, MineClaw.Data.Blocks.StoneBricks => 3, _ => 1 };
            for (int m = 0; m < variants; m++)
            {
                int meta = id == MineClaw.Data.Blocks.Torch ? 5 : m;
                if (BuildBlocks.MetaProblem(id, meta) != null) continue;
                list.Add((id, meta, BuildBlocks.Name(id, meta), facing));
            }
        }
        return list;
    }
}
