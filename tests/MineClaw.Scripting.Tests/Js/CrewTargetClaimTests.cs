using System.Collections.Concurrent;
using MineClaw.Hive.Claims;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>
/// Live 10-29: "I just saw the agents begin to work, but they all started to try to go to the same tree at once": every body of a
/// hive ran its own nearest-target pick. forEach leases each target on the hive's shared claim board while it works on it (and
/// keeps a finished one marked), so N bodies running the same job over the same targets never work one target together, and every
/// target gets done. Generic: trees, ores, crops, any positions.
/// </summary>
public class CrewTargetClaimTests
{
    [Fact]
    public async Task Twenty_bodies_over_25_targets_never_share_one_and_every_target_is_done_once()
    {
        var board = new ClaimBoard(TimeSpan.FromMinutes(5));
        var active = new ConcurrentDictionary<string, int>();
        var done = new ConcurrentDictionary<string, int>();
        int overlaps = 0;
        var targets = Enumerable.Range(0, 25).Select(i => $"{(i % 5) * 3},65,{(i / 5) * 3}").ToList();
        var list = "[" + string.Join(",", targets.Select(t => $"'{t}'")) + "]";
        var rigs = Enumerable.Range(0, 20).Select(i =>
        {
            var rig = Rig.Js(body: $"{101 + i}");
            var owner = $"maid_chan_{101 + i}";
            rig.Host.Claims = () => board.ViewFor(owner);
            rig.On["chop_tree"] = async (args, ct) =>
            {
                var at = args.GetProperty("at").GetString()!;
                if (active.AddOrUpdate(at, 1, (_, n) => n + 1) > 1) Interlocked.Increment(ref overlaps);
                await Task.Delay(15, ct);
                active.AddOrUpdate(at, 0, (_, n) => n - 1);
                done.AddOrUpdate(at, 1, (_, n) => n + 1);
                return ActResult.Success("chopped");
            };
            return rig;
        }).ToList();
        try
        {
            var runs = rigs.Select(r => r.Run($"return forEach({list}, t => chopTree(t)).msg")).ToList();
            var sessions = await Task.WhenAll(runs);
            Assert.All(sessions, s => Assert.True(s.Ok, s.Outcome));
            Assert.Equal(0, overlaps);
            Assert.Equal(25, done.Count);
            Assert.All(done, kv => Assert.Equal(1, kv.Value));
            // Bodies whose list was all taken say so (typed), instead of standing silently.
            Assert.Contains(sessions, s => s.Outcome.Contains("taken by other bodies"));
        }
        finally { foreach (var r in rigs) r.Dispose(); }
    }

    [Fact]
    public async Task Find_with_nothing_in_range_returns_exhausted_with_the_frontier_and_forEach_reports_it()
    {
        using var rig = Rig.Js();
        rig.World.SetBlock(14, 65, 0, MineClaw.Data.Blocks.Log, 0); // 13.5 away: beyond r 8, inside 64
        var s = await rig.Run("""
            const near = find('log', { r: 8 })
            const r = forEach(near, t => chopTree(t))
            return [near.length, near.exhausted, near.beyond.length, near.beyond[0].x, r.exhausted, r.msg].join('|')
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("0|true|1|14|true|exhausted: no targets; nearest beyond: log 14,65,0", s.Outcome);
    }

    [Fact]
    public async Task A_single_bot_without_a_board_works_every_target_as_before()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("return forEach(['1,65,1', '2,65,2'], t => chopTree(t)).msg");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("2/2 done", s.Outcome);
    }
}
