using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MineClaw.Scripting.Character;
using MineClaw.Scripting.Events;
using MineClaw.Scripting.Sdk;
using MineClaw.World;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>The character SDK module and the typed game events (docs/design/reactive-character.md).</summary>
public class CharacterSdkTests
{
    private sealed class FakePort : ICharacterPort
    {
        public readonly ConcurrentQueue<string> Calls = new();
        public string? NextLine = "omw foxy~";
        public bool Look(string? player, BlockPos? pos) { Calls.Enqueue($"look {player ?? pos?.ToString()}"); return true; }
        public ActResult Gesture(string name, string? target) { Calls.Enqueue($"gesture {name} {target}"); return ActResult.Success("gestured " + name); }
        public string? Line(string kind, string? player) { Calls.Enqueue($"line {kind} {player}"); var l = NextLine; NextLine = null; return l; }
        public void Said(string line) => Calls.Enqueue("said " + line);
        public JsonNode? Mood() => new JsonObject { ["mood"] = "cheery" };
        public JsonNode? Relation(string player) => new JsonObject { ["player"] = player, ["affinity"] = 0.6 };
        public JsonNode? Recall(string query, int k) => new JsonArray(new JsonObject { ["id"] = 3, ["text"] = "the gen is east" });
        public ActResult Remember(string text) { Calls.Enqueue("remember " + text); return ActResult.Success("note#1 saved"); }
        public JsonNode Heard(string? player, double seconds) => new JsonArray(new JsonObject { ["player"] = "kiwi", ["text"] = "hi", ["intent"] = "greeting" });
        public string Intent(string text) => text.Contains("thank") ? "thanks" : "banter";
    }

    [Fact]
    public void Every_character_global_is_documented_and_the_events_are_in_the_reference()
    {
        Assert.Contains("character", SdkModules.Globals);
        Assert.Contains("react", SdkModules.Globals);
        var doc = SdkReference.Build();
        Assert.Contains("## character:", doc);
        foreach (var f in new[] { "character.look", "character.gesture", "character.follow", "character.stopFollow", "character.face", "character.line",
                     "character.mood", "character.relation", "character.recall", "character.remember", "character.heard", "character.typing", "react(trigger" })
            Assert.Contains(f, doc);
        foreach (var e in GameEventCatalog.Names) Assert.Contains(e, doc);
        Assert.Contains("character", SdkReference.Documented());
    }

    [Fact]
    public async Task Character_calls_go_through_the_port_and_gesture_is_a_budgeted_action()
    {
        using var rig = Rig.Js();
        var port = new FakePort();
        rig.Host.Character = port;
        var s = await rig.Run("""
            character.look('kiwi')
            const g = character.gesture('wave', 'kiwi')
            const t = character.speak('come', 'coming!', 'kiwi')
            const m = character.mood().mood
            const r = character.relation('kiwi').affinity
            const k = character.recall('gen', 2)[0].text
            const h = character.heard('kiwi', 30)[0].intent
            const n = character.remember('kiwi likes the gen')
            return [g.ok, t, m, r, k, h, n.ok, character.intent('thanks!')].join('|')
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("true|omw foxy~|cheery|0.6|the gen is east|greeting|true|thanks", s.Outcome);
        Assert.Contains("look kiwi", port.Calls);
        Assert.Contains("gesture wave kiwi", port.Calls);
        Assert.Contains("said omw foxy~", port.Calls);
        Assert.Contains("omw foxy~", rig.Said);
        Assert.True(s.Actions >= 3); // gesture, say, remember count as actions
    }

    [Fact]
    public async Task Without_a_port_everything_is_a_harmless_no_op()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("""
            const a = character.look('kiwi'), b = character.gesture('nod').ok, c = character.line('greet'), d = character.mood()
            const e = character.speak('greet', 'hi!')
            return [a, b, c, d, e, character.heard().length].join('|')
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("false|true|||hi!|0", s.Outcome);
    }

    [Fact]
    public async Task Remember_is_capped_per_run()
    {
        using var rig = Rig.Js();
        rig.Host.Character = new FakePort();
        var s = await rig.Run("let n = 0\nfor (let i = 0; i < 5; i++) if (character.remember('x' + i).ok) n++\nreturn 'notes ' + n");
        Assert.Contains("notes 3", s.Outcome);
    }

    private static ScriptEvent Ev(string name, JsonObject data) =>
        new(ScriptEventKind.Signal, data.ToJsonString(), data["player"]?.ToString(), name) { Data = data };

    [Fact]
    public async Task On_game_event_with_filters_runs_the_handler_with_the_payload()
    {
        using var rig = Rig.Js();
        var s = rig.Start("""
            const hits = []
            on('player.punchBlock', { player: 'PoofImaFox', held: 'pickaxe' }, e => { hits.push(e.block + '@' + e.pos.x); dig(e.pos) })
            react('thanks', e => { hits.push('ty ' + e.player) })
            wait(1.5)
            return hits.join(',')
            """);
        await Rig.Until(() => s.Status == ScriptStatus.Running);
        await Task.Delay(100);
        var pos = new JsonObject { ["x"] = 3, ["y"] = 64, ["z"] = -2 };
        rig.C.Deliver(Ev("player.punchBlock", new JsonObject { ["player"] = "kiwi", ["pos"] = pos.DeepClone(), ["block"] = "log", ["held"] = "stone_pickaxe" }));
        rig.C.Deliver(Ev("player.punchBlock", new JsonObject { ["player"] = "PoofImaFox", ["pos"] = pos.DeepClone(), ["block"] = "log", ["held"] = "stone_axe" }));
        rig.C.Deliver(Ev("player.punchBlock", new JsonObject { ["player"] = "PoofImaFox", ["pos"] = pos.DeepClone(), ["block"] = "log", ["held"] = "iron_pickaxe" }));
        rig.C.Deliver(Ev("player.say", new JsonObject { ["player"] = "kiwi", ["text"] = "ty maid", ["intent"] = "thanks" }));
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("log@3,ty kiwi", s.Outcome);
        Assert.Single(rig.CallList, c => c.StartsWith("dig "));
    }

    private static JsonObject Gesture(string kind, string? at = "me", string player = "PoofImaFox")
    {
        var d = new JsonObject { ["player"] = player, ["kind"] = kind, ["strength"] = 0.8, ["pos"] = new JsonObject { ["x"] = 4, ["y"] = 65, ["z"] = 0 } };
        if (at != null) d["at"] = at;
        if (kind is "nod" or "shake") d["answer"] = kind == "nod" ? "yes" : "no";
        return d;
    }

    [Fact]
    public async Task Gesture_handlers_filter_by_kind_at_and_answer_and_a_machine_transitions_on_one()
    {
        using var rig = Rig.Js();
        var s = rig.Start("""
            const got = []
            on('player.gesture', { kind: 'nod', at: 'me', player: 'PoofImaFox' }, e => { got.push('yes:' + e.answer) })
            react('player.gesture', { answer: 'no' }, e => { got.push('no:' + e.player) })
            const end = machine({
              initial: 'idle',
              states: {
                idle: { tick: () => { wait(0.1) }, on: { 'player.gesture': e => e.kind === 'bob' ? 'come' : null } },
                come: { enter: () => { got.push('come') }, tick: () => 'done' },
              },
            })
            return got.join(',') + '|' + end
            """);
        await Rig.Until(() => s.MachineState == "idle");
        rig.C.Deliver(Ev("player.gesture", Gesture("nod", at: null)));     // a nod at nobody: not the filter's
        rig.C.Deliver(Ev("player.gesture", Gesture("nod")));
        rig.C.Deliver(Ev("player.gesture", Gesture("shake", player: "kiwi")));
        rig.C.Deliver(Ev("player.gesture", Gesture("wave")));               // the machine ignores it
        await Task.Delay(300);
        Assert.Equal("idle", s.MachineState);
        rig.C.Deliver(Ev("player.gesture", Gesture("bob")));
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(s.Ok, s.Outcome);
        Assert.Equal("yes:yes,no:kiwi,come|done", s.Result);
    }

    [Fact]
    public void Event_filters_match_classes_regions_and_cooldowns()
    {
        var d = new JsonObject { ["player"] = "Fox", ["held"] = "stone_pickaxe", ["block"] = "oak_log", ["pos"] = new JsonObject { ["x"] = 2, ["y"] = 64, ["z"] = 5 } };
        foreach (var j in new[] { """{"held":"pickaxe","block":"log"}""", """{"held":"pickaxe"}""", """{"block":"log"}""", """{"held":"tool"}""", """{"player":["kiwi","fox"]}""", """{"near":{"x":0,"y":64,"z":5,"r":3}}""", """{"pos":"2,64,5"}""" })
            Assert.True(EventFilter.Parse(JsonNode.Parse(j)).Accepts(d), j);
        Assert.False(EventFilter.Parse(JsonNode.Parse("""{"held":"axe"}""")).Accepts(d)); // a pickaxe is no axe
        Assert.True(EventFilter.Parse(JsonNode.Parse("""{"held":"tool"}""")).Accepts(d));
        Assert.True(EventFilter.Parse(JsonNode.Parse("""{"player":["kiwi","fox"]}""")).Accepts(d));
        Assert.True(EventFilter.Parse(JsonNode.Parse("""{"near":{"x":0,"y":64,"z":5,"r":3}}""")).Accepts(d));
        Assert.False(EventFilter.Parse(JsonNode.Parse("""{"region":[10,60,10,20,70,20]}""")).Accepts(d));
        Assert.True(EventFilter.Parse(JsonNode.Parse("""{"pos":"2,64,5"}""")).Accepts(d));
        var cd = EventFilter.Parse(JsonNode.Parse("""{"cooldown":5}"""));
        var t0 = DateTimeOffset.UnixEpoch;
        Assert.True(cd.TryFire(t0));
        Assert.False(cd.TryFire(t0.AddSeconds(2)));
        Assert.True(cd.TryFire(t0.AddSeconds(6)));
        var once = EventFilter.Parse(JsonNode.Parse("""{"once":true}"""));
        Assert.True(once.TryFire(t0));
        Assert.False(once.Accepts(d));
    }

    [Fact]
    public void Lint_knows_the_game_events_in_machines()
    {
        var r = ScriptLint.Check("""
            machine({ initial: 'idle', states: { idle: { on: { 'player.punchBlock': 'work', 'me.hurt': 'idle' }, tick() { wait(1) } }, work: { tick() { wait(1) } } } })
            """, ["dig"], language: ScriptLanguage.Js);
        Assert.DoesNotContain(r.Findings, f => f.Message.Contains("unknown machine event"));
    }
}
