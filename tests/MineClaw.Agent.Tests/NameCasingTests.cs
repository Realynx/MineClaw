using MineClaw.Agent.Chat;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Self;

namespace MineClaw.Agent.Tests;

/// <summary>Live session 2, item 11: the server shows "PoofimaFox", config says "poofimafox"; every comparison ignores case.</summary>
public class NameCasingTests
{
    [Fact]
    public void Command_gate_trusts_the_server_casing_of_a_configured_name()
    {
        var gate = new CommandGate(new CommandOptions(), () => ["poofimafox"]);
        var msg = new ChatMessage(ChatKind.Public, "PoofimaFox", "maid chan run /time set day", "<PoofimaFox> maid chan run /time set day");
        Assert.Equal(1, gate.Observe(msg, mine: true, explicitAddress: true));
    }

    [Fact]
    public void Self_phrases_are_one_key_per_player_whatever_the_casing()
    {
        using var db = AgentDb.Open(null);
        SelfSchema.Ensure(db);
        var store = new SelfStore(db, new SelfOptions());
        var t = new DateTimeOffset(2026, 10, 27, 1, 0, 0, TimeSpan.Zero);
        Assert.False(store.NotePhrase("main", "PoofimaFox", "nick", "maid chan", "ep1", t, 2));
        Assert.True(store.NotePhrase("main", "poofimafox", "nick", "maid chan", "ep2", t.AddMinutes(1), 2)); // same counter
        Assert.Equal("maid chan", Assert.Single(store.Phrases("main", "POOFIMAFOX", "nick")).Phrase);
    }
}
