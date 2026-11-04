using System.Text.Json;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>
/// The light model's planning side (zero tokens): which cells of an area can host a hostile spawn after dark (the b1.8.1
/// rule over WorldState's light, <see cref="SpawnRules"/>), and the fewest torch spots that light all of them (greedy set
/// cover: each candidate's own light field is a bucket BFS from 14 losing max(1, opacity) per step, exactly like World.b,
/// and torches combine by max). Candidates: empty cells a torch attaches to (PlaceRules.AttachFaces: floor first, then a
/// wall), never over the void, never in an excluded region (a farm's spawn deck) or a protected cell; spots the bot can stand
/// at or beside rank first.
/// </summary>
public static class LightPlan
{
    public const int MaxColumns = 64 * 64, MaxBand = 24;
    /// <summary>Torch light (mc-dev BlockTorch: a(0.9375F) = 14).</summary>
    public const int TorchLight = 14;

    /// <summary>A box of spawn cells (feet levels <see cref="Y0"/>..<see cref="Y1"/>).</summary>
    public sealed record Region(int X0, int Y0, int Z0, int X1, int Y1, int Z1, string Label)
    {
        public bool Contains(BlockPos p) => p.X >= X0 && p.X <= X1 && p.Y >= Y0 && p.Y <= Y1 && p.Z >= Z0 && p.Z <= Z1;
        public int Columns => (X1 - X0 + 1) * (Z1 - Z0 + 1);
        public string Box => $"x {X0}..{X1}, z {Z0}..{Z1}, y {(Y0 == Y1 ? $"{Y0}" : $"{Y0}..{Y1}")}";

        public IEnumerable<BlockPos> Cells()
        {
            for (int x = X0; x <= X1; x++)
            for (int z = Z0; z <= Z1; z++)
            for (int y = Y0; y <= Y1; y++)
                yield return new BlockPos(x, y, z);
        }
    }

    /// <summary>Cells never lit: boxes (a whole design) and cell sets (a design's decks), with a note for the result.</summary>
    public sealed class Exclusion
    {
        public List<Region> Boxes { get; } = new();
        public HashSet<BlockPos> Cells { get; } = new();
        public List<string> Notes { get; } = new();
        public bool Contains(BlockPos p) => Cells.Contains(p) || Boxes.Any(b => b.Contains(p));
        public bool IsEmpty => Boxes.Count == 0 && Cells.Count == 0;
    }

    /// <summary>One torch spot: the cell, the face to click (Top = on the floor), and whether the bot can stand near it.</summary>
    public readonly record struct Spot(BlockPos Pos, BlockFace Face, bool Reachable);

    public sealed record Plan(List<Spot> Torches, List<BlockPos> Dark, List<BlockPos> Unlit);

    /// <summary>Cells of <paramref name="r"/> where a hostile can spawn after dark (World.k 11) with light below
    /// <paramref name="level"/> (8 = the rule), minus the excluded ones (counted in <paramref name="excludedDark"/>).</summary>
    public static List<BlockPos> Dark(WorldState w, Region r, Exclusion? ex, int level, out int excludedDark, long? timeOfDay = null)
    {
        excludedDark = 0;
        int k = timeOfDay is { } t ? w.SkylightSubtracted(t) : LightEngine.NightSubtracted;
        var list = new List<BlockPos>();
        foreach (var p in r.Cells())
        {
            if (SpawnRules.Check(w, p, k, maxLight: level - 1) != SpawnBlock.Spawnable) continue;
            if (ex != null && ex.Contains(p)) { excludedDark++; continue; }
            list.Add(p);
        }
        return list;
    }

    /// <summary>Spawnable cells of a set of extra cells (an excluded deck), for the report.</summary>
    public static int CountDark(WorldState w, IEnumerable<BlockPos> cells) =>
        cells.Count(p => SpawnRules.Check(w, p, LightEngine.NightSubtracted) == SpawnBlock.Spawnable);

    /// <summary>The greedy torch plan for <paramref name="dark"/>; <paramref name="allowed"/> filters candidate cells
    /// (protection). Null <paramref name="me"/> = no distance tie-break.</summary>
    public static Plan Cover(WorldState w, Region r, List<BlockPos> dark, Exclusion? ex, int level, Func<BlockPos, bool>? allowed = null,
        Vec3d? me = null, int maxTorches = 256)
    {
        var index = new Dictionary<BlockPos, int>(dark.Count);
        for (int i = 0; i < dark.Count; i++) index[dark[i]] = i;
        var (spots, cover) = Candidates(w, r, dark, ex, level, allowed, index);
        // Two greedy passes (max new coverage; and "hardest cell first": the uncovered cell with the fewest torch spots, then
        // the spot that lights most of the rest), each pruned of torches the others make redundant; the smaller plan wins.
        double Bias(int i)
        {
            var s = spots[i];
            double b = (s.Reachable ? 0 : -0.5) + (s.Face == BlockFace.Top ? 0.01 : 0);
            if (me is { } m) b -= 0.0001 * Math.Min(1000, Math.Pow(s.Pos.X + 0.5 - m.X, 2) + Math.Pow(s.Pos.Z + 0.5 - m.Z, 2));
            return b;
        }
        var a = Prune(Greedy(spots.Count, cover, dark.Count, maxTorches, Bias, hardestFirst: false), cover, dark.Count);
        var b = Prune(Greedy(spots.Count, cover, dark.Count, maxTorches, Bias, hardestFirst: true), cover, dark.Count);
        var pick = Better(a, b, cover, dark.Count);
        return new Plan(Tour(pick.Select(t => spots[t]).ToList(), me), dark, Unlit(pick, cover, dark));
    }

    /// <summary>Candidate torch spots and the dark cells each lights: the region grown by one (walls at its rim, a torch one
    /// above/below the spawn level), within a torch's reach of a dark cell.</summary>
    private static (List<Spot> Spots, List<int[]> Cover) Candidates(WorldState w, Region r, List<BlockPos> dark, Exclusion? ex, int level,
        Func<BlockPos, bool>? allowed, Dictionary<BlockPos, int> index)
    {
        var spots = new List<Spot>();
        var cover = new List<int[]>();
        if (dark.Count == 0) return (spots, cover);
        int reach = TorchLight - level; // how far a torch still gives ≥ level
        int x0 = Math.Max(r.X0 - 1, dark.Min(d => d.X) - reach), x1 = Math.Min(r.X1 + 1, dark.Max(d => d.X) + reach);
        int z0 = Math.Max(r.Z0 - 1, dark.Min(d => d.Z) - reach), z1 = Math.Min(r.Z1 + 1, dark.Max(d => d.Z) + reach);
        int y0 = Math.Max(1, r.Y0 - 1), y1 = Math.Min(w.WorldHeight - 2, r.Y1 + 1);
        var field = new Field(reach);
        for (int x = x0; x <= x1; x++)
        for (int z = z0; z <= z1; z++)
        {
            if (!w.IsLoaded(x, z)) continue;
            for (int y = y0; y <= y1; y++)
            {
                var c = new BlockPos(x, y, z);
                if (TorchFace(w, c, ex, allowed) is not { } face) continue;
                var lit = field.Lit(w, c, level, index);
                if (lit.Length == 0) continue;
                spots.Add(new Spot(c, face, StandNear(w, c)));
                cover.Add(lit);
            }
        }
        return (spots, cover);
    }

    /// <summary>The face a torch in <paramref name="c"/> would hang on: an empty, dry, allowed cell, not excluded, not over the void.</summary>
    private static BlockFace? TorchFace(WorldState w, BlockPos c, Exclusion? ex, Func<BlockPos, bool>? allowed)
    {
        int id = w.GetBlock(c);
        if (!Aiming.IsReplaceable(w, id) || BlockShapes.IsLiquid(id)) return null;
        if (ex != null && ex.Contains(c)) return null;
        var faces = PlaceRules.AttachFaces(w, c, Blocks.Torch);
        if (faces.Count == 0 || Safety.IsOverVoid(w, c.X, c.Y, c.Z)) return null;
        if (allowed != null && !allowed(c)) return null;
        return faces[0];
    }

    /// <summary>The plan that lights more cells; on a tie the one with fewer torches (the first pass wins a full tie).</summary>
    private static List<int> Better(List<int> a, List<int> b, List<int[]> cover, int darkCount) =>
        Covered(b, cover, darkCount) > Covered(a, cover, darkCount)
        || (Covered(b, cover, darkCount) == Covered(a, cover, darkCount) && b.Count < a.Count) ? b : a;

    private static List<BlockPos> Unlit(List<int> pick, List<int[]> cover, List<BlockPos> dark)
    {
        var done = new bool[dark.Count];
        foreach (var t in pick) foreach (var d in cover[t]) done[d] = true;
        var unlit = new List<BlockPos>();
        for (int i = 0; i < dark.Count; i++) if (!done[i]) unlit.Add(dark[i]);
        return unlit;
    }

    private static List<int> Greedy(int n, List<int[]> cover, int darkCount, int maxTorches, Func<int, double> bias, bool hardestFirst)
    {
        var uncovered = new bool[darkCount];
        Array.Fill(uncovered, true);
        int left = darkCount;
        var by = SpotsByCell(n, cover, darkCount);
        var used = new bool[n];
        var chosen = new List<int>();
        while (left > 0 && chosen.Count < maxTorches)
        {
            IEnumerable<int> pool = Enumerable.Range(0, n);
            if (hardestFirst)
            {
                int hard = Hardest(uncovered, by);
                if (hard < 0) break;
                pool = by[hard];
            }
            int best = MostGain(pool, used, cover, uncovered, bias);
            if (best < 0) break;
            used[best] = true;
            chosen.Add(best);
            foreach (var d in cover[best])
                if (uncovered[d]) { uncovered[d] = false; left--; }
        }
        return chosen;
    }

    /// <summary>Dark cell → the spots that light it.</summary>
    private static List<int>[] SpotsByCell(int n, List<int[]> cover, int darkCount)
    {
        var by = new List<int>[darkCount];
        for (int d = 0; d < darkCount; d++) by[d] = new List<int>();
        for (int i = 0; i < n; i++) foreach (var d in cover[i]) by[d].Add(i);
        return by;
    }

    /// <summary>The uncovered cell the fewest spots light (some do); -1 when none is left.</summary>
    private static int Hardest(bool[] uncovered, List<int>[] by)
    {
        int hard = -1;
        for (int d = 0; d < uncovered.Length; d++)
            if (uncovered[d] && by[d].Count > 0 && (hard < 0 || by[d].Count < by[hard].Count)) hard = d;
        return hard;
    }

    /// <summary>The unused spot of <paramref name="pool"/> with the best biased new coverage; -1 when none adds any.</summary>
    private static int MostGain(IEnumerable<int> pool, bool[] used, List<int[]> cover, bool[] uncovered, Func<int, double> bias)
    {
        int best = -1;
        double bestScore = 0;
        foreach (var i in pool)
        {
            if (used[i]) continue;
            int gain = Gain(cover[i], uncovered);
            if (gain == 0) continue;
            double score = gain + bias(i) * Math.Max(1, gain);
            if (best < 0 || score > bestScore) { best = i; bestScore = score; }
        }
        return best;
    }

    private static int Gain(int[] lit, bool[] uncovered)
    {
        int gain = 0;
        foreach (var d in lit) if (uncovered[d]) gain++;
        return gain;
    }

    /// <summary>Drops torches whose every cell another chosen torch also lights (latest picks first).</summary>
    private static List<int> Prune(List<int> chosen, List<int[]> cover, int darkCount)
    {
        var count = new int[darkCount];
        foreach (var t in chosen) foreach (var d in cover[t]) count[d]++;
        var keep = new List<int>(chosen);
        for (int k = keep.Count - 1; k >= 0; k--)
        {
            var t = keep[k];
            if (cover[t].All(d => count[d] > 1))
            {
                foreach (var d in cover[t]) count[d]--;
                keep.RemoveAt(k);
            }
        }
        return keep;
    }

    private static int Covered(List<int> chosen, List<int[]> cover, int darkCount)
    {
        var lit = new bool[darkCount];
        int n = 0;
        foreach (var t in chosen) foreach (var d in cover[t]) if (!lit[d]) { lit[d] = true; n++; }
        return n;
    }

    /// <summary>Nearest-neighbour order from the bot.</summary>
    private static List<Spot> Tour(List<Spot> spots, Vec3d? me)
    {
        if (spots.Count < 2 || me is not { } start) return spots;
        var left = new List<Spot>(spots);
        var order = new List<Spot>(spots.Count);
        double cx = start.X, cy = start.Y, cz = start.Z;
        while (left.Count > 0)
        {
            int bi = 0;
            double bd = double.MaxValue;
            for (int i = 0; i < left.Count; i++)
            {
                var p = left[i].Pos;
                double d = Math.Pow(p.X + 0.5 - cx, 2) + Math.Pow(p.Y - cy, 2) + Math.Pow(p.Z + 0.5 - cz, 2);
                if (d < bd) { bd = d; bi = i; }
            }
            var s = left[bi];
            left.RemoveAt(bi);
            order.Add(s);
            (cx, cy, cz) = (s.Pos.X + 0.5, s.Pos.Y, s.Pos.Z + 0.5);
        }
        return order;
    }

    /// <summary>A player can stand in the cell itself or under/beside it with feet up to 2 below: a spot in reach on foot.</summary>
    internal static bool StandNear(IBlockAccess w, BlockPos c)
    {
        for (int dy = 0; dy <= 2; dy++)
        {
            if (SkillWorld.Standable(w, new BlockPos(c.X, c.Y - dy, c.Z))) return true;
            foreach (var o in SkillWorld.Horizontal)
                if (SkillWorld.Standable(w, new BlockPos(c.X + o.X, c.Y - dy, c.Z + o.Z))) return true;
        }
        return false;
    }

    /// <summary>One torch's light field (bucket BFS from 14, each step −max(1, opacity), unloaded = opaque) → the dark cells it
    /// brings to ≥ level. Reuses one stamp array per plan.</summary>
    private sealed class Field(int radius)
    {
        private readonly int _side = 2 * radius + 1;
        private readonly int[] _stamp = new int[(2 * radius + 1) * (2 * radius + 1) * (2 * radius + 1)];
        private readonly int[] _best = new int[(2 * radius + 1) * (2 * radius + 1) * (2 * radius + 1)];
        private int _gen;
        private readonly List<BlockPos>[] _buckets = Enumerable.Range(0, TorchLight + 1).Select(_ => new List<BlockPos>()).ToArray();
        private readonly List<int> _hits = new();

        /// <summary>The six neighbours in spreading order: +x, -x, +y, -y, +z, -z.</summary>
        private static readonly (int X, int Y, int Z)[] Steps = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

        public int[] Lit(WorldState w, BlockPos src, int level, Dictionary<BlockPos, int> dark)
        {
            _gen++;
            int h = w.WorldHeight;
            _hits.Clear();
            foreach (var b in _buckets) b.Clear();
            Visit(src, src, TorchLight);
            _buckets[TorchLight].Add(src);
            for (int l = TorchLight; l >= level; l--)
            {
                var bucket = _buckets[l];
                for (int i = 0; i < bucket.Count; i++)
                {
                    var c = bucket[i];
                    if (Best(src, c) != l) continue;
                    if (dark.TryGetValue(c, out int di)) _hits.Add(di);
                    Spread(w, h, src, c, l, level);
                }
            }
            return _hits.Distinct().ToArray();
        }

        /// <summary>Light <paramref name="l"/> at <paramref name="c"/> reaches each loaded neighbour in the box less max(1, its opacity).</summary>
        private void Spread(WorldState w, int h, BlockPos src, BlockPos c, int l, int level)
        {
            foreach (var (dx, dy, dz) in Steps)
            {
                var nb = c.Offset(dx, dy, dz);
                if (!InBox(src, nb) || (uint)nb.Y >= h || !w.IsLoaded(nb.X, nb.Z)) continue;
                int v = l - Math.Max(1, w.Physics.LightOpacity(w.GetBlock(nb)));
                if (v >= level && Visit(src, nb, v)) _buckets[v].Add(nb);
            }
        }

        private bool InBox(BlockPos src, BlockPos nb) =>
            Math.Abs(nb.X - src.X) <= radius && Math.Abs(nb.Y - src.Y) <= radius && Math.Abs(nb.Z - src.Z) <= radius;

        private int Slot(BlockPos src, BlockPos c) =>
            ((c.X - src.X + radius) * _side + (c.Y - src.Y + radius)) * _side + (c.Z - src.Z + radius);

        private int Best(BlockPos src, BlockPos c)
        {
            int s = Slot(src, c);
            return _stamp[s] == _gen ? _best[s] : -1;
        }

        private bool Visit(BlockPos src, BlockPos c, int v)
        {
            int s = Slot(src, c);
            if (_stamp[s] == _gen && _best[s] >= v) return false;
            _stamp[s] = _gen;
            _best[s] = v;
            return true;
        }
    }

    // ------------------------------------------------------------------ args → region / exclusion

    /// <summary>
    /// The region and default exclusion of a light_area/spawn_map call: <c>design</c> (its footprint ± <c>pad</c> 4, feet levels
    /// base−2..base+2; its decks excluded), <c>area</c> ("x0,z0,x1,z1" with <c>y</c> = feet level or "y0..y1", or
    /// "x0,y0,z0,x1,y1,z1"), <c>around</c> (x,y,z or a player) + <c>radius</c> (12), else around me. Error = a message.
    /// </summary>
    public static (Region? Region, Exclusion Ex, DesignRegion? Design, string? Error) Resolve(SkillContext ctx, JsonElement args)
    {
        var ex = new Exclusion();
        DesignRegion? design = null;
        Region? r;
        string? error;
        string? dsg = args.Str("design");
        if (!string.IsNullOrWhiteSpace(dsg)) (r, design, error) = ForDesign(ctx, args, dsg);
        else if (args.Str("area") is { } area && !string.IsNullOrWhiteSpace(area)) (r, error) = ForArea(ctx, args, area);
        else (r, error) = Around(ctx, args);
        if (r is null) return (null, ex, null, error);
        if (TooBig(r) is { } big) return (null, ex, design, big);
        if (Exclusions(ctx, args, design, ex) is { } bad) return (null, ex, design, bad);
        return (r, ex, design, null);
    }

    /// <summary>A design's footprint ± <c>pad</c> (4), feet levels base−2..base+2.</summary>
    private static (Region? R, DesignRegion? Design, string? Error) ForDesign(SkillContext ctx, JsonElement args, string dsg)
    {
        if (ctx.Registry.Design is not { } find) return (null, null, "no design list here");
        var design = SafeFind(find, dsg);
        if (design is null) return (null, null, $"no design '{dsg}'");
        int pad = Math.Clamp(args.Int("pad") ?? 4, 0, 16);
        var (y0, y1) = Band(args, design.Min.Y - 2, design.Min.Y + 2, ctx.World.WorldHeight);
        return (new Region(design.Min.X - pad, y0, design.Min.Z - pad, design.Max.X + pad, y1, design.Max.Z + pad, $"{design.Label} floor"), design, null);
    }

    /// <summary>"x0,z0,x1,z1" (feet levels from <c>y</c>, else around mine) or "x0,y0,z0,x1,y1,z1".</summary>
    private static (Region? R, string? Error) ForArea(SkillContext ctx, JsonElement args, string area)
    {
        var me = ctx.Me;
        var v = Ints(area);
        if (v is { Length: 4 })
        {
            var (y0, y1) = Band(args, me.Y - 6, me.Y + 3, ctx.World.WorldHeight);
            return (new Region(Math.Min(v[0], v[2]), y0, Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), y1, Math.Max(v[1], v[3]), "area"), null);
        }
        if (v is { Length: 6 })
            return (new Region(Math.Min(v[0], v[3]), Math.Min(v[1], v[4]), Math.Min(v[2], v[5]), Math.Max(v[0], v[3]), Math.Max(v[1], v[4]), Math.Max(v[2], v[5]), "area"), null);
        return (null, "area must be x0,z0,x1,z1 (with y=) or x0,y0,z0,x1,y1,z1");
    }

    /// <summary><c>around</c> (x,y,z or a player I can see; me by default) + <c>radius</c> (12).</summary>
    private static (Region? R, string? Error) Around(SkillContext ctx, JsonElement args)
    {
        var at = ctx.Me;
        string label = "around me";
        if (args.Str("around") is { } around && !string.IsNullOrWhiteSpace(around) && around != "me")
        {
            if (Centre(ctx, around) is not { } found) return (null, $"around '{around}': not a position or a player I can see");
            (at, label) = found;
        }
        int rad = Math.Clamp(args.Int("radius") ?? 12, 2, 32);
        var (y0, y1) = Band(args, at.Y - 4, at.Y + 3, ctx.World.WorldHeight);
        return (new Region(at.X - rad, y0, at.Z - rad, at.X + rad, y1, at.Z + rad, label), null);
    }

    private static (BlockPos At, string Label)? Centre(SkillContext ctx, string around)
    {
        if (Ints(around) is { Length: 3 } p) return (new BlockPos(p[0], p[1], p[2]), $"around {around}");
        if (ctx.World.Entities.FindPlayer(around) is { } pl) return (pl.Position.ToBlockPos(), $"around {pl.Name}");
        return null;
    }

    private static string? TooBig(Region r)
    {
        if (r.Columns > MaxColumns) return $"{r.Box} is {r.Columns} columns; at most {MaxColumns} per call";
        if (r.Y1 - r.Y0 + 1 > MaxBand) return $"y {r.Y0}..{r.Y1} spans more than {MaxBand} levels";
        return null;
    }

    /// <summary>Exclusions: explicit ones, else a design target's own decks (a mob farm's spawning floor is never lit by default).</summary>
    private static string? Exclusions(SkillContext ctx, JsonElement args, DesignRegion? design, Exclusion ex)
    {
        var spec = args.Str("exclude")?.Trim();
        bool none = string.Equals(spec, "none", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(spec) && !none)
            foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (AddExclusion(ctx, part, design, ex) is { } err) return err;
        if (design != null && !none && (string.IsNullOrWhiteSpace(spec) || !spec.Contains("deck", StringComparison.OrdinalIgnoreCase)))
            AddDecks(design, ex);
        return null;
    }

    private static (int, int) Band(JsonElement args, int dy0, int dy1, int worldHeight)
    {
        var s = args.Str("y");
        if (!string.IsNullOrWhiteSpace(s))
        {
            var v = Ints(s.Replace("..", ","));
            if (v is { Length: 1 }) return (v[0], v[0]);
            if (v is { Length: 2 }) return (Math.Min(v[0], v[1]), Math.Max(v[0], v[1]));
        }
        return (Math.Max(1, dy0), Math.Min(worldHeight - 2, dy1));
    }

    private static int[]? Ints(string s)
    {
        var parts = s.Split([',', ' ', ';', '[', ']'], StringSplitOptions.RemoveEmptyEntries);
        var v = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++) if (!int.TryParse(parts[i], out v[i])) return null;
        return v;
    }

    private static DesignRegion? SafeFind(Func<string, DesignRegion?> find, string name)
    {
        try { return find(name.Trim().TrimStart('#')); }
        catch (Exception) { return null; }
    }

    /// <summary>"none" | "deck" / "design 4 deck" / "#4 deck" (its decks) | "design 4" / "#4" (its whole box) | a box.</summary>
    private static string? AddExclusion(SkillContext ctx, string part, DesignRegion? target, Exclusion ex)
    {
        if (Ints(part) is { Length: 6 } v)
        {
            var box = new Region(Math.Min(v[0], v[3]), Math.Min(v[1], v[4]), Math.Min(v[2], v[5]), Math.Max(v[0], v[3]), Math.Max(v[1], v[4]), Math.Max(v[2], v[5]), "box");
            ex.Boxes.Add(box);
            ex.Notes.Add(box.Box);
            return null;
        }
        bool deck = part.Contains("deck", StringComparison.OrdinalIgnoreCase) || part.Contains("spawn", StringComparison.OrdinalIgnoreCase);
        var name = System.Text.RegularExpressions.Regex.Replace(part, @"(?i)\b(design|deck|spawn(ing)?|floor|platform|the|its|of)\b", " ").Trim().TrimStart('#').Trim();
        var d = name.Length == 0 ? target : ctx.Registry.Design is { } find ? SafeFind(find, name) : null;
        if (d is null) return name.Length == 0 ? "exclude=deck needs design=N" : $"exclude '{part}': no design '{name}'";
        if (deck) AddDecks(d, ex);
        else
        {
            ex.Boxes.Add(new Region(d.Min.X, d.Min.Y, d.Min.Z, d.Max.X, d.Max.Y, d.Max.Z, d.Label));
            ex.Notes.Add($"{d.Label} (whole box)");
        }
        return null;
    }

    /// <summary>A design's decks: every cell standing on one of its own blocks above its base layer (the spawning platform of a
    /// mob farm), so lighting "the floor of the build" never lights the farm itself.</summary>
    public static void AddDecks(DesignRegion d, Exclusion ex)
    {
        var levels = new SortedSet<int>();
        foreach (var b in d.Solid)
        {
            if (b.Y <= d.Min.Y) continue;
            var above = b.Up();
            if (d.Solid.Contains(above)) continue;
            if (ex.Cells.Add(above)) levels.Add(above.Y);
        }
        if (levels.Count == 0) return;
        var ys = levels.Count <= 3 ? string.Join(",", levels) : $"{levels.Min}..{levels.Max}";
        ex.Notes.Add($"{d.Label} decks y{ys} ({(d.Roles is { Length: > 0 } r ? $"DSL: {r}" : "no DSL roles: its own floors above the base")})");
    }

    /// <summary>"-30,64,12 -29,64,12 …" (up to <paramref name="n"/>).</summary>
    public static string Sample(IReadOnlyList<BlockPos> cells, int n = 3) =>
        cells.Count == 0 ? "" : string.Join(" ", cells.Take(n).Select(SkillJson.Fmt)) + (cells.Count > n ? " …" : "");
}

/// <summary>
/// <c>light_area{area|design|around, y?, exclude?, level?, max?, report?}</c>: light every cell of an area where a hostile mob
/// could spawn after dark. Plans the fewest torches over the real light model (<see cref="LightPlan"/>), gets them through the
/// Obtainer (inventory, chests, crafting from coal or charcoal + sticks; logs smelted to charcoal when that's the only way),
/// places them floor-first from stands the pathfinder reaches (never over the void), then re-checks the light and plans
/// again for whatever is still dark. <c>report=true</c> = the spawn map only (<see cref="SpawnMapSkill"/>). A design target
/// never lights its own decks (a mob farm's spawning platform) unless <c>exclude=none</c>.
/// </summary>
[Skill]
public sealed class LightAreaSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "light_area", Category = SkillCategory.Building, Summary = "torch every mob-spawnable cell (fewest torches)",
        Args = "area?|design?|around?,y?,exclude?,level?,report?",
        Schema = SkillJson.Schema("area:str?,design:str?,around:str?,radius:int?,y:str?,pad:int?,exclude:str?,level:int?,max:int?,report:bool?"),
        Estimate = TimeSpan.FromSeconds(60),
        Keywords = "torch torches light lamp dark mobs spawn spawnproof brighten night floor farm island spawning",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (args.Flag("report")) return SpawnMapSkill.Report(ctx, args);
        var (region, ex, design, error) = LightPlan.Resolve(ctx, args);
        if (error != null || region is null) return Fail(error ?? "no area");
        int level = Math.Clamp(args.Int("level") ?? 8, 8, 14);
        int max = Math.Clamp(args.Int("max") ?? 64, 1, 256);
        var dark = LightPlan.Dark(ctx.World, region, ex, level, out int exDark);
        string excl = Excluded(ex, exDark);
        if (dark.Count == 0) return Ok($"{region.Label} ({region.Box}): no spawnable cells after dark{excl}");
        var job = new Lighter(ctx, region, ex, level, max, dark);
        await job.RunAsync(ct).ConfigureAwait(false);
        return job.Result(excl);
    }

    private static string Excluded(LightPlan.Exclusion ex, int exDark) =>
        ex.Notes.Count == 0 ? "" : $"; excluded {string.Join(", ", ex.Notes.Take(2))}" + (exDark > 0 ? $" ({exDark} dark there, left alone)" : "");

    /// <summary>Up to 3 rounds of plan → obtain → place, re-measuring the dark cells after each.</summary>
    private sealed class Lighter(SkillContext ctx, LightPlan.Region region, LightPlan.Exclusion ex, int level, int max, List<BlockPos> dark)
    {
        private readonly WorldState _w = ctx.World;
        private readonly int _was = dark.Count;
        private readonly HashSet<BlockPos> _tried = new();
        private List<BlockPos> _dark = dark;
        private int _placed, _failed;
        private string? _stop, _note, _lastFail;

        public async Task RunAsync(CancellationToken ct)
        {
            for (int round = 0; round < 3 && _placed < max && _stop == null; round++)
            {
                // A torch goes into air (nothing is broken): protection only matters for a plant it would replace.
                var plan = LightPlan.Cover(_w, region, _dark, ex, level, c => !_tried.Contains(c) && (ctx.Block(c) == 0 || !ctx.Protected(c)), ctx.Feet, max - _placed);
                if (plan.Torches.Count == 0) break;
                if (round == 0) await ObtainAsync(plan.Torches.Count, ct).ConfigureAwait(false);
                await PlaceAllAsync(plan.Torches, ct).ConfigureAwait(false);
                _dark = LightPlan.Dark(_w, region, ex, level, out _);
                if (_dark.Count == 0) break;
            }
        }

        private async Task ObtainAsync(int need, CancellationToken ct)
        {
            if (ctx.Count(Blocks.Torch) >= need || ctx.Registry.Obtain is not { } obtain) return;
            try { _note = await obtain(ctx, Blocks.Torch, need, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { ctx.Log?.Invoke($"light_area obtain threw: {e.Message}"); }
        }

        private async Task PlaceAllAsync(List<LightPlan.Spot> torches, CancellationToken ct)
        {
            foreach (var spot in torches)
            {
                ct.ThrowIfCancellationRequested();
                if (_placed >= max || Stopped(torches, spot)) break;
                await PlaceAsync(spot.Pos, ct).ConfigureAwait(false);
            }
        }

        private bool Stopped(List<LightPlan.Spot> torches, LightPlan.Spot spot)
        {
            if (!ctx.Has(Blocks.Torch)) _stop = $"out of torches (~{torches.Count - torches.IndexOf(spot)} more needed{(_note is null ? "" : $": {_note}")})";
            // Mobs are why we're here: only one in my face at a void edge stops the job (the client's combat reflex
            // handles the rest; a ground torch isn't edge work).
            else if (BuildOps.HostileVeto(ctx, 3) is { } h && NearVoid(_w, ctx.Me)) _stop = h;
            return _stop != null;
        }

        private async Task PlaceAsync(BlockPos c, CancellationToken ct)
        {
            _tried.Add(c);
            if (!SkillWorld.Replaceable(_w, c)) return;
            var faces = PlaceRules.AttachFaces(_w, c, Blocks.Torch);
            if (faces.Count == 0) return;
            await ctx.ReachAsync(c, ct).ConfigureAwait(false);
            var r = await ctx.Game.PlaceAsync(c, Blocks.Torch, faces[0], ct).ConfigureAwait(false);
            if (r.Ok && await ctx.WaitBlockAsync(c, (id, _) => id == Blocks.Torch, ct).ConfigureAwait(false)) _placed++;
            else { _failed++; _lastFail = r.Message; }
        }

        public SkillResult Result(string excl)
        {
            var unlit = _dark;
            var sb = new System.Text.StringBuilder();
            sb.Append($"lit {region.Label} ({region.Box}): placed {_placed} torch{(_placed == 1 ? "" : "es")}");
            if (_failed > 0) sb.Append($" ({_failed} failed{(_lastFail is null ? "" : $": {_lastFail}")})");
            sb.Append($"; {unlit.Count} spawnable cells left (was {_was})");
            if (unlit.Count > 0) sb.Append($", e.g. {LightPlan.Sample(unlit, 2)}");
            if (_stop != null) sb.Append($"; stopped: {_stop}");
            sb.Append(excl);
            if (_note != null && _placed == 0 && _stop == null) sb.Append($"; torches: {_note}");
            sb.Append($"; {ctx.Count(Blocks.Torch)} torches left");
            return new SkillResult(_placed > 0 || unlit.Count == 0, sb.ToString());
        }
    }

    private static bool NearVoid(IBlockAccess w, BlockPos me)
    {
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
            if (SkillWorld.OverVoid(w, new BlockPos(me.X + dx, me.Y, me.Z + dz))) return true;
        return false;
    }
}

/// <summary>
/// <c>spawn_map{area|design|around, y?, exclude?}</c> (also <c>light_area{report:true}</c>): how many cells of an area can host
/// a hostile spawn after dark and now, a few of them, how many torches would light them all, and what was excluded (a mob
/// farm's own deck). Read-only, zero tokens.
/// </summary>
[Skill]
public sealed class SpawnMapSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "spawn_map", Category = SkillCategory.Building, Summary = "count dark mob-spawnable cells in an area or design",
        Args = "area?|design?|around?,y?,exclude?",
        Schema = SkillJson.Schema("area:str?,design:str?,around:str?,radius:int?,y:str?,pad:int?,exclude:str?,level:int?"),
        Estimate = TimeSpan.FromSeconds(1),
        Keywords = "spawn spawnable dark mobs light level map where do mobs spawn torches needed",
    };

    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct) =>
        Task.FromResult(Report(ctx, args));

    internal static SkillResult Report(SkillContext ctx, JsonElement args)
    {
        var (region, ex, design, error) = LightPlan.Resolve(ctx, args);
        if (error != null || region is null) return SkillResult.Fail(error ?? "no area");
        int level = Math.Clamp(args.Int("level") ?? 8, 8, 14);
        var w = ctx.World;
        var dark = LightPlan.Dark(w, region, ex, level, out int exDark);
        int now = LightPlan.Dark(w, region, ex, 8, out _, w.TimeOfDay).Count;
        var sb = new System.Text.StringBuilder($"{region.Label} ({region.Box}): {dark.Count} spawnable cells after dark");
        if (now != dark.Count) sb.Append($" ({now} now)");
        if (dark.Count > 0) AppendPlan(ctx, sb, region, ex, level, dark);
        AppendExcluded(sb, ex, exDark);
        if (!w.IsLoaded(region.X0, region.Z0) || !w.IsLoaded(region.X1, region.Z1)) sb.Append("; part of it isn't loaded");
        return SkillResult.Success(sb.ToString());
    }

    private static void AppendExcluded(System.Text.StringBuilder sb, LightPlan.Exclusion ex, int exDark)
    {
        if (ex.Notes.Count > 0) sb.Append($"; excluded {string.Join(", ", ex.Notes.Take(2))}" + (exDark > 0 ? $": {exDark} dark there" : ""));
    }

    /// <summary>", e.g. a b c …; 6 torches would light all (but N)".</summary>
    private static void AppendPlan(SkillContext ctx, System.Text.StringBuilder sb, LightPlan.Region region, LightPlan.Exclusion ex, int level, List<BlockPos> dark)
    {
        var plan = LightPlan.Cover(ctx.World, region, dark, ex, level, c => ctx.Block(c) == 0 || !ctx.Protected(c), ctx.Feet);
        sb.Append($", e.g. {LightPlan.Sample(dark)}; {plan.Torches.Count} torch{(plan.Torches.Count == 1 ? "" : "es")} would light")
          .Append(plan.Unlit.Count == 0 ? " all" : $" all but {plan.Unlit.Count}");
    }
}
