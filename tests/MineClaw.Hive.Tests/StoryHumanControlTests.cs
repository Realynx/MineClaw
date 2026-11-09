using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryHumanControlTests
{
    [Fact]
    public void Leaving_revokes_eligibility_and_a_removed_rejoined_account_has_no_old_human_ownership()
    {
        var (c, h, w) = StoryRig.Make(2);
        var pawn = c.P(1);
        Assert.True(c.CanHumanControl(pawn));
        Assert.True(c.SetHumanControl(pawn, true));
        var colonies = new StoryColonies(h);
        Assert.Null(colonies.RemovePawn(c, pawn).Error);
        Assert.False(c.CanHumanControl(pawn)); // the live adapter expires the lease even if input heartbeats continue
        Assert.False(c.SetHumanControl(pawn, true));
        h.Lives[pawn] = PawnLive.Offline;
        c.Tick();
        Assert.False(c.HasPawn(pawn));
        Assert.False(c.HumanControlled(pawn));
        Assert.False(c.SetHumanControl(pawn, false)); // a late lease cleanup is harmless
        h.Spawn(c.Id, 1, [pawn]);
        c.Joined([pawn]);
        Assert.True(c.CanHumanControl(pawn));
        Assert.False(c.HumanControlled(pawn));
        w.Set(3, 64, 0, 1);
        c.Designate("add", "mine", Area.Of(3, 64, 0, 3, 64, 0), null, null);
        Assert.Equal("dig", c.Next(pawn)!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Late_disable_clears_ownership_even_when_the_pawn_is_already_missing()
    {
        var (c, _, _) = StoryRig.Make(2);
        var pawn = c.P(1);
        Assert.True(c.SetHumanControl(pawn, true));
        c.Pawns.Remove(pawn);
        Assert.False(c.SetHumanControl(pawn, false));
        c.Joined([pawn]);
        Assert.False(c.HumanControlled(pawn));
    }

    [Fact]
    public void Human_control_releases_work_and_suppresses_director_without_changing_pawn_configuration()
    {
        var (c, h, w) = StoryRig.Make(2);
        c.DoneGrace = TimeSpan.Zero;
        var pawn = c.Pawns[c.P()];
        var priorities = pawn.Priorities.ToDictionary();
        var schedule = pawn.Schedule.ToArray();
        w.Set(3, 64, 0, 1);
        c.Designate("add", "mine", Area.Of(3, 64, 0, 3, 64, 0), null, null);
        Assert.Equal("dig", c.Next()["kind"]!.GetValue<string>());
        Assert.True(c.SetHumanControl(c.P(), true));
        Assert.Null(c.CurrentOf(c.P()));
        Assert.Equal("hold", c.Next()["kind"]!.GetValue<string>());
        Assert.Equal("controlled", c.State()["pawns"]![0]!["state"]!.GetValue<string>());
        h.Lives[c.P()] = h.Live(c.P()) with { ScriptRunning = false };
        c.Tick();
        Assert.DoesNotContain(c.P(), h.Started);
        Assert.Null(c.IdleReasonOf(c.P()));
        Assert.Equal(priorities, pawn.Priorities);
        Assert.Equal(schedule, pawn.Schedule);
        Assert.False(pawn.Drafted);
        Assert.True(c.SetHumanControl(c.P(), false));
        Assert.Equal("dig", c.Next()["kind"]!.GetValue<string>());
    }
}
