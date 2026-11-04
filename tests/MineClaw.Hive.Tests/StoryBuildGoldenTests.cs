using System.Text;
using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;
using static MineClaw.Hive.Tests.StoryGoldenTests;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Behaviour pins for the build hot path (docs/dev/complexity.md WP01): batches and their stands, the per-cell progress notes
/// (done, occupied, out of reach, wrong rotation, malformed), the batch's end (done with and without a batch note, failed), the
/// cells' and builds' states and alerts. Goldens written by the code before it was reshaped (see <see cref="StoryGoldenTests"/>).
/// </summary>
public sealed class StoryBuildGoldenTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static JsonArray A(params int[] v) => new(v.Select(x => (JsonNode)x).ToArray());

    private static string Run(IGameData? data)
    {
        var t = new Clock(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var (c, h, w) = StoryRig.Make(3, data, t);
        c.Rng = new Random(1);
        var sb = new StringBuilder();
        void Rec(string label, (JsonNode? Result, string? Error) r) => sb.Append(label).Append(" => ").Append(R(r)).Append('\n');
        P3 chest = new(0, 64, 9), chest2 = new(4, 64, 9);
        Rec("storage", c.StorageOp("set", [chest, chest2], null, null, null));
        foreach (var s in new[] { new Stack(4, 0, 64), new Stack(5, 0, 64), new Stack(67, 0, 30), new Stack(61, 0, 2), new Stack(54, 0, 2) })
            c.AddContents(c.StorageAt(chest)!, s);
        foreach (var s in new[] { new Stack(4, 0, 30), new Stack(5, 0, 10), new Stack(17, 0, 8) }) c.AddContents(c.StorageAt(chest2)!, s);
        for (int x = -12; x <= 30; x++) for (int z = -12; z <= 30; z++) w.Set(x, 63, z, 2); // a grass floor
        w.Set(13, 64, 13, 1); // stone in the wall's line: cleared first
        JsonObject It(string kind, JsonNode area, string? mat = null, JsonObject? extra = null)
        {
            var o = new JsonObject { ["kind"] = kind, ["area"] = area };
            if (mat != null) o["material"] = mat;
            foreach (var (k, v) in extra ?? []) o[k] = v?.DeepClone();
            return o;
        }
        foreach (var items in new JsonArray[]
                 {
                     [It("wall", A(10, 64, 10, 16, 64, 16), "cobblestone", new JsonObject { ["height"] = 2 })],
                     [It("floor", A(-8, 63, -8, -4, 63, -5), "oak planks")],
                     [It("stairs", A(0, 64, 20, 3, 65, 20), "cobblestone")],
                     [It("furnace", A(-3, 64, 5, -3, 64, 5), null, new JsonObject { ["facing"] = "s" }), It("chest", A(-5, 64, 5, -5, 64, 5))],
                     [It("block", A(20, 64, 0, 24, 64, 0), "cobblestone")],
                     [It("block", A(-10, 64, 25, -4, 64, 25), "cobblestone_stairs", new JsonObject { ["facing"] = "e" })],
                 })
            Rec("builds add " + items.ToJsonString(), c.BuildsOp("add", items, null));
        // A furnace already there, turned the wrong way (holds items: never taken back), and a stair the wrong way.
        w.Set(-3, 64, 5, 61, 2);
        string Builds() => string.Join(" | ", c.Builds.Select(b => $"{b.Id}:{b.Status}:{b.Reason}:"
            + string.Join(",", b.Ghosts.Select(g => g.State[0] + (g.Reason == null ? "" : "!")))));
        var ps = c.Pawns.Values.ToList();
        for (int i = 0; i < ps.Count; i++) h.Lives[ps[i].Name] = FakeStoryHost.Live(i * 3, 64, 2);
        for (int r = 0; r < 40; r++)
        {
            for (int i = 0; i < ps.Count; i++)
            {
                var name = ps[i].Name;
                var job = c.Next(name);
                sb.Append($"r{r} {name} job => ").Append(J(job)).Append('\n');
                if (job?["id"]?.GetValue<string>() is not { } id) continue;
                void P(string note) => sb.Append("  progress ").Append(note).Append(" => ").Append(c.Report(name, id, "progress", note, null)).Append('\n');
                var cells = (job["cells"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? [];
                if (cells.Count > 0) P("fetched");
                if (r == 2 && i == 0) { P("cell nope"); P("cell 1,2"); P("cell 99,99,99 done"); P("something else"); }
                for (int k = 0; k < cells.Count; k++)
                {
                    var at = cells[k].Split(',').Select(int.Parse).ToArray();
                    var g = c.Builds.SelectMany(b => b.Ghosts).First(x => x.At == new P3(at[0], at[1], at[2]));
                    switch ((k + r + i) % 7)
                    {
                        case 0: w.Set(at[0], at[1], at[2], g.Id, g.Meta); P($"cell {cells[k]} done"); break;
                        case 1: P($"cell {cells[k]} done"); break;
                        case 2: w.Set(at[0], at[1], at[2], 1); P($"cell {cells[k]} done"); break;
                        case 3: P($"cell {cells[k]} failed: can't place at {cells[k]}: I couldn't get closer"); break;
                        case 4: w.Set(at[0], at[1], at[2], g.Id, g.Meta ^ 3); P($"cell {cells[k]} failed: wrong rotation"); break;
                        case 5: break; // unreported: the batch's end looks at it
                        default: P($"cell {cells[k]} failed: no support face"); break;
                    }
                }
                if (cells.Count > 0 && (r + i) % 2 == 0) w.Set(int.Parse(cells[^1].Split(',')[0]), int.Parse(cells[^1].Split(',')[1]), int.Parse(cells[^1].Split(',')[2]), 4);
                var (status, note) = ((r + i) % 4) switch
                {
                    0 => ("done", "batch: placed"),
                    1 => ("done", (string?)null),
                    2 => ("failed", "fetch failed: chest is busy"),
                    _ => ("failed", "script error"),
                };
                sb.Append("  report ").Append(status).Append(' ').Append(note).Append(" => ").Append(c.Report(name, id, status, note, null)).Append('\n');
                sb.Append("  builds ").Append(Builds()).Append('\n');
            }
            c.Tick();
            t.Now += TimeSpan.FromSeconds(r % 3 == 2 ? 20 : 2);
        }
        sb.Append("alerts\n");
        foreach (var a in c.Alerts) sb.Append("  ").Append(a.Level).Append(' ').Append(a.Text).Append('\n');
        sb.Append("leases ").Append(string.Join(" ", c.Leases().OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value))).Append('\n');
        var st = c.State(null);
        st.Remove("categories");
        st.Remove("plants");
        sb.Append("state => ").Append(J(st)).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void Batches_progress_and_ends_b181() => Golden("builds-progress-b181", Run(null));

    [Fact]
    public void Batches_progress_and_ends_1122() => Golden("builds-progress-1122", Run(Release1122GameData.Instance));

    [Fact]
    public void Item_holder_table_matches_the_old_pattern_for_every_id()
    {
        static bool Old(int id) => id is 54 or 146 or 61 or 62 or 23 or 158 or 154 or 117 or 84 or (>= 219 and <= 234);
        for (int id = -1; id <= 4096; id++) Assert.Equal(Old(id), StoryColony.KeepsItems(id));
    }
}
