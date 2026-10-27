using MineClaw.Agent.Brainstem;
using MineClaw.Bot.Brainstem;
using MineClaw.Client;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Body-language gestures: the static classifier on real bot lines (live log 2026-10-27, maid_chan and clones), and the
/// brainstem component's triggers, safety (void, danger, airborne, jobs), degrade-to-head rules, debounce and per-clone flag.
/// </summary>
public class GestureTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 27, 21, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    // ------------------------------------------------------------------ classifier on the live log

    [Theory]
    // agree → nod (the opening decides, even when a "sorry" follows)
    [InlineData("Got it robin uwu, 67's on the way: it'll empty everything into the chest first, then go fight mobs.", "nod")]
    [InlineData("Got it foxy, just those two blocks: the one by the lava, and the one at the far corner. Sorry for", "nod")]
    [InlineData("On it foxy, chopping the next one uwu", "nod")]
    [InlineData("I'm on it, foxy. Going to work the cobble now :3", "nod")]
    [InlineData("Okie foxy, I'll just keep mining and stop chasing the single blocks uwu", "nod")]
    [InlineData("Ok foxy, 5 rows going out from that edge. I'll mine more cobble at the gen when I run out uwu", "nod")]
    [InlineData("Okay master, 69 is coming to help. It takes the east trees and I take the west, same way you showed", "nod")]
    [InlineData("Sure thing foxy~ I'll bring you 40 cobble along with the 30 slabs, straight from the stash uwu", "nod")]
    [InlineData("Yep, I see it, the edge to your west by the void. What do you want me to do with it, extend it or", "nod")]
    [InlineData("On my way master fox uwu, also i'm starving lol, got any food?", "nod")]
    // refuse / sorry → shake
    [InlineData("Sorry robin, 67 bailed again.", "shake")]
    [InlineData("Sorry master, I got mixed up, I thought you meant clone 69. Heading out to chop the trees now.", "shake")]
    [InlineData("I can't teleport anyone unless you type the exact command, foxy.", "shake")]
    [InlineData("I can't make a helper called 1337, so I'm sending 67 over to you instead, robin.", "shake")]
    [InlineData("No spiders anywhere, robin.", "shake")]
    [InlineData("PoofimaFox, sorry foxy, maid brain lagging uwu. Can you punch the first and last block of the edge I", "shake")]
    [InlineData("Aww you finished it yourself, sorry foxy, the edge kept tripping me up. Want me to do anything else?", "shake")]
    [InlineData("Oops, I picked up the bread and auto-ate it before I could pass it to 67, sorry robin.", "shake")]
    // hello → wave + look-at
    [InlineData("Hi robin, hope you're okay after that fall. I'm just waiting by the gen, not touching anything till", "greet")]
    [InlineData("Welcome back foxy~ your maid kept the trees chopped while you were gone uwu", "greet")]
    [InlineData("ROBIN hiii uwu, maid-chan reporting for tree duty, also lowkey starving rn, you got any food?", "greet")]
    // thanks → bow
    [InlineData("Thank you so much uwu, I'll make bread the second you hand it over", "bow")]
    [InlineData("I made it out, thanks foxy! I'm right by you with a stack of cobble. What should I build and where?", "bow")]
    [InlineData("Thanks for saving 420, foxy.", "bow")]
    [InlineData("Got the new pickaxe, ty 69, and I'm working side B now uwu", "bow")]
    [InlineData("Baked 7 bread and my tummy's totally full now, thank you master, you're so rizz uwu.", "bow")]
    // the bot's own /me (live lines are "* maid_chan curtsies ..." as relayed; the bot sends "/me curtsies ...")
    [InlineData("/me curtsies with the axe uwu", "bow")]
    [InlineData("/me bows deeply and holds the bread close, starving maid chan says thank you Fox uwu", "bow")]
    [InlineData("Hai, foxy sama~ /me curtsies. Your maid has chopped the trees", "bow")]
    [InlineData("*waves* hi foxy", "wave")]
    [InlineData("/me nods", "nod")]
    [InlineData("/me happily hops around", "hop")]
    [InlineData("/me shakes her head", "shake")]
    // excitement (not in today's log; the persona's words)
    [InlineData("yay we did it uwu!", "excite")]
    [InlineData("the farm is done, let's gooo", "excite")]
    public void Real_bot_lines_map_to_the_expected_gesture(string line, string gesture)
    {
        var hit = new GestureClassifier().ClassifyOutgoing(line);
        Assert.True(hit.HasValue, $"no gesture for: {line}");
        Assert.Equal(gesture, hit!.Value.Gesture);
    }

    [Theory]
    [InlineData("Chopped that tree, 5 logs are in the chest and I replanted the sapling, uwu")]
    [InlineData("Master, all the grown trees are chopped and replanted, and 23 logs are in the chest uwu.")]
    [InlineData("Foxy, 420 spawned in over the void and it's stuck hanging out there, I can't get it back myself.")]
    [InlineData("As far as I know you can't make dirt in this version, and our dirt doesn't regrow, so it's a finite")]
    [InlineData("Want me to harvest and replant it, foxy? uwu")]
    [InlineData("It's still on its way, give it a sec uwu")]
    [InlineData("Here's your bucket, master uwu. Clone 69 is on tree duty now.")]
    [InlineData("Tummy's at half and there's only 2 wheat in the chest, so no bread yet :(")]
    [InlineData("Phew, glad you're okay, robin.")]
    [InlineData("Only 5 it is, foxy, coming right up uwu")]
    [InlineData("Trying again uwu")]
    [InlineData("lmaooo fair, no cap we're all broke, i'll hunt for apples while chopping and we can farm some wheat")]
    [InlineData("/tell PoofimaFox psst")]
    public void Plain_reports_and_questions_play_nothing(string line) =>
        Assert.Null(new GestureClassifier().ClassifyOutgoing(line));

    [Fact]
    public void Config_rows_come_first_and_none_silences_a_phrase()
    {
        var c = new GestureClassifier([new GestureRule("start", @"^got it\b", "none"), new GestureRule("any", @"\bheck yes\b", "hop")]);
        Assert.Null(c.ClassifyOutgoing("Got it foxy, chopping now"));
        Assert.Equal("hop", c.ClassifyOutgoing("heck yes, the gen works")!.Value.Gesture);
        Assert.Equal("shake", c.ClassifyOutgoing("Sorry robin")!.Value.Gesture); // defaults still apply
        var only = new GestureClassifier([new GestureRule("start", "^meow", "bob"), new GestureRule("start", "([", "nod")], replaceDefaults: true);
        Assert.Null(only.ClassifyOutgoing("Sorry robin")); // defaults off; the bad regex row is skipped
        Assert.Equal("bob", only.ClassifyOutgoing("meow")!.Value.Gesture);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("yeah go ahead", true)]
    [InlineData("nope", true)]
    [InlineData("no, not now", true)]
    [InlineData("now go chop the trees by the house please ok", false)]
    [InlineData("yes and then build me a big cobble house on the west side", false)]
    public void Yes_no_answers(string text, bool expected) => Assert.Equal(expected, GestureClassifier.IsYesNo(text));

    // ------------------------------------------------------------------ the component

    private static (Gestures G, FakeGameClient C, BotHost H, Clock T) Setup(Action<GestureOptions>? configure = null)
    {
        var c = new FakeGameClient();
        var o = TestBot.Options();
        configure?.Invoke(o.Gestures);
        var h = BotHost.Create(c, o, null, new NoLlm(), persist: false);
        c.Floor(64, 10);
        var t = new Clock();
        var g = new Gestures(h.Runtime, c, o.Gestures, null, t);
        g.Attach();
        return (g, c, h, t);
    }

    private static void Stand(FakeGameClient c, double x, double z) =>
        c.World.Player.ServerTeleport(new Vec3d(x, 65, z), 66.62, 0, 0, true);

    [Fact]
    public void Own_agreement_nods_and_the_head_comes_back_by_design()
    {
        var (g, c, _, _) = Setup();
        Assert.Equal("nod", g.OnSaid("On it foxy, chopping the next one uwu"));
        var plan = Assert.Single(c.GesturePlans);
        Assert.True(plan.Frames.Max(f => f.Pitch) >= 20); // visible: ≥ 11.25° per tracker update
        Assert.Equal(0, plan.Frames[^1].Pitch); // ends on the real look
        Assert.False(plan.UsesBody);
    }

    [Fact]
    public void Greeting_waves_and_looks_at_the_named_player()
    {
        var (g, c, _, _) = Setup();
        c.World.Entities.SpawnPlayer(7, "PoofimaFox", new Vec3d(-3.5, 65, 0.5), 0, 0);
        Assert.Equal("look+wave", g.OnSaid("Welcome back PoofimaFox~ your maid kept the trees chopped uwu"));
        var plan = c.GesturePlans.Single();
        Assert.All(plan.Frames, f => Assert.True(f.Absolute));
        Assert.Equal(90, plan.Frames[0].Yaw, 1); // -X is yaw 90 in beta
        Assert.Equal(3, plan.Frames.Count(f => f.Swing));
    }

    [Fact]
    public void At_most_one_gesture_per_interval()
    {
        var (g, c, _, t) = Setup();
        Assert.NotNull(g.OnSaid("Got it foxy"));
        t.Advance(1.5);
        Assert.Null(g.OnSaid("Sorry robin, 67 bailed again."));
        Assert.Equal("debounced", g.LastSuppressed);
        t.Advance(1.6);
        Assert.Equal("shake", g.OnSaid("Sorry robin, 67 bailed again."));
        Assert.Equal(2, c.GesturePlans.Count);
    }

    [Fact]
    public void Nothing_within_two_blocks_of_the_void()
    {
        var (g, c, _, _) = Setup();
        Stand(c, 9.5, 0.5); // the floor ends at x = 10: the void is 2 blocks east
        Assert.Null(g.OnSaid("Got it foxy"));
        Assert.Equal("void", g.LastSuppressed);
        Stand(c, 7.5, 0.5);
        Assert.Equal("nod", g.OnSaid("Got it foxy"));
    }

    [Fact]
    public void Nothing_in_danger_a_fight_or_off_the_ground()
    {
        var (g, c, _, t) = Setup();
        c.World.Entities.SpawnMob(50, MobTypes.Zombie, new Vec3d(4.5, 65, 0.5), 0, 0);
        Assert.Null(g.OnSaid("Got it foxy"));
        Assert.Equal("danger", g.LastSuppressed);
        c.World.Entities.Destroy(50);

        c.Safety = true;
        Assert.Null(g.OnSaid("Got it foxy"));
        Assert.Equal("safety", g.LastSuppressed);
        c.Safety = false;

        c.RaiseDanger(new MineClaw.Client.DangerEvent(DangerKind.Damage, "hurt"));
        Assert.True(c.GestureCancels >= 1); // a running gesture stops at once
        Assert.Null(g.OnSaid("Got it foxy"));
        Assert.Equal("danger", g.LastSuppressed);
        t.Advance(6);
        c.RaiseDanger(new MineClaw.Client.DangerEvent(DangerKind.Hunger, "starving")); // hunger isn't danger to a nod

        c.World.Player.ServerTeleport(new Vec3d(0.5, 66.2, 0.5), 67.8, 0, 0, false); // jumping / pillaring / falling
        Assert.Null(g.OnSaid("Got it foxy"));
        Assert.Equal("airborne", g.LastSuppressed);
        Stand(c, 0.5, 0.5);
        Assert.Equal("nod", g.OnSaid("Got it foxy"));
    }

    [Fact]
    public void A_job_or_script_blocks_body_gestures_but_not_head_ones()
    {
        var (g, c, h, t) = Setup();
        c.CurrentAction = "harvest_generator";
        Assert.Equal("bow(head)", g.Play("bow")); // the dip without the sneak
        t.Advance(4);
        Assert.Equal("wave", g.Play("excite")); // no bob/hop: an arm wave instead
        t.Advance(4);
        Assert.Equal("nod", g.Play("nod"));
        c.CurrentAction = null;

        h.Runtime.Agent.BodyHolder = () => "script cobble_loop";
        t.Advance(4);
        Assert.Equal("bow(head)", g.Play("bow"));
        h.Runtime.Agent.BodyHolder = null;
        t.Advance(4);
        Assert.Equal("bow", g.Play("bow"));
        Assert.True(c.GesturePlans.Last().UsesBody);
        Assert.All(c.GesturePlans.Take(4), p => Assert.False(p.UsesBody));
    }

    [Fact]
    public void A_hop_needs_ground_under_all_four_corners()
    {
        var (g, c, _, t) = Setup();
        Assert.True(g.HopSafe());
        Assert.Equal("hop", g.Play("hop"));
        // Standing on the corner of a block over a hole (the void margin is off so only the hop rule decides).
        var (g2, c2, _, _) = Setup(o => o.VoidMargin = 0);
        c2.World.SetBlock(1, 64, 0, 0, 0);
        c2.World.SetBlock(1, 63, 0, 0, 0);
        c2.World.Player.ServerTeleport(new Vec3d(0.85, 65, 0.5), 66.62, 0, 0, true);
        Assert.False(g2.HopSafe());
        Assert.Equal("bob", g2.Play("hop")); // the bob instead
    }

    [Fact]
    public void Addressed_chat_looks_at_the_speaker_and_a_yes_to_my_question_gets_a_small_nod()
    {
        var (g, c, _, t) = Setup();
        c.World.Entities.SpawnPlayer(7, "PoofimaFox", new Vec3d(0.5, 65, 4.5), 0, 0);
        Assert.Equal("look PoofimaFox", g.OnHeard("PoofimaFox", "claudechan how's it going?"));
        Assert.Null(g.OnHeard("PoofimaFox", "and the gen?")); // per-player interval
        t.Advance(4);
        Assert.Null(g.OnSaid("Want me to harvest and replant it, foxy? uwu")); // a question: no gesture, but remembered
        t.Advance(2);
        Assert.Equal("look+nod", g.OnHeard("PoofimaFox", "claudechan yes"));
        var ack = c.GesturePlans.Last();
        Assert.Contains(ack.Frames, f => f.Pitch > 10);
        t.Advance(4);
        Assert.Equal("look PoofimaFox", g.OnHeard("PoofimaFox", "yes")); // one acknowledgement per question
    }

    [Fact]
    public void Disabled_per_clone_or_by_login()
    {
        var (g, c, h, _) = Setup(o => o.Clones = false);
        h.Runtime.Options.Hive.MainUsername = "maid_chan";
        Assert.True(g.Enabled);
        c.Username = "maid_chan_67";
        Assert.False(g.Enabled);
        Assert.Null(g.OnSaid("Got it foxy"));
        var (g2, c2, _, _) = Setup(o => o.DisabledFor = ["maid_chan"]);
        Assert.False(g2.Enabled);
        Assert.Empty(c2.GesturePlans);
    }

    [Fact]
    public async Task The_bots_own_chat_plays_through_the_host_wiring()
    {
        var c = new FakeGameClient();
        var h = BotHost.Create(c, TestBot.Options(), null, new NoLlm(), persist: false);
        c.Floor(64, 10);
        await h.Runtime.Chat.SayAsync("/me curtsies with the axe uwu");
        Assert.Contains(c.GesturePlans, p => p.Name == "bow");
        Assert.Contains("bow", h.BodyGestures!.Played);
    }
}
