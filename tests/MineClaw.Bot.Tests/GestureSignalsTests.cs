using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using MineClaw.Agent;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Routines;
using MineClaw.Bot.Brainstem;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Perception;
using MineClaw.Scripting.Events;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// player.gesture end to end inside the bot: entity look/metadata updates → <see cref="GameSignals"/> (aimed at me or not) →
/// scripts/bindings (<c>on('player.gesture', {kind:'bob'})</c>, <c>script{op:bind, params:{on:'player.gesture', kind:'bob'}}</c>)
/// and the brain (<see cref="GestureFeed"/>: a nod answers a pending question; aimed gestures wake at Low; the rest is Background).
/// </summary>
public class GestureSignalsTests
{
    private static (GameSignals S, FakeGameClient C, SignalClock T, ConcurrentQueue<GameSignal> Got) Make()
    {
        var c = new FakeGameClient();
        c.Floor(64, 10);
        c.World.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, 0, 0, true);
        var t = new SignalClock();
        var s = new GameSignals(c, n => n == "robinchan", t);
        s.Attach();
        var got = new ConcurrentQueue<GameSignal>();
        s.Raised += got.Enqueue;
        return (s, c, t, got);
    }

    /// <summary>Two down-up nods as look packets every 100 ms (pitch 0 → 30 → 0), facing <paramref name="yaw"/>.</summary>
    internal static void Nod(FakeGameClient c, SignalClock t, int id, float yaw, Action? tick = null)
    {
        foreach (var p in new float[] { 0, 10, 25, 30, 20, 5, 0, 12, 26, 30, 18, 4, 0 })
        {
            t.Advance(0.1);
            c.World.Entities.Look(id, yaw, p);
            tick?.Invoke();
        }
    }

    [Fact]
    public void Look_updates_raise_a_nod_aimed_at_me_with_its_answer()
    {
        var (s, c, t, got) = Make();
        c.World.Entities.SpawnPlayer(7, "PoofimaFox", new Vec3d(5.5, 65, 0.5), 90, 0); // facing −X: at me
        Nod(c, t, 7, 90);
        var g = Assert.Single(got, x => x.Name == "player.gesture");
        Assert.Equal("PoofimaFox", g.Player);
        Assert.Equal("nod", g.Data["kind"]!.ToString());
        Assert.Equal("me", g.Data["at"]!.ToString());
        Assert.Equal("yes", g.Data["answer"]!.ToString());
        Assert.True(EventFilter.Parse(JsonNode.Parse("{\"kind\":\"nod\",\"at\":\"me\",\"player\":\"PoofimaFox\"}")).Accepts(g.Data));
        Assert.False(EventFilter.Parse(JsonNode.Parse("{\"kind\":\"shake\"}")).Accepts(g.Data));

        // Facing away (+X): the same nod is at nobody.
        var (s2, c2, t2, got2) = Make();
        c2.World.Entities.SpawnPlayer(7, "PoofimaFox", new Vec3d(5.5, 65, 0.5), 270, 0);
        Nod(c2, t2, 7, 270);
        Assert.Null(Assert.Single(got2, x => x.Name == "player.gesture").Data["at"]);
        // Other bots' body language is never ours to read.
        c2.World.Entities.SpawnPlayer(8, "robinchan", new Vec3d(3.5, 65, 0.5), 90, 0);
        t2.Advance(5);
        Nod(c2, t2, 8, 90);
        Assert.DoesNotContain(got2, x => x.Player == "robinchan");
        s.Dispose(); s2.Dispose();
    }

    [Fact]
    public void Metadata_sneak_toggles_are_a_crouch_bob_and_a_stare_needs_the_poll()
    {
        var (s, c, t, got) = Make();
        c.World.Entities.SpawnPlayer(7, "PoofimaFox", new Vec3d(4.5, 65, 0.5), 90, 0);
        foreach (var on in new[] { true, false, true, false })
        {
            t.Advance(0.15);
            c.World.Entities.SetMetadata(7, new Dictionary<int, object> { [0] = (sbyte)(on ? 2 : 0) });
        }
        var bob = Assert.Single(got, x => x.Name == "player.gesture");
        Assert.Equal(("bob", "me"), (bob.Data["kind"]!.ToString(), bob.Data["at"]!.ToString()));
        // The player now just looks at me, sending nothing: the 4 Hz poll sees the stare.
        for (int i = 0; i < 6; i++) { t.Advance(0.25); s.Tick(); }
        Assert.Contains(got, x => x.Name == "player.gesture" && x.Data["kind"]!.ToString() == "stare" && x.Data["at"]!.ToString() == "me");
    }

    [Fact]
    public void Swings_into_air_wave_but_swings_at_a_block_punch()
    {
        var (s, c, t, got) = Make();
        c.World.Entities.SpawnPlayer(7, "PoofimaFox", new Vec3d(4.5, 65, 0.5), 90, 0); // eye level: nothing solid in reach
        for (int i = 0; i < 3; i++) { t.Advance(0.3); s.OnSwing(7); }
        t.Advance(0.4); s.Tick();
        Assert.Equal("wave", Assert.Single(got, x => x.Name == "player.gesture").Data["kind"]!.ToString());
        Assert.DoesNotContain(got, x => x.Name == "player.punchBlock");

        var (s2, c2, t2, got2) = Make();
        c2.World.Entities.SpawnPlayer(7, "PoofimaFox", new Vec3d(4.5, 65, 0.5), 90, 80); // looking at the floor
        for (int i = 0; i < 3; i++) { t2.Advance(0.3); s2.OnSwing(7); }
        t2.Advance(0.4); s2.Tick();
        Assert.DoesNotContain(got2, x => x.Name == "player.gesture");
        Assert.Contains(got2, x => x.Name == "player.punchBlock");
    }

    // ------------------------------------------------------------------ the brain's view

    private static (GestureFeed F, ChatRouter Chat, EventQueue Q, SignalClock T, List<string> Acks) Feed(bool main = true, bool answersOnly = false)
    {
        var t = new SignalClock();
        var q = new EventQueue(time: t);
        var chat = new ChatRouter(new ChatOptions { Username = "maid_chan", LineDelayMs = 0 }, q, (_, _) => Task.CompletedTask, t);
        var acks = new List<string>();
        var f = new GestureFeed(chat, q, main ? null : "maid_chan_67", t, p => { acks.Add(p); return "ack"; }, main, answersOnly);
        return (f, chat, q, t, acks);
    }

    private static GameSignal G(string kind, string? at = "me", string player = "PoofimaFox")
    {
        var d = new JsonObject { ["kind"] = kind, ["strength"] = 0.8, ["pos"] = new JsonObject { ["x"] = 4, ["y"] = 65, ["z"] = 0 } };
        if (at != null) d["at"] = at;
        if (kind == "nod") d["answer"] = "yes";
        if (kind == "shake") d["answer"] = "no";
        if (kind == "point") { d["target"] = new JsonObject { ["x"] = 9, ["y"] = 64, ["z"] = 2 }; d["block"] = "chest"; }
        d["player"] = player;
        return new GameSignal("player.gesture", d, player, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_nod_answers_an_ask_player_waiting_on_them()
    {
        var (f, chat, q, _, acks) = Feed();
        var wait = chat.WaitForReplyAsync("PoofimaFox", TimeSpan.FromSeconds(5), default);
        f.OnSignal(G("nod"));
        Assert.Equal("PoofimaFox: yes (nod)", await wait);
        Assert.Equal(["PoofimaFox"], acks);
        Assert.Empty(q.Snapshot()); // the ask's own path tells the brain
        var no = chat.WaitForReplyAsync("PoofimaFox", TimeSpan.FromSeconds(5), default);
        f.OnSignal(G("shake", at: null));
        Assert.Equal("PoofimaFox: no (head shake)", await no);
        Assert.True(Agent.Brainstem.GestureClassifier.IsYesNo(Agent.Human.ReplyIntent.Strip("PoofimaFox: yes (nod)")));
    }

    [Fact]
    public async Task A_nod_right_after_our_question_wakes_the_brain_as_the_answer_once()
    {
        var (f, chat, q, t, acks) = Feed();
        await chat.SayAsync("foxy, should I build the tower now?", audience: ["PoofimaFox"]);
        Assert.True(chat.AwaitsAnswerFrom("PoofimaFox"));
        t.Advance(3);
        f.OnSignal(G("nod"));
        var e = Assert.Single(q.Snapshot(), x => x.Kind == EventKind.Gesture);
        Assert.Equal(EventPriority.Normal, e.Priority);
        Assert.True(e.Wakes);
        Assert.Equal("[gesture] PoofimaFox nods yes at you (answering your question)", e.Format());
        Assert.Equal(["PoofimaFox"], acks);
        Assert.True(BotAgent.IsPlayerFacing(e));
        // It closed the question: a second nod (after the detector's cooldown) is plain body language, and no second wake.
        t.Advance(5);
        f.OnSignal(G("nod"));
        Assert.DoesNotContain(q.Snapshot(), x => x.Kind == EventKind.Gesture && x.Priority > EventPriority.Background && x != e);
        // Too late: a nod 40 s after a question answers nothing.
        var (f2, chat2, q2, t2, _) = Feed();
        await chat2.SayAsync("foxy, want the cobble in the chest?", audience: ["PoofimaFox"]);
        t2.Advance(40);
        f2.OnSignal(G("nod", at: null));
        Assert.All(q2.Snapshot(), x => Assert.Equal(EventPriority.Background, x.Priority));
    }

    [Fact]
    public void Aimed_gestures_wake_low_and_rate_limited_others_fold_into_the_next_turn()
    {
        var (f, _, q, t, _) = Feed();
        f.OnSignal(G("wave"));
        f.OnSignal(G("bob", at: null));
        t.Advance(5);
        f.OnSignal(G("wave")); // aimed again within 30 s: background only
        f.OnSignal(G("stare"));
        var list = q.Snapshot().Where(x => x.Kind == EventKind.Gesture).ToList();
        Assert.Equal(EventPriority.Low, list.Single(x => x.Text == "waves at you" && x.Wakes).Priority);
        Assert.Contains(list, x => x.Text == "crouch-bobs" && x.Priority == EventPriority.Background);
        Assert.Contains(list, x => x.Text == "stares at you" && !x.Wakes); // stares never wake
        Assert.Equal("points at the chest (9,64,2)", GestureFeed.Describe("point", null, G("point").Data));
    }

    [Fact]
    public async Task Bodies_split_the_gestures_and_one_at_another_bot_is_never_our_answer()
    {
        var (main, chat, q, _, _) = Feed();
        await chat.SayAsync("foxy, should I go?", audience: ["PoofimaFox"]);
        main.OnSignal(G("nod", at: "robinchan"));        // nodding at another bot: its answer
        Assert.True(chat.AwaitsAnswerFrom("PoofimaFox"));
        Assert.Equal("nods yes at robinchan", Assert.Single(q.Snapshot(), x => x.Kind == EventKind.Gesture).Text);

        var (clone, _, cq, _, _) = Feed(main: false);
        clone.OnSignal(G("wave", at: null));             // unaimed: main's to report
        Assert.Empty(cq.Snapshot());
        clone.OnSignal(G("wave"));                       // aimed at this body
        var e = Assert.Single(cq.Snapshot());
        Assert.Equal("[67 gesture] PoofimaFox waves at you", e.Format());

        var (proc, _, pq, _, _) = Feed(answersOnly: true);
        proc.OnSignal(G("wave"));
        Assert.Empty(pq.Snapshot());
    }

    // ------------------------------------------------------------------ the whole host: bindings and the brain

    [Fact]
    public async Task A_taught_binding_runs_on_a_crouch_bob_and_a_nod_answers_the_bots_question()
    {
        var c = new FakeGameClient();
        var o = TestBot.Options();
        o.Self.TrustedPlayers = ["PoofimaFox"];
        o.Brainstem.Social.Enabled = false;
        using var h = BotHost.Create(c, o, null, new NoLlm(), persist: false);
        c.Floor(64, 10);
        c.World.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, 0, 0, true);
        c.World.Player.SetHealth(20, 20, 5);
        c.World.Entities.SpawnPlayer(7, "PoofimaFox", new Vec3d(-5.5, 65, 0.5), 270, 0); // facing +X: at me
        c.World.Entities.SetPlayerListEntry("PoofimaFox", true);

        // "when I crouch twice, come here": taught as a binding, the meaning lives in the script, not in code.
        var saved = await h.Run("script", new { op = "save", name = "come_on_bob", code = "// come when Fox crouch-bobs\ncharacter.look(args.player)\nlog('bob from ' + args.player + ' at ' + args.at)", force = "test" });
        Assert.True(saved.Ok, saved.Text);
        TurnContext.Current = new TurnInfo("PoofimaFox", ["PoofimaFox"], false);
        var bound = await h.Run("script", new { op = "bind", name = "come_on_bob", @params = new { on = "player.gesture", kind = "bob", player = "PoofimaFox" } });
        TurnContext.Current = null;
        Assert.True(bound.Ok, bound.Text);
        var lib = ScriptTools.Of(h.Runtime)!.Library;
        var b = lib.Get("come_on_bob")!.Script!.Binding!;
        Assert.Equal(("player.gesture", "binding"), (b.On, b.Kind));
        Assert.Contains("\"kind\":\"bob\"", b.Filter);

        foreach (var on in new[] { true, false, true, false })
        {
            await Task.Delay(120);
            c.World.Entities.SetMetadata(7, new Dictionary<int, object> { [0] = (sbyte)(on ? 2 : 0) });
        }
        await Until(() => lib.Get("come_on_bob")!.Script!.Binding!.Fires >= 1);

        // The bot asks; Fox nods instead of typing.
        await h.Runtime.Chat.SayAsync("foxy, should I build the tower now?", audience: ["PoofimaFox"]);

        foreach (var p in new float[] { 0, 10, 25, 30, 20, 5, 0, 12, 26, 30, 18, 4, 0 })
        {
            await Task.Delay(90);
            c.World.Entities.Look(7, 270, p);
        }
        await Until(() => h.Runtime.Events.Snapshot().Any(e => e.Kind == EventKind.Gesture && e.Priority == EventPriority.Normal));
        var answer = h.Runtime.Events.Snapshot().First(e => e.Kind == EventKind.Gesture && e.Priority == EventPriority.Normal);
        Assert.Equal("[gesture] PoofimaFox nods yes at you (answering your question)", answer.Format());
    }

    private static async Task Until(Func<bool> cond, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond()) { if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met"); await Task.Delay(20); }
    }
}
