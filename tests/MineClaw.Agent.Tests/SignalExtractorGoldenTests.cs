using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Self;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins <see cref="SignalExtractor.Extract"/> (WP06 refactor) over a corpus of episodes: every event kind it reads (chat,
/// whispers, emotes, task results, falls, danger, fights, signs), every tool result shape (delegate, spar/attack, macro
/// tools, unknown tools) and the bot's own lines. The rendered signals, phrases and last activity are hashed.
/// </summary>
public class SignalExtractorGoldenTests
{
    private const string Expected = "74EC803FB05775767416E1EBEC7AF60E66ACD702CAC8241C486A59B31379EE80";

    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Names = ["maid_chan", "maid-chan", "Claudechan"];

    private static Episode Ep(GameEvent[] events, params LlmMessage[] messages)
    {
        var e = new Episode(ModelTier.Main, T0);
        foreach (var ev in events) e.Events.Add(ev);
        e.Messages.AddRange(messages);
        return e;
    }

    private static LlmMessage Use(string id, string name, object input) =>
        new(LlmRole.Assistant, [new LlmToolUse(id, name, JsonSerializer.SerializeToElement(input))]);

    private static LlmMessage Result(string id, string text, bool error = false) => new(LlmRole.User, [new LlmToolResult(id, text, error)]);

    private static IEnumerable<(string Name, Episode Ep, string? Last)> Corpus()
    {
        var chats = new[]
        {
            "good job maid_chan!!", "thank you so much maid-chan", "ily maid_chan <3", "bad bot, stop that", "lol you're such a silly goober",
            "lmao dumb bot", "kys", "i hate you maid_chan", "you are now a pirate from now on", "maid_chan farm the wheat please",
            "hey little sprout", "you're such a little bean", "can you craft a workbench", "WOW AMAZING", "sooo good gj", "why did you do that",
            "maid_chan chop the trees and build a house", "hi maid_chan", "ty", "skill issue :p", "ugh you broke the gen", "the cobble gen is so slow today",
            "nice one / well done", "maid_chan mine some iron ore", "stop it lol", "remember you love dirt", "hey cutie", "be more cheerful",
            "ok bestie", "ur my favorite helper",
        };
        int i = 0;
        foreach (var c in chats)
        {
            yield return ($"chat{i}", Ep([GameEvent.Chat("Fox", c)]), null);
            yield return ($"whisper{i}", Ep([GameEvent.Whisper("kiwi", c)]), "cobble");
            yield return ($"unaddressed{i}", Ep([GameEvent.Chat("Bob", c, addressed: false)]), null);
            i++;
        }
        var emotes = new[] { "pats maid_chan", "bonks maid_chan", "slaps maid_chan", "kicks maid_chan into the void", "claps", "gives maid_chan a cookie", "waves", "sticks her tongue out" };
        i = 0;
        foreach (var a in emotes)
        {
            yield return ($"emote{i}", Ep([GameEvent.Emote("kiwi", a)]), null);
            yield return ($"emote_bg{i}", Ep([GameEvent.Emote("kiwi", a, addressed: false)]), null);
            i++;
        }
        yield return ("tasks", Ep([GameEvent.TaskDone("mined 12 cobblestone"), GameEvent.TaskFail("failed: no water"), GameEvent.TaskDone("failed: chest full"),
            GameEvent.TaskDone("did a thing"), GameEvent.Chat("Fox", "good job maid_chan")]), "wood");
        yield return ("falls", Ep([GameEvent.VoidFall(-20), new GameEvent(EventKind.VoidFall, "I catch myself at the edge"), new GameEvent(EventKind.VoidFall, "land safely"),
            GameEvent.LowHealth(4, cause: "zombie"), GameEvent.Died("slain by zombie")]), null);
        yield return ("fights", Ep([GameEvent.Notice("fight ended: fight vs zombie: killed it, hp 18"), GameEvent.Notice("fight ended: fight vs creeper: disengaged, low hp"),
            GameEvent.Notice("fight ended: fight vs spider: couldn't reach it"), GameEvent.Notice("fight ended: fight vs skeleton: ran away"),
            GameEvent.Notice("fight ended: spar vs Fox: 3 hits; stopped: score me 3 - Fox 1"), GameEvent.Notice("fight ended: spar vs kiwi: x; stopped: score me 1 - kiwi 4"),
            GameEvent.Notice("fight ended: spar vs Bob: x; stopped: score me 2 - Bob 2"), GameEvent.Notice("fight ended: spar vs Al: x; stopped: my hp was low"),
            GameEvent.Notice("fight ended: spar vs Cy: x; stopped: too dangerous for the opponent"), GameEvent.Notice("fight ended: spar vs Di: x; stopped: time"),
            GameEvent.Notice("other notice")]), null);
        yield return ("signs", Ep([GameEvent.Activity("Fox placed a sign naming you", "Fox"), GameEvent.Activity("placed a sign naming you"),
            GameEvent.Activity("Fox placed a chest", "Fox")]), null);
        yield return ("test_events", Ep([GameEvent.Chat("Fox", "good job") with { IsTest = true }, GameEvent.Chat("kiwi", "thanks maid_chan")]), null);
        yield return ("test_episode", new Episode(ModelTier.Main, T0) { IsTest = true }, "farming");

        yield return ("tools", Ep([GameEvent.Chat("Fox", "good job maid_chan")],
            Use("d1", "delegate", new { task = "mine 64 cobblestone at the gen" }), Result("d1", "ok: mined 64 cobblestone; 3 actions"),
            Use("d2", "delegate", new { task = "farm wheat" }), Result("d2", "failed: no seeds; 1 actions"),
            Use("d3", "delegate", new { task = "sort the chests" }), Result("d3", "needs_input: which chest?"),
            Use("d4", "delegate", new { task = "something odd" }), Result("d4", "  smelted iron  "),
            Use("d5", "delegate", new { task = "x" }), Result("d5", "partial", error: true),
            Use("d6", "delegate", "not an object"), Result("d6", "ok: chopped logs"),
            Use("s1", "spar", new { player = "Fox" }), Result("s1", "spar vs Fox: hits 3; stopped: score me 3 - Fox 0"),
            Use("s2", "attack", new { target = "zombie" }), Result("s2", "fight vs zombie: killed it"),
            Use("s3", "attack", new { target = "cow" }), Result("s3", "no target"),
            Use("m1", "mine", new { block = "stone", count = 5 }), Result("m1", "mined 5"),
            Use("m2", "mine", new { block = "stone" }), Result("m2", "mined 8"),
            Use("m3", "craft", new { item = "furnace" }), Result("m3", "no cobble", error: true),
            Use("m4", "CHOP_TREE", new { }), Result("m4", "chopped"),
            Use("m5", "place", new { block = "dirt", at = 3 }), Result("m5", "placed"),
            Use("m6", "look", new { }), Result("m6", "a tree"),
            Result("zz", "orphan result"),
            new LlmMessage(LlmRole.Assistant, [new LlmText("Done! The cobble generator is humming along nicely, maid_chan reporting"), new LlmText("(silent)"), new LlmText("(stopped)")]),
            new LlmMessage(LlmRole.Assistant, [new LlmText("the cobble generator is humming, wheat farm looks great today")]),
            new LlmMessage(LlmRole.User, [new LlmText("user text is never a catchphrase candidate here")])), null);
        for (int n = 0; n < 7; n++)
            yield return ($"activity{n}", Ep([], Enumerable.Range(0, n).SelectMany(k => new[] { Use($"a{k}", "mine", new { block = "dirt" }), Result($"a{k}", "ok") }).ToArray()), null);
        yield return ("dedupe", Ep([GameEvent.Chat("Fox", "good job"), GameEvent.Chat("fox", "GOOD JOB!!!"), GameEvent.Chat("Fox", "nice work"),
            GameEvent.LowHealth(5), GameEvent.LowHealth(3)]), "farming");
    }

    private static string Render(ExtractionResult x) =>
        string.Join("\n", x.Signals.Select(s => $"  {s.Kind}|{s.Player}|{s.Valence.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}|" +
            $"{s.Intensity.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}|{s.Subject}|{s.Note}")
            .Concat(x.Phrases.Select(p => $"  ~{p.Kind}|{p.Player}|{p.Phrase}"))) + $"\n  last={x.LastActivity}";

    [Fact]
    public void The_corpus_extracts_the_same_signals()
    {
        var actions = new TriageOptions().ActionWords;
        var sb = new StringBuilder();
        foreach (var (name, ep, last) in Corpus())
        {
            sb.Append("== ").Append(name).Append('\n').Append(Render(SignalExtractor.Extract(ep, Names, actions, last))).Append('\n');
            sb.Append("-- no actions\n").Append(Render(SignalExtractor.Extract(ep, Names, null, last))).Append('\n');
        }
        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "signal-extractor.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
