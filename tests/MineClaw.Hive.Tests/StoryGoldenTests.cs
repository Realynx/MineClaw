using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Behaviour pins for the Story director's refactors (docs/dev/complexity.md WP01): each test renders a wide matrix of inputs
/// through a public entry point to text and compares it with a checked-in golden file under <c>StoryGolden/</c>. The goldens were
/// written by the code before it was reshaped, so a refactor that changes any string, JSON shape or job must update them on purpose.
/// A missing golden is written (and the test fails once, so a new one is never silently accepted); set
/// <c>STORY_GOLDEN_UPDATE=1</c> to rewrite them all.
/// </summary>
public sealed class StoryGoldenTests
{
    /// <summary>A golden too big to keep in the repo: only its SHA-256 and line count are stored; on a mismatch the actual text is
    /// written to the temp folder (regenerate the expected one by running the test on the commit before).</summary>
    internal static void GoldenHash(string name, string actual, [CallerFilePath] string here = "")
    {
        actual = actual.Replace("\r\n", "\n");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(actual)));
        var summary = $"sha256 {hash}\nlines {actual.Count(ch => ch == '\n')}\n";
        try { Golden(name, summary, here); }
        catch
        {
            var dump = Path.Combine(Path.GetTempPath(), "story-golden-" + name + ".actual.txt");
            File.WriteAllText(dump, actual);
            throw new Xunit.Sdk.XunitException($"golden {name} differs: the actual text is in {dump}");
        }
    }

    internal static void Golden(string name, string actual, [CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(here)!, "StoryGolden", name + ".txt");
        actual = actual.Replace("\r\n", "\n");
        if (!File.Exists(path) || Environment.GetEnvironmentVariable("STORY_GOLDEN_UPDATE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bool existed = File.Exists(path);
            File.WriteAllText(path, actual);
            if (!existed) Assert.Fail($"golden {name} written: run again");
            return;
        }
        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        if (expected == actual) return;
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        int i = 0;
        while (i < Math.Min(e.Length, a.Length) && e[i] == a[i]) i++;
        Assert.Fail($"golden {name} differs at line {i + 1}:\n- {(i < e.Length ? e[i] : "<end>")}\n+ {(i < a.Length ? a[i] : "<end>")}");
    }

    internal static string J(JsonNode? n) => n?.ToJsonString() ?? "null";

    internal static string R((JsonNode? Result, string? Error) r) => r.Error != null ? "ERR " + r.Error : "OK " + J(r.Result);

    // ---------------------------------------------------------------- lookup tables vs the switches they replaced

    private static int OldStairsId(Material m) => m.Id switch
    {
        5 => m.Meta switch { 1 => 134, 2 => 135, 3 => 136, 4 => 163, 5 => 164, _ => 53 },
        4 or 1 or 48 => 67, 45 => 108, 98 => 109, 112 => 114, 24 => 128, 155 => 156, 179 => 180, 201 => 203,
        53 or 67 or 108 or 109 or 114 or 128 or 134 or 135 or 136 or 156 or 163 or 164 or 180 or 203 => m.Id,
        _ => 53,
    };

    private static (int, int) OldSlab(bool modern, Material m) => m.Id switch
    {
        5 => modern ? (126, m.Meta) : (44, 2),
        4 => (44, 3), 24 => (44, 1), 45 => (44, 4), 98 => (44, 5),
        112 when modern => (44, 6), 155 when modern => (44, 7),
        44 or 126 => (m.Id, m.Meta),
        _ => (44, 0),
    };

    [Fact]
    public void Stairs_and_slab_tables_match_the_old_switches_for_every_state()
    {
        foreach (var d in new IGameData[] { Beta181GameData.Instance, Release1122GameData.Instance })
            for (int id = -1; id < 260; id++)
                for (int meta = -1; meta < 17; meta++)
                {
                    var m = new Material(id, meta, "x", "x");
                    int old = OldStairsId(m);
                    Assert.Equal(d.BlockExists(old) ? old : 53, StoryConstruction.StairsFor(d, m));
                    Assert.Equal(OldSlab(d.BlockExists(126), m), StoryConstruction.SlabFor(d, m));
                }
    }

    [Fact]
    public void Placing_item_and_liquid_names_for_every_state()
    {
        var sb = new StringBuilder();
        foreach (var d in new IGameData[] { Beta181GameData.Instance, Release1122GameData.Instance })
            for (int id = 0; id < 260; id++)
                for (int meta = 0; meta < 16; meta++)
                {
                    if (!d.BlockExists(id) && meta > 0) continue;
                    var (item, why) = StoryConstruction.PlacingItem(d, id, meta);
                    sb.Append(d.VersionId).Append(' ').Append(id).Append(':').Append(meta).Append(' ').Append(item ?? "-").Append(' ').Append(why ?? "").Append('\n');
                }
        foreach (var m in new JsonNode?[] { null, "water", " Lava ", "still_water", "flowing_water", "water_bucket", "still_lava", "flowing_lava",
                     "lava_bucket", "mud", 3, new JsonArray(8), new JsonArray(9, 1), new JsonArray(10), new JsonArray(11), new JsonArray(12), new JsonArray(),
                     new JsonArray("8"), new JsonObject() })
            sb.Append(J(m)).Append(" => ").Append(StoryConstruction.LiquidOf(new JsonObject { ["material"] = m?.DeepClone() }) ?? "null").Append('\n');
        GoldenHash("placing-items", sb.ToString());
    }

    // ================================================================ StoryConstruction.Expand

    private static IEnumerable<JsonObject> ExpandItems()
    {
        JsonArray A(params int[] v) => new(v.Select(x => (JsonNode)x).ToArray());
        JsonArray Bl(params int[][] ps) => new(ps.Select(p => (JsonNode)A(p)).ToArray());
        var areas = new JsonNode?[] { null, A(0, 64, 0, 4, 64, 2), A(0, 64, 0, 2, 67, 5), A(-1, 64, -1, -1, 66, 3), A(0, 64, 0, 0, 64, 0), A(0, -1, 0, 1, 1, 1) };
        var blockSets = new JsonNode?[] { null, Bl([1, 64, 1], [2, 64, 1], [2, 65, 1]), Bl([5, 70, 5]), new JsonArray(), Bl([0, 0, 0], [3, 64, 3]) };
        var materials = new JsonNode?[]
        {
            null, "oak planks", "planks", "cobblestone", "stone_bricks", "bricks", "sandstone", "iron_block", "nonsense", "  Glass ",
            A(5, 2), A(5), A(35, 14), A(9), A(8, 0), A(10, 0), A(11, 3), A(64, 0), A(71, 0), A(26, 0), A(59, 0), A(63, 0), A(54, 2),
            A(61, 3), A(53, 1), A(98, 0), A(44, 1), A(126, 0), A(145, 4), A(29, 1), A(23, 0), A(999), A(-1, 0), A(43, 0), A(112), A(155),
            "water", "lava", "lava_bucket", "flowing_water", "still_lava", "dirt",
        };
        var facings = new JsonNode?[] { null, "n", "e", "s", "w", "x", "north" };
        var roofs = new JsonNode?[]
        {
            null, new JsonObject { ["style"] = "flat" }, new JsonObject { ["style"] = "slab", ["overhang"] = 1 },
            new JsonObject { ["style"] = "gable" }, new JsonObject { ["style"] = "gable", ["overhang"] = 1, ["material"] = "bricks" },
            new JsonObject { ["style"] = "none" }, new JsonObject { ["style"] = "dome" }, new JsonObject { ["style"] = "flat", ["material"] = "nothing" },
        };
        var kinds = StoryConstruction.Kinds.Concat(["", "WALL ", "tower"]).ToList();
        int n = 0;
        foreach (var kind in kinds)
            foreach (var area in areas)
                foreach (var blocks in blockSets)
                {
                    if (area != null && blocks != null && n++ % 3 != 0) continue;
                    foreach (var mat in materials)
                    {
                        var it = new JsonObject { ["kind"] = kind };
                        if (area != null) it["area"] = area.DeepClone();
                        if (blocks != null) it["blocks"] = blocks.DeepClone();
                        if (mat != null) it["material"] = mat.DeepClone();
                        yield return it;
                    }
                    foreach (var f in facings)
                        foreach (var h in new int?[] { null, 0, 2, 20 })
                        {
                            var it = new JsonObject { ["kind"] = kind };
                            if (area != null) it["area"] = area.DeepClone();
                            if (blocks != null) it["blocks"] = blocks.DeepClone();
                            if (f != null) it["facing"] = f.DeepClone();
                            if (h != null) it["height"] = h;
                            yield return it;
                        }
                    foreach (var roof in roofs)
                        foreach (var level in new int?[] { null, 70 })
                        {
                            var it = new JsonObject { ["kind"] = kind, ["material"] = "cobblestone" };
                            if (area != null) it["area"] = area.DeepClone();
                            if (blocks != null) it["blocks"] = blocks.DeepClone();
                            if (roof != null) it["roof"] = roof.DeepClone();
                            if (level != null) it["level"] = level;
                            yield return it;
                        }
                }
        // Mine items: dig cells and place lists.
        var places = new JsonNode?[]
        {
            null, new JsonArray(A(1, 64, 1, 50, 5), A(2, 65, 1, 64, 0), A(2, 66, 1, 64, 8)), new JsonArray(A(1, 0, 1, 50)), new JsonArray(A(1, 64, 1)),
            new JsonArray(A(1, 64, 1, 9999)), new JsonArray(A(1, 64, 1, 59, 0)), new JsonArray(A(3, 64, 3, 50)),
        };
        foreach (var area in areas)
            foreach (var blocks in blockSets)
                foreach (var place in places)
                {
                    var it = new JsonObject { ["kind"] = "mine" };
                    if (area != null) it["area"] = area.DeepClone();
                    if (blocks != null) it["blocks"] = blocks.DeepClone();
                    if (place != null) it["place"] = place.DeepClone();
                    yield return it;
                }
    }

    private static string Ghost(GhostState g) =>
        $"{g.At.X},{g.At.Y},{g.At.Z} {g.Id}:{g.Meta} {g.Item ?? "-"} f={g.Facing ?? "-"} j={(g.Job ? 1 : 0)} r={(g.Remove ? 1 : 0)} {g.State} {g.Reason ?? ""}";

    private static string ExpandAll(IGameData d, IStoryWorld? world)
    {
        var sb = new StringBuilder();
        foreach (var it in ExpandItems())
        {
            List<GhostState> ghosts; Material? mat; string? err;
            try { (ghosts, mat, err) = StoryConstruction.Expand(d, it, world); }
            catch (Exception ex) { sb.Append(it.ToJsonString()).Append(" => THROWS ").Append(ex.GetType().Name).Append('\n'); continue; }
            sb.Append(it.ToJsonString()).Append(" => ").Append(err ?? "ok").Append(" mat=")
                .Append(mat == null ? "-" : $"{mat.Id}:{mat.Meta}/{mat.Item}/{mat.Name}").Append(" n=").Append(ghosts.Count).Append('\n');
            foreach (var g in ghosts) sb.Append("  ").Append(Ghost(g)).Append('\n');
        }
        return sb.ToString();
    }

    private static FakeStoryWorld ExpandWorld(IGameData d)
    {
        var w = new FakeStoryWorld(d);
        w.Set(1, 64, 1, 4); w.Set(2, 64, 1, 7); w.Set(2, 65, 1, 17, 1); w.Set(0, 64, 0, 7); w.Set(5, 70, 5, 3);
        w.Set(0, 65, 0, 54, 2); w.Set(3, 64, 3, 8);
        return w;
    }

    [Fact]
    public void Expand_b181() => GoldenHash("expand-b181", ExpandAll(Beta181GameData.Instance, ExpandWorld(Beta181GameData.Instance)));

    [Fact]
    public void Expand_1122() => GoldenHash("expand-1122", ExpandAll(Release1122GameData.Instance, ExpandWorld(Release1122GameData.Instance)));

    [Fact]
    public void Expand_without_world() => GoldenHash("expand-noworld", ExpandAll(Beta181GameData.Instance, null));
}
