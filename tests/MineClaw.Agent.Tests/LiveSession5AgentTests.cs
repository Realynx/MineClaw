using System.Text.Json;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>Live session 5 (2026-10-27 evening): capability talk in chat, food/hand-off verbs in <c>do</c>, chest freshness.</summary>
public class LiveSession5AgentTests
{
    [Fact]
    public void Capability_talk_is_rewritten_to_its_real_clause()
    {
        // 21:07, clone 67 in public chat.
        var t = MetaSpeech.Strip("Eating isn't something I can trigger with my current tools, so I'll stop fiddling and just thank Fox.", out var gone);
        Assert.Equal("I'll stop fiddling and just thank Fox.", t);
        Assert.Single(gone);
    }

    [Theory]
    [InlineData("I can't trigger that with my tools.")]
    [InlineData("That's not something I can call.")]
    [InlineData("My toolset doesn't cover eating.")]
    [InlineData("As an AI I can't eat.")]
    [InlineData("My system prompt says no.")]
    [InlineData("There's no function for that.")]
    public void Pure_capability_talk_is_dropped(string line)
    {
        Assert.Equal("", MetaSpeech.Strip(line, out var gone));
        Assert.Single(gone);
    }

    [Theory]
    [InlineData("Fox is asking about the mob chamber, so I'll check memory for it.")] // 21:5x, clone 420
    [InlineData("I'll check my memory for that.")]
    [InlineData("Let me check my map.")]
    [InlineData("Checking my notes.")]
    [InlineData("Kiwi is wondering where the iron went, so let me look through my ledger.")]
    public void Narration_of_its_own_reasoning_is_dropped(string line)
    {
        Assert.Equal("", MetaSpeech.Strip(line, out var gone));
        Assert.NotEmpty(gone);
    }

    [Fact]
    public void Third_person_lead_in_goes_and_the_real_reply_stays() =>
        Assert.Equal("I'll chop the big oak.", MetaSpeech.Strip("Fox is asking for wood, so I'll chop the big oak.", out _));

    [Theory]
    [InlineData("Let me check the chest by the gen.")]
    [InlineData("I'll look at the map you drew on the sign!")]
    public void Real_speech_near_narration_stays(string line) => Assert.Equal(line, MetaSpeech.Strip(line, out _));

    [Theory]
    [InlineData("I can't mine obsidian with my current tools, I need a diamond pickaxe.")]
    [InlineData("My tools broke, can you make me a new pickaxe?")]
    [InlineData("I need a tool to break this faster.")]
    [InlineData("Better tools would help a lot!")]
    public void Minecraft_tool_talk_stays(string line) => Assert.Equal(line, MetaSpeech.Strip(line, out _));

    [Fact]
    public void Do_offers_food_and_hand_off_verbs()
    {
        foreach (var a in new[] { "eat", "give_items", "drop_items", "equip_armor" }) Assert.Contains(a, DoTool.Actions);
        var eat = DoTool.Expand(JsonSerializer.SerializeToElement(new { action = "eat" }), out var err);
        Assert.Null(err);
        Assert.Equal("eat", Assert.Single(eat).Tool);
        var give = DoTool.Expand(JsonSerializer.SerializeToElement(new { action = "give_items", args = new { player = "PoofimaFox", item = "torch", count = 5 } }), out err);
        Assert.Null(err);
        var (tool, input) = Assert.Single(give);
        Assert.Equal("skill", tool);
        Assert.Equal("give_items", input.GetProperty("name").GetString());
        Assert.Equal(5, input.GetProperty("args").GetProperty("count").GetInt32());
        var drop = DoTool.Expand(JsonSerializer.SerializeToElement(new { drop_items = "bread" }), out err);
        Assert.Null(err);
        Assert.Equal("bread", Assert.Single(drop).Input.GetProperty("args").GetProperty("item").GetString());
    }

    [Fact]
    public void Where_is_gives_the_age_of_the_last_open_not_the_chest_sighting()
    {
        // 21:06:40: "bread x5: chest@14,64,-5..15,64,-5 (seen just now)" was main's deposit at 21:05:51 (and Fox had emptied it).
        var time = new ManualTime();
        using var db = AgentDb.Open(null);
        var idx = new RagIndex(db, new RagOptions(), new FakeEmbedder(), time: time, backgroundEmbedding: false);
        var world = new ObservationStore(idx, time);
        world.Upsert(new Observation("chest", "14,64,-5", "double chest")
        {
            Bounds = new Box(14, 64, -5, 15, 64, -5),
            Contents = [new ItemCount(297, "bread", 5)],
            ContentsBy = "maid_chan",
        });
        time.Advance(TimeSpan.FromSeconds(49));
        // The chest block is seen again (chunk rescan, no open): its contents stay as old as the open.
        world.Upsert(new Observation("chest", "14,64,-5", "double chest") { Bounds = new Box(14, 64, -5, 15, 64, -5) });
        var row = world.Get("chest", "14,64,-5")!;
        Assert.Equal("maid_chan", row.ContentsBy);
        Assert.Equal(time.GetUtcNow().AddSeconds(-49), row.ContentsAt);

        var r = WorldTools.WhereIs(world, "bread", null, new BlockPos(11, 64, -6), time.GetUtcNow());
        Assert.Contains("bread x5: chest@14,64,-5..15,64,-5", r.Text);
        Assert.Contains("(opened by maid_chan 49s ago, may have changed)", r.Text);
        Assert.DoesNotContain("just now", r.Text);
        Assert.Contains("opened by maid_chan 9s ago)", WorldTools.WhereIs(world, "bread", null, null, time.GetUtcNow().AddSeconds(-40)).Text);
    }
}
