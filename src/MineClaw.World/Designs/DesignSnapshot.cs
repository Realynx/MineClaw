using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MineClaw.Data;

namespace MineClaw.World.Designs;

/// <summary>
/// One revision's block set (ARCHITECTURE.md "Design revisions"): every design cell's exact state (id, meta: the version's own
/// numbers, so a b1.8.1 and a 1.12.2 design each diff in their own ids) and the cells it expects to be air (its forced '.'
/// cells, plus, for a capture or a commit from the world, the air around it when it was taken: a block added there later is
/// "added"). Cells in neither set are don't-care (terrain under a copy, '_' cells). Full snapshots, not deltas: designs are a
/// few hundred to a few thousand blocks, and a snapshot diffs against any other revision or the world without replaying.
/// Encoded as x-runs per (y, z) row and state (<see cref="EncodeStates"/>), a few KB per revision.
/// </summary>
public sealed class DesignSnapshot
{
    public Dictionary<BlockPos, (int Id, int Meta)> Blocks { get; } = new();
    public HashSet<BlockPos> Air { get; } = new();

    public int Count => Blocks.Count;

    /// <summary>The blocks' box (air cells excluded), or null when empty.</summary>
    public (BlockPos Min, BlockPos Max)? Box
    {
        get
        {
            if (Blocks.Count == 0) return null;
            int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
            foreach (var p in Blocks.Keys)
            {
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
            }
            return (new BlockPos(x0, y0, z0), new BlockPos(x1, y1, z1));
        }
    }

    /// <summary>Content hash (blocks + air): a save that changes nothing makes no new revision.</summary>
    public string Hash()
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(EncodeStates(Blocks) + "|" + EncodeCells(Air)));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    public DesignSnapshot Clone()
    {
        var s = new DesignSnapshot();
        foreach (var kv in Blocks) s.Blocks[kv.Key] = kv.Value;
        s.Air.UnionWith(Air);
        return s;
    }

    /// <summary>
    /// The air around a block set as the world shows it now (a capture/commit/built revision): every loaded cell of the blocks'
    /// box grown by <paramref name="shell"/> that is air and not a design cell. The bot's chunk view (live or remembered).
    /// </summary>
    public void AddWorldAir(IBlockAccess world, int shell = 1)
    {
        if (Box is not { } b) return;
        for (int y = Math.Max(0, b.Min.Y - shell); y <= Math.Min(world.WorldHeight - 1, b.Max.Y + shell); y++)
            for (int z = b.Min.Z - shell; z <= b.Max.Z + shell; z++)
                for (int x = b.Min.X - shell; x <= b.Max.X + shell; x++)
                {
                    var p = new BlockPos(x, y, z);
                    if (!Blocks.ContainsKey(p) && world.IsLoaded(x, z) && world.GetBlock(x, y, z) == 0) Air.Add(p);
                }
    }

    // ------------------------------------------------------------------ codec

    /// <summary>"id:meta=y,z:x0-x1,x2;y,z:x0\n…": one line per state, its cells as x-runs per (y, z) row.</summary>
    public static string EncodeStates(IReadOnlyDictionary<BlockPos, (int Id, int Meta)> blocks)
    {
        var sb = new StringBuilder();
        foreach (var g in blocks.GroupBy(kv => kv.Value).OrderBy(g => g.Key.Id).ThenBy(g => g.Key.Meta))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{g.Key.Id}:{g.Key.Meta}=");
            sb.Append(EncodeCells(g.Select(kv => kv.Key)));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>"y,z:x0-x1,x2;…" (rows sorted, runs merged).</summary>
    public static string EncodeCells(IEnumerable<BlockPos> cells)
    {
        var sb = new StringBuilder();
        foreach (var row in cells.GroupBy(p => (p.Y, p.Z)).OrderBy(r => r.Key.Y).ThenBy(r => r.Key.Z))
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append(CultureInfo.InvariantCulture, $"{row.Key.Y},{row.Key.Z}:");
            var xs = row.Select(p => p.X).Distinct().OrderBy(x => x).ToList();
            for (int i = 0; i < xs.Count;)
            {
                int j = i;
                while (j + 1 < xs.Count && xs[j + 1] == xs[j] + 1) j++;
                if (i > 0) sb.Append(',');
                sb.Append(xs[i].ToString(CultureInfo.InvariantCulture));
                if (j > i) sb.Append('/').Append(xs[j].ToString(CultureInfo.InvariantCulture));
                i = j + 1;
            }
        }
        return sb.ToString();
    }

    public static IEnumerable<BlockPos> DecodeCells(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (var row in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = row.IndexOf(':');
            if (colon < 0 || !RowYz(row[..colon], out int y, out int z)) continue;
            foreach (var run in row[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Run(run, out int x0, out int x1)) continue;
                for (int x = x0; x <= x1; x++) yield return new BlockPos(x, y, z);
            }
        }
    }

    /// <summary>A row's "y,z" head.</summary>
    private static bool RowYz(string head, out int y, out int z)
    {
        y = z = 0;
        var yz = head.Split(',');
        return yz.Length == 2 && int.TryParse(yz[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out y)
            && int.TryParse(yz[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out z);
    }

    /// <summary>One run: "x" or "x0/x1" (runs use '/': x can be negative, "-5/-2").</summary>
    private static bool Run(string run, out int x0, out int x1)
    {
        var ab = run.Split('/');
        x1 = 0;
        if (!int.TryParse(ab[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x0)) return false;
        x1 = ab.Length > 1 && int.TryParse(ab[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e : x0;
        return true;
    }

    public static DesignSnapshot Decode(string? states, string? air)
    {
        var s = new DesignSnapshot();
        foreach (var line in (states ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var im = line[..eq].Split(':');
            if (im.Length != 2 || !int.TryParse(im[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || !int.TryParse(im[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var meta)) continue;
            foreach (var p in DecodeCells(line[(eq + 1)..])) s.Blocks[p] = (id, meta);
        }
        foreach (var p in DecodeCells(air)) if (!s.Blocks.ContainsKey(p)) s.Air.Add(p);
        return s;
    }
}

/// <summary>One differing cell: what the older side (a revision) has and what the newer side (another revision, or the world) has.</summary>
public readonly record struct DiffCell(BlockPos Pos, int FromId, int FromMeta, int ToId, int ToMeta);

/// <summary>
/// <c>diff(rA, rB)</c> and <c>diff(rev, world)</c>: added, removed and changed cells, and the compact text the brain reads
/// ("+12 stone, −3 planks, ~2 oak stairs facing"). Block names come from the process's version data (<see cref="Blocks"/>
/// over <c>GameData.Current</c>), so a 1.12.2 design names its 1.12.2 blocks. States that only say a door/gate/trapdoor is open,
/// a button/lever is on, a furnace is lit or dirt grew grass are the same block (using the build is not editing it).
/// </summary>
public sealed class DesignDiff
{
    public List<DiffCell> Added { get; } = new();
    public List<DiffCell> Removed { get; } = new();
    public List<DiffCell> Changed { get; } = new();
    /// <summary>Design cells the world couldn't show (unloaded): neither same nor different.</summary>
    public int Unknown { get; set; }

    public bool Empty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
    public int Count => Added.Count + Removed.Count + Changed.Count;
    public IEnumerable<DiffCell> All => Added.Concat(Removed).Concat(Changed);

    /// <summary>Revision <paramref name="a"/> → revision <paramref name="b"/> (block sets only; air is world-facing).</summary>
    public static DesignDiff Between(DesignSnapshot a, DesignSnapshot b)
    {
        var d = new DesignDiff();
        foreach (var (p, s) in a.Blocks)
        {
            if (!b.Blocks.TryGetValue(p, out var t)) d.Removed.Add(new DiffCell(p, s.Id, s.Meta, 0, 0));
            else if (!Same(s.Id, s.Meta, t.Id, t.Meta)) d.Changed.Add(new DiffCell(p, s.Id, s.Meta, t.Id, t.Meta));
        }
        foreach (var (p, t) in b.Blocks)
            if (!a.Blocks.ContainsKey(p)) d.Added.Add(new DiffCell(p, 0, 0, t.Id, t.Meta));
        return d;
    }

    /// <summary>
    /// A revision against the world (the bot's chunk view: live, or remembered where <see cref="WorldState.PlanningView"/> fills
    /// in): a design cell that is air now is removed, another state changed; an expected-air cell holding a block is added
    /// (fluids, fire, snow and plants that drift in are not edits). Unloaded cells count as <see cref="Unknown"/>.
    /// </summary>
    public static DesignDiff Against(DesignSnapshot rev, IBlockAccess world)
    {
        var d = new DesignDiff();
        foreach (var (p, s) in rev.Blocks) d.CompareBlock(p, s, world);
        foreach (var p in rev.Air) d.CompareAir(p, world);
        return d;
    }

    /// <summary>A design block against the world: unknown (unloaded), removed (air, or something drifted in), or changed.</summary>
    private void CompareBlock(BlockPos p, (int Id, int Meta) s, IBlockAccess world)
    {
        if (!world.IsLoaded(p.X, p.Z)) { Unknown++; return; }
        int id = world.GetBlock(p.X, p.Y, p.Z), meta = world.GetMeta(p.X, p.Y, p.Z);
        if (id == 0 || Drifts(id) && !Drifts(s.Id)) Removed.Add(new DiffCell(p, s.Id, s.Meta, id, meta));
        else if (!Same(s.Id, s.Meta, id, meta)) Changed.Add(new DiffCell(p, s.Id, s.Meta, id, meta));
    }

    /// <summary>An expected-air cell holding a block that didn't drift in: added.</summary>
    private void CompareAir(BlockPos p, IBlockAccess world)
    {
        if (!world.IsLoaded(p.X, p.Z)) return;
        int id = world.GetBlock(p.X, p.Y, p.Z);
        if (id != 0 && !Drifts(id)) Added.Add(new DiffCell(p, 0, 0, id, world.GetMeta(p.X, p.Y, p.Z)));
    }

    /// <summary>The same cells the other way round (world → revision: what a revert places, digs and swaps).</summary>
    public DesignDiff Inverse()
    {
        var d = new DesignDiff { Unknown = Unknown };
        d.Added.AddRange(Removed.Select(c => new DiffCell(c.Pos, c.ToId, c.ToMeta, c.FromId, c.FromMeta)));
        d.Removed.AddRange(Added.Select(c => new DiffCell(c.Pos, c.ToId, c.ToMeta, c.FromId, c.FromMeta)));
        d.Changed.AddRange(Changed.Select(c => new DiffCell(c.Pos, c.ToId, c.ToMeta, c.FromId, c.FromMeta)));
        return d;
    }

    /// <summary>Fluids, fire, snow layers and plants: they flow, burn, fall and grow into cells by themselves.</summary>
    public static bool Drifts(int id)
    {
        if (id == 0) return false;
        var n = Blocks.Get(id).Name;
        return Blocks.IsWater(id) || Blocks.IsLava(id) || id == Blocks.Fire || DriftingPlant(id) || n is "double_plant" or "waterlily" or "web";
    }

    /// <summary>Tall grass, dead bushes, snow layers, vines, mushrooms and flowers.</summary>
    private static bool DriftingPlant(int id) => id is Blocks.TallGrass or Blocks.DeadBush or Blocks.SnowLayer
        or Blocks.Vine or Blocks.BrownMushroom or Blocks.RedMushroom or 37 or 38;

    /// <summary>The same block for a design: exact id and meta, except use-state bits (open, powered, lit, grown).</summary>
    public static bool Same(int aId, int aMeta, int bId, int bMeta)
    {
        if (!PlacedBlockLog.SameBlock(aId, bId)) return false;
        if (aId != bId) return true; // lit furnace, grass on dirt, toggled redstone torch: one family
        if (aMeta == bMeta) return true;
        return UseState(aId, aMeta) is { } mask && (aMeta & ~mask) == (bMeta & ~mask);
    }

    /// <summary>The meta bits using a block flips (mc-dev BlockDoor/BlockTrapDoor/BlockFenceGate/BlockLever/BlockButton;
    /// 1.12.2 keeps the same bits): a door's lower half 4 = open, its upper half (8 set) 4 and 2 (1.12.2's powered bit; the
    /// lower half carries the facing), trapdoors and gates 4, levers and buttons 8, plates 1. Null = every bit is shape.</summary>
    private static int? UseState(int id, int meta)
    {
        var n = Blocks.Get(id).Name;
        if (n.Contains("trapdoor", StringComparison.Ordinal) || n.Contains("fence_gate", StringComparison.Ordinal)) return 4;
        if (n.Contains("door", StringComparison.Ordinal)) return (meta & 8) != 0 ? 4 | 2 : 4;
        if (n is "lever" || n.Contains("button", StringComparison.Ordinal)) return 8;
        if (n.Contains("pressure_plate", StringComparison.Ordinal)) return 1;
        return null;
    }

    /// <summary>
    /// "+12 stone, −3 planks, ~2 oak stairs facing" (most first, at most <paramref name="max"/> kinds per sign; a changed block
    /// of another kind reads "~1 planks→stone"). Empty = "no changes".
    /// </summary>
    public string Text(int max = 3)
    {
        if (Empty) return "no changes";
        var parts = new List<string>();
        static string N(int id) => Blocks.Get(id).Name.Replace('_', ' ');
        void Kinds(string sign, IEnumerable<string> names)
        {
            var g = names.GroupBy(x => x).Select(x => (x.Key, Count: x.Count())).OrderByDescending(x => x.Count).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
            foreach (var (name, n) in g.Take(max)) parts.Add($"{sign}{n} {name}");
            if (g.Count > max) parts.Add($"{sign}{g.Skip(max).Sum(x => x.Count)} other");
        }
        Kinds("+", Added.Select(c => N(c.ToId)));
        Kinds("−", Removed.Select(c => N(c.FromId)));
        Kinds("~", Changed.Select(c => c.FromId == c.ToId ? $"{N(c.ToId)} {What(c.ToId)}" : $"{N(c.FromId)}→{N(c.ToId)}"));
        return string.Join(", ", parts);
    }

    /// <summary>What a same-block meta change is: "facing" for blocks whose meta is their direction, else "state".</summary>
    private static string What(int id)
    {
        var n = Blocks.Get(id).Name;
        string[] facing = ["stairs", "door", "torch", "ladder", "furnace", "chest", "pumpkin", "lantern", "sign", "lever", "button", "bed", "rail",
            "piston", "repeater", "comparator", "observer", "dispenser", "dropper", "hopper", "gate", "log", "anvil", "skull", "banner", "vine", "cocoa"];
        return facing.Any(f => n.Contains(f, StringComparison.Ordinal)) ? "facing" : "state";
    }

    /// <summary>"12,64,-3 +stone; 12,65,-3 −planks; …" (first <paramref name="max"/> cells), for the Explorer and logs.</summary>
    public string Cells(int max = 8)
    {
        static string N(int id) => Blocks.Get(id).Name.Replace('_', ' ');
        var all = Added.Select(c => (c.Pos, T: "+" + N(c.ToId)))
            .Concat(Removed.Select(c => (c.Pos, T: "−" + N(c.FromId))))
            .Concat(Changed.Select(c => (c.Pos, T: c.FromId == c.ToId ? $"~{N(c.ToId)} {c.FromMeta}→{c.ToMeta}" : $"~{N(c.FromId)}→{N(c.ToId)}"))).ToList();
        return string.Join("; ", all.Take(max).Select(c => $"{c.Pos.X},{c.Pos.Y},{c.Pos.Z} {c.T}")) + (all.Count > max ? $"; +{all.Count - max} more" : "");
    }
}
