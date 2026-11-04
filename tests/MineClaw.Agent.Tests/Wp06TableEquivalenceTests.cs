using System.Globalization;
using MineClaw.Agent.Authoring;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Memory.Grooming;
using MineClaw.Agent.Telemetry;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Old-vs-new equivalence for the switches WP06 turned into tables or split (the old code is kept here as the reference):
/// patch continuations, capture lanes, player-facing events, activity verb phrases and verb classes.
/// </summary>
public class Wp06TableEquivalenceTests
{
    private static string OldNormalizeThen(string? s) => (s ?? "").Trim().ToLowerInvariant().Replace('-', '_') switch
    {
        "" or "resume" or "continue" or "ok" => "resume",
        "restart_step" or "restart" or "retry" or "redo" => "restart_step",
        "stop" or "cancel" or "end" => "stop",
        _ => "escalate",
    };

    [Fact]
    public void Patch_continuations_match_the_old_switch()
    {
        string?[] words = [null, "", "  ", "resume", "Resume ", "continue", "ok", "OK", "restart_step", "restart-step", "Restart", "retry",
            "redo", "stop", " STOP", "cancel", "end", "escalate", "abort", "resume please", "restart_step_now", "-", "_", "ok!"];
        foreach (var w in words) Assert.Equal(OldNormalizeThen(w), PatchAgent.NormalizeThen(w));
    }

    private static string OldLaneOf(string purpose, ModelTier tier) => purpose switch
    {
        PromptPurpose.ChatTurn => BrainContextCapture.Lanes.Brain,
        "quick_ack" or "quick_reply" => BrainContextCapture.Lanes.Reply,
        PromptPurpose.Compaction => BrainContextCapture.Lanes.Compaction,
        PromptPurpose.DelegateRound => BrainContextCapture.Lanes.Worker,
        PromptPurpose.AskMemory => BrainContextCapture.Lanes.Memory,
        "perception" => BrainContextCapture.Lanes.Perception,
        "patch" or "blueprint" => BrainContextCapture.Lanes.Author,
        _ when purpose.StartsWith("author", StringComparison.Ordinal) => BrainContextCapture.Lanes.Author,
        _ when tier == ModelTier.Worker => BrainContextCapture.Lanes.Worker,
        _ => BrainContextCapture.Lanes.Background,
    };

    [Fact]
    public void Capture_lanes_match_the_old_switch()
    {
        string[] purposes = [PromptPurpose.ChatTurn, "quick_ack", "quick_reply", PromptPurpose.Compaction, PromptPurpose.DelegateRound,
            PromptPurpose.AskMemory, "perception", "patch", "blueprint", "author-script", "author", "authoring", "Author-script", PromptPurpose.Reflection,
            PromptPurpose.RaptorSummary, "compact", "", "chat", "CHAT_TURN", "quick", "patch ", "unknown"];
        foreach (var p in purposes)
            foreach (var tier in Enum.GetValues<ModelTier>())
                Assert.Equal(OldLaneOf(p, tier), BrainContextCapture.LaneOf(p, tier));
    }

    private static bool OldIsPlayerFacing(GameEvent e) => e.Kind switch
    {
        EventKind.Chat or EventKind.Whisper or EventKind.Emote or EventKind.PlayerJoined => e.Player != null,
        EventKind.Gesture => e.Player != null && e.Wakes,
        EventKind.Notice => e.Player != null,
        EventKind.Activity => e.Player != null && e.Priority >= EventPriority.Normal,
        EventKind.LowHealth or EventKind.VoidFall or EventKind.Death => true,
        _ => false,
    };

    [Fact]
    public void Player_facing_events_match_the_old_switch()
    {
        foreach (var kind in Enum.GetValues<EventKind>())
            foreach (var player in new[] { null, "kiwi" })
                foreach (var prio in Enum.GetValues<EventPriority>())
                    foreach (var wakes in new[] { false, true })
                    {
                        var e = new GameEvent(kind, "x", player, prio, wakes);
                        Assert.Equal(OldIsPlayerFacing(e), BotAgent.IsPlayerFacing(e));
                    }
    }

    private static string OldQty(ActivityPart p) => p.Count == 1 && !p.Approx ? "" : (p.Approx ? "~" : "") + p.Count.ToString(CultureInfo.InvariantCulture) + " ";

    private static (string Verb, string Obj) OldSplit(ActivityPart p)
    {
        string times = p.Count > 1 ? $" {p.Count} times" : "";
        var (v, o) = p.Verb switch
        {
            "mine" => (ActivityVerbs.Broken(p.Item), $"{OldQty(p)}{p.Item}"),
            "harvest" => ("harvested", $"{OldQty(p)}{p.Item}"),
            "plant" => ("planted", $"{OldQty(p)}{p.Item}"),
            "place" => ("placed", $"{OldQty(p)}{p.Item}"),
            "take" => ("took", $"{OldQty(p)}{p.Item}"),
            "store" => ("stored", $"{OldQty(p)}{p.Item}"),
            "craft" => ("crafted", $"{OldQty(p)}{p.Item}"),
            "collect" => ("collected", $"{OldQty(p)}{p.Item}"),
            "open" => ("opened", $"the {p.Item}{times}"),
            "light" => ("lit", $"the furnace{times}"),
            "chests" => ("used", $"{p.Count} chests"),
            _ => (p.Verb, $"{OldQty(p)}{p.Item}"),
        };
        return ((p.Probably ? "probably " : "") + v, o);
    }

    private static string OldVerbClass(string v) => v switch
    {
        "mined" or "broke" or "chopped" or "dug" or "cleared" or "took down" => "mine",
        "harvested" => "harvest", "planted" => "plant", "placed" => "place", "crafted" => "craft", "stored" => "store",
        "took" => "take", "picked up" or "collected" => "collect",
        _ => v,
    };

    [Fact]
    public void Activity_phrases_and_verb_classes_match_the_old_switches()
    {
        string[] verbs = ["mine", "harvest", "plant", "place", "take", "store", "craft", "collect", "open", "light", "chests", "fish", "", "Mine"];
        foreach (var verb in verbs)
            foreach (var item in new[] { "cobblestone", "log", "chest", "wheat" })
                foreach (var count in new[] { 1, 2, 312 })
                    foreach (var approx in new[] { false, true })
                        foreach (var probably in new[] { false, true })
                        {
                            var p = new ActivityPart(verb, item, count, approx, probably);
                            Assert.Equal(OldSplit(p), ActivityCoalesceStage.Split(p));
                        }

        string[] past = ["mined", "broke", "chopped", "dug", "cleared", "took down", "harvested", "planted", "placed", "crafted", "stored", "took",
            "picked up", "collected"];
        foreach (var v in past)
        {
            foreach (var phrase in new[] { $"{v} 5 stone", $"{v} ~12 oak log", $"probably {v} dirt", $"{v} 3 \"sign\"", $"{v} (x)" })
            {
                var part = ActivityCoalesceStage.ParsePart(phrase);
                if (phrase.Contains('"') || phrase.Contains('(')) { Assert.Null(part); continue; }
                Assert.NotNull(part);
                Assert.Equal(OldVerbClass(v), part!.Verb);
            }
        }
        Assert.Equal(new ActivityPart("open", "chest", 4, false, false), ActivityCoalesceStage.ParsePart("opened the chest 4 times"));
        Assert.Equal(new ActivityPart("open", "door", 1, false, true), ActivityCoalesceStage.ParsePart("probably opened the door"));
        Assert.Equal(new ActivityPart("light", "furnace", 3, false, false), ActivityCoalesceStage.ParsePart("lit the furnace 3 times"));
        Assert.Equal(new ActivityPart("light", "furnace", 1, false, false), ActivityCoalesceStage.ParsePart(" lit the furnace "));
        Assert.Equal(new ActivityPart("chests", "chests", 7, false, false), ActivityCoalesceStage.ParsePart("reorganized 7 chests"));
        Assert.Null(ActivityCoalesceStage.ParsePart("placed sign at the gate"));
        Assert.Null(ActivityCoalesceStage.ParsePart("died to a creeper"));
    }
}
