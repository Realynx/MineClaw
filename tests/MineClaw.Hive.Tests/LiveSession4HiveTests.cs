using MineClaw.Agent;
using MineClaw.Agent.Telemetry;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Supervisor;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 2026-10-27 15:44-15:46 (main log): kiwi "spawn in a subagent, have it dump all it's stuff in a chest and go fight mobs".
/// The unnamed spawn took the first idle slot, 67 (hp 7, food 0, stone tools only), while 69 (hp 20 food 20 at its 14:36 login)
/// was free. 67 refused; "she can try" re-sent it; then a script spawn re-sent it again; three refusals for the same reason.
/// Task texts are the real ones.
/// </summary>
public class LiveSession4HiveTests
{
    private const string Fight1 = "For kiwi: deposit ALL your inventory items into the nearest chest (e.g. chest at 14,64,-5), keep only a sword or axe if you have one. " +
        "Then fight hostile mobs on the main island at night (zombies, spiders, skeletons), staying well away from void edges, and pick up drops " +
        "(rotten flesh is food: eat it if starving). Retreat inside the main house if hp <= 6.";
    private const string Fight2 = "For kiwi (she approved the risk): deposit all inventory into chest at 14,64,-5 except a stone axe/sword. Then fight hostile mobs on " +
        "the main island at night, staying 3+ blocks from void edges, pick up drops; eat rotten flesh right away. Retreat into the main house if hp <= 4.";
    private const string Refusal = "hp 7/20, food 0, no food anywhere in storage; too risky to fight at night. Only have stone axe+pickaxe.";

    private static HiveConfig TwoClones()
    {
        var cfg = TestHive.Config();
        cfg.CloneUsernames = ["maid_chan_67", "maid_chan_69"];
        return cfg;
    }

    private static readonly TurnInfo Kiwi = new("kiwi", ["kiwi"], false);

    private static HiveMessage Frame(string type, object? payload) => new() { T = type, D = HiveJson.ToElement(payload) };

    private static BotSnapshot Body(int hp, int food) => new() { Name = "x", Hp = hp, Food = food, Pos = new BotPos(9.5, 65, 1.6) };

    /// <summary>A clone that is logged in and free (no pending task), so a spawn reassigns it without a process launch.</summary>
    private static CloneSlot Free(TestHive h, string name, BotSnapshot? body = null)
    {
        var s = h.Supervisor.Slot(name)!;
        s.State = CloneState.Running;
        s.Connected = true;
        s.Task = null;
        s.Snapshot = body;
        return s;
    }

    private static CloneSlot Busy(TestHive h, string name)
    {
        var s = h.Supervisor.Slot(name)!;
        s.Task = new TaskMsg("t-" + name, "Work the tree farm near 9,65,0 / 9,65,3");
        s.State = CloneState.Running;
        s.Connected = true;
        return s;
    }

    [Fact]
    public void Fight_intent_is_read_from_task_and_script_text()
    {
        Assert.Equal(TaskIntent.Fight, CloneFitness.IntentOf(Fight1));
        Assert.Equal(TaskIntent.Fight, CloneFitness.IntentOf("script hive_e4b353 -- deposit, then attack zombies until dawn"));
        Assert.Equal(TaskIntent.Mine, CloneFitness.IntentOf("Mine the cobble generator on main island at 4,64,6"));
        Assert.Equal(TaskIntent.Chop, CloneFitness.IntentOf("Work the tree farm: cut all logs, replant"));
    }

    [Fact]
    public async Task Unnamed_fight_goes_to_healthy_69_not_starving_67()
    {
        await using var h = new TestHive(TwoClones());
        // 67 just came back from the 15:42 outing: hp 7, food 0 (live snapshot). 69 last logged in at 14:36 with hp 20, food 20:
        // main restarted at 15:27, so only the store remembers it.
        h.Supervisor.Slot("maid_chan_67")!.Snapshot = Body(7, 0);
        h.Store.SaveVitals("maid_chan_69", 20, 20, 15.3, 64, -1.5);
        var (pick, why) = h.Supervisor.PickFor(CloneFitness.IntentOf(Fight1));
        Assert.Null(why);
        Assert.Equal("maid_chan_69", pick!.Name);
    }

    [Fact]
    public async Task No_fit_says_why_and_suggests_food_instead_of_sending_67()
    {
        await using var h = new TestHive(TwoClones());
        h.Supervisor.Slot("maid_chan_67")!.Snapshot = Body(7, 0);
        Busy(h, "maid_chan_69");
        TurnContext.Current = Kiwi;
        try
        {
            var r = h.Supervisor.Spawn(Fight1);
            Assert.StartsWith("no: no clone fits fighting: 67 hp 7, food 0 (starving), no food: needs food", r);
            Assert.Contains("fix: a clone needs food first", r);
            Assert.Equal(CloneState.Idle, h.Supervisor.Slot("maid_chan_67")!.State); // nothing launched
            // Any job is blocked while starving at hp ≤ 10 with no food (mining included).
            Assert.Contains("starving", h.Supervisor.Spawn("Mine the cobble generator on main island at 4,64,6"));
        }
        finally { TurnContext.Current = null; }
    }

    [Fact]
    public async Task A_refused_fight_is_not_resent_to_the_same_clone_until_something_changes()
    {
        await using var h = new TestHive(TwoClones());
        Busy(h, "maid_chan_69");
        h.Supervisor.ScriptPrep = (script, code) => ("hive_e4b353", null, code);
        // Fit enough on paper (hp 12, food 8) so only the refusal memory can stop the loop.
        var s67 = Free(h, "maid_chan_67", Body(12, 8));
        TurnContext.Current = Kiwi;
        try
        {
            Assert.StartsWith("ok: 67 got the new task", h.Supervisor.Spawn(Fight1));
            await h.Supervisor.HandleAsync(s67, s67.Generation, Frame(Msg.Result, new ResultMsg(s67.Task!.Id, false, Refusal)), default);
            s67.Task = null; // logged out (task failed) and back as a free clone

            // 15:45:25 kiwi "she can try": no new information; same clone, same intent → refused with the reason.
            h.Supervisor.NotePlayerChat("kiwi", "she can try");
            var again = h.Supervisor.Spawn(Fight2);
            Assert.StartsWith("no: no clone fits fighting: 67 already refused fighting: hp 7/20, food 0", again);
            Assert.Contains("nothing changed since", again);
            var named = h.Supervisor.Spawn(Fight2, "67");
            Assert.StartsWith("no: not reassigned: 67 already refused fighting", named);
            Assert.Contains("it reruns if a player says try again or brings what was missing", named);
            // 15:45:48 the script spawn of the same job (intent read from the code): refused too.
            var script = h.Supervisor.Spawn("", null, options: new HiveSpawnOptions(Code: "deposit_all()\nwhile (is_night()) attack_nearest('zombie')"));
            Assert.StartsWith("no: no clone fits fighting", script);

            // Something relevant changed → it may go again: a player brings what was missing...
            h.Supervisor.NotePlayerChat("kiwi", "I gave her 1 bread");
            Assert.StartsWith("ok: 67 got the new task", h.Supervisor.Spawn(Fight2));
        }
        finally { TurnContext.Current = null; }
    }

    [Fact]
    public void Refusal_memory_clears_on_vitals_inventory_or_day_night_change()
    {
        var m = new AssignFailureMemory();
        var body = new CloneVitals(7, 0, [new ItemMsg(275, 0, 1, "stone_axe")], 9, 65, 1);
        m.Record("maid_chan_67", TaskIntent.Fight, Refusal, body, night: true);
        Assert.NotNull(m.Blocking("maid_chan_67", TaskIntent.Fight, body, true));
        Assert.Null(m.Blocking("maid_chan_67", TaskIntent.Mine, body, true));    // keyed on intent
        Assert.Null(m.Blocking("maid_chan_69", TaskIntent.Fight, body, true));   // and on clone
        Assert.NotNull(m.Blocking("maid_chan_67", TaskIntent.Fight, body with { Items = null, X = null }, null)); // unknown = unchanged
        Assert.Null(m.Blocking("maid_chan_67", TaskIntent.Fight, body with { Food = 5 }, true));                   // ate bread

        m.Record("maid_chan_67", TaskIntent.Fight, Refusal, body, night: true);
        Assert.Null(m.Blocking("maid_chan_67", TaskIntent.Fight, body with { Items = [.. body.Items!, new ItemMsg(297, 0, 1, "bread")] }, true));
        m.Record("maid_chan_67", TaskIntent.Fight, Refusal, body, night: true);
        Assert.Null(m.Blocking("maid_chan_67", TaskIntent.Fight, body, night: false)); // morning
        m.Record("maid_chan_67", TaskIntent.Fight, Refusal, body, night: true);
        Assert.Equal(0, m.OnPlayerChat("ok whatever"));
        Assert.Equal(1, m.OnPlayerChat("try again"));
    }

    [Fact]
    public async Task Vitals_seen_in_a_session_survive_a_main_restart_via_the_store()
    {
        await using var h = new TestHive(TwoClones());
        var s69 = Free(h, "maid_chan_69");
        await h.Supervisor.HandleAsync(s69, s69.Generation, Frame(Msg.Snapshot, Body(20, 20).ToJson()), default);
        s69.Snapshot = null; // main restarted: in-memory snapshot gone
        s69.State = CloneState.Idle;
        s69.Connected = false;
        var v = h.Supervisor.VitalsOf(s69);
        Assert.Equal(20, v.Hp);
        Assert.Equal(20, v.Food);
    }
}
