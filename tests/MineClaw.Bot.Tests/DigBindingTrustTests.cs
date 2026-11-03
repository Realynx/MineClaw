using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Routines;
using MineClaw.Bot;
using MineClaw.Bot.GameTools;

namespace MineClaw.Bot.Tests;

/// <summary>
/// The dig module's own functions (excavate, seal, lightUp, pattern.learn / pattern.apply) dig the world, so they count as
/// world calls: a regular player's binding may not use them, a trusted player's is bound.
/// </summary>
public class DigBindingTrustTests
{
    [Theory]
    [InlineData("excavate('0,64,0,3,68,3')")]
    [InlineData("seal('0,64,0,3,68,3')")]
    [InlineData("lightUp('0,64,0,3,68,3')")]
    [InlineData("pattern.learn('columns', '0,64,0,9,68,9')")]
    [InlineData("pattern.apply('columns', 20)")]
    public async Task A_regular_players_dig_module_binding_is_refused_and_a_trusted_players_is_bound(string code)
    {
        var o = TestBot.Options();
        o.Self.TrustedPlayers = ["PoofimaFox"];
        using var h = BotHost.Create(new FakeGameClient(), o, null, new NoLlm(), persist: false);
        var lib = ScriptTools.Of(h.Runtime)!.Library;

        var refused = await Bind(h, "dig_module", code, "player_one");
        Assert.StartsWith("error", refused);
        Assert.Contains("trusted", refused);
        Assert.DoesNotContain(lib.Bindings(), x => x.Name == "dig_module");

        var bound = await Bind(h, "dig_module", code, "PoofimaFox");
        Assert.StartsWith("ok", bound);
        Assert.Contains(lib.Bindings(), x => x.Name == "dig_module" && x.Binding.World);
    }

    private static async Task<string> Bind(BotHost h, string name, string code, string speaker)
    {
        var saved = await h.Run("script", new { op = "save", name, code, force = "test: same body, different trigger" });
        Assert.True(saved.Ok, saved.Text);
        TurnContext.Current = new TurnInfo(speaker, [speaker], false);
        try
        {
            var r = await h.Run("script", new { op = "bind", name, @params = new { on = "player.punchBlock" } });
            return (r.Ok ? "ok " : "error ") + r.Text;
        }
        finally { TurnContext.Current = null; }
    }
}
