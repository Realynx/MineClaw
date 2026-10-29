using MineClaw.Agent.Chat;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Hierarchy;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 10-29: Fox said "420 come here" (420 = the DeepSeek hive's own body): main stayed out ("hive: another bot's") and 420's own
/// arbiter said "not us" too (main's roster no longer listed it live while it put things away): nobody answered until "maid tell
/// 420 to come here". And none of 420's 20 subhive bodies were in any arbiter, so "113 stop" was nobody's either. A line naming a
/// hive is that hive's (it is always live to itself); a line naming a subhive body is its host hive's; main relays only when the
/// host is unreachable.
/// </summary>
public class HiveAddressingTests
{
    private static ChatMessage Say(string text, string player = "PoofimaFox") => new(ChatKind.Public, player, text, $"<{player}> {text}");

    private static HiveTree Tree()
    {
        var t = new HiveTree("maid_chan");
        t.Put(new HiveNode("maid_chan_420", CloneKind.Hive, BrainKind.DeepSeek, HiveTree.Root));
        t.Put(new HiveNode("maid_chan_113", CloneKind.Subhive, null, "maid_chan_420"));
        t.Put(new HiveNode("maid_chan_106", CloneKind.Subhive, null, HiveTree.Root)); // main's own body
        return t;
    }

    [Fact]
    public void On_main_420_come_here_and_113_stop_belong_to_the_hive_420_and_main_stays_out()
    {
        var main = new ChatArbiter("maid_chan", "maid_chan", []);
        main.AddClone("maid_chan_420");
        main.Follow(Tree());
        main.SetLive(["maid_chan_420"]);

        Assert.Equal("maid_chan_420", main.OwnerOf(Say("420 come here")));
        Assert.False(main.Decide(Say("420 come here"), locallyAddressed: true));
        Assert.Equal("maid_chan_420", main.HostOf("maid_chan_113"));
        Assert.Equal("maid_chan_420", main.OwnerOf(Say("113 stop")));
        Assert.False(main.Decide(Say("113 stop"), locallyAddressed: true));
        // Main's own subhive body is main's.
        Assert.Equal("maid_chan", main.OwnerOf(Say("106 come here")));
        Assert.True(main.Decide(Say("106 come here"), locallyAddressed: false));
    }

    [Fact]
    public void When_the_host_hive_is_unreachable_main_takes_the_line_and_relays()
    {
        var main = new ChatArbiter("maid_chan", "maid_chan", []);
        main.AddClone("maid_chan_420");
        main.Follow(Tree());
        main.SetLive([]); // 420's process is gone
        Assert.Equal("maid_chan", main.OwnerOf(Say("113 stop")));
        Assert.True(main.Decide(Say("113 stop"), locallyAddressed: false));
    }

    [Fact]
    public void In_the_hive_process_its_own_name_and_its_bodies_names_are_its_own_even_with_a_stale_roster()
    {
        var hive = new ChatArbiter("maid_chan_420", "main", []);
        hive.Apply(new MineClaw.Hive.Ipc.RosterMsg("maid_chan", ["maid chan"], [], new(), Clones: ["maid_chan_420"])); // not listed live
        var addressed = new List<string>();
        hive.AddressedHere += m => addressed.Add(m.Text);

        Assert.Equal("maid_chan_420", hive.OwnerOf(Say("420 come here")));
        Assert.True(hive.Decide(Say("420 come here"), locallyAddressed: false));

        hive.SetHost("maid_chan_113", "maid_chan_420"); // a body this process drives
        Assert.True(hive.Decide(Say("113 stop"), locallyAddressed: false));
        Assert.True(hive.IsHiveBot("maid_chan_113"));   // its own chat lines are hive-internal
        Assert.Equal(new[] { "420 come here", "113 stop" }, addressed); // direct orders (they pre-empt a logout deposit)

        hive.SetHost("maid_chan_113", null);            // the body logged out for good
        Assert.Null(hive.HostOf("maid_chan_113"));
    }

    [Fact]
    public void Leaving_the_tree_drops_the_host()
    {
        var main = new ChatArbiter("maid_chan", "maid_chan", []);
        var tree = Tree();
        main.Follow(tree);
        Assert.Equal("maid_chan_420", main.HostOf("maid_chan_113"));
        tree.Remove("maid_chan_113");
        Assert.Null(main.HostOf("maid_chan_113"));
    }
}
