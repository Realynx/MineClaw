using System.Globalization;
using System.Text;
using MineClaw.Bot.WorldIndex;
using MineClaw.Render;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Bot.Perception.Vision;

/// <summary>A hive bot as the hive mind knows it: last snapshot position, job and vitals.</summary>
public sealed record HiveBotState(string Name, int Dim, double X, double Y, double Z, string? Job, int Hp, int Food, DateTimeOffset At,
    bool IsMain = false, string? Brain = null)
{
    /// <summary>"67" for maid_chan_67, "main" for the hive mind.</summary>
    public string Short => IsMain ? "main" : HiveVision.ShortName(Name);
}

/// <summary>A clone's live eye pose ("send me your view" over IPC); its client flushed its world memory to the shared db first.</summary>
public sealed record HiveEyePose(string Name, int Dim, double X, double Y, double Z, float Yaw, float Pitch, DateTimeOffset At, int Flushed = 0);

/// <summary>A live claim-board lease (gen cells, chests, areas being worked).</summary>
public sealed record HiveLease(string Key, string Owner);

/// <summary>
/// The hive seen from main (implemented by the Hive layer: supervisor snapshots, claim board, IPC). Positions, jobs and claims
/// are data: the hive overview and remote <c>see</c> foci come from here, never from a model.
/// </summary>
public interface IHiveVision
{
    /// <summary>Live clones (main itself is added by the hub).</summary>
    IReadOnlyList<HiveBotState> Clones();
    IReadOnlyList<HiveLease> Leases();
    /// <summary>Asks a live clone for its view: it flushes its world memory (fresh columns in the shared db) and returns its pose.</summary>
    Task<HiveEyePose?> ViewAsync(string clone, CancellationToken ct);
}

/// <summary>Who saw a chunk column last and when (for staleness labels).</summary>
public sealed record ColumnSource(string? By, DateTimeOffset At, bool Live);

/// <summary>
/// A region of the shared world memory for one look: main's live columns (when it is in that dimension) merged with the
/// remembered columns every hive bot writes to the db (<c>world_chunks</c>, any dimension), with who saw each column when.
/// Lets main render a landmark, a clone's surroundings or the whole island, wherever main itself is.
/// </summary>
public sealed class RegionWorld
{
    public required WorldState World { get; init; }
    public int Dim => World.Dimension;
    public Dictionary<(int Cx, int Cz), ColumnSource> Seen { get; } = new();

    public static RegionWorld Build(WorldState live, string liveName, IWorldMemoryStore? store, int dim, int x, int z, int radius, DateTimeOffset now)
    {
        var w = new WorldState { Dimension = dim };
        var region = new RegionWorld { World = w };
        int r = Math.Clamp(radius, 1, 64) + 4;
        for (int cx = (x - r) >> 4; cx <= (x + r) >> 4; cx++)
            for (int cz = (z - r) >> 4; cz <= (z + r) >> 4; cz++)
            {
                if (live.Dimension == dim && live.GetColumn(cx, cz) is { HasData: true } col)
                {
                    var snap = ChunkSnapshot.Capture(col);
                    if (w.Remember(new RememberedColumn(cx, cz, dim, now, snap))) region.Seen[(cx, cz)] = new ColumnSource(liveName, now, true);
                    continue;
                }
                ChunkRow? row = null;
                try { row = store?.GetChunk(dim, cx, cz); } catch (Exception) { /* memory unavailable: unknown */ }
                if (row != null)
                {
                    var data = row.Data;
                    if (w.Remember(new RememberedColumn(cx, cz, dim, row.LastSeen, () => ChunkSnapshot.Decode(data))))
                        region.Seen[(cx, cz)] = new ColumnSource(row.SeenBy, row.LastSeen, false);
                    continue;
                }
                if (live.Dimension == dim && live.GetRemembered(cx, cz) is { } rem && w.Remember(new RememberedColumn(cx, cz, dim, rem.LastSeen, () => rem.Blocks)))
                    region.Seen[(cx, cz)] = new ColumnSource(null, rem.LastSeen, false);
            }
        return region;
    }

    public static string Age(TimeSpan t) => t.TotalSeconds < 90 ? $"{Math.Max(0, (int)t.TotalSeconds)} s"
        : t.TotalMinutes < 90 ? $"{(int)t.TotalMinutes} min" : t.TotalHours < 48 ? $"{(int)t.TotalHours} h" : $"{(int)t.TotalDays} d";

    /// <summary>
    /// Staleness per region around the focus: "here live (main); E side seen by 67 12 s ago; N 40 min ago; S never seen".
    /// The focus column first, then the four sides (the oldest column of each side decides).
    /// </summary>
    public string Staleness((int X, int Y, int Z) f, int radius, DateTimeOffset now, string? landmark = null)
    {
        string Desc(ColumnSource s, bool here) => s.Live ? $"live ({ShortBy(s.By)})"
            : $"seen {(s.By != null ? "by " + ShortBy(s.By) + " " : "")}{Age(now - s.At)} ago";
        int fcx = f.X >> 4, fcz = f.Z >> 4;
        var parts = new List<string>();
        string here = Seen.TryGetValue((fcx, fcz), out var hs) ? Desc(hs, true) : "never seen";
        parts.Add((landmark != null ? landmark + " " : "here ") + here);
        var sides = new Dictionary<string, List<ColumnSource?>> { ["N"] = new(), ["E"] = new(), ["S"] = new(), ["W"] = new() };
        int r = Math.Clamp(radius, 1, 64);
        for (int cx = (f.X - r) >> 4; cx <= (f.X + r) >> 4; cx++)
            for (int cz = (f.Z - r) >> 4; cz <= (f.Z + r) >> 4; cz++)
            {
                if (cx == fcx && cz == fcz) continue;
                int dx = cx - fcx, dz = cz - fcz;
                string side = Math.Abs(dx) >= Math.Abs(dz) ? (dx > 0 ? "E" : "W") : (dz > 0 ? "S" : "N");
                sides[side].Add(Seen.GetValueOrDefault((cx, cz)));
            }
        var groups = new List<(string Text, List<string> Sides)>();
        foreach (var (side, cols) in sides)
        {
            if (cols.Count == 0) continue;
            string text;
            if (cols.All(c => c == null)) text = "never seen";
            else
            {
                var oldest = cols.Where(c => c != null).OrderBy(c => c!.Live ? 1 : 0).ThenBy(c => c!.At).First()!;
                text = Desc(oldest, false) + (cols.Any(c => c == null) ? " (partly unknown)" : "");
            }
            if (text == here && !text.StartsWith("never", StringComparison.Ordinal)) continue; // same as the focus: not news
            var g = groups.FirstOrDefault(x => x.Text == text);
            if (g.Text == null) groups.Add((text, new List<string> { side }));
            else g.Sides.Add(side);
        }
        foreach (var (text, ss) in groups) parts.Add($"{string.Join(",", ss)} side {text}");
        return string.Join("; ", parts);
    }

    private static string ShortBy(string? by) => by == null ? "?" : HiveVision.ShortName(by);
}

/// <summary>The hive overview (deterministic: positions, jobs, claims and dangers are data) and name helpers.</summary>
public static class HiveVision
{
    /// <summary>"maid_chan_67" → "67"; other names unchanged.</summary>
    public static string ShortName(string name)
    {
        int i = name.LastIndexOf('_');
        return i >= 0 && i < name.Length - 1 && name[(i + 1)..].All(char.IsDigit) ? name[(i + 1)..] : name;
    }

    /// <summary>A clone named by its number, full name or "clone 67"; null = not a hive bot.</summary>
    public static HiveBotState? Find(IEnumerable<HiveBotState> bots, string text)
    {
        var t = text.Trim().TrimStart('@').ToLowerInvariant();
        if (t.StartsWith("clone ", StringComparison.Ordinal)) t = t[6..].Trim();
        foreach (var b in bots)
            if (b.Name.Equals(t, StringComparison.OrdinalIgnoreCase) || b.Short.Equals(t, StringComparison.OrdinalIgnoreCase)) return b;
        return null;
    }

    private static string P(double x, double y, double z) => string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Floor(x)},{(int)Math.Floor(y)},{(int)Math.Floor(z)}");

    private static string DimName(int d) => d switch { -1 => "nether", 0 => "overworld", 1 => "end", _ => "dim " + d };

    /// <summary>A lease as text: "block 4,64,6", "chest 14,64,-5", "area 0,63,0..8,63,8", "task chop".</summary>
    public static (string Text, ClaimKey? Key) Lease(string key)
    {
        if (!ClaimKey.TryParse(key, out var k)) return (key, null);
        string Pt(BlockPos p) => $"{p.X},{p.Y},{p.Z}";
        var text = k.Kind switch
        {
            ClaimKind.Task => "task " + k.Name,
            ClaimKind.Area => $"area {Pt(k.Min)}..{Pt(k.Max)}",
            _ => $"{k.Kind.ToString().ToLowerInvariant()} {Pt(k.Min)}",
        };
        return (text, k);
    }

    /// <summary>
    /// The overview text (≈100-150 tokens): every bot with position, job, vitals and snapshot age; claims; dangers; staleness.
    /// </summary>
    public static string Text(IReadOnlyList<HiveBotState> bots, IReadOnlyList<HiveLease> leases, IReadOnlyList<string> dangers,
        DateTimeOffset now, string? staleness)
    {
        var sb = new StringBuilder($"[hive] {bots.Count} bot{(bots.Count == 1 ? "" : "s")}: ");
        var lines = new List<string>();
        foreach (var b in bots.OrderBy(b => b.IsMain ? 0 : 1).ThenBy(b => b.Short, StringComparer.Ordinal))
        {
            var mine = leases.Where(l => l.Owner.Equals(b.Name, StringComparison.OrdinalIgnoreCase)).Select(l => Lease(l.Key).Text).Take(2).ToList();
            string job = string.IsNullOrWhiteSpace(b.Job) ? "idle" : Trunc(b.Job!.Trim(), 48);
            string where = (b.Dim != 0 ? DimName(b.Dim) + " " : "") + "@" + P(b.X, b.Y, b.Z);
            string age = b.IsMain || now - b.At < TimeSpan.FromSeconds(10) ? "" : $", {RegionWorld.Age(now - b.At)} ago";
            lines.Add($"{b.Short} {where} {job}{(mine.Count > 0 ? " (claims " + string.Join(", ", mine) + ")" : "")} hp{b.Hp} food{b.Food}{age}");
        }
        sb.Append(string.Join("; ", lines));
        var free = leases.Where(l => !bots.Any(b => b.Name.Equals(l.Owner, StringComparison.OrdinalIgnoreCase))).Take(3).ToList();
        if (free.Count > 0) sb.Append(" | other claims: ").Append(string.Join(", ", free.Select(l => $"{ShortName(l.Owner)} {Lease(l.Key).Text}")));
        sb.Append(" | danger: ").Append(dangers.Count == 0 ? "none" : string.Join("; ", dangers.Take(4)));
        if (!string.IsNullOrWhiteSpace(staleness)) sb.Append(" | map: ").Append(staleness);
        return sb.ToString();
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>Danger markers from vitals: low hp/food (the hub adds hazards from the world index).</summary>
    public static List<string> VitalDangers(IEnumerable<HiveBotState> bots)
    {
        var d = new List<string>();
        foreach (var b in bots)
        {
            if (b.Hp is > 0 and <= 6) d.Add($"{b.Short} hp {b.Hp}");
            if (b.Food is >= 0 and <= 4 && b.Hp > 0) d.Add($"{b.Short} food {b.Food}");
        }
        return d;
    }

    /// <summary>
    /// The overview picture: one top-down panel of the area holding the bots of <paramref name="dim"/> (≈300 tokens), every bot
    /// as a labelled hive marker, claimed blocks/areas as gold boxes, dangers as labelled marks.
    /// </summary>
    public static VisionImage Image(RegionWorld region, IReadOnlyList<HiveBotState> bots, IReadOnlyList<HiveLease> leases,
        IReadOnlyList<(string Text, int X, int Y, int Z)> dangerMarks, (int X, int Y, int Z) centre, int radius, RoleMap? roles = null,
        VisionRenderer? renderer = null, TextureAtlas? atlas = null)
    {
        var ents = bots.Where(b => b.Dim == region.Dim)
            .Select(b => new VisionEntity("hive", b.Short, b.X, b.Y, b.Z, b.Short[0])).ToList();
        var marks = new List<VisionMark>();
        foreach (var l in leases)
        {
            var (_, k) = Lease(l.Key);
            if (k is not { } key || key.Kind == ClaimKind.Task || key.Dim != region.Dim) continue;
            var label = ShortName(l.Owner) + " claim";
            marks.Add(new VisionMark(label, key.Min.X, key.Min.Y, key.Min.Z, key.Max.X, key.Max.Y, key.Max.Z, Box: true));
        }
        foreach (var (t, x, y, z) in dangerMarks) marks.Add(VisionMark.At("! " + t, x, y, z));
        int r = Math.Clamp(radius, 6, 24), n = 2 * r + 1;
        return (renderer ?? new VisionRenderer()).Render(new VisionScene
        {
            View = VisionView.Top, Radius = r, Cx = centre.X, Cy = centre.Y, Cz = centre.Z, Blocks = region.World.PlanningView,
            Entities = ents, Marks = marks.Take(12).ToList(), Roles = roles, CellPx = Math.Clamp(440 / n, 6, 16), Up = 2, Down = 8, CutAway = true,
            Atlas = atlas, MaxPx = 440,
        });
    }
}
