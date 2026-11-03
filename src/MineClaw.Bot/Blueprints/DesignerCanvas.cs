using System.Globalization;
using System.Text;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Designs;

namespace MineClaw.Bot.Blueprints;

/// <summary>One cell change on the Design Studio canvas: the new state (id 0 = air).</summary>
public readonly record struct CanvasCell(int X, int Y, int Z, int Id, int Meta)
{
    public int[] Wire => [X, Y, Z, Id, Meta];
}

/// <summary>
/// The Design Studio's sandbox canvas (docs/web/api.md "Design Studio"): the AI's head, never the live world. A 64×64 plot,
/// 48 high, local coordinates (x, z 0..63, y 0..47; y=0 sits on the plot's grass, +x east, +z south), holding blocks in the
/// designs system's own format (<see cref="DesignSnapshot"/>: the server version's ids and metas), so a save is an ordinary
/// design (<see cref="DesignerSave"/>). Only design-palette blocks are accepted (<see cref="BuildBlocks.Buildable"/>); a door is
/// kept as its two halves (lower = facing meta, upper = lower | 8, as the normalizer draws it).
/// </summary>
public sealed class DesignerCanvas
{
    public const int Size = 64, Height = 48;
    /// <summary>Cells one op may change (a full plot fill is refused: a design holds at most 32³).</summary>
    public const int MaxCellsPerOp = 40_000;

    private readonly Dictionary<BlockPos, (int Id, int Meta)> _blocks = new();

    public IReadOnlyDictionary<BlockPos, (int Id, int Meta)> Blocks => _blocks;
    public int Count => _blocks.Count;
    /// <summary>Bumped by every applied change set (the browser's optimistic edits carry it).</summary>
    public int Rev { get; set; }

    public static bool Inside(int x, int y, int z) => x is >= 0 and < Size && z is >= 0 and < Size && y is >= 0 and < Height;

    public (int Id, int Meta) Get(int x, int y, int z) => _blocks.TryGetValue(new BlockPos(x, y, z), out var s) ? s : (0, 0);

    /// <summary>Why (id, meta) can't stand on the canvas (null = it can): air always can, else a design-palette block.</summary>
    public static string? Refusal(int id, int meta)
    {
        if (id == 0) return null;
        if (BuildBlocks.Refusal(id) is { } r) return r;
        return BuildBlocks.MetaProblem(id, meta);
    }

    /// <summary>
    /// Applies cell states in order (later wins) and returns the cells that really changed, with door halves kept paired. Cells
    /// outside the plot or not in the design palette are skipped and counted in <paramref name="rejected"/>.
    /// </summary>
    public List<CanvasCell> Apply(IEnumerable<CanvasCell> cells, out int rejected)
    {
        rejected = 0;
        var changed = new Dictionary<BlockPos, CanvasCell>();
        int n = 0;
        foreach (var c0 in cells)
        {
            if (++n > MaxCellsPerOp) { rejected++; continue; }
            var c = c0.Id == 0 ? c0 with { Meta = 0 } : c0;
            if (!Inside(c.X, c.Y, c.Z) || Refusal(c.Id, c.Meta) != null) { rejected++; continue; }
            Set(c, changed);
            if (BuildBlocks.KindOf(c.Id) == BuildKind.Door && c.Id != 0)
            {
                // A drawn lower half brings its upper; an upper half alone is accepted as drawn (the browser sends both).
                if ((c.Meta & 8) == 0 && Inside(c.X, c.Y + 1, c.Z)) Set(new CanvasCell(c.X, c.Y + 1, c.Z, c.Id, (c.Meta & 7) | 8), changed);
            }
        }
        if (changed.Count > 0) Rev++;
        return changed.Values.ToList();
    }

    public List<CanvasCell> Apply(IEnumerable<CanvasCell> cells) => Apply(cells, out _);

    private void Set(CanvasCell c, Dictionary<BlockPos, CanvasCell> changed)
    {
        var p = new BlockPos(c.X, c.Y, c.Z);
        (int Id, int Meta) old = _blocks.TryGetValue(p, out var o) ? o : (0, 0);
        if (old == (c.Id, c.Meta)) return;
        // Replacing one half of a door drops the other half (a door is never left half standing).
        if (old.Id != 0 && BuildBlocks.KindOf(old.Item1) == BuildKind.Door && c.Id != old.Item1)
        {
            var other = (old.Item2 & 8) != 0 ? p.Down() : p.Up();
            if (_blocks.TryGetValue(other, out var os) && os.Id == old.Item1)
            {
                _blocks.Remove(other);
                changed[other] = new CanvasCell(other.X, other.Y, other.Z, 0, 0);
            }
        }
        if (c.Id == 0) _blocks.Remove(p);
        else _blocks[p] = (c.Id, c.Meta);
        changed[p] = c;
    }

    /// <summary>Every cell set to air (the canvas cleared).</summary>
    public List<CanvasCell> Clear() => Apply(_blocks.Keys.ToList().Select(p => new CanvasCell(p.X, p.Y, p.Z, 0, 0)));

    /// <summary>The blocks' box, or null when the canvas is empty.</summary>
    public (BlockPos Min, BlockPos Max)? Bounds
    {
        get
        {
            if (_blocks.Count == 0) return null;
            int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
            foreach (var p in _blocks.Keys)
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

    // ------------------------------------------------------------------ persistence (the designs system's own encoding)

    public string Encode() => DesignSnapshot.EncodeStates(_blocks);

    public static DesignerCanvas Decode(string? states, int rev = 0)
    {
        var c = new DesignerCanvas { Rev = rev };
        foreach (var kv in DesignSnapshot.Decode(states, null).Blocks)
            if (Inside(kv.Key.X, kv.Key.Y, kv.Key.Z)) c._blocks[kv.Key] = kv.Value;
        return c;
    }

    /// <summary>The canvas as a design block set (local coordinates as they are).</summary>
    public DesignSnapshot ToSnapshot()
    {
        var s = new DesignSnapshot();
        foreach (var kv in _blocks) s.Blocks[kv.Key] = kv.Value;
        return s;
    }

    /// <summary>
    /// A design revision's block set on a fresh canvas: its box centred on the plot, standing on the grass (y = 0). Returns the
    /// offset added to every position (design → canvas), so a save can put it back where the design stands.
    /// </summary>
    public static (DesignerCanvas Canvas, BlockPos Offset) FromSnapshot(DesignSnapshot snap)
    {
        var c = new DesignerCanvas();
        if (snap.Box is not { } box) return (c, default);
        int w = box.Max.X - box.Min.X + 1, d = box.Max.Z - box.Min.Z + 1;
        var off = new BlockPos(Size / 2 - w / 2 - box.Min.X, -box.Min.Y, Size / 2 - d / 2 - box.Min.Z);
        foreach (var kv in snap.Blocks)
        {
            var p = new BlockPos(kv.Key.X + off.X, kv.Key.Y + off.Y, kv.Key.Z + off.Z);
            if (Inside(p.X, p.Y, p.Z)) c._blocks[p] = kv.Value;
        }
        return (c, off);
    }

    // ------------------------------------------------------------------ what the AI reads

    /// <summary>The palette spelling of a state ("planks", "oak stairs" → "wooden_stairs:2"), as the AI's tools accept it.</summary>
    public static string Spec(int id, int meta) => StructureCapture.PaletteName(id, (BuildBlocks.KindOf(id) == BuildKind.Door ? meta & 7 : meta)) ?? BuildBlocks.Name(id, meta).Replace(' ', '_');

    /// <summary>
    /// The compact summary sent with every message: "r12 214 blocks, box x28..36 y0..5 z29..35 (9×6×7); planks 120, cobblestone
    /// 60…; y0 49 cobblestone · y1-3 28 planks+glass · …" (layers with the same counts merged).
    /// </summary>
    public string Describe(int maxNames = 8)
    {
        if (Bounds is not { } b) return $"r{Rev} empty (plot x,z 0..63, y 0..47)";
        var sb = new StringBuilder();
        int w = b.Max.X - b.Min.X + 1, h = b.Max.Y - b.Min.Y + 1, d = b.Max.Z - b.Min.Z + 1;
        sb.Append(CultureInfo.InvariantCulture, $"r{Rev} {_blocks.Count} blocks, box x{b.Min.X}..{b.Max.X} y{b.Min.Y}..{b.Max.Y} z{b.Min.Z}..{b.Max.Z} ({w}×{h}×{d})");
        if (w > BlueprintParser.MaxSide || d > BlueprintParser.MaxSide || h > BlueprintParser.MaxHeight) sb.Append(" OVER the 32 limit");
        var names = _blocks.Values.Where(s => !(BuildBlocks.KindOf(s.Id) == BuildKind.Door && (s.Meta & 8) != 0))
            .GroupBy(s => BuildBlocks.Name(s.Id, s.Meta)).OrderByDescending(g => g.Count()).ToList();
        sb.Append("; ").Append(string.Join(", ", names.Take(maxNames).Select(g => $"{g.Key} {g.Count()}"))).Append(names.Count > maxNames ? ", …" : "");
        var layers = new List<(int Y0, int Y1, string Text)>();
        for (int y = b.Min.Y; y <= b.Max.Y; y++)
        {
            var at = _blocks.Where(kv => kv.Key.Y == y).ToList();
            var text = at.Count == 0 ? "0" : $"{at.Count} " + string.Join("+", at.GroupBy(kv => BuildBlocks.Name(kv.Value.Id, kv.Value.Meta)).OrderByDescending(g => g.Count()).Take(2).Select(g => g.Key));
            if (layers.Count > 0 && layers[^1].Text == text) layers[^1] = (layers[^1].Y0, y, text);
            else layers.Add((y, y, text));
        }
        sb.Append("; ").Append(string.Join(" · ", layers.Select(l => (l.Y0 == l.Y1 ? $"y{l.Y0}" : $"y{l.Y0}-{l.Y1}") + " " + l.Text)));
        return sb.ToString();
    }

    /// <summary>One layer as rows (north row first, west char first) over the blocks' x/z box, with a legend; '.' = air.</summary>
    public string Layer(int y)
    {
        if (Bounds is not { } b) return "empty";
        var legend = new Dictionary<(int, int), char>();
        const string Symbols = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"y{y}, rows z{b.Min.Z}..{b.Max.Z}, cols x{b.Min.X}..{b.Max.X}:\n");
        for (int z = b.Min.Z; z <= b.Max.Z; z++)
        {
            for (int x = b.Min.X; x <= b.Max.X; x++)
            {
                if (!_blocks.TryGetValue(new BlockPos(x, y, z), out var s)) { sb.Append('.'); continue; }
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
        return sb.ToString();
    }

    /// <summary>"placed 12 planks, removed 3 glass": what a change set did, for the AI's next turn and the transcript.</summary>
    public static string Summarize(IReadOnlyCollection<CanvasCell> changes, int max = 4)
    {
        if (changes.Count == 0) return "nothing changed";
        var placed = changes.Where(c => c.Id != 0 && !(BuildBlocks.KindOf(c.Id) == BuildKind.Door && (c.Meta & 8) != 0))
            .GroupBy(c => BuildBlocks.Name(c.Id, c.Meta)).OrderByDescending(g => g.Count()).ToList();
        int removed = changes.Count(c => c.Id == 0);
        var parts = new List<string>();
        if (placed.Count > 0) parts.Add("placed " + string.Join(", ", placed.Take(max).Select(g => $"{g.Count()} {g.Key}")) + (placed.Count > max ? ", …" : ""));
        if (removed > 0) parts.Add($"removed {removed}");
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
