using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Events;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Self;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins the model-facing [self] block (WP06 refactor of <see cref="SelfGrowth.RenderBlock"/>, <c>OwnTraits</c> and the reflection
/// description) for main and a clone, as entries, jokes, people, preferences and milestones grow and the caps bite.
/// </summary>
public class SelfRenderGoldenTests
{
    private const string Expected = "CE71DD14F063D29B1D213E438E63EA1F17263609536062A6AED23C26745923BA";

    private static void Feed(SelfGrowth g, ManualTime t, params GameEvent[] events)
    {
        t.Advance(TimeSpan.FromMinutes(2));
        g.OnEpisode(GrowthKit.Ep(t, events));
    }

    [Fact]
    public void Self_blocks_render_the_same()
    {
        var sb = new StringBuilder();
        var t = new ManualTime();
        using var db = AgentDb.Open(null);
        var main = GrowthKit.Growth(db, t, "maid_chan");
        var clone = GrowthKit.Growth(db, t, "maid_chan_420", hive: GrowthKit.Hive(("maid_chan_420", "sleepy, dramatic")));
        void Render(string label)
        {
            foreach (var (name, g) in new[] { ("main", main), ("420", clone) })
            {
                sb.Append("== ").Append(label).Append(' ').Append(name).Append('\n').Append(g.RenderBlock()).Append('\n');
                sb.Append("-- with players\n").Append(g.RenderBlock(["kiwi", "poofimafox", "a1", "nobody"])).Append('\n');
                sb.Append("-- reflection\n").Append(g.DescribeForReflection()).Append('\n');
            }
        }

        Render("empty");
        foreach (var g in new[] { main, clone })
        {
            g.Profile.Apply(new SelfChange("add", SelfSection.Trait, "hums while mining", Reason: "seen"), SelfProfile.SourceTool);
            g.Profile.Apply(new SelfChange("add", SelfSection.Like, "sunsets over the void", Reason: "said"), SelfProfile.SourceTool);
            g.Profile.Apply(new SelfChange("add", SelfSection.Dislike, "creepers near the farm", Reason: "said"), SelfProfile.SourceTool);
        }
        Render("budding");
        foreach (var g in new[] { main, clone })
        {
            for (int i = 0; i < 3; i++)
                foreach (var s in new[] { "hums while mining away all day long", "narrates every single block placed", "very dramatic about creepers", "counts cobble out loud" })
                    g.Profile.Apply(new SelfChange("add", SelfSection.Trait, s, Reason: "seen"), SelfProfile.SourceTool);
            foreach (var s in new[] { "blames creepers for literally everything", "the cobble tax is real", "skill issue (affectionate)" })
                g.Profile.Apply(new SelfChange("add", SelfSection.Joke, s, Reason: "lol"), SelfProfile.SourceTool);
            g.Profile.Apply(new SelfChange("add", SelfSection.Opinion, "wheat beats carrots", Reason: "argued"), SelfProfile.SourceTool);
            g.Profile.Apply(new SelfChange("add", SelfSection.Relationship, "kiwi is my favourite tease target", "kiwi", Reason: "seen"), SelfProfile.SourceTool);
            g.Profile.Apply(new SelfChange("add", SelfSection.Nickname, "maidy", "poofimafox", Reason: "he calls me that"), SelfProfile.SourceTool);
            g.Profile.Apply(new SelfChange("add", SelfSection.Milestone, "first night on the island", Reason: "remember"), SelfProfile.SourceTool);
            foreach (var p in new[] { "kiwi", "poofimafox", "a1" })
                for (int i = 0; i < 4; i++)
                    Feed(g, t, GrowthKit.Chat(p, "lol skill issue maid_chan, good job tho, can you farm wheat"), GameEvent.Emote(p, "pats maid_chan"));
            Feed(g, t, GrowthKit.Chat("kiwi", "maid_chan you're so annoying"), GameEvent.Emote("kiwi", "bonks maid_chan"));
        }
        Render("grown");
        for (int i = 0; i < 4; i++)
        {
            main.OnEpisode(GrowthKit.Tool(t, "script", new { op = "run", name = "cobble_loop" }, "ok: mined 64 cobblestone"));
            clone.OnEpisode(GrowthKit.Tool(t, "smelt", new { item = "iron_ore" }, "failed: no fuel", error: true));
            t.Advance(TimeSpan.FromMinutes(3));
        }
        Feed(main, t, GrowthKit.Chat("poofimafox", "great cobble work maid_chan, you're a pro"));
        Render("preferences");

        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "self-render.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
