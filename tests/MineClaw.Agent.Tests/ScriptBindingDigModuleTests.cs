using MineClaw.Agent.Routines;

namespace MineClaw.Agent.Tests;

/// <summary>
/// A player's own standing binding may only move, look, gesture and talk; only a trusted player's may dig, place or take
/// items. The check is a denylist of calls, so every dig-module function has to be on it: excavate, seal, lightUp and
/// pattern.learn / pattern.apply included.
/// </summary>
public class ScriptBindingDigModuleTests
{
    [Theory]
    [InlineData("excavate('0,64,0,3,68,3')")]
    [InlineData("excavate({site: 'basement', to: 20})")]
    [InlineData("seal('0,64,0,3,68,3')")]
    [InlineData("seal({site: 'basement'})")]
    [InlineData("lightUp('0,64,0,3,68,3')")]
    [InlineData("pattern.learn('columns', '0,64,0,9,68,9')")]
    [InlineData("pattern.learn('column 3 step 0,-1,0', args.pos)")]
    [InlineData("pattern.apply('columns', 20)")]
    [InlineData("pattern.apply('column 3 step 0,-1,0', 20, args.pos)")]
    public void A_dig_module_call_acts_on_the_world(string code) => Assert.True(ScriptBindings.ActsOnWorld(code));

    [Theory]
    [InlineData("character.gesture('wave', args.player)")]
    [InlineData("move.to(args.pos)")]
    [InlineData("say('hi')")]
    [InlineData("say('the pattern.learn call would dig here')")]
    [InlineData("// pattern.learn('columns', args.pos) is not called here")]
    public void A_harmless_call_does_not_act_on_the_world(string code) => Assert.False(ScriptBindings.ActsOnWorld(code));
}
