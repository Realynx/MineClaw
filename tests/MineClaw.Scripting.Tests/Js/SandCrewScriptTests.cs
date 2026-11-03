using MineClaw.Data;
using MineClaw.Scripting;
using MineClaw.World;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>
/// Every body gathers sand and stashes it in the staging chest whenever its bag fills.
/// Runs the example script from docs/pyramid-plan.md on the offline rig.
/// </summary>
public class SandCrewScriptTests
{
    internal const string SandRun = """
        // purpose: every body digs n sand near the shore and stashes it in the staging chest as its bag fills
        const P = params({ n: 2500, chest: '10,64,10', r: 40 })
        function digger(a) {
          const r = gather('sand', a.n, { from: 'mine', stash: P.chest, r: P.r })
          crew.inc('sand', r.n)
          return `${me.body} ${r.n}`
        }
        function main() {
          const r = everyBody('digger', { n: P.n })
          return `sand run: ${crew.get('sand')} sand (${r.msg})`
        }
        """;

    private static readonly string[] Acts = ["goto", "go", "mine", "deposit", "take", "withdraw", "say"];

    /// <summary>A body whose shore always has sand: each mine call adds up to 256 (the tool's cap) in 64-stacks; a deposit empties the bag.</summary>
    private static void Digger(Rig rig, System.Collections.Concurrent.ConcurrentQueue<(string Body, int Count)> trips, string body)
    {
        int sand = Items.ByName("sand")!.Id, slot = 10;
        rig.On["mine"] = (args, _) =>
        {
            int n = Math.Min(args.GetProperty("count").GetInt32(), 256);
            if (args.GetProperty("block").GetString() != "sand") return Task.FromResult(ActResult.Fail("not sand"));
            for (int left = n; left > 0 && slot < 45; left -= 64) rig.Give(slot++, sand, Math.Min(64, left));
            return Task.FromResult(ActResult.Success($"mined {n} sand"));
        };
        rig.On["deposit"] = (_, _) =>
        {
            trips.Enqueue((body, rig.World.Inventory.CountItem(sand)));
            for (int i = 10; i < 45; i++) rig.World.Inventory.SetSlot(0, i, default);
            slot = 10;
            return Task.FromResult(ActResult.Success("stored"));
        };
    }

    [Fact]
    public void The_sand_run_script_lints_clean()
    {
        var r = ScriptLint.Check(SandRun, Acts, language: ScriptLanguage.Js);
        Assert.False(r.HasErrors, string.Join(" | ", r.Errors));
    }

    [Fact]
    public async Task Every_body_digs_sand_and_stashes_it_each_time_its_bag_fills()
    {
        using var crew = new FakeCrew("67", "69");
        var trips = new System.Collections.Concurrent.ConcurrentQueue<(string Body, int Count)>();
        foreach (var (name, rig) in crew.Rigs) Digger(rig, trips, name);
        using var main = Rig.Js(crew: crew);
        var s = await main.Run(SandRun, "sand_run", @params: """{"n":2500}""");
        Assert.True(s.Ok, s.Outcome + " | " + string.Join(";", main.Logs));
        Assert.Contains("sand run: 5000 sand", s.Outcome);   // each of the two bodies got its 2500
        foreach (var b in new[] { "67", "69" })
        {
            var mines = crew.Rigs[b].Norm().Where(c => c.StartsWith("mine")).ToList();
            Assert.NotEmpty(mines);
            Assert.All(mines, m => Assert.StartsWith("""mine {"block":"sand",""", m));
            Assert.Contains(crew.Rigs[b].Norm(), c => c.StartsWith("""deposit {"at":"10,64,10""")); // into the staging chest
            Assert.Equal(2500, trips.Where(t => t.Body == b).Sum(t => t.Count));                       // all of it ended in the chest
        }
        Assert.All(trips, t => Assert.InRange(t.Count, 1, 35 * 64));                                    // never more than a bag at once
        Assert.True(trips.Count(t => t.Body == "67") >= 2, "2500 sand is more than a bag: stashed when the bag fills, not only at the end");
        Assert.DoesNotContain(main.CallList, c => c.StartsWith("mine"));                               // the coordinator itself never dug
    }
}
