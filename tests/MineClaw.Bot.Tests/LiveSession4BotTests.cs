using MineClaw.Agent.Events;
using MineClaw.Bot.Perception;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 2026-10-27: hand-off intents from chat (15:46-15:48, the bread for 67) and server-forced moves explained to the brain
/// (15:51 "I somehow ended up back at spawn").
/// </summary>
public class LiveSession4BotTests
{
    private static readonly string[] Names = ["maid_chan", "maid_chan_67", "maid_chan_69", "kiwi", "PoofimaFox"];
    private static readonly string[] Self = ["maid_chan", "maid"];

    [Fact]
    public void The_bots_own_promise_opens_a_hand_off_for_67_with_the_bread()
    {
        var hit = HandoffIntent.Detect("Give the bread to me and I'll pass it to 67 and send her out, but she'll run back to the house if it gets dicey uwu", Names, Self);
        Assert.NotNull(hit);
        Assert.Equal("maid_chan_67", hit!.Target);
        Assert.Equal(Items.Bread, hit.ItemId);
    }

    [Theory]
    [InlineData("pass this to 69", "maid_chan_69", null)]
    [InlineData("bread for 67", "maid_chan_67", Items.Bread)]
    [InlineData("this is for kiwi", "kiwi", null)]
    [InlineData("give the apples to PoofimaFox", "PoofimaFox", Items.Apple)]
    [InlineData("hand it over to clone 67", "maid_chan_67", null)]
    public void Player_lines_that_hand_items_on(string line, string target, int? item)
    {
        var hit = HandoffIntent.Detect(line, Names, Self);
        Assert.NotNull(hit);
        Assert.Equal(target, hit!.Target);
        Assert.Equal(item, hit.ItemId);
    }

    [Theory]
    [InlineData("breads on the floor")]
    [InlineData("give it to me")]
    [InlineData("wait for 67")]
    [InlineData("go fight mobs for a while")]
    [InlineData("give her 1 bread")] // a pronoun is no target (the bot's reply named 67 explicitly)
    [InlineData("bring 64 cobble to the chest")]
    public void Lines_that_are_not_hand_offs(string line) => Assert.Null(HandoffIntent.Detect(line, Names, Self));

    [Fact]
    public void Server_move_note_names_from_to_and_the_visible_cause()
    {
        var from = new Vec3d(-39.5, 65, 6.8);
        var to = new Vec3d(8.5, 64, 8.5);
        var plain = GameBridge.ServerMoveText(from, to, [], "maid_chan", null);
        Assert.StartsWith("server moved me from -40,65,6 to 8,64,8 (48 blocks; teleport)", plain);
        Assert.Contains("cause not visible", plain);
        var asked = GameBridge.ServerMoveText(from, to, ["<kiwi> nice", "<kiwi> /tp maid_chan kiwi"], "maid_chan", "kiwi");
        Assert.Contains("kiwi said \"/tp maid_chan kiwi\" just before", asked);
        Assert.Contains("kiwi is right here (probably a tp to them)", asked);
        var server = GameBridge.ServerMoveText(from, to, ["Teleporting maid_chan to kiwi."], "maid_chan", null);
        Assert.Contains("server said \"Teleporting maid_chan to kiwi.\"", server);
        Assert.DoesNotContain("said", GameBridge.ServerMoveText(from, to, ["<kiwi> stop that"], "maid_chan", null).Replace("server moved", ""));
    }

    [Fact]
    public async Task A_server_move_posts_a_note_for_the_brain()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        c.RaiseChat("<kiwi> tp maid to me");
        c.RaiseServerMoved(new Vec3d(-39.5, 65, 6.5), new Vec3d(8.5, 64, 8.5));
        GameEvent? e = null;
        for (int i = 0; i < 100 && e == null; i++)
        {
            e = h.Runtime.Events.Snapshot().FirstOrDefault(x => x.Text.StartsWith("server moved me"));
            if (e == null) await Task.Delay(20);
        }
        Assert.NotNull(e);
        Assert.Contains("from -40,65,6 to 8,64,8", e!.Text);
        Assert.Contains("kiwi said \"tp maid to me\"", e.Text);
        Assert.Equal(EventPriority.Normal, e.Priority);
    }
}
