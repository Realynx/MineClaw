using System.Text;
using MineClaw.World.Claims;
using MineClaw.World.Designs;
using static MineClaw.World.Tests.Golden;

namespace MineClaw.World.Tests;

/// <summary>
/// World odds and ends golden (complexity refactor WP04, checkpoint 3), taken before they were reshaped: lighting (sky and block
/// light, light levels and spawn checks after building, relighting and edits over the <see cref="GoldenWorlds"/>), block-change
/// events and block searches, door looks, line-of-sight filters, column payloads both ways (and their rejections), chop
/// ownership, design cell codes and diffs, and claim keys (parse and conflicts) over a corpus.
/// </summary>
public class MiscGoldenTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void World_matches_the_golden(bool release1122) => Check($"world-{(release1122 ? "1122" : "b181")}",
        release1122 ? "E69F4BA4C9DDA9A4BFDE2034681142CD91B604526FF0E02DF0DCF5049F6C43E7" : "7021F39E2F68E948C04705CDBE03E51D92F889EE6B7FF33479A16D24C67285FF", sb =>
    {
        using var _ = GoldenWorlds.Data(release1122);
        foreach (var kind in GoldenWorlds.Kinds)
        {
            var w = GoldenWorlds.Build(kind, 3, release1122);
            sb.Append("== ").Append(kind).Append('\n');
            var changes = new List<BlockChange>();
            w.BlockChanged += c => changes.Add(c);
            var rng = new Random(kind.Length * 7 + (release1122 ? 1 : 0));
            for (int i = 0; i < 12; i++)
            {
                int x = rng.Next(GoldenWorlds.Min, GoldenWorlds.Max + 1), z = rng.Next(GoldenWorlds.Min, GoldenWorlds.Max + 1);
                int y = (GoldenWorlds.Surface(w, x, z)?.Y ?? 64) + rng.Next(-2, 3);
                int id = new[] { 0, 1, 50, 89, 20, 8, 10, 18, 44, 0 }[rng.Next(10)];
                w.SetBlock(x, y, z, id, id == 44 ? 8 : 0);
                w.SetBlock(x, y, z, id, id == 44 ? 8 : 0); // the same again: no change
            }
            foreach (var c in changes) sb.Append(" chg ").Append(c.Pos).Append(' ').Append(c.OldId).Append('/').Append(c.OldMeta).Append('>').Append(c.NewId).Append('/').Append(c.NewMeta).Append('\n');
            for (int i = 0; i < 40; i++)
            {
                int x = rng.Next(GoldenWorlds.Min, GoldenWorlds.Max + 1), z = rng.Next(GoldenWorlds.Min, GoldenWorlds.Max + 1);
                int y = (GoldenWorlds.Surface(w, x, z)?.Y ?? 64) + rng.Next(-4, 3);
                var p = new BlockPos(x, y, z);
                sb.Append(' ').Append(p).Append(" sky=").Append(w.Light.Get(true, x, y, z)).Append(" blk=").Append(w.Light.Get(false, x, y, z))
                    .Append(" lvl=").Append(w.Light.LightLevel(x, y, z, 0)).Append('/').Append(w.Light.LightLevel(x, y, z, 11)).Append('/').Append(w.Light.LightLevel(x, y, z, 4, false))
                    .Append(" h=").Append(w.Light.Height(x, z)).Append(" seesky=").Append(w.Light.CanSeeSky(x, y, z))
                    .Append(" spawn=").Append(SpawnRules.Check(w, p, 11)).Append('/').Append(SpawnRules.Check(w, p, 0, false, 9))
                    .Append(" looks=").Append(Openables.LooksOpen(w, p))
                    .Append(" line=").Append(GoalFiltered.ClearLine(w, x, y, z, new BlockPos(x + rng.Next(-6, 7), y + rng.Next(-3, 4), z + rng.Next(-6, 7))))
                    .Append('\n');
            }
            foreach (int id in new[] { 1, 17, 54, 64, 8 })
            {
                var found = w.FindBlocks(new Vec3d(0.5, 64, 0.5), 20, id, 12);
                var some = w.FindBlocks(new Vec3d(-3.5, 60, 2.5), 14, (b, m) => b == id || m == 3, p => (p.X + p.Z) % 3 != 0, 9);
                sb.Append(" find ").Append(id).Append(':').Append(string.Join(' ', found)).Append(" | ").Append(string.Join(' ', some)).Append('\n');
            }
            for (int x = GoldenWorlds.Min; x <= GoldenWorlds.Max; x++)
            for (int z = GoldenWorlds.Min; z <= GoldenWorlds.Max; z++)
            for (int y = 60; y < 72; y++)
                if (Openables.IsOpenable(w.GetBlock(x, y, z))) sb.Append(" door ").Append(x).Append(',').Append(y).Append(',').Append(z).Append('=').Append(Openables.LooksOpen(w, new BlockPos(x, y, z)));
            sb.Append('\n');
            w.Light.Relight(0, 0);
            for (int x = -4; x <= 4; x++) sb.Append(w.Light.Get(true, x, 62, 3)).Append(w.Light.Get(false, x, 63, 3)).Append(',');
            sb.Append('\n');
        }
    });

    [Fact]
    public void Codes_match_the_golden() => Check("codes", "00BA6BD412C9EC9150E9EA8E44673EB21D7BBCFB247E4B2DFF2F0985312E26F7", sb =>
    {
        // Column payloads.
        var cols = new List<ColumnPayloadEntry>
        {
            new(0, 0, 12345, 99, "alice", true, [1, 2, 3]),
            new(-3, 7, -1, 0, null, false, []),
            new(5, -5, long.MaxValue, long.MinValue, new string('x', 80), true, [9, 8, 7, 6], [4, 4]),
        };
        foreach (var set in new[] { cols.Take(2).ToList(), cols })
        {
            var bytes = ColumnPayload.Encode(-1, set);
            sb.Append("payload ").Append(Convert.ToHexString(bytes)).Append('\n');
            var (dim, back) = ColumnPayload.Decode(bytes);
            sb.Append(" dim=").Append(dim);
            foreach (var c in back)
                sb.Append(" [").Append(c.Cx).Append(',').Append(c.Cz).Append(',').Append(c.Hash).Append(',').Append(c.SeenMs).Append(',').Append(c.Live)
                    .Append(',').Append(c.By ?? "-").Append(',').Append(Convert.ToHexString(c.Data)).Append(',').Append(c.Light is null ? "-" : Convert.ToHexString(c.Light)).Append(']');
            sb.Append('\n');
            for (int cut = 0; cut <= bytes.Length; cut += Math.Max(1, bytes.Length / 9))
                sb.Append(" cut").Append(cut).Append('=').Append(Try(() => ColumnPayload.Decode(bytes.AsSpan(0, cut)).Columns.Count.ToString())).Append('\n');
            var extra = bytes.Concat(new byte[] { 0 }).ToArray();
            sb.Append(" trailing=").Append(Try(() => ColumnPayload.Decode(extra).Columns.Count.ToString())).Append('\n');
            var bad = (byte[])bytes.Clone();
            bad[4] = 9;
            sb.Append(" version=").Append(Try(() => ColumnPayload.Decode(bad).Columns.Count.ToString())).Append('\n');
            bad = (byte[])bytes.Clone();
            bad[12] = 0xFF; bad[13] = 0xFF;
            sb.Append(" count=").Append(Try(() => ColumnPayload.Decode(bad).Columns.Count.ToString())).Append('\n');
        }

        // Chop ownership.
        var log = new ChopLog();
        var t0 = DateTimeOffset.UnixEpoch;
        log.Note(new ChopRecord(new(0, 64, 0), new(0, 63, 0), 0, 17, new HashSet<BlockPos> { new(0, 64, 0), new(0, 65, 0) }, new HashSet<BlockPos> { new(0, 66, 0) }, "a", t0));
        log.Note(new ChopRecord(new(5, 64, 5), new(5, 63, 5), -1, 17, new HashSet<BlockPos> { new(5, 64, 5) }, new HashSet<BlockPos>(), "b", t0.AddMinutes(30)));
        log.Note(new ChopRecord(new(9, 64, 9), new(9, 63, 9), 0, 17, new HashSet<BlockPos>(), new HashSet<BlockPos>(), "c", t0));
        for (int x = -1; x <= 6; x++)
        for (int y = 62; y <= 69; y++)
        foreach (int z in new[] { -1, 0, 1, 5, 6 })
        foreach (int dim in new[] { 0, -1 })
        foreach (var now in new[] { t0.AddMinutes(10), t0.AddHours(2.2), t0.AddHours(3) })
            if (log.Owner(new BlockPos(x, y, z), dim, now) is { } r) sb.Append(" chop ").Append(x).Append(',').Append(y).Append(',').Append(z).Append('@').Append(dim).Append('/').Append(now.ToUnixTimeSeconds()).Append('=').Append(r.Bot);
        sb.Append('\n');

        // Design cell codes and diffs.
        foreach (var text in new[] { null, "", "  ", "64,0:1/3", "64,0:-5/-2,4;63,1:0", "bad;64:1;x,y:1;64,0:a,2/b,7/9/11", "64,0:3/1", "64,0:" })
            sb.Append(" cells [").Append(text ?? "null").Append("] ").Append(string.Join(' ', DesignSnapshot.DecodeCells(text))).Append('\n');
        var a = new DesignSnapshot();
        var b = new DesignSnapshot();
        for (int i = 0; i < 12; i++)
        {
            a.Blocks[new BlockPos(i, 64, 0)] = (i % 3 == 0 ? 64 : 1, i % 2 == 0 ? 4 : 0);
            if (i % 4 != 1) b.Blocks[new BlockPos(i, 64, 0)] = (i % 3 == 0 ? 64 : i % 5 == 0 ? 4 : 1, 0);
        }
        b.Blocks[new BlockPos(0, 65, 0)] = (5, 0);
        a.Air.Add(new BlockPos(0, 66, 0));
        var d = DesignDiff.Between(a, b);
        sb.Append(" between +").Append(d.Added.Count).Append(" -").Append(d.Removed.Count).Append(" ~").Append(d.Changed.Count).Append(' ').Append(string.Join(' ', d.All)).Append('\n');
        var world = GoldenWorlds.Build("doors", 1, false);
        foreach (var (p, s) in a.Blocks) if (p.X % 2 == 0) world.SetBlock(p.X, p.Y, p.Z, s.Id, s.Meta ^ 4);
        world.SetBlock(0, 66, 0, 8, 0);
        a.Air.Add(new BlockPos(1, 66, 0));
        world.SetBlock(1, 66, 0, 1, 0);
        a.Blocks[new BlockPos(2, 66, 0)] = (31, 1);
        a.Blocks[new BlockPos(3, 66, 0)] = (1, 0);
        world.SetBlock(3, 66, 0, 31, 1);
        a.Blocks[new BlockPos(500, 64, 0)] = (1, 0);
        var against = DesignDiff.Against(a, world);
        sb.Append(" against +").Append(against.Added.Count).Append(" -").Append(against.Removed.Count).Append(" ~").Append(against.Changed.Count).Append(" ?").Append(against.Unknown)
            .Append(' ').Append(string.Join(' ', against.All)).Append('\n');
        for (int id = 0; id < 300; id++) sb.Append(DesignDiff.Drifts(id) ? '1' : '0');
        sb.Append('\n');

        // Claim keys.
        var ids = new[]
        {
            "block:1,64,-3", "block:1,64,-3@-1", "area:0,60,0..4,70,4", "area:4,70,4..0,60,0@1", "chest:1,64,-3", "chest:2,64,-3", "chest:1,65,-3",
            "chest:1,64,-2@-1", "furnace:1,64,-3", "task:Cobble", "task:", "task:a@b", "block:1,64", "block:a,b,c", "area:0,0,0", "area:0,0,0..x",
            "chest:1,64,-3@x", "nope:1,2,3", ":1,2,3", "", null, "block", "area:1,64,-3..1,64,-3", "block:2,64,-3",
        };
        var keys = new List<ClaimKey>();
        foreach (var id in ids)
        {
            bool ok = ClaimKey.TryParse(id, out var k);
            sb.Append(" claim [").Append(id ?? "null").Append("] ").Append(ok).Append(' ').Append(ok ? k.Id : "-").Append('\n');
            if (ok) keys.Add(k);
        }
        keys.Add(ClaimKey.Task("cobble"));
        foreach (var x in keys)
        {
            sb.Append(' ');
            foreach (var y in keys) sb.Append(x.ConflictsWith(y) ? '1' : '0');
            sb.Append('\n');
        }
    });

    private static string Try(Func<string> f)
    {
        try { return f(); }
        catch (Exception e) { return e.GetType().Name + ":" + e.Message; }
    }
}
