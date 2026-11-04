using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;
using MineClaw.World.Versioning;

namespace MineClaw.Skills.Tests;

/// <summary>
/// Pins what the live skills DO (WP09 complexity refactor): every goto / dig / place / use / click a skill asks of the game, in
/// order, with its result text, then the skill's own result and a hash of the world and inventory afterwards, over the offline
/// <see cref="FakeGame"/> on b1.8.1 and 1.12.2 (scenarios taken from the skills' behaviour tests). A refactor must leave
/// <see cref="SkillTraceGoldenData"/> unchanged. Regenerate (only for an intended change) with WP09_GOLDEN_OUT=&lt;dir&gt;:
/// each area is written to &lt;dir&gt;/&lt;area&gt;.txt.
/// </summary>
public class SkillTraceGoldenTests
{
    // ------------------------------------------------------------------ harness

    private static async Task<SkillResult> Run(StringBuilder sb, FakeGame g, string skill, object? args = null, SkillRegistry? reg = null)
    {
        reg ??= SkillRegistry.CreateDefault();
        var t = new TraceGame(g);
        var r = await reg.RunAsync(new SkillContext(t, reg), skill, args is null ? SkillJson.Empty : FakeGame.J(args), CancellationToken.None);
        sb.Append("> ").Append(skill).Append(' ').Append(args is null ? "{}" : JsonSerializer.Serialize(args)).Append('\n');
        var trace = t.Trace.ToString();
        if (Hashed.Value)
            sb.Append("  ").Append(trace.Count(ch => ch == '\n')).Append(" actions ")
                .Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trace)))[..16]).Append('\n');
        else sb.Append(trace);
        // The move planner's report ends with the seconds it took: not part of the behaviour.
        var text = System.Text.RegularExpressions.Regex.Replace(r.ToString().ReplaceLineEndings(" | "), @"(\d+ clicks), \d+s", "$1, Ns");
        sb.Append("= ").Append(text).Append('\n');
        sb.Append("  world ").Append(WorldHash(g)).Append(" violations ").Append(g.Violations.Count).Append('\n');
        return r;
    }

    private static string WorldHash(FakeGame g)
    {
        var w = g.World;
        var bytes = new List<byte>(1 << 20);
        for (int x = -32; x < 32; x++)
            for (int z = -32; z < 32; z++)
                for (int y = 0; y < w.WorldHeight; y++)
                {
                    bytes.Add((byte)w.GetBlock(x, y, z));
                    bytes.Add((byte)w.GetMeta(x, y, z));
                }
        foreach (var s in w.Inventory.PlayerSlots()) { bytes.Add((byte)(s.Id & 0xFF)); bytes.Add((byte)(s.Id >> 8)); bytes.Add(s.Count); }
        foreach (var (p, items) in g.Chests.OrderBy(kv => kv.Key.X).ThenBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.Z))
            foreach (var s in items) { bytes.Add((byte)(s.Id & 0xFF)); bytes.Add((byte)(s.Id >> 8)); bytes.Add(s.Count); }
        var f = w.Player.Position;
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()))[..16] + $" @{f.X:0.##},{f.Y:0.##},{f.Z:0.##}";
    }

    private static IDisposable? Version(bool release1122) => release1122 ? GameData.Override(Release1122GameData.Instance) : null;

    /// <summary>Long traces (the field's water simulation) are pinned by count and hash per call instead of line by line.</summary>
    private static readonly AsyncLocal<bool> Hashed = new();

    private static async Task<string> Area(string name, Func<StringBuilder, Task> body, bool hashed = false)
    {
        Hashed.Value = hashed;
        var sb = new StringBuilder();
        await body(sb);
        var text = sb.ToString().ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("WP09_GOLDEN_OUT") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".txt"), text);
        }
        return text;
    }

    private static void Same(string expected, string actual) =>
        Assert.Equal(expected.ReplaceLineEndings("\n").TrimEnd('\n'), actual.TrimEnd('\n'));

    private static IEnumerable<BlockPos> Cuboid(BlockPos a, BlockPos b)
    {
        for (int x = Math.Min(a.X, b.X); x <= Math.Max(a.X, b.X); x++)
            for (int y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++)
                for (int z = Math.Min(a.Z, b.Z); z <= Math.Max(a.Z, b.Z); z++)
                    yield return new BlockPos(x, y, z);
    }

    private static void Carve(FakeGame g, IEnumerable<BlockPos> cells) { foreach (var c in cells) g.Set(c, 0); }

    private static ItemStack S(int id, int n, int d = 0) => new((short)id, (byte)n, (short)d);

    // ------------------------------------------------------------------ excavation

    private const string FoxRule = "column 3 step 0,-1,0 turn -90 pivot 0.5,0.5";
    private static DigPattern Fox(int top = 60) => DigPattern.Parse(FoxRule, new BlockPos(0, top, 0), out _)!;

    private static FakeGame Rock(bool release1122, int top = 70) =>
        new(release1122 ? 256 : 128, (_, y, _) => y == 0 ? Blocks.Bedrock : y <= top ? Blocks.Stone : 0,
            release1122 ? Release1122PhysicsRules.Instance : null)
        { StrictReach = true };

    private static async Task Excavation(StringBuilder sb, bool v)
    {
        using var _ = Version(v);
        sb.Append("## room from a pocket\n");
        {
            int y = v ? 11 : 30;
            var g = Rock(v);
            Carve(g, [new(0, y, 0), new(0, y + 1, 0)]);
            g.Stand(0.5, y, 0.5);
            g.World.Time = 18000;
            g.Give(Blocks.Torch, 16);
            g.Give(Blocks.Cobblestone, 16);
            await Run(sb, g, "excavate", new { box = $"3,{y},-1,6,{y + 2},2" });
        }
        sb.Append("## spiral stair learned and extended\n");
        {
            int top = v ? 40 : 60;
            var g = Rock(v, top: top + 4);
            var fox = Fox(top);
            Carve(g, fox.Cells(0, 8));
            var bottom = fox.Repeat(7).MinBy(c => c.Y);
            g.Stand(bottom.X + 0.5, bottom.Y, bottom.Z + 0.5);
            g.World.Time = 18000;
            g.Give(Blocks.Torch, 32);
            g.Give(Blocks.Cobblestone, 16);
            var reg = SkillRegistry.CreateDefault();
            await Run(sb, g, "excavate", new { op = "learn", name = "spiral stair", box = $"-1,{top - 20},-1,2,{top + 2},2" }, reg);
            await Run(sb, g, "excavate", new { site = "spiral stair", repeats = 12 }, reg);
        }
        sb.Append("## a cave under a column\n");
        {
            var g = Rock(v);
            var fox = Fox();
            Carve(g, fox.Cells(0, 4));
            var col5 = fox.Repeat(5).MinBy(c => c.Y);
            Carve(g, Enumerable.Range(0, 5).Select(i => col5.Down(1 + i)));
            var feet = fox.Repeat(3).MinBy(c => c.Y);
            g.Stand(feet.X + 0.5, feet.Y, feet.Z + 0.5);
            var reg = SkillRegistry.CreateDefault();
            await Run(sb, g, "excavate", new { op = "learn", name = "spiral stair", rule = FoxRule, at = "0,60,0" }, reg);
            await Run(sb, g, "excavate", new { site = "spiral stair", repeats = 6, torches = false }, reg);
            g.Give(Blocks.Cobblestone, 16);
            await Run(sb, g, "excavate", new { site = "spiral stair", repeats = 3, torches = false }, reg);
        }
        sb.Append("## strip mine with ores and signs\n");
        {
            int y = v ? 12 : 20;
            var g = Rock(v);
            var room = Cuboid(new(0, y, 0), new(3, y + 2, 3)).ToList();
            Carve(g, room);
            var reg = SkillRegistry.CreateDefault();
            reg.Sites.Save(new DigSite { Name = "v1 room y" + y, Kind = "room", Cells = room });
            g.World.KeepAir.Set("v1 room y" + y, 0, room);
            g.Stand(1.5, y, 1.5);
            g.World.Time = 18000;
            g.Set(5, y, 2, Blocks.ByName("iron_ore")!.Id);
            g.Set(5, y, 3, Blocks.ByName("diamond_ore")!.Id);
            g.Set(8, y + 1, 4, Blocks.ByName("coal_ore")!.Id);
            g.Give(Blocks.Torch, 32);
            g.Give(Blocks.Cobblestone, 32);
            g.Give(Items.Sign, 4);
            await Run(sb, g, "strip_mine", new { site = "v1 room y" + y, dir = "east", length = 8, spacing = 3, branch = 4, signs = true }, reg);
        }
        if (v) return;
        sb.Append("## seal openings\n");
        {
            var g = Rock(false);
            var fox = Fox();
            Carve(g, fox.Cells(0, 12));
            var reg = SkillRegistry.CreateDefault();
            await Run(sb, g, "excavate", new { op = "learn", name = "spiral stair", rule = FoxRule, at = "0,60,0" }, reg);
            Carve(g, Cuboid(new(-4, 50, 0), new(-2, 52, 1)));
            g.Set(-1, 51, 0, 0);
            g.Set(0, 49, 0, 0);
            var room = Cuboid(new(2, 53, 1), new(2, 54, 1)).Concat(Cuboid(new(3, 53, 0), new(6, 55, 3))).ToList();
            Carve(g, room);
            reg.Sites.Save(new DigSite { Name = "stair room", Kind = "room", Parent = "spiral stair", Cells = room });
            g.World.KeepAir.Set("stair room", 0, room);
            var feet = fox.Repeat(5).MinBy(c => c.Y);
            g.Stand(feet.X + 0.5, feet.Y, feet.Z + 0.5);
            g.Give(Blocks.Cobblestone, 16);
            await Run(sb, g, "seal_openings", new { site = "spiral stair" }, reg);
        }
        foreach (int gap in new[] { 1, 0 })
        {
            sb.Append("## dig a room off the stair, gap ").Append(gap).Append('\n');
            var g = Rock(false);
            var fox = Fox();
            Carve(g, fox.Cells(0, 12));
            var reg = SkillRegistry.CreateDefault();
            await Run(sb, g, "excavate", new { op = "learn", name = "spiral stair", rule = FoxRule, at = "0,60,0" }, reg);
            var landing = fox.Repeat(6).MinBy(c => c.Y);
            g.Stand(landing.X + 0.5, landing.Y, landing.Z + 0.5);
            g.Give(Blocks.Torch, 8);
            g.Give(Blocks.Cobblestone, 8);
            await Run(sb, g, "dig_room", new { site = "spiral stair", y = landing.Y, gap }, reg);
        }
        sb.Append("## falling gravel and lava behind a wall\n");
        {
            var g = Rock(false);
            g.Gravity = true;
            Carve(g, [new(0, 30, 0), new(0, 31, 0)]);
            g.Stand(0.5, 30, 0.5);
            g.Give(Blocks.Cobblestone, 16);
            g.Give(Blocks.Torch, 8);
            g.Set(3, 32, 0, Blocks.Gravel);
            g.Set(3, 33, 0, Blocks.Gravel);
            g.Set(5, 30, 1, Blocks.Lava);
            await Run(sb, g, "excavate", new { box = "1,30,0,5,31,0" });
        }
        sb.Append("## a room beside an open stair, no block to seal, ores on, protected cell\n");
        {
            var g = Rock(false);
            var fox = Fox();
            Carve(g, fox.Cells(0, 8));
            g.World.KeepAir.Set("spiral stair", 0, fox.Cells(0, 8));
            var land = fox.Repeat(6).MinBy(c => c.Y);
            g.Stand(land.X + 0.5, land.Y, land.Z + 0.5);
            g.Set(3, land.Y + 1, 3, Blocks.ByName("gold_ore")!.Id);
            g.Set(4, land.Y, 1, 0);
            g.Set(4, land.Y - 1, 1, 0);
            g.Set(4, land.Y - 2, 1, 0);
            g.ProtectedBlocks.Add(new BlockPos(4, land.Y + 2, 2));
            await Run(sb, g, "excavate", new { box = $"2,{land.Y},0,4,{land.Y + 2},2", ores = true, torches = false });
            g.Give(Blocks.Cobblestone, 32);
            g.Give(Blocks.Torch, 8);
            await Run(sb, g, "excavate", new { box = $"2,{land.Y},0,4,{land.Y + 2},2", ores = true });
        }
    }

    // ------------------------------------------------------------------ fields, floors, light, storage

    private const int Y = 64;

    private static FakeGame Land(Func<int, int, int>? h = null, bool kit = true, int buckets = 2, bool fence = true)
    {
        h ??= (_, _) => Y;
        var g = new FakeGame(128, (x, yy, z) => yy > h(x, z) ? 0 : yy == h(x, z) ? Blocks.Grass : yy >= h(x, z) - 3 ? Blocks.Dirt : Blocks.Stone) { Fluids = true };
        g.Stand(3.5, Y + 1, 3.5);
        if (!kit) return g;
        g.Give(Items.StoneHoe, 1);
        if (buckets > 0) g.Give(Items.WaterBucket, buckets);
        if (fence) { g.Give(Blocks.Fence, 64); g.Give(Blocks.Fence, 40); g.Give(Blocks.FenceGate, 2); g.Give(Blocks.Torch, 16); }
        return g;
    }

    private static object Field(int width = 9, int length = 10, string axis = "x", bool level = false, bool fence = true) =>
        new { corner = "-10,64,-10", width, length, axis, level, fence };

    private static async Task Fields(StringBuilder sb)
    {
        sb.Append("## standard x\n");
        await Run(sb, Land(), "lay_field", Field());
        sb.Append("## along z\n");
        await Run(sb, Land(), "lay_field", Field(length: 8, axis: "z"));
        sb.Append("## 17 wide\n");
        await Run(sb, Land(), "lay_field", Field(width: 17, length: 6));
        sb.Append("## bumpy without level\n");
        await Run(sb, Land((x, z) => x is >= -6 and <= -4 && z is >= -8 and <= -6 ? Y + 2 : Y), "lay_field", Field());
        sb.Append("## bumpy, level\n");
        {
            var g = Land((x, z) => x is >= -6 and <= -4 && z is >= -8 and <= -6 ? Y + 2 : x is >= -3 and <= -2 && z is >= -4 and <= -3 ? Y - 2 : Y);
            g.Give(Blocks.Dirt, 30);
            await Run(sb, g, "lay_field", Field(level: true));
        }
        sb.Append("## flat, level\n");
        await Run(sb, Land(), "lay_field", Field(level: true));
        sb.Append("## steep / wet / lava\n");
        await Run(sb, Land((x, z) => x == -5 && z == -5 ? Y + 6 : Y), "lay_field", Field(level: true));
        {
            var wet = Land();
            wet.Set(-5, Y, -8, Blocks.Water);
            await Run(sb, wet, "lay_field", Field(level: true));
            var lava = Land();
            lava.Set(-5, Y, -8, Blocks.Lava);
            await Run(sb, lava, "lay_field", Field());
        }
        sb.Append("## protected\n");
        {
            var g = Land((x, z) => x == -5 && z == -5 ? Y + 2 : Y);
            g.ProtectedBlocks.Add(new BlockPos(-5, Y + 2, -5));
            await Run(sb, g, "lay_field", Field(level: true));
        }
        sb.Append("## dip under the trench end\n");
        {
            var g = Land((x, z) => x == -11 && z == -6 ? Y - 3 : Y);
            g.Give(Blocks.Dirt, 10);
            await Run(sb, g, "lay_field", Field());
        }
        sb.Append("## hole under a trench cell\n");
        {
            var g = Land();
            g.Fill(-7, Y - 1, -6, -7, Y - 3, -6, 0);
            g.Give(Blocks.Dirt, 10);
            await Run(sb, g, "lay_field", Field());
        }
        sb.Append("## stray water\n");
        {
            var g = Land();
            g.Changed = (p, id, _) => { if (id == Blocks.Water && p == new BlockPos(-10, Y, -6)) g.World.SetBlock(-12, Y, -6, Blocks.Water, 0); };
            await Run(sb, g, "lay_field", Field());
        }
        sb.Append("## one bucket\n");
        await Run(sb, Land(buckets: 1), "lay_field", Field());
        sb.Append("## one bucket and a pond\n");
        {
            var g = Land(buckets: 1);
            g.Give(Items.Bucket, 1);
            foreach (var (x, z) in new[] { (10, 10), (11, 10), (10, 11), (11, 11) }) g.Set(x, Y, z, Blocks.Water);
            await Run(sb, g, "lay_field", Field());
        }
        sb.Append("## too few fences, then without\n");
        {
            var g = Land(fence: false);
            g.Give(Blocks.Fence, 5);
            await Run(sb, g, "lay_field", Field());
            await Run(sb, g, "lay_field", Field(fence: false));
        }
        sb.Append("## rerun, half built\n");
        {
            var g = Land();
            await Run(sb, g, "lay_field", Field());
            await Run(sb, g, "lay_field", Field());
            var h = Land();
            for (int u = 0; u < 4; u++) h.Set(-10 + u, Y, -6, 0);
            h.Set(-10, Y, -6, Blocks.Water);
            h.Set(-8, Y, -6, Blocks.Water);
            for (int v = 0; v <= 2; v++) for (int u = 0; u < 10; u++) h.Set(-10 + u, Y, -10 + v, Blocks.Farmland);
            await Run(sb, h, "lay_field", Field());
        }
    }

    private static FakeGame Island(int r)
    {
        var g = new FakeGame();
        g.Fill(-r, 61, -r, r, 63, r, Blocks.Dirt);
        g.Stand(0.5, 64, 0.5);
        return g;
    }

    private static async Task Floors(StringBuilder sb)
    {
        sb.Append("## grow a rectangle\n");
        {
            var g = Island(2);
            g.Give(Blocks.Cobblestone, 64);
            await Run(sb, g, "expand_island", new { area = "-2,-2,6,2" });
        }
        sb.Append("## a second run picks up\n");
        {
            var g = Island(2);
            g.Give(Blocks.Cobblestone, 7);
            await Run(sb, g, "expand_island", new { area = "3,-2,6,2", y = 63 });
            g.Give(Blocks.Cobblestone, 30);
            await Run(sb, g, "expand_island", new { area = "3,-2,6,2", y = 63 });
            await Run(sb, g, "expand_island", new { area = "3,-2,6,2", y = 63 });
        }
        sb.Append("## refusals\n");
        {
            var g = Island(2);
            g.Give(Blocks.Cobblestone, 64);
            await Run(sb, g, "expand_island", new { area = "10,10,12,12", y = 63 });
            await Run(sb, g, "expand_island", new { area = "1,2,3" });
            await Run(sb, g, "expand_island", new { area = "0,0,40,40" });
            g.World.Entities.SpawnMob(7, MobTypes.Zombie, new Vec3d(2.5, 64, 0.5), 0, 0);
            await Run(sb, g, "expand_island", new { area = "3,-2,6,2" });
        }
        sb.Append("## a design partly covered, obtain hook\n");
        {
            var g = Island(2);
            g.Give(Blocks.Cobblestone, 64);
            var reg = SkillRegistry.CreateDefault();
            reg.Footprints = () => [new DesignFootprint("design #4", 3, -2, 8, 2, 64, 79)];
            await Run(sb, g, "expand_island", new { area = "3,-2,6,2" }, reg);
            var o = Island(2);
            var reg2 = SkillRegistry.CreateDefault();
            reg2.Obtain = (_, id, n, _) => { o.Give(id, n); return Task.FromResult<string?>($"took {n} from the chest"); };
            await Run(sb, o, "expand_island", new { area = "3,-2,6,2", y = 63 }, reg2);
        }
        sb.Append("## grow the edge, walls, lines\n");
        {
            var g = Island(3);
            g.Give(Blocks.Cobblestone, 64);
            await Run(sb, g, "expand_island", new { dir = "east", width = 2 });
            await Run(sb, g, "expand_island", new { });
            await Run(sb, g, "place_line", new { from = "-3,64,-3", to = "3,64,-3" });
            await Run(sb, g, "fill_floor", new { from = "-2,64,1", to = "0,64,3" });
            await Run(sb, g, "build_wall", new { from = "-3,65,3", to = "3,65,3", height = 2 });
            await Run(sb, g, "pillar_down", new { depth = 2 });
        }
    }

    /// <summary>Solid stone up to y63 (dirt on top) over bedrock: ground under everything.</summary>
    private static FakeGame Ground()
    {
        var g = new FakeGame(128, (_, y, _) => y == 0 ? Blocks.Bedrock : y < 63 ? Blocks.Stone : y == 63 ? Blocks.Dirt : 0);
        g.Stand(0.5, 64, 0.5);
        return g;
    }

    private static async Task Building(StringBuilder sb)
    {
        sb.Append("## grow one side, two rows\n");
        {
            var g = Island(3);
            g.Give(Blocks.Cobblestone, 64);
            await Run(sb, g, "expand_island", new { side = "east", rows = 2 });
            await Run(sb, g, "expand_island", new { side = "n", rows = 1, max = 3 });
            await Run(sb, g, "expand_island", new { side = "up" });
            await Run(sb, g, "expand_island", new { rows = 3, max = 10, block = "dirt" });
        }
        sb.Append("## grow with a mob near, out of blocks\n");
        {
            var g = Island(2);
            g.Give(Blocks.Cobblestone, 64);
            g.World.Entities.SpawnMob(7, MobTypes.Zombie, new Vec3d(2.5, 64, 0.5), 0, 0);
            await Run(sb, g, "expand_island", new { side = "west" });
            var o = Island(2);
            o.Give(Blocks.Cobblestone, 5);
            await Run(sb, o, "expand_island", new { rows = 2 });
            await Run(sb, Island(2), "expand_island", new { });
        }
        sb.Append("## lines, floors, walls, pillars, ladders, torches\n");
        {
            var g = Island(4);
            g.Give(Blocks.Cobblestone, 128);
            g.Give(Blocks.Torch, 4);
            g.Give(Blocks.Ladder, 8);
            await Run(sb, g, "place_line", new { from = "-3,64,-3", to = "3,64,-3" });
            await Run(sb, g, "fill_floor", new { from = "-2,64,1", to = "0,64,3" });
            await Run(sb, g, "build_wall", new { from = "-4,64,4", to = "4,64,4", height = 3 });
            await Run(sb, g, "place_ladder", new { pos = "2,64,3", height = 3 });
            await Run(sb, g, "place_torch", new { pos = "1,64,0" });
            await Run(sb, g, "place_torch", new { });
            await Run(sb, g, "pillar_up", new { height = 3 });
        }
        sb.Append("## pillar down\n");
        {
            var g = Ground();
            await Run(sb, g, "pillar_down", new { depth = 3 });
            await Run(sb, g, "pillar_down", new { depth = 2, side = "west" });
            var lava = Ground();
            for (int x = -2; x <= 2; x++) for (int z = -2; z <= 2; z++) if (x != 0 || z != 0) lava.Set(x, 61, z, Blocks.Lava);
            await Run(sb, lava, "pillar_down", new { depth = 3 });
            var sand = Ground();
            sand.Set(1, 64, 0, Blocks.Sand);
            sand.ProtectedBlocks.Add(new BlockPos(-1, 63, 0));
            await Run(sb, sand, "pillar_down", new { depth = 1 });
        }
    }

    private static FakeGame Lava(int dirt = 40, bool lava = true, int waterBuckets = 1, int emptyBuckets = 1)
    {
        var g = Island(8);
        if (lava) for (int x = 3; x <= 8; x++) for (int z = 6; z <= 7; z++) g.Set(x, 63, z, Blocks.Lava);
        g.Give(Items.WaterBucket, waterBuckets);
        g.Give(Items.Bucket, emptyBuckets);
        g.Give(Blocks.Cobblestone, dirt);
        g.Stand(0.5, 64, 4.5);
        return g;
    }

    private static async Task Portal(StringBuilder sb)
    {
        sb.Append("## cast\n");
        await Run(sb, Lava(), "cast_portal", new { pos = "-2,64,-4" });
        sb.Append("## refusals\n");
        await Run(sb, Lava(waterBuckets: 0), "cast_portal", new { pos = "-2,64,-4" });
        await Run(sb, Lava(lava: false), "cast_portal", new { pos = "-2,64,-4" });
        await Run(sb, Lava(dirt: 5), "cast_portal", new { pos = "-2,64,-4" });
        {
            var g = Lava();
            g.Set(0, 66, -4, Blocks.Stone);
            await Run(sb, g, "cast_portal", new { pos = "-2,64,-4" });
            var wood = Lava();
            wood.Set(3, 66, -4, Blocks.Planks);
            await Run(sb, wood, "cast_portal", new { pos = "-2,64,-4" });
            var hole = Lava();
            hole.Set(-2, 63, -3, 0);
            await Run(sb, hole, "cast_portal", new { pos = "-2,64,-4" });
            var prot = Lava();
            prot.ProtectedBlocks.Add(new BlockPos(-1, 64, -3));
            await Run(sb, prot, "cast_portal", new { pos = "-2,64,-4" });
        }
        sb.Append("## find a spot along z, the water fails, light it\n");
        await Run(sb, Lava(), "cast_portal", new { axis = "z" });
        await Run(sb, Lava(), "cast_portal", new { });
        {
            var g = Lava();
            var third = new BlockPos(-2, 65, -4);
            g.Changed = (p, id, _) => { if (BlockShapes.IsLava(id) && p == third) g.World.SetBlock(p.X, p.Y + 1, p.Z, Blocks.Cobblestone, 0); };
            await Run(sb, g, "cast_portal", new { pos = "-2,64,-4" });
            var l = Lava();
            l.Give(Items.FlintAndSteel, 1);
            await Run(sb, l, "cast_portal", new { pos = "-2,64,-4", light = true });
        }
    }

    private static async Task Farm(StringBuilder sb)
    {
        sb.Append("## harvest wheat\n");
        {
            var g = Island(4);
            g.Set(3, 63, 3, Blocks.Water);
            for (int x = 1; x <= 2; x++) { g.Set(x, 63, 2, Blocks.Farmland); g.Set(x, 64, 2, Blocks.Crops, 7); }
            g.Give(Items.StoneHoe, 1);
            await Run(sb, g, "harvest_wheat");
            var t = Island(4);
            t.Set(3, 63, 3, Blocks.Water);
            t.Set(1, 63, 2, Blocks.Farmland);
            t.Set(1, 64, 2, Blocks.Crops, 7);
            t.Give(Items.WoodenHoe, 1);
            var crop = new BlockPos(1, 64, 2);
            t.AfterDig = p => { if (p == crop) t.Set(1, 63, 2, Blocks.Dirt); };
            await Run(sb, t, "harvest_wheat");
            var a = Island(4);
            a.Set(1, 63, 2, Blocks.Farmland);
            a.Set(1, 64, 2, Blocks.Crops, 7);
            var reg = SkillRegistry.CreateDefault();
            reg.AcquireMissing = (_, _, _, _) => { a.Give(Items.StoneHoe, 1); return Task.FromResult<string?>("crafted stone hoe"); };
            await Run(sb, a, "harvest_wheat", null, reg);
            var e = Island(4);
            e.Set(3, 63, 4, Blocks.Water);
            for (int x = 1; x <= 2; x++) { e.Set(x, 63, 2, Blocks.Farmland); e.Set(x, 64, 2, Blocks.Crops, 7); }
            for (int x = 1; x <= 4; x++) e.Set(x, 63, 3, Blocks.Farmland);
            e.Give(Items.StoneHoe, 1);
            await Run(sb, e, "harvest_wheat");
            var n = Island(4);
            n.Set(1, 63, 2, Blocks.Farmland);
            n.Set(1, 64, 2, Blocks.Crops, 2);
            await Run(sb, n, "harvest_wheat");
            var h = Island(4);
            h.Set(1, 63, 2, Blocks.Farmland);
            h.Set(1, 64, 2, Blocks.Crops, 7);
            var none = SkillRegistry.CreateDefault();
            none.AcquireMissing = (_, _, _, _) => Task.FromResult<string?>(null);
            await Run(sb, h, "harvest_wheat", null, none);
        }
        sb.Append("## till, bonemeal, sapling, cane, cactus, melons\n");
        {
            var g = Island(4);
            g.Set(3, 63, 3, Blocks.Water);
            g.Give(Items.StoneHoe, 1);
            g.Give(Items.Seeds, 4);
            await Run(sb, g, "till_soil", new { count = 2 });
            var w = Island(4);
            w.Set(2, 63, 2, Blocks.Water);
            w.ProtectedBlocks.Add(new BlockPos(2, 63, 2));
            w.Give(Items.StoneHoe, 1);
            await Run(sb, w, "till_soil", new { pos = "2,63,2", count = 4 });
            await Run(sb, w, "till_soil", new { pos = "2,63,2", count = 4 });
            var b = Island(4);
            b.Set(1, 63, 2, Blocks.Farmland);
            b.Set(1, 64, 2, Blocks.Crops, 2);
            b.Give(Items.Dye, 4, 15);
            await Run(sb, b, "apply_bonemeal", new { count = 2 });
            var s = Island(4);
            s.Give(Blocks.Sapling, 2);
            await Run(sb, s, "plant_sapling", new { pos = "1,64,1" });
            await Run(sb, s, "plant_sapling", new { pos = "2,64,2" });
            var c = Island(4);
            for (int y = 64; y <= 66; y++) c.Set(2, y, 2, Blocks.SugarCane);
            for (int y = 64; y <= 65; y++) c.Set(-2, y, -2, Blocks.Cactus);
            c.Set(1, 64, 2, Blocks.MelonStem, 7);
            c.Set(1, 64, 3, Blocks.Melon);
            await Run(sb, c, "harvest_cane");
            await Run(sb, c, "harvest_cactus");
            await Run(sb, c, "harvest_melons");
        }
        sb.Append("## liquids\n");
        {
            var g = Island(4);
            g.Fluids = true;
            g.Give(Items.WaterBucket, 2);
            await Run(sb, g, "infinite_water", new { pos = "2,63,2" });
            g.Give(Items.Bucket, 1);
            await Run(sb, g, "fill_bucket", new { pos = "2,63,2" });
            await Run(sb, g, "empty_bucket", new { pos = "-2,63,-2" });
            var l = Island(4);
            l.Fluids = true;
            l.Give(Items.WaterBucket, 2);
            await Run(sb, l, "infinite_water", new { pos = "-3,63,2", shape = "line" });
            await Run(sb, Island(4), "infinite_water");
            var o = Island(4);
            o.Fluids = true;
            o.Set(2, 63, 2, Blocks.Lava);
            o.Set(3, 63, 2, Blocks.Lava);
            o.Give(Items.WaterBucket, 1);
            o.Give(Items.Bucket, 1);
            await Run(sb, o, "make_obsidian", new { pos = "2,63,2" });
            await Run(sb, o, "make_obsidian");
        }
    }

    [Fact]
    public async Task Portal_traces_match_the_golden() => Same(SkillTraceGoldenData.Portal, await Area("portal", Portal, hashed: true));

    [Fact]
    public async Task Farm_traces_match_the_golden() => Same(SkillTraceGoldenData.Farm, await Area("farm", Farm));

    [Fact]
    public async Task Building_traces_match_the_golden() => Same(SkillTraceGoldenData.Building, await Area("building", Building));

    private static FakeGame NightIsland()
    {
        var g = new FakeGame();
        g.Fill(-7, 61, -7, 7, 63, 7, Blocks.Dirt);
        g.Fill(6, 61, -2, 7, 63, 2, 0);
        g.Stand(0.5, 64, 0.5);
        g.World.Time = 18000;
        return g;
    }

    private static async Task Light(StringBuilder sb)
    {
        sb.Append("## light the island\n");
        {
            var g = NightIsland();
            g.Give(Blocks.Torch, 64);
            await Run(sb, g, "light_area", new { area = "-9,-9,9,9", y = "64" });
        }
        sb.Append("## spawn map\n");
        {
            var g = NightIsland();
            await Run(sb, g, "spawn_map", new { area = "-9,-9,9,9", y = "64" });
            await Run(sb, g, "light_area", new { area = "-9,-9,9,9", y = "64", report = true });
            g.World.Time = 6000;
            await Run(sb, g, "spawn_map", new { area = "-9,-9,9,9", y = "64" });
        }
        sb.Append("## a design's deck\n");
        {
            var g = NightIsland();
            var solid = new HashSet<BlockPos>();
            void Put(int x, int y, int z) { g.Set(x, y, z, Blocks.Cobblestone); solid.Add(new BlockPos(x, y, z)); }
            foreach (var (px, pz) in new[] { (-4, -4), (3, -4), (-4, 3), (3, 3) })
                for (int y = 64; y < 70; y++) Put(px, y, pz);
            for (int x = -4; x <= 3; x++) for (int z = -4; z <= 3; z++) Put(x, 70, z);
            for (int x = -4; x <= 3; x++) for (int z = -4; z <= 3; z++)
                if (x is -4 or 3 || z is -4 or 3) { Put(x, 71, z); Put(x, 72, z); }
            var reg = SkillRegistry.CreateDefault();
            reg.Design = id => id == "4" ? new DesignRegion("design #4", new BlockPos(-4, 64, -4), new BlockPos(3, 72, 3), solid, null) : null;
            g.Give(Blocks.Torch, 64);
            await Run(sb, g, "light_area", new { design = "#4" }, reg);
            await Run(sb, g, "spawn_map", new { design = "4", y = "71" }, reg);
            await Run(sb, g, "light_area", new { design = "#4", exclude = "none" }, reg);
        }
        sb.Append("## obtain hook, none\n");
        {
            var g = NightIsland();
            var reg = SkillRegistry.CreateDefault();
            reg.Obtain = (_, id, count, _) => { if (id == Blocks.Torch) g.Give(Blocks.Torch, count); return Task.FromResult<string?>($"crafted {count} torches"); };
            await Run(sb, g, "light_area", new { area = "-9,-9,9,9", y = "64" }, reg);
            await Run(sb, NightIsland(), "light_area", new { area = "-9,-9,9,9", y = "64" });
            await Run(sb, NightIsland(), "light_area", new { });
            await Run(sb, NightIsland(), "light_area", new { area = "-9,-9,9" });
        }
    }

    private static FakeGame Storage()
    {
        var g = Island(6);
        foreach (var p in new[] { new BlockPos(3, 64, 0), new BlockPos(-3, 64, 0), new BlockPos(0, 64, 4) })
        {
            g.Set(p, Blocks.Chest);
            g.Chests[p] = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
        }
        return g;
    }

    private static async Task Inventory(StringBuilder sb)
    {
        var a = new BlockPos(3, 64, 0);
        var b = new BlockPos(-3, 64, 0);
        var c = new BlockPos(0, 64, 4);
        sb.Append("## move items, one visit per chest\n");
        {
            var g = Storage();
            g.Chests[a][0] = S(Items.IronIngot, 20);
            g.Chests[a][1] = S(Blocks.Log, 64);
            g.Give(Blocks.Cobblestone, 70);
            await Run(sb, g, "move_items", new
            {
                moves = new object[]
                {
                    new { item = "iron ingot", count = "all", from = "chest@3,64,0", to = "inv" },
                    new { item = "cobblestone", count = 64, from = "inv", to = "chest@-3,64,0" },
                    new { item = "log", count = 10, from = "chest@3,64,0", to = "chest@0,64,4" },
                },
            });
        }
        sb.Append("## move items replans\n");
        {
            var g = Storage();
            g.Chests[a][0] = S(Blocks.Sand, 64);
            g.RejectClick = 2;
            await Run(sb, g, "move_items", new { moves = new object[] { new { item = "sand", count = 5, from = "chest@3,64,0", to = "inv" } } });
        }
        sb.Append("## move items refusals, labels, hotbar\n");
        {
            var g = Storage();
            g.Give(Items.DiamondPickaxe, 1);
            g.ReservedItems = new HashSet<int> { Items.DiamondPickaxe };
            await Run(sb, g, "move_items", new { moves = new object[] { new { item = "diamond pickaxe", from = "inv", to = "chest@3,64,0" } } });
            await Run(sb, g, "move_items", new { moves = new object[] { new { item = "dirt", from = "inv", to = "Nowhere storage" } } });
            await Run(sb, g, "move_items", new { moves = new object[] { new { item = "obsidian", from = "inv", to = "chest@3,64,0" } } });
            await Run(sb, g, "move_items", new { });
            var h = Storage();
            h.SignText[new BlockPos(3, 65, 0)] = ["Iron", "storage", "", ""];
            h.Give(Items.IronIngot, 5);
            h.SetSlot(20, Items.Bread, 8);
            await Run(sb, h, "move_items", new
            {
                moves = new object[]
                {
                    new { item = "iron ingot", count = "all", from = "inv", to = "Iron storage" },
                    new { item = "bread", count = "all", from = "inv", to = "hotbar", slot = 4 },
                },
            });
        }
        sb.Append("## arrange chest\n");
        {
            var g = Storage();
            var ch = g.Chests[a];
            ch[0] = S(Blocks.Dirt, 64); ch[5] = S(Blocks.Cobblestone, 64); ch[6] = S(Blocks.Cobblestone, 20); ch[7] = S(Blocks.Cobblestone, 30);
            var reg = SkillRegistry.CreateDefault();
            await Run(sb, g, "arrange_chest", new { chest = "3,64,0", layout = "cobblestone*2,dirt" }, reg);
            var h = Storage();
            var hc = h.Chests[a];
            hc[10] = S(Items.IronIngot, 3); hc[2] = S(Blocks.Log, 5); hc[20] = S(Items.Bread, 4);
            await Run(sb, h, "arrange_chest", new { chest = "3,64,0", layout = "by_category" });
            await Run(sb, h, "arrange_chest", new { chest = "3,64,0", layout = "dirt*12" });
        }
        sb.Append("## organize storage\n");
        {
            var g = Storage();
            g.Chests[a][0] = S(Items.IronIngot, 10);
            g.Chests[a][1] = S(Blocks.Log, 8);
            g.Chests[b][0] = S(Blocks.Log, 30);
            g.Chests[b][1] = S(Blocks.Log, 20);
            g.Chests[c][4] = S(Items.Bread, 6);
            await Run(sb, g, "organize_storage");
            await Run(sb, g, "organize_storage");
            var e = Storage();
            await Run(sb, e, "organize_storage");
        }
        sb.Append("## deposit and restock\n");
        {
            var g = Storage();
            g.Give(Blocks.Cobblestone, 30);
            g.Give(Items.Wheat, 5);
            await Run(sb, g, "deposit_everything");
            g.Chests[c][0] = S(Items.StonePickaxe, 1);
            g.Chests[c][1] = S(Blocks.Torch, 64);
            await Run(sb, g, "restock_loadout", new { items = "pickaxe torches" });
        }
    }

    [Fact]
    public async Task Field_traces_match_the_golden() => Same(SkillTraceGoldenData.Fields, await Area("fields", Fields, hashed: true));

    [Fact]
    public async Task Floor_traces_match_the_golden() => Same(SkillTraceGoldenData.Floors, await Area("floors", Floors));

    [Fact]
    public async Task Light_traces_match_the_golden() => Same(SkillTraceGoldenData.Light, await Area("light", Light));

    [Fact]
    public async Task Storage_traces_match_the_golden() => Same(SkillTraceGoldenData.Storage, await Area("storage", Inventory));

    /// <summary>The same executor on the real client over the in-process fake server (real dig packets and walking).</summary>
    private static async Task ExcavationLive(StringBuilder sb, bool v)
    {
        using var data = Version(v);
        foreach (bool stair in new[] { false, true })
        {
            using var s = ExcavationFakeServerTests.Rock(v);
            var p = DigPattern.Parse("column 3 step 0,-1,0 turn -90 pivot 0.5,0.5", new BlockPos(8, 62, 8), out _)!;
            if (stair)
            {
                foreach (var cell in p.Cells(0, 2)) s.Blocks.Remove((cell.X, cell.Y, cell.Z));
                var feet = p.Repeat(1).MinBy(q => q.Y);
                s.SpawnAt = (feet.X + 0.5, feet.Y, feet.Z + 0.5);
            }
            await using var c = await ExcavationFakeServerTests.Connect(s, v);
            var reg = SkillRegistry.CreateDefault();
            var t = new TraceGame(new ClientGameActions(c));
            var ctx = new SkillContext(t, reg);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var steps = stair
                ? new object[] { new { op = "learn", name = "stair", rule = p.Rule, at = "8,62,8" }, new { site = "stair", repeats = 4, torches = false } }
                : [new { box = "10,60,7,12,61,9", torches = false }];
            foreach (var a in steps)
            {
                var r = await reg.RunAsync(ctx, "excavate", FakeGame.J(a), cts.Token);
                sb.Append("> excavate ").Append(JsonSerializer.Serialize(a)).Append('\n').Append(Settled(t.Trace.ToString())).Append("= ").Append(r).Append('\n');
                t.Trace.Clear();
            }
            sb.Append("  server ").Append(Hash(s)).Append('\n');
        }
    }

    /// <summary>Real walking ends a few tenths off the cell centre depending on the tick it arrived on: only the goal cell is pinned.</summary>
    private static string Settled(string trace) =>
        System.Text.RegularExpressions.Regex.Replace(trace, @" @\S+ -> ok arrived at .*", " -> ok arrived");

    private static string Hash(FakeServer s)
    {
        var sb = new StringBuilder();
        for (int x = 2; x <= 14; x++)
            for (int y = 54; y <= 63; y++)
                for (int z = 2; z <= 14; z++)
                    sb.Append(s.Blocks.GetValueOrDefault((x, y, z))).Append(',');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Excavation_on_the_fake_server_matches_the_golden(bool v) =>
        Same(v ? SkillTraceGoldenData.ExcavationServer1122 : SkillTraceGoldenData.ExcavationServerBeta,
            await Area(v ? "excavation_server_1122" : "excavation_server_b181", sb => ExcavationLive(sb, v)));

    [Fact]
    public async Task Excavation_traces_match_the_golden_b181() =>
        Same(SkillTraceGoldenData.ExcavationBeta, await Area("excavation_b181", sb => Excavation(sb, false)));

    [Fact]
    public async Task Excavation_traces_match_the_golden_1122() =>
        Same(SkillTraceGoldenData.Excavation1122, await Area("excavation_1122", sb => Excavation(sb, true)));
}
