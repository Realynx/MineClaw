using MineClaw.Agent.Tools;
using MineClaw.Bot.Tests;
using MineClaw.Scripting;

namespace MineClaw.Scripting.Tests;

/// <summary>
/// Live 10-27 21:12: an Opus script failed at runtime "script at line 3 (bad input: missing 'at'): cp 1/2, 5 acts, 3s" — its
/// <c>gen{}</c> (harvest_generator) needs <c>at</c>, and the cheat-sheet exemplar itself showed <c>gen{}</c>. The lint now
/// checks every binding call against the real tool schema before anything runs (JS for every new script; the legacy Lua lint
/// keeps the same rule for saved Lua versions).
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

    private static LintReport Js(string code) => ScriptLint.Check(code, Acts, signatures: Sigs, language: ScriptLanguage.Js);
    private static LintReport Lua(string code) => ScriptLint.Check(code, Acts, signatures: Sigs, language: ScriptLanguage.Lua);

    /// <summary>The live script's shape, in JS: gen() with no cell.</summary>
    private const string LiveScript = """
        // purpose: mine cobble at the gen, store it
        ensure('pickaxe'); room(2); const b = inv('cobblestone')
        cp('mine'); upto(args.n ?? 16, () => { expect(retry(3, () => gen())); return inv.free() > 0 })
        expect(inv('cobblestone') > b, 'mined nothing')
        cp('store'); expect(deposit()); return 'stored'
        """;

    /// <summary>The same script as it ran live (legacy Lua).</summary>
    private const string LiveLuaScript = """
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
        var r = Js(LiveScript);
        var e = Assert.Single(r.Errors);
        Assert.Equal("missing-arg", e.Rule);
        Assert.Equal(3, e.Line);
        Assert.StartsWith("gen() needs at=", e.Message);
        Assert.Contains("L3 gen() needs at=", r.Format(LintSeverity.Error, 300));

        var lua = Assert.Single(Lua(LiveLuaScript).Errors);
        Assert.Equal("missing-arg", lua.Rule);
        Assert.Equal(3, lua.Line);
        Assert.StartsWith("gen needs at=", lua.Message);
    }

    [Theory]
    [InlineData("place('torch')", "place() needs at=")]
    [InlineData("place({at: '1,64,2'})", "place() needs block=")]
    [InlineData("dig()", "dig() needs at=")]
    [InlineData("const r = place('torch')", "place() needs at=")]
    [InlineData("place('torch', null)", "place() needs at=")]
    [InlineData("go()", "go() needs to=")]
    [InlineData("take({n: 8})", "take() needs item=")]
    [InlineData("mine()", "mine() needs block=")]
    public void Missing_required_args_are_errors(string code, string message)
    {
        var r = Js(code);
        var e = Assert.Single(r.Errors);
        Assert.Equal("missing-arg", e.Rule);
        Assert.StartsWith(message, e.Message);
    }

    [Theory]
    [InlineData("gen('4,64,6')")]
    [InlineData("gen(args.at, 1)")]
    [InlineData("gen({at: '4,64,6', n: 8})")]
    [InlineData("gen(4, 64, 6)")]
    [InlineData("place('torch', '1,64,2')")]
    [InlineData("place('torch', 1, 64, 2)")]
    [InlineData("place('torch', me.pos)")]
    [InlineData("place({block: 'torch', at: {x: 1, y: 64, z: 2}})")]
    [InlineData("dig({x: 1, y: 64, z: 2})")]
    [InlineData("dig(1, 64, 2)")]
    [InlineData("dig(me.pos)")]
    [InlineData("const t = '1,64,2'\ndig(t)")]
    [InlineData("const spots = [1, 64, 2]\ndig(...spots)")]
    [InlineData("go('4,64,6')\ngo('chest')\ngo({to: args.to})")]
    [InlineData("chop()\ndeposit()\ndeposit('14,64,-5', ['log'])\ntake('coal', 8)\nmine('dirt')")]
    [InlineData("upto(3, () => gen('4,64,6').ok)")]
    [InlineData("deposit({})\nlog('x')")]
    public void Good_calls_pass(string code)
    {
        var r = Js(code);
        Assert.False(r.HasErrors, r.Format(LintSeverity.Error, 400));
    }

    [Theory]
    [InlineData("place{'torch'}", "place needs at=")]
    [InlineData("place{at='1,64,2'}", "place needs block=")]
    [InlineData("dig{}", "dig needs at=")]
    [InlineData("go{}", "go needs to=")]
    [InlineData("take{n=8}", "take needs item=")]
    public void Legacy_lua_lint_keeps_the_missing_arg_rule(string code, string message)
    {
        var e = Assert.Single(Lua(code).Errors);
        Assert.Equal("missing-arg", e.Rule);
        Assert.StartsWith(message, e.Message);
        Assert.False(Lua("gen{'4,64,6'} dig{1,64,2} go{to=args.to} local function gen() return true end gen{}").HasErrors);
    }

    [Fact]
    public void Author_examples_fill_every_required_arg()
    {
        Assert.Empty(Js(ScriptPrompts.Exemplar).Errors);
        foreach (var (title, code) in ScriptPrompts.Examples)
            Assert.DoesNotContain(Js(code).Errors, f => f.Rule == "missing-arg");
        Assert.DoesNotContain("gen{}", ScriptPrompts.AuthorSystem);
    }

    [Fact]
    public async Task Bot_script_tool_rejects_the_live_script_with_zero_actions()
    {
        var (h, c, b) = BotScriptTests.Bot();
        var bad = await h.Run("script", new { op = "run", code = LiveScript });
        Assert.False(bad.Ok);
        Assert.Contains("lint errors (fix and resubmit): L3 gen() needs at=", bad.Text);
        Assert.Null(b.Controller.Last);
        Assert.Empty(c.CallList);

        var good = await h.Run("script", new { op = "run", code = "return dig('1,64,1').msg" });
        Assert.DoesNotContain("lint errors", good.Text);
    }
}
