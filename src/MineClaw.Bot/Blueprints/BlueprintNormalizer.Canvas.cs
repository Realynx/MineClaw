using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

public static partial class BlueprintNormalizer
{
    private delegate void OpDrawer(Canvas canvas, BlueprintOp op, (int X, int Y, int Z) lo, (int X, int Y, int Z) hi);

    /// <summary>How each primitive draws (op kinds from <see cref="BlueprintParser"/>); lo/hi are the op's corners sorted.</summary>
    private static readonly Dictionary<string, OpDrawer> OpDrawers = new()
    {
        ["box"] = (c, op, lo, hi) => c.Solid(op, lo, hi, op.Symbol),
        ["wall"] = (c, op, lo, hi) => c.Solid(op, lo, hi, op.Symbol),
        ["window"] = (c, op, lo, hi) => c.Solid(op, lo, hi, op.Symbol),
        ["clear"] = (c, op, lo, hi) => c.Solid(op, lo, hi, Air),
        ["hollow"] = (c, op, lo, hi) => c.Hollow(op, lo, hi),
        ["walls"] = (c, op, lo, hi) => c.Walls(op, lo, hi),
        ["door"] = (c, op, _, _) => c.Door(op),
        ["stairs"] = (c, op, lo, hi) => c.Stairs(op, lo, hi),
        ["roof"] = (c, op, lo, hi) => c.Roof(op, lo, hi),
        ["pyramid"] = (c, op, lo, hi) => c.Pyramid(op, lo, hi),
    };

    /// <summary>The canonical W×H×D grid of one normalize: palette symbols, '.' air, '_' keep, plus the facings ops gave cells.</summary>
    private sealed partial class Canvas
    {
        private readonly Blueprint bp;
        private readonly List<BlueprintIssue> issues;
        private readonly int W, H, D;
        private readonly char[,,] grid;
        private readonly Dictionary<(int, int, int), Dir> facing = new();
        private readonly HashSet<char> used = new();
        private readonly HashSet<char> undefined = new();

        public Canvas(Blueprint bp, List<BlueprintIssue> issues)
        {
            this.bp = bp;
            this.issues = issues;
            (W, H, D) = (bp.W, bp.H, bp.D);
            grid = new char[W, H, D];
            for (int x = 0; x < W; x++) for (int y = 0; y < H; y++) for (int z = 0; z < D; z++) grid[x, y, z] = Keep;
        }

        private void Err(string code, string text, int line = 0) => issues.Add(new(IssueLevel.Error, code, text, line));

        private bool In(int x, int y, int z) => x >= 0 && x < W && y >= 0 && y < H && z >= 0 && z < D;

        private static bool IsBlock(char c) => c != Air && c != Keep;

        private void Set(int x, int y, int z, char c, int line)
        {
            if (!In(x, y, z)) return;
            if (IsBlock(c) && !bp.Palette.ContainsKey(c)) { Undefined(c, line); return; }
            grid[x, y, z] = c;
            facing.Remove((x, y, z));
            if (IsBlock(c)) used.Add(c);
        }

        private void Undefined(char c, int line)
        {
            if (undefined.Add(c)) Err("palette", $"'{c}' is not in the palette", line);
        }

        private bool Bounds((int X, int Y, int Z) a, (int X, int Y, int Z) b, BlueprintOp op)
        {
            if (In(a.X, a.Y, a.Z) && In(b.X, b.Y, b.Z)) return true;
            Err("bounds", $"{op.Kind} {a.X},{a.Y},{a.Z} {b.X},{b.Y},{b.Z} is outside the {W}x{H}x{D} size", op.Line);
            return false;
        }

        private static void Each((int X, int Y, int Z) lo, (int X, int Y, int Z) hi, Action<int, int, int> f)
        {
            for (int y = lo.Y; y <= hi.Y; y++) for (int z = lo.Z; z <= hi.Z; z++) for (int x = lo.X; x <= hi.X; x++) f(x, y, z);
        }

        private void Fill((int X, int Y, int Z) lo, (int X, int Y, int Z) hi, Func<int, int, int, char> sym, int line = 0) =>
            Each(lo, hi, (x, y, z) => Set(x, y, z, sym(x, y, z), line));

        /// <summary>Layers and ops in the order written (later wins).</summary>
        public void Draw()
        {
            foreach (var (what, index) in bp.Order)
            {
                if (what == "layer") DrawLayer(index);
                else DrawOp(bp.Ops[index]);
            }
        }

        private void DrawLayer(int index)
        {
            var (rows, line) = bp.Layers[index];
            if (index >= H) { Err("layer", $"layer {index} is above the height {H} (layers are 0..{H - 1})", line); return; }
            if (rows.Count != D) Err("rows", $"layer {index} has {rows.Count} rows, size says {D} (north row first)", line);
            for (int z = 0; z < Math.Min(D, rows.Count); z++)
            {
                var row = rows[z];
                if (row.Length != W) Err("rows", $"layer {index} row {z + 1} is {row.Length} wide, size says {W}: '{row}'", line + 1 + z);
                for (int x = 0; x < Math.Min(W, row.Length); x++) Set(x, index, z, row[x], line + 1 + z);
            }
        }

        private void DrawOp(BlueprintOp op)
        {
            if (OpDrawers.TryGetValue(op.Kind, out var draw)) draw(this, op, Min(op.A, op.B), Max(op.A, op.B));
        }

        public void Solid(BlueprintOp op, (int X, int Y, int Z) lo, (int X, int Y, int Z) hi, char c)
        {
            if (Bounds(lo, hi, op)) Fill(lo, hi, (_, _, _) => c);
        }

        public void Hollow(BlueprintOp op, (int X, int Y, int Z) lo, (int X, int Y, int Z) hi)
        {
            if (Bounds(lo, hi, op))
                Fill(lo, hi, (x, y, z) => x == lo.X || x == hi.X || y == lo.Y || y == hi.Y || z == lo.Z || z == hi.Z ? op.Symbol : Air);
        }

        public void Walls(BlueprintOp op, (int X, int Y, int Z) lo, (int X, int Y, int Z) hi)
        {
            if (Bounds(lo, hi, op)) Fill(lo, hi, (x, _, z) => x == lo.X || x == hi.X || z == lo.Z || z == hi.Z ? op.Symbol : Air);
        }

        public void Door(BlueprintOp op)
        {
            if (!Bounds(op.A, op.B, op)) return;
            Set(op.A.X, op.A.Y, op.A.Z, op.Symbol, op.Line);
            Set(op.B.X, op.B.Y, op.B.Z, op.Symbol, op.Line);
            if (op.Facing is { } df) { facing[op.A] = df; facing[op.B] = df; }
        }

        public void Stairs(BlueprintOp op, (int X, int Y, int Z) lo, (int X, int Y, int Z) hi)
        {
            if (!Bounds(lo, hi, op)) return;
            Fill(lo, hi, (_, _, _) => op.Symbol);
            if (op.Facing is { } sf) Each(lo, hi, (x, y, z) => facing[(x, y, z)] = sf);
        }

        // A stepped pyramid: layer k is the square (lo.X+k..hi.X-k, lo.Z+k..hi.Z-k), one row in per layer. Every block rests on the
        // layer below (it covers the whole footprint of the one above), so sand and gravel never hang. With core:Y the cells more
        // than two rows from their layer's edge are Y: the outer two rows (the skin) cover the next layer's whole outer ring, so
        // a skin of sand always rests on skin or core, never on air.
        public void Pyramid(BlueprintOp op, (int X, int Y, int Z) lo, (int X, int Y, int Z) hi)
        {
            if (!Bounds(lo, hi, op)) return;
            char core = PyramidCore(op);
            for (int k = 0; lo.X + k <= hi.X - k && lo.Z + k <= hi.Z - k && lo.Y + k <= hi.Y; k++)
            {
                (int x0, int x1, int z0, int z1) = (lo.X + k, hi.X - k, lo.Z + k, hi.Z - k);
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int edge = Math.Min(Math.Min(x - x0, x1 - x), Math.Min(z - z0, z1 - z));
                        Set(x, lo.Y + k, z, core != '\0' && edge >= 2 ? core : op.Symbol, op.Line);
                    }
            }
        }

        /// <summary>The pyramid's core symbol, '\0' for none (or one not in the palette, reported).</summary>
        private char PyramidCore(BlueprintOp op)
        {
            char core = op.Arg.Length == 1 ? op.Arg[0] : '\0';
            if (core == '\0' || bp.Palette.ContainsKey(core)) return core;
            Undefined(core, op.Line);
            return '\0';
        }
    }
}
