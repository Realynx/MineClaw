using MineClaw.Bot.Blueprints;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Tests;

namespace MineClaw.Scripting.Tests;

/// <summary>
/// The hive lane job (<see cref="BuildJobs.LaneScript"/>, what a clone runs for its lane of a crew build) is lint-clean JavaScript,
/// and its <c>build({bp, lane})</c> host binding reaches <see cref="BlueprintService.BuildLaneAsync"/> with both arguments.
/// </summary>
public class BuildLaneScriptTests
{
    [Fact]
    public void The_lane_job_is_lint_clean_javascript()
    {
        Assert.Equal(ScriptLanguage.Js, ScriptLanguages.Detect(BuildJobs.LaneScript));
        var r = ScriptLint.Check(BuildJobs.LaneScript, ScriptBridge.ActionTools, language: ScriptLanguage.Js);
        Assert.False(r.HasErrors, r.Format(LintSeverity.Error));
    }

    [Fact]
    public async Task The_lane_job_runs_the_designs_lane_through_the_build_binding()
    {
        var (h, _, _) = BotScriptTests.Bot();
        using var _ = h;
        var s = h.Blueprints!.Service;
        var rec = s.Store.Insert(new BlueprintRecord
        {
            Name = "wall", Description = "a wall", Bot = "maid_chan", Status = DesignStatus.Accepted, AcceptedBy = "PoofImaFox",
            Dsl = "blueprint wall\nsize 3x1x1\npalette C=cobblestone\nlayer 0\nCCC",
        });
        var r = await h.Run("script", new { op = "run", code = BuildJobs.LaneScript, @params = new { bp = rec.Id, lane = 1 } });
        // No crew plan was sliced for it: the lane says so (the binding carried bp and lane to the lane runner).
        Assert.Contains("no crew plan", r.Text);
    }
}
