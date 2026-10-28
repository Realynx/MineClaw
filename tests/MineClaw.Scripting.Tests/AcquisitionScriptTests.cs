using MineClaw.Agent.Rag;
using MineClaw.Bot.Tests;
using MineClaw.Data;

namespace MineClaw.Scripting.Tests;

/// <summary>ensure goes through the acquisition planner (hidden acquire action); the lint asks for ensure before tool jobs.</summary>
public class AcquisitionScriptTests
{
    private static readonly string[] Acts = ["gen", "harvest_generator", "mine", "dig", "deposit", "craft", "acquire"];

    [Theory]
    [InlineData("gen()\nreturn 1", true)]
    [InlineData("upto(4, () => harvest_generator('5,65,0', 8).ok)", true)]
    [InlineData("return mine('cobblestone', 8).msg", true)]
    [InlineData("return mine('dirt', 8).msg", false)]
    [InlineData("ensure('pickaxe')\nreturn gen().ok", false)]
    [InlineData("expect(acquire('pickaxe'))\ngen()\nreturn 1", false)]
    public void Lint_warns_when_a_tool_job_has_no_ensure(string code, bool warns)
    {
        var r = ScriptLint.Check(code, Acts, language: ScriptLanguage.Js);
        Assert.False(r.HasErrors, r.Format(LintSeverity.Error));
        Assert.Equal(warns, r.Findings.Any(f => f.Rule == "no-ensure"));
        if (warns) Assert.Contains("ensure('pickaxe')", r.Format(LintSeverity.Warning));
    }

    [Fact]
    public void The_author_exemplar_stays_lint_clean() =>
        Assert.DoesNotContain(ScriptLint.Check(ScriptPrompts.Exemplar, Acts, language: ScriptLanguage.Js).Findings, f => f.Rule == "no-ensure");

    [Fact]
    public async Task Ensure_runs_the_acquisition_planner()
    {
        var (h, c, _) = BotScriptTests.Bot();
        using var _ = h;
        c.World.SetBlock(2, 65, 2, Blocks.Chest, 0);
        h.Runtime.Observations.Upsert(new Observation("chest", "2,65,2", "chest")
        {
            Bounds = Box.Point(2, 65, 2), Contents = [new ItemCount(Items.StonePickaxe, "stone_pickaxe", 1)],
        });
        c.Effects["withdraw"] = () => c.Give(36, Items.StonePickaxe, 1);
        var r = await h.Run("script", new { op = "run", code = "return String(ensure('pickaxe'))" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("stone_pickaxe", r.Text);
        Assert.Contains(c.CallList, x => x.StartsWith($"withdraw {Items.StonePickaxe} 1 2,65,2"));
    }

    [Fact]
    public async Task Ensure_fails_with_the_planners_reason()
    {
        var (h, _, _) = BotScriptTests.Bot();
        using var _ = h;
        var r = await h.Run("script", new { op = "run", code = "ensure('axe')\nreturn 'x'" });
        Assert.Contains("ensure: no axe (", r.Text);
    }
}
