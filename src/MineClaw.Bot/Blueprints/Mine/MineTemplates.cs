using System.Globalization;
using System.Text.Json;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints.Mine;

/// <summary>A spiral staircase shaft: an n×n square around a c×c core left standing, walked one block down per step.</summary>
/// <param name="X">The square's west column (canvas x).</param>
/// <param name="Z">The square's north row (canvas z).</param>
/// <param name="Top">The first step's feet level (the shaft's entry: the surface there for a mine that starts at the grass).</param>
/// <param name="Bottom">The lowest step's feet level.</param>
/// <param name="Clockwise">Seen from above, going down.</param>
/// <param name="TorchEvery">A wall torch every N steps (0 = none), on the outer wall above the head.</param>
/// <param name="Landings">Feet levels where the whole square (core too) is dug 2 high: rooms and tunnels branch off there.</param>
public sealed record SpiralSpec(int X, int Z, int Top, int Bottom, int Size = 4, int Core = 2, bool Clockwise = true, int TorchEvery = 0,
    IReadOnlyList<int>? Landings = null);

/// <summary>A strip mine: a main corridor along x or z with 1×2 branches off both sides, <see cref="Spacing"/> solid blocks apart.</summary>
/// <param name="X">The corridor's first cell (its west / north side).</param>
/// <param name="Y">The feet level.</param>
/// <param name="Width">The main corridor's width (1 or 2) …</param>
/// <param name="Height">… and height (2 or 3): "1x2" or "2x3".</param>
/// <param name="Sides">both | left (−z for an x corridor, −x for a z one) | right | none.</param>
public sealed record StripSpec(int X, int Y, int Z, char Axis = 'x', int Length = 30, int Width = 1, int Height = 2, int Spacing = 3, int BranchLength = 12,
    string Sides = "both", int TorchEvery = 0);

/// <summary>A template's cells (id 0 = dig, else a block placed after its cell is dug), its entry (the top step) and the walk path.</summary>
public sealed record MineCells(List<CanvasCell> Cells, BlockPos Entry, List<BlockPos> Path, string Label);

/// <summary>
/// The Design Studio's parametric mine pieces (docs/web/api.md "Mine designs"), for the user's template panel and the AI designer's
/// tools alike; <c>mineTemplates.ts</c> is the browser's port (the same cells for the same parameters: a golden hash in both tests).
/// A mine design is the inverse of a build: its cells are the ones to dig (forced air), plus the few blocks placed in them
/// (torches, ladders, stairs) once dug.
/// </summary>
public static class MineTemplates
{
    public const int MinSize = 3, MaxSize = 6;

    // ------------------------------------------------------------------ spiral shaft

    /// <summary>The ring edge of a walkway cell of an n×n square: 0 north, 1 east, 2 south, 3 west (corners go clockwise: the
    /// north-east corner is east's, the north-west north's).</summary>
    internal static int EdgeOf(int lx, int lz, int n)
    {
        int dN = lz, dE = n - 1 - lx, dS = n - 1 - lz, dW = lx;
        int m = Math.Min(Math.Min(dN, dE), Math.Min(dS, dW));
        if (dN == m && dE == m) return 1;
        if (dE == m && dS == m) return 2;
        if (dS == m && dW == m) return 3;
        if (dW == m && dN == m) return 0;
        return dN == m ? 0 : dE == m ? 1 : dS == m ? 2 : 3;
    }

    /// <summary>A walkway cell's place on the ring (0..4(n−1)−1, clockwise from the north-west corner).</summary>
    internal static int RingIndex(int lx, int lz, int n) => EdgeOf(lx, lz, n) switch
    {
        0 => Math.Min(lx, n - 2),
        1 => n - 1 + Math.Min(lz, n - 2),
        2 => 2 * (n - 1) + Math.Min(n - 1 - lx, n - 2),
        _ => 3 * (n - 1) + Math.Min(n - 1 - lz, n - 2),
    };

    /// <summary>The outer ring's cell at a ring index.</summary>
    internal static (int Lx, int Lz) Outer(int idx, int n)
    {
        int e = n - 1;
        return (idx / e) switch
        {
            0 => (idx, 0),
            1 => (e, idx - e),
            2 => (e - (idx - 2 * e), e),
            _ => (0, e - (idx - 3 * e)),
        };
    }

    /// <summary>The side (dx, dz) of the wall outside an outer ring cell.</summary>
    private static (int Dx, int Dz) OutSide(int lx, int lz, int n) => EdgeOf(lx, lz, n) switch
    {
        0 => (0, -1),
        1 => (1, 0),
        2 => (0, 1),
        _ => (-1, 0),
    };

    /// <summary>
    /// The spiral shaft's cells: every walkway cell (the square minus its centred core) belongs to one place on the ring; step k
    /// (feet at Top − k) is the ring place k mod 4(n−1) (clockwise or not) and digs its cells 3 high (feet, head and the headroom a
    /// step up or down needs), so a lap later the same cell is 4(n−1) lower with solid floor in between. Landings dig the whole
    /// square 2 high; torches hang on the outer wall over the head.
    /// </summary>
    public static MineCells Spiral(SpiralSpec s)
    {
        int n = Math.Clamp(s.Size, MinSize, MaxSize), c = Math.Clamp(s.Core, 1, n - 2), off = (n - c) / 2, ring = 4 * (n - 1);
        bool Core(int lx, int lz) => lx >= off && lx < off + c && lz >= off && lz < off + c;
        var byPlace = new List<(int Lx, int Lz)>[ring];
        for (int i = 0; i < ring; i++) byPlace[i] = [];
        for (int lz = 0; lz < n; lz++)
            for (int lx = 0; lx < n; lx++)
                if (!Core(lx, lz)) byPlace[RingIndex(lx, lz, n)].Add((lx, lz));
        int Place(int k) => s.Clockwise ? k % ring : (ring - k % ring) % ring;
        var cells = new List<CanvasCell>();
        var path = new List<BlockPos>();
        int top = s.Top, bottom = Math.Max(1, s.Bottom);
        for (int k = 0; top - k >= bottom; k++)
        {
            int y = top - k, at = Place(k);
            foreach (var (lx, lz) in byPlace[at])
                for (int dy = 0; dy < 3; dy++) cells.Add(new CanvasCell(s.X + lx, y + dy, s.Z + lz, 0, 0));
            var (ox, oz) = Outer(at, n);
            path.Add(new BlockPos(s.X + ox, y, s.Z + oz));
        }
        foreach (var ly in (s.Landings ?? []).Distinct().Where(l => l >= bottom && l <= top).Order())
            for (int dy = 0; dy < 2; dy++)
                for (int lz = 0; lz < n; lz++)
                    for (int lx = 0; lx < n; lx++) cells.Add(new CanvasCell(s.X + lx, ly + dy, s.Z + lz, 0, 0));
        if (s.TorchEvery > 0)
            for (int k = s.TorchEvery; k < path.Count; k += s.TorchEvery)
            {
                var p = path[k];
                var side = OutSide(p.X - s.X, p.Z - s.Z, n);
                cells.Add(new CanvasCell(p.X, p.Y + 2, p.Z, Blocks.Torch, DesignerCanvas.MetaOn(Blocks.Torch, side)));
            }
        var entry = path.Count > 0 ? path[0] : new BlockPos(s.X, top, s.Z);
        var label = string.Create(CultureInfo.InvariantCulture,
            $"spiral shaft {n}×{n} (core {c}×{c}) y{top}→{bottom}{(s.Clockwise ? "" : " ccw")}{((s.Landings?.Count ?? 0) > 0 ? ", landings " + string.Join("/", s.Landings!) : "")}");
        return new MineCells(cells, entry, path, label);
    }

    // ------------------------------------------------------------------ strip mine

    /// <summary>
    /// The strip mine's cells: the main corridor (Width × Height) along the axis for Length; 1×2 branches off its sides at every
    /// (Spacing + 1)th cell from Spacing on (Spacing solid blocks between two branches), BranchLength long; torches on the
    /// corridor's left wall every TorchEvery cells (never in a branch's mouth).
    /// </summary>
    public static MineCells Strip(StripSpec s)
    {
        int len = Math.Clamp(s.Length, 1, 64), w = Math.Clamp(s.Width, 1, 2), h = Math.Clamp(s.Height, 2, 3), sp = Math.Clamp(s.Spacing, 1, 8),
            bl = Math.Clamp(s.BranchLength, 0, 64);
        bool alongX = char.ToLowerInvariant(s.Axis) != 'z';
        (int X, int Z) P(int a, int across) => alongX ? (s.X + a, s.Z + across) : (s.X + across, s.Z + a);
        var cells = new List<CanvasCell>();
        void Dig(int a, int across, int y)
        {
            var (x, z) = P(a, across);
            cells.Add(new CanvasCell(x, y, z, 0, 0));
        }
        for (int a = 0; a < len; a++)
            for (int across = 0; across < w; across++)
                for (int dy = 0; dy < h; dy++) Dig(a, across, s.Y + dy);
        var sides = (s.Sides ?? "both").ToLowerInvariant();
        var branches = new HashSet<int>();
        for (int a = sp; a < len; a += sp + 1)
        {
            branches.Add(a);
            for (int b = 1; b <= bl; b++)
                for (int dy = 0; dy < 2; dy++)
                {
                    if (sides is "both" or "left") Dig(a, -b, s.Y + dy);
                    if (sides is "both" or "right") Dig(a, w - 1 + b, s.Y + dy);
                }
        }
        if (s.TorchEvery > 0)
            for (int a = s.TorchEvery - 1; a < len; a += s.TorchEvery)
            {
                int at = branches.Contains(a) ? a + 1 : a;
                if (at >= len || branches.Contains(at)) continue;
                var (x, z) = P(at, 0);
                var wall = alongX ? (0, -1) : (-1, 0);
                cells.Add(new CanvasCell(x, s.Y + 1, z, Blocks.Torch, DesignerCanvas.MetaOn(Blocks.Torch, wall)));
            }
        var (ex, ez) = P(0, 0);
        var path = Enumerable.Range(0, len).Select(a => P(a, 0)).Select(p => new BlockPos(p.X, s.Y, p.Z)).ToList();
        var label = string.Create(CultureInfo.InvariantCulture,
            $"strip mine at y{s.Y} along {(alongX ? "x" : "z")}: {w}×{h} × {len}, {branches.Count} branch rows {sp} apart ({bl} long, {sides})");
        return new MineCells(cells, new BlockPos(ex, s.Y, ez), path, label);
    }

    // ------------------------------------------------------------------ steps (presets and the AI's tools)

    /// <summary>
    /// One template step from JSON (a preset's step, or the AI's tool input): <c>{template: "spiral"|"strip", …}</c> with the
    /// record's fields in camelCase or snake_case; a spiral's <c>top</c> may be "surface" (the highest ground over its square on
    /// <paramref name="terrain"/>).
    /// </summary>
    public static MineCells FromStep(JsonElement step, MineTerrain? terrain, string? template = null)
    {
        template ??= Str(step, "template") ?? "spiral";
        switch (template.ToLowerInvariant())
        {
            case "spiral" or "spiral_shaft" or "shaft":
                {
                    int x = Int(step, "x") ?? 30, z = Int(step, "z") ?? 30, size = Int(step, "size") ?? 4;
                    int top = Int(step, "top") ?? (terrain?.SurfaceOver(x, z, x + size - 1, z + size - 1) ?? 64);
                    var dir = (Str(step, "dir") ?? "cw").ToLowerInvariant();
                    var landings = Ints(step, "landings");
                    return Spiral(new SpiralSpec(x, z, top, Int(step, "bottom") ?? 11, size, Int(step, "core") ?? 2, dir is not ("ccw" or "counterclockwise"),
                        Int(step, "torchEvery", "torch_every") ?? 0, landings));
                }
            case "strip" or "strip_mine":
                {
                    var axis = (Str(step, "axis") ?? "x").Trim();
                    return Strip(new StripSpec(Int(step, "x") ?? 0, Int(step, "y") ?? 11, Int(step, "z") ?? 0, axis.Length > 0 ? axis[0] : 'x',
                        Int(step, "length") ?? 30, Int(step, "width") ?? 1, Int(step, "height") ?? 2, Int(step, "spacing") ?? 3,
                        Int(step, "branchLength", "branch_length") ?? 12, Str(step, "sides") ?? "both", Int(step, "torchEvery", "torch_every") ?? 0));
                }
            default:
                throw new ArgumentException($"no mine template '{template}' (spiral, strip)");
        }
    }

    private static string? Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, params string[] keys)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var k in keys)
            if (e.TryGetProperty(k, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)) return m;
            }
        return null;
    }

    private static List<int>? Ints(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToList()
            : null;

    /// <summary>FNV-1a over the cells (x, y, z, id, meta in order): the golden value both languages' tests check.</summary>
    public static uint Hash(IEnumerable<CanvasCell> cells)
    {
        uint h = 2166136261;
        unchecked
        {
            foreach (var c in cells)
                foreach (int v in new[] { c.X, c.Y, c.Z, c.Id, c.Meta }) h = (h ^ (uint)v) * 16777619u;
        }
        return h;
    }
}
