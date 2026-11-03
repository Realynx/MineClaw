using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

/// <summary>
/// Blueprint → voxels: layers and primitives in the order written (later wins) on a canonical W×H×D grid, door halves derived
/// (lower = facing meta, upper = lower | 8, per mc-dev ItemDoor), rotated to the blueprint's facing (metas too) and placed at
/// the origin. Structural validation (world-independent) happens here; site checks are <see cref="DesignChecks"/>.
/// </summary>
public static class BlueprintNormalizer
{
    /// <summary>Blocks per design: a base-63 stepped pyramid is 43,680 blocks; designs past <see cref="BlueprintOptions.SectionBlocks"/>
    /// are built band by band, so slicing never takes on more than a band at once.</summary>
    public const int MaxBlocks = 50_000;
    private const char Air = '.', Keep = '_';

    /// <param name="worldHeight">The world's height (<see cref="IBlockAccess.WorldHeight"/>): b1.8.1 128, 1.12.2 256.</param>
    public static (VoxelPlan? Plan, List<BlueprintIssue> Issues) Normalize(Blueprint bp, BlockPos? defaultOrigin = null, int worldHeight = ChunkColumn.SizeY)
    {
        var issues = new List<BlueprintIssue>();
        void Err(string code, string text, int line = 0) => issues.Add(new(IssueLevel.Error, code, text, line));
        if (bp.W < 1 || bp.H < 1 || bp.D < 1) { Err("size", "no valid size"); return (null, issues); }
        int W = bp.W, H = bp.H, D = bp.D;
        var grid = new char[W, H, D];
        for (int x = 0; x < W; x++) for (int y = 0; y < H; y++) for (int z = 0; z < D; z++) grid[x, y, z] = Keep;
        var facing = new Dictionary<(int, int, int), Dir>();
        var used = new HashSet<char>();
        bool In(int x, int y, int z) => x >= 0 && x < W && y >= 0 && y < H && z >= 0 && z < D;
        void Set(int x, int y, int z, char c, int line)
        {
            if (!In(x, y, z)) return;
            if (c != Air && c != Keep && !bp.Palette.ContainsKey(c)) { Undefined(c, line); return; }
            grid[x, y, z] = c;
            facing.Remove((x, y, z));
            if (c != Air && c != Keep) used.Add(c);
        }
        var undefined = new HashSet<char>();
        void Undefined(char c, int line)
        {
            if (undefined.Add(c)) Err("palette", $"'{c}' is not in the palette", line);
        }
        bool Bounds((int X, int Y, int Z) a, (int X, int Y, int Z) b, BlueprintOp op)
        {
            if (In(a.X, a.Y, a.Z) && In(b.X, b.Y, b.Z)) return true;
            Err("bounds", $"{op.Kind} {a.X},{a.Y},{a.Z} {b.X},{b.Y},{b.Z} is outside the {W}x{H}x{D} size", op.Line);
            return false;
        }

        foreach (var (what, index) in bp.Order)
        {
            if (what == "layer")
            {
                var (rows, line) = bp.Layers[index];
                if (index >= H) { Err("layer", $"layer {index} is above the height {H} (layers are 0..{H - 1})", line); continue; }
                if (rows.Count != D) Err("rows", $"layer {index} has {rows.Count} rows, size says {D} (north row first)", line);
                for (int z = 0; z < Math.Min(D, rows.Count); z++)
                {
                    var row = rows[z];
                    if (row.Length != W) Err("rows", $"layer {index} row {z + 1} is {row.Length} wide, size says {W}: '{row}'", line + 1 + z);
                    for (int x = 0; x < Math.Min(W, row.Length); x++) Set(x, index, z, row[x], line + 1 + z);
                }
                continue;
            }
            var op = bp.Ops[index];
            var (lo, hi) = (Min(op.A, op.B), Max(op.A, op.B));
            switch (op.Kind)
            {
                case "box" or "wall" or "window" or "clear":
                    if (!Bounds(lo, hi, op)) break;
                    Fill(lo, hi, (_, _, _) => op.Kind == "clear" ? Air : op.Symbol);
                    break;
                case "hollow":
                    if (!Bounds(lo, hi, op)) break;
                    Fill(lo, hi, (x, y, z) => x == lo.X || x == hi.X || y == lo.Y || y == hi.Y || z == lo.Z || z == hi.Z ? op.Symbol : Air);
                    break;
                case "walls":
                    if (!Bounds(lo, hi, op)) break;
                    Fill(lo, hi, (x, _, z) => x == lo.X || x == hi.X || z == lo.Z || z == hi.Z ? op.Symbol : Air);
                    break;
                case "door":
                    if (!Bounds(op.A, op.B, op)) break;
                    Set(op.A.X, op.A.Y, op.A.Z, op.Symbol, op.Line);
                    Set(op.B.X, op.B.Y, op.B.Z, op.Symbol, op.Line);
                    if (op.Facing is { } df) { facing[op.A] = df; facing[op.B] = df; }
                    break;
                case "stairs":
                    if (!Bounds(lo, hi, op)) break;
                    Fill(lo, hi, (_, _, _) => op.Symbol);
                    if (op.Facing is { } sf) Each(lo, hi, (x, y, z) => facing[(x, y, z)] = sf);
                    break;
                case "roof":
                    Roof(op, lo, hi);
                    break;
                case "pyramid":
                    if (!Bounds(lo, hi, op)) break;
                    Pyramid(op, lo, hi);
                    break;
            }
        }

        // A stepped pyramid: layer k is the square (lo.X+k..hi.X-k, lo.Z+k..hi.Z-k), one row in per layer. Every block rests on the
        // layer below (it covers the whole footprint of the one above), so sand and gravel never hang. With core:Y the cells more
        // than two rows from their layer's edge are Y: the outer two rows (the skin) cover the next layer's whole outer ring, so
        // a skin of sand always rests on skin or core, never on air.
        void Pyramid(BlueprintOp op, (int X, int Y, int Z) lo, (int X, int Y, int Z) hi)
        {
            char core = op.Arg.Length == 1 ? op.Arg[0] : '\0';
            if (core != '\0' && !bp.Palette.ContainsKey(core)) { Undefined(core, op.Line); core = '\0'; }
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

        void Each((int X, int Y, int Z) lo, (int X, int Y, int Z) hi, Action<int, int, int> f)
        {
            for (int y = lo.Y; y <= hi.Y; y++) for (int z = lo.Z; z <= hi.Z; z++) for (int x = lo.X; x <= hi.X; x++) f(x, y, z);
        }
        void Fill((int X, int Y, int Z) lo, (int X, int Y, int Z) hi, Func<int, int, int, char> sym, int line = 0) =>
            Each(lo, hi, (x, y, z) => Set(x, y, z, sym(x, y, z), line));

        void Roof(BlueprintOp op, (int X, int Y, int Z) lo, (int X, int Y, int Z) hi)
        {
            var styleParts = op.Arg.Split('|');
            var style = styleParts[0];
            char ridge = styleParts.Length > 1 && styleParts[1].Length == 1 ? styleParts[1][0] : op.Symbol;
            bool stairs = bp.Palette.TryGetValue(op.Symbol, out var pe) && BuildBlocks.KindOf(pe.Id) == BuildKind.Stairs;
            if (!In(lo.X, lo.Y, lo.Z) || !In(hi.X, lo.Y, hi.Z)) { Err("bounds", $"roof {lo.X},{lo.Y},{lo.Z}..{hi.X},{hi.Z} is outside the size", op.Line); return; }
            if (style == "flat") { Fill(lo, (hi.X, lo.Y, hi.Z), (_, _, _) => op.Symbol, op.Line); return; }
            if (style == "slope")
            {
                var d = op.Facing!.Value;
                var (dx, dz) = d.Offset();
                int steps = dx != 0 ? hi.X - lo.X + 1 : hi.Z - lo.Z + 1;
                for (int k = 0; k < steps; k++)
                {
                    int y = lo.Y + k;
                    if (y >= H) { Err("bounds", $"slope roof needs {steps} layers from y{lo.Y}: raise the size", op.Line); return; }
                    for (int z = lo.Z; z <= hi.Z; z++)
                        for (int x = lo.X; x <= hi.X; x++)
                        {
                            int along = dx > 0 ? x - lo.X : dx < 0 ? hi.X - x : dz > 0 ? z - lo.Z : hi.Z - z;
                            if (along != k) continue;
                            Set(x, y, z, op.Symbol, op.Line);
                            if (stairs) facing[(x, y, z)] = d;
                        }
                }
                return;
            }
            // gable: ridge along the longer side; each layer up steps one row in from both long edges.
            bool ridgeAlongX = hi.X - lo.X >= hi.Z - lo.Z;
            int span = ridgeAlongX ? hi.Z - lo.Z + 1 : hi.X - lo.X + 1;
            for (int k = 0; k < (span + 1) / 2; k++)
            {
                int y = lo.Y + k;
                if (y >= H) { Err("bounds", $"gable roof over {span} rows needs {(span + 1) / 2} layers from y{lo.Y}: raise the size", op.Line); return; }
                int a = (ridgeAlongX ? lo.Z : lo.X) + k, b = (ridgeAlongX ? hi.Z : hi.X) - k;
                foreach (int r in a == b ? new[] { a } : new[] { a, b })
                    for (int s = ridgeAlongX ? lo.X : lo.Z; s <= (ridgeAlongX ? hi.X : hi.Z); s++)
                    {
                        var (x, z) = ridgeAlongX ? (s, r) : (r, s);
                        bool isRidge = a == b;
                        Set(x, y, z, isRidge ? ridge : op.Symbol, op.Line);
                        if (stairs && !isRidge) facing[(x, y, z)] = ridgeAlongX ? (r == a ? Dir.South : Dir.North) : (r == a ? Dir.East : Dir.West);
                    }
            }
        }

        foreach (var c in bp.Palette.Keys.Where(k => !used.Contains(k)))
            issues.Add(new(IssueLevel.Info, "palette", $"palette '{c}' unused"));

        // Canonical states, door halves.
        var states = new Dictionary<(int X, int Y, int Z), (int Id, int Meta)>();
        for (int y = 0; y < H; y++)
            for (int z = 0; z < D; z++)
                for (int x = 0; x < W; x++)
                {
                    char c = grid[x, y, z];
                    if (c is Air or Keep || !bp.Palette.TryGetValue(c, out var e)) continue;
                    int meta = facing.TryGetValue((x, y, z), out var f) && BuildBlocks.MetaFor(e.Id, f) is { } fm ? fm : e.CanonicalMeta;
                    states[(x, y, z)] = (e.Id, meta);
                }
        // A door is two blocks: three or more drawn on top of each other is a mistake, not two doors.
        foreach (var ((x, y, z), (id, _)) in states.ToList())
            if (BuildBlocks.KindOf(id) == BuildKind.Door && states.TryGetValue((x, y + 1, z), out var d1) && d1.Id == id
                && states.TryGetValue((x, y + 2, z), out var d2) && d2.Id == id && !(states.TryGetValue((x, y - 1, z), out var d0) && d0.Id == id))
            {
                Err("door", $"door cells at {x},{y}..{y + 2},{z} stack three high: a door is two blocks");
                for (int k = y + 2; states.TryGetValue((x, k, z), out var dk) && dk.Id == id; k++) states.Remove((x, k, z));
            }
        foreach (var ((x, y, z), (id, meta)) in states.OrderBy(kv => kv.Key.Y).ToList())
        {
            if (!states.ContainsKey((x, y, z)) || BuildBlocks.KindOf(id) != BuildKind.Door) continue;
            bool lowerBelow = states.TryGetValue((x, y - 1, z), out var below) && below.Id == id && (below.Meta & 8) == 0;
            if (lowerBelow)
            {
                states[(x, y, z)] = (id, (below.Meta & 7) | 8);
                continue;
            }
            // A lower half: the cell above becomes the upper half (drawn as the same symbol, or left free).
            if (y + 1 >= H) { Err("door", $"door at {x},{y},{z} is on the top layer: its upper half needs the layer above"); continue; }
            if (states.TryGetValue((x, y + 1, z), out var above) && above.Id != id)
            {
                Err("door", $"door at {x},{y},{z} needs its upper half free, but {BuildBlocks.Name(above.Id, above.Meta)} is drawn above it");
                continue;
            }
            if (grid[x, y + 1, z] == Keep || grid[x, y + 1, z] == Air) states[(x, y + 1, z)] = (id, (meta & 7) | 8);
            if (y >= 1 && grid[x, y - 1, z] == Air) Err("door", $"door at {x},{y},{z} has air drawn under it: a door stands on a full block");
            else if (y >= 1 && states.TryGetValue((x, y - 1, z), out var floor) && !BuildBlocks.NormalCube(floor.Id))
                Err("door", $"door at {x},{y},{z} stands on {BuildBlocks.Name(floor.Id, floor.Meta)}: it needs a full opaque block under it");
        }
        // Slab on a same-type slab merges into a double slab in the lower cell (mc-dev BlockStep.onPlace).
        foreach (var ((x, y, z), (id, meta)) in states)
            if (id == Blocks.Slab && states.TryGetValue((x, y - 1, z), out var b2) && b2.Id == Blocks.Slab && (b2.Meta & 7) == (meta & 7))
                Err("slab", $"slab at {x},{y},{z} sits on the same slab type: they would merge into a double slab (use a full block)");
        foreach (var (k, (id, meta)) in states)
            if (BuildBlocks.MetaProblem(id, meta) is { } mp) Err("meta", $"{k.X},{k.Y},{k.Z}: {mp}");

        // The drawn front. Canonically the front is the south row and `facing` turns the drawing (facing north = a 180° turn: local
        // z=0 lands on the world's south side, x flips too). An author who drew every door on the very side `facing` names (live
        // design #8: `facing north`, `door D 3,0,0 north`) drew the build as it stands: turning it would put the door on the back,
        // so such a drawing is kept as drawn.
        bp.DrawnFront = DrawnFront(bp, states, W, D);
        if (bp.DrawnFront != Dir.South)
            issues.Add(new(IssueLevel.Info, "facing", $"door(s) drawn on the {bp.Facing.Name()} side: built as drawn (facing {bp.Facing.Name()} doesn't turn it)"));

        if (states.Count == 0) Err("empty", "the blueprint places no blocks");
        if (states.Count > MaxBlocks) Err("size", $"{states.Count} blocks: at most {MaxBlocks} per blueprint");

        // World placement: rotate from the drawn front to the facing (Blueprint.Quarters), then offset by the origin.
        var origin = bp.Origin ?? defaultOrigin ?? new BlockPos(0, 64, 0);
        var plan = new VoxelPlan { Blueprint = bp, Origin = origin };
        int q = bp.Quarters;
        BlockPos ToWorld(int x, int y, int z)
        {
            var (wx, wz) = q switch
            {
                1 => (D - 1 - z, x),
                2 => (W - 1 - x, D - 1 - z),
                3 => (z, W - 1 - x),
                _ => (x, z),
            };
            return new BlockPos(origin.X + wx, origin.Y + y, origin.Z + wz);
        }
        foreach (var ((x, y, z), (id, meta)) in states)
        {
            var p = ToWorld(x, y, z);
            plan.Blocks[p] = new PlannedBlock(p, id, BuildBlocks.Rotate(id, meta, q));
        }
        for (int y = 0; y < H; y++)
            for (int z = 0; z < D; z++)
                for (int x = 0; x < W; x++)
                    if (grid[x, y, z] == Air && !states.ContainsKey((x, y, z))) plan.Air.Add(ToWorld(x, y, z));
        if (origin.Y < 1 || origin.Y + H > worldHeight) Err("bounds", $"build y {origin.Y}..{origin.Y + H - 1} leaves the world (1..{worldHeight - 1})");
        return (issues.Any(i => i.Level == IssueLevel.Error) && states.Count == 0 ? null : plan, issues);
    }

    /// <summary>
    /// Which side of the drawing is the front: <see cref="Dir.South"/> (canonical: <c>facing</c> turns it) unless every door on the
    /// drawing's edge is drawn on the side <c>facing</c> names (and none on the south edge), then that side (no turn).
    /// </summary>
    internal static Dir DrawnFront(Blueprint bp, Dictionary<(int X, int Y, int Z), (int Id, int Meta)> states, int W, int D)
    {
        if (bp.Facing == Dir.South) return Dir.South;
        bool any = false;
        foreach (var ((x, _, z), (id, meta)) in states)
        {
            if (BuildBlocks.KindOf(id) != BuildKind.Door || (meta & 8) != 0) continue;
            var sides = new List<Dir>(2);
            if (z == 0) sides.Add(Dir.North);
            if (z == D - 1) sides.Add(Dir.South);
            if (x == 0) sides.Add(Dir.West);
            if (x == W - 1) sides.Add(Dir.East);
            if (sides.Count == 0) continue; // an inner door says nothing about the front
            if (!sides.Contains(bp.Facing) || sides.Contains(Dir.South)) return Dir.South;
            any = true;
        }
        return any ? bp.Facing : Dir.South;
    }

    private static (int X, int Y, int Z) Min((int X, int Y, int Z) a, (int X, int Y, int Z) b) => (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    private static (int X, int Y, int Z) Max((int X, int Y, int Z) a, (int X, int Y, int Z) b) => (Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    /// <summary>Parse + normalize in one go (tests, the design loop).</summary>
    public static (VoxelPlan? Plan, List<BlueprintIssue> Issues) FromText(string dsl, BlockPos? defaultOrigin = null, int worldHeight = ChunkColumn.SizeY)
    {
        var (bp, issues) = BlueprintParser.Parse(dsl);
        if (issues.Any(i => i.Level == IssueLevel.Error && i.Code is "size")) return (null, issues);
        var (plan, more) = Normalize(bp, defaultOrigin, worldHeight);
        issues.AddRange(more);
        return (plan, issues);
    }
}
