using MineClaw.Hive.Crew;
using MineClaw.Scripting;
using MineClaw.Scripting.Crew;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 10-30 00:23: main's deforest crew counted every managed account it had ever used as a body and a lane on each launched a
/// whole clone process (no resource ceiling, no player cap): 16 processes, CT 114 out of memory. A crew uses the clones in game.
/// </summary>
public class HiveCrewPortTests
{
    [Fact]
    public async Task Offline_clone_accounts_are_no_crew_bodies_and_a_lane_on_one_launches_nothing()
    {
        await using var h = new TestHive();
        var port = new HiveCrewPort(h.Supervisor, new InMemoryCrewBoard());
        Assert.Empty(port.Bodies());

        var lane = port.Start(new LaneSpec("420", "deforest", "", ScriptLanguage.Js, "cutter", null, "c1"));
        var r = await lane.Completion;
        Assert.False(r.Ok);
        Assert.Contains("isn't in game", r.Msg);
        Assert.All(h.Supervisor.Slots, s => Assert.Null(s.Task));
    }
}
