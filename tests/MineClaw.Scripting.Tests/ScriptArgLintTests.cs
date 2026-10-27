using MineClaw.Agent.Tools;
using MineClaw.Bot.Tests;
using MineClaw.Scripting;

namespace MineClaw.Scripting.Tests;

/// <summary>
/// Live 10-27 21:12: an Opus script failed at runtime "script at line 3 (bad input: missing 'at'): cp 1/2, 5 acts, 3s" — its
/// <c>gen{}</c> (harvest_generator) needs <c>at</c>, and the cheat-sheet exemplar itself showed <c>gen{}</c>. The lint now
/// checks every binding call against the real tool schema before anything runs.
/// </summary>
public class ScriptArgLintTests
{
    private static readonly string[] Acts = ["mine", "gen", "deposit", "take", "craft", "go", "dig", "place", "chop"];

    /// <summary>Signatures built from the same ToolSchema calls GameToolSet uses (positional order = schema order).</summary>
    private static readonly Dictionary<string, BindingSignature> Sigs = new()
    {
        ["harvest_generator"] = ScriptLint.SignatureOf(ToolSchema.Create().Str("at", "output block").Int("count", "up to 20000")
            .Str("store", required: false).Str("stand", required: false).Build())!,
        ["place"] = ScriptLint.SignatureOf(ToolSchema.Create().Str("block").Str("at", "x,y,z").Build())!,
        ["dig"] = ScriptLint.SignatureOf(ToolSchema.Create().Str("at", "x,y,z").Build())!,
        ["goto"] = ScriptLint.SignatureOf(ToolSchema.Create().Str("to").Num("dist", required: false).Build())!,
        ["mine"] = ScriptLint.SignatureOf(ToolSchema.Create().Str("block").Int("count", required: false).Int("radius", required: false).Build())!,
        ["withdraw"] = ScriptLint.SignatureOf(ToolSchema.Create().Str("item").Int("count", required: false).Str("at", required: false).Build())!,
        ["deposit"] = ScriptLint.SignatureOf(ToolSchema.Create().Str("at", required: false).StrArray("items", required: false).Build())!,
        ["chop_tree"] = ScriptLint.SignatureOf(ToolSchema.Create().Str("at", required: false).Build())!,
    };

    /// <summary>The live script's shape: the cheat-sheet exemplar as it was (gen{} with no cell).</summary>
    private const string LiveScript = """
        -- purpose: mine cobble at the gen, store it
        ensure"pickaxe" room(2) local b=inv"cobblestone"
        cp"mine" upto(args.n or 16,function() expect(retry(3,function() return gen{} end)) return inv.free()>0 end)
        expect(inv"cobblestone">b,"mined nothing")
        cp"store" local ok,m=deposit{} expect(ok,m) return "stored"
        """;

    [Fact]
    public void Schema_signature_requires_only_strings_the_tool_cannot_default()
    {
        var gen = Sigs["harvest_generator"];
        Assert.Equal(["at", "count", "store", "stand"], gen.Params);
        Assert.Equal(["at"], gen.Required); // count is schema-required but always defaults (16)
    }

    [Fact]
    public void Live_script_is_rejected_at_line_3_before_anything_runs()
    {
        var r = ScriptLint.Check(LiveScript, Acts, signatures: Sigs);
        var e = Assert.Single(r.Errors);
        Assert.Equal("missing-arg", e.Rule);
        Assert.Equal(3, e.Line);
        Assert.StartsWith("gen needs at=", e.Message);
        Assert.Contains("L3 gen needs at=", r.Format(LintSeverity.Error, 300));
    }

    [Theory]
    [InlineData("place{'torch'}", "place needs at=")]
    [InlineData("place{at='1,64,2'}", "place needs block=")]
    [InlineData("dig{}", "dig needs at=")]
    [InlineData("local ok = place('torch')", "place needs at=")]
    [InlineData("place{'torch', nil}", "place needs at=")]
    [InlineData("go{}", "go needs to=")]
    [InlineData("take{n=8}", "take needs item=")]
    [InlineData("mine{}", "mine needs block=")]
    public void Missing_required_args_are_errors(string code, string message)
    {
        var r = ScriptLint.Check(code, Acts, signatures: Sigs);
        var e = Assert.Single(r.Errors);
        Assert.Equal("missing-arg", e.Rule);
        Assert.StartsWith(message, e.Message);
    }

    [Theory]
    [InlineData("gen{'4,64,6'}")]
    [InlineData("gen{args.at,1}")]
    [InlineData("gen{at='4,64,6',n=8}")]
    [InlineData("gen'4,64,6'")]
    [InlineData("gen(4,64,6)")]
    [InlineData("place{'torch','1,64,2'}")]
    [InlineData("place{'torch',1,64,2}")]
    [InlineData("place('torch', me.pos)")]
    [InlineData("place{block='torch',at={x=1,y=64,z=2}}")]
    [InlineData("dig{x=1,y=64,z=2}")]
    [InlineData("dig{1,64,2}")]
    [InlineData("dig(me.pos)")]
    [InlineData("local t={'1,64,2'} dig(t)")]
    [InlineData("dig(unpack(spots))")]
    [InlineData("go{'4,64,6'} go'chest' go{to=args.to}")]
    [InlineData("chop{} deposit{} deposit{'14,64,-5',{'log'}} take{'coal',8} mine'dirt'")]
    [InlineData("local function gen() return true end gen{}")]
    [InlineData("upto(3,function() local ok,m=gen{'4,64,6'} return ok end)")]
    [InlineData("eat{} log{'x'}")]
    public void Good_calls_pass(string code)
    {
        var r = ScriptLint.Check(code, Acts, signatures: Sigs);
        Assert.False(r.HasErrors, r.Format(LintSeverity.Error, 400));
    }

    [Fact]
    public void Cheat_sheet_exemplar_and_api_line_name_the_required_args()
    {
        var r = ScriptLint.Check(ScriptPrompts.Exemplar, Acts, signatures: Sigs);
        Assert.Empty(r.Errors);
        Assert.Contains("gen{\"x,y,z\"}", ScriptPrompts.CheatSheet);
        Assert.DoesNotContain("gen{}", ScriptPrompts.CheatSheet);
        // Every binding the cheat-sheet's API lines show fills its required args.
        var api = string.Join("\n", ScriptPrompts.CheatSheet.Split('\n').Where(l => l.Contains('{') && !l.Contains("function")).Take(3));
        var lint = ScriptLint.Check(api.Replace("|place|player", "").Replace("at?,", ""), Acts, signatures: Sigs);
        Assert.DoesNotContain(lint.Errors, f => f.Rule == "missing-arg");
    }

    [Fact]
    public async Task Bot_script_tool_rejects_the_live_script_with_zero_actions()
    {
        var (h, c, b) = BotScriptTests.Bot();
        var bad = await h.Run("script", new { op = "run", code = LiveScript });
        Assert.False(bad.Ok);
        Assert.Contains("lint errors (fix and resubmit): L3 gen needs at=", bad.Text);
        Assert.Null(b.Controller.Last);
        Assert.Empty(c.CallList);

        var good = await h.Run("script", new { op = "run", code = "local ok,m=dig{'1,64,1'} return m" });
        Assert.DoesNotContain("lint errors", good.Text);
    }
}
