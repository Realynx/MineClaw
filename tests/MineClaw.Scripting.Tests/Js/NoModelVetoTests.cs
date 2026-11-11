namespace MineClaw.Scripting.Tests.Js;

/// <summary>
/// A spent model budget vetoes scripts a brain must supervise, but not a no-model run: a Story Mode pawn is directed by the colony's
/// code, and live (11-11) a paused monthly budget left all 30 pawns online with no work for hours.
/// </summary>
public sealed class NoModelVetoTests
{
    private const string Digs = """
        dig({ at: [1, 64, 1] });
        dig({ at: [2, 64, 1] });
        return { ok: true, msg: 'dug' };
        """;

    [Fact]
    public void A_brain_supervised_run_does_not_start_while_the_budget_is_vetoed()
    {
        using var rig = Rig.Js();
        rig.Veto = "budget low";
        var (s, err) = rig.C.Start(new ScriptRequest { Name = "t", Code = Digs, Language = ScriptLanguage.Js });
        Assert.Null(s);
        Assert.Equal("can't run scripts now: budget low", err);
    }

    [Fact]
    public async Task A_no_model_run_starts_and_acts_while_the_budget_is_vetoed()
    {
        using var rig = Rig.Js();
        rig.Veto = "budget low";
        var (s, err) = rig.C.Start(new ScriptRequest { Name = "story_pawn", Code = Digs, Language = ScriptLanguage.Js, NoModel = true, Unattended = true });
        Assert.True(s != null, err);
        await s!.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(ScriptStatus.Done, s.Status);
        Assert.Equal(2, rig.CallList.Count(c => c.StartsWith("dig ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_veto_that_comes_mid_run_stops_a_supervised_run_but_not_a_no_model_one()
    {
        foreach (var noModel in new[] { false, true })
        {
            using var rig = Rig.Js();
            rig.On["dig"] = (_, _) => { rig.Veto = "budget low"; return Task.FromResult(ActResult.Success("dig ok")); };
            var (s, err) = rig.C.Start(new ScriptRequest { Name = "t", Code = Digs, Language = ScriptLanguage.Js, NoModel = noModel });
            Assert.True(s != null, err);
            await s!.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            if (noModel)
            {
                Assert.Equal(ScriptStatus.Done, s.Status);
                Assert.Equal(2, rig.CallList.Count(c => c.StartsWith("dig ", StringComparison.Ordinal)));
            }
            else
            {
                Assert.NotEqual(ScriptStatus.Done, s.Status);
                Assert.Contains("budget low", s.Outcome);
                Assert.Equal(1, rig.CallList.Count(c => c.StartsWith("dig ", StringComparison.Ordinal)));
            }
        }
    }
}
