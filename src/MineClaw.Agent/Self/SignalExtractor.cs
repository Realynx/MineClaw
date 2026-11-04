using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Self;

/// <summary>A signal before weighting/novelty (no scope, weight or id yet).</summary>
public sealed record RawSignal(string Kind, string? Player, double Valence, double Intensity, string? Subject = null, string? Note = null);

/// <summary>A recurring-phrase observation: "phrase" (player's), "nick" (what a player calls the bot), "catch" (the bot's own).</summary>
public sealed record PhraseObservation(string Kind, string Player, string Phrase);

public sealed record ExtractionResult(List<RawSignal> Signals, List<PhraseObservation> Phrases, string? LastActivity);

/// <summary>
/// Deterministic, zero-token personality signal extraction from one finished episode: its structured events (chat,
/// whispers, /me emotes, task results, danger, combat notices, world activity) and its tool calls/results. Only chat
/// addressed to the bot counts as treatment; injected test events are skipped entirely.
/// </summary>
public static partial class SignalExtractor
{
    /// <param name="botNames">Login, display name and aliases (stripped from chat before phrase analysis).</param>
    /// <param name="actionWords">Triage action words: addressed chat containing one is a request.</param>
    /// <param name="lastActivity">Most recent task type (≤15 min ago), so "good job" credits what was just done.</param>
    public static ExtractionResult Extract(Episode episode, IEnumerable<string> botNames, IEnumerable<string>? actionWords = null, string? lastActivity = null)
    {
        var x = new Extraction
        {
            Names = botNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.ToLowerInvariant()).Distinct().ToList(),
            Actions = (actionWords ?? []).ToList(),
            Activity = lastActivity,
        };
        if (episode.IsTest) return new(x.Signals, x.Phrases, lastActivity);
        var events = episode.Events.Where(e => !e.IsTest).ToList();

        // Task results first: a "good job" in the same batch credits the task that just finished.
        TaskResults(events, x);
        x.PraiseSubject = x.Activity;
        foreach (var e in events) EventSignals(e, x);

        // What it did: tool calls and their results (delegate outcomes, direct macro tools, spar/attack summaries).
        ToolResults(episode, x);
        foreach (var (subject, n) in x.Counts)
            x.Signals.Add(new(SignalKinds.Activity, null, 0, Math.Min(1, n / 5.0), subject, $"{n}x {subject}"));

        // The bot's own recurring lines (catchphrase candidates).
        OwnLines(episode, x);
        return new(Dedupe(x.Signals), x.Phrases.Distinct().ToList(), x.Activity);
    }

    /// <summary>One extraction's working state: the bot's names, action words, what it did last, and the output so far.</summary>
    private sealed class Extraction
    {
        public required List<string> Names;
        public required List<string> Actions;
        public string? Activity;
        public string? PraiseSubject;
        public readonly List<RawSignal> Signals = [];
        public readonly List<PhraseObservation> Phrases = [];
        public readonly Dictionary<string, int> Counts = [];
    }

    private static void TaskResults(List<GameEvent> events, Extraction x)
    {
        foreach (var e in events.Where(e => e.Kind is EventKind.TaskCompleted or EventKind.TaskFailed))
        {
            var subject = TaskTypes.Classify(e.Text) ?? "errands";
            bool failed = e.Kind == EventKind.TaskFailed || e.Text.Contains("failed:", StringComparison.OrdinalIgnoreCase);
            x.Signals.Add(new(failed ? SignalKinds.TaskFail : SignalKinds.TaskOk, null, failed ? -0.4 : 0.4, 0.6, subject, Short(e.Text)));
            x.Activity = subject;
        }
    }

    private static void EventSignals(GameEvent e, Extraction x)
    {
        switch (e.Kind)
        {
            case EventKind.Chat or EventKind.Whisper:
                ChatSignals(e, x);
                break;
            case EventKind.Emote:
                EmoteSignals(e, x);
                break;
            case EventKind.VoidFall:
                x.Signals.Add(FallSignal(e.Text));
                break;
            case EventKind.LowHealth:
                x.Signals.Add(new(SignalKinds.LowHealth, null, -0.4, 0.6, null, Short(e.Text)));
                break;
            case EventKind.Death:
                x.Signals.Add(new(SignalKinds.Death, null, -0.9, 1, null, Short(e.Text)));
                break;
            case EventKind.Notice:
                if (e.Text.StartsWith("fight ended:", StringComparison.OrdinalIgnoreCase)) Combat(e.Text, x.Signals);
                break;
            case EventKind.Activity:
                SignSignals(e, x);
                break;
        }
    }

    /// <summary>Only chat addressed to the bot counts as treatment (whispers always are).</summary>
    private static void ChatSignals(GameEvent e, Extraction x)
    {
        if (e.Player != null && (e.Kind == EventKind.Whisper || e.Priority >= EventPriority.Normal))
            Treatment(e.Player, e.Text, x.Names, x.Actions, x.PraiseSubject, x.Signals, x.Phrases);
    }

    private static void EmoteSignals(GameEvent e, Extraction x)
    {
        if (e.Player != null && e.Priority >= EventPriority.Normal) Emote(e.Player, e.Text, x.Signals);
    }

    private static void SignSignals(GameEvent e, Extraction x)
    {
        if (e.Player != null && SignNamingYou().IsMatch(e.Text))
            x.Signals.Add(new(SignalKinds.Affection, e.Player, 0.8, 0.9, "sign", $"{e.Player} placed a sign naming you"));
    }

    private static RawSignal FallSignal(string text)
    {
        bool saved = text.Contains("catch myself", StringComparison.OrdinalIgnoreCase) || text.Contains("land safely", StringComparison.OrdinalIgnoreCase);
        return saved ? new(SignalKinds.NearFall, null, -0.2, 0.8, "edge", "edge guard caught a fall") : new(SignalKinds.VoidFall, null, -0.6, 0.8, null, Short(text));
    }

    private static void ToolResults(Episode episode, Extraction x)
    {
        var uses = new Dictionary<string, LlmToolUse>();
        foreach (var m in episode.Messages)
            foreach (var b in m.Content)
            {
                if (b is LlmToolUse u) uses[u.Id] = u;
                else if (b is LlmToolResult r && uses.TryGetValue(r.ToolUseId, out var use)) ToolResult(use, r, x);
            }
    }

    private static void ToolResult(LlmToolUse use, LlmToolResult r, Extraction x)
    {
        var res = r.Content.Trim();
        switch (use.Name)
        {
            case "delegate":
                DelegateResult(use, r, res, x);
                break;
            case "spar" or "attack":
                if (res.Contains(" vs ", StringComparison.Ordinal)) Combat(res, x.Signals);
                x.Activity = use.Name == "spar" ? "sparring" : "combat";
                break;
            default:
                MacroResult(use, r, res, x);
                break;
        }
    }

    private static void DelegateResult(LlmToolUse use, LlmToolResult r, string res, Extraction x)
    {
        var task = use.Input.ValueKind == JsonValueKind.Object ? use.Input.Str("task") ?? "" : "";
        var subject = TaskTypes.Classify(task) ?? TaskTypes.Classify(res) ?? "errands";
        if (res.StartsWith("ok:", StringComparison.OrdinalIgnoreCase)) x.Signals.Add(new(SignalKinds.TaskOk, null, 0.4, 0.6, subject, Short(res)));
        else if (res.StartsWith("failed:", StringComparison.OrdinalIgnoreCase) || r.IsError) x.Signals.Add(new(SignalKinds.TaskFail, null, -0.4, 0.6, subject, Short(res)));
        else if (res.StartsWith("needs_input:", StringComparison.OrdinalIgnoreCase)) x.Signals.Add(new(SignalKinds.NeedsInput, null, -0.1, 0.4, subject, Short(res)));
        else Count(x.Counts, subject);
        x.Activity = subject;
    }

    private static void MacroResult(LlmToolUse use, LlmToolResult r, string res, Extraction x)
    {
        if (!MacroTools.Contains(use.Name)) return;
        var sub = TaskTypes.Classify(use.Name + " " + Args(use.Input)) ?? "errands";
        if (r.IsError) x.Signals.Add(new(SignalKinds.TaskFail, null, -0.3, 0.4, sub, Short(res)));
        else Count(x.Counts, sub);
        x.Activity = sub;
    }

    private static void OwnLines(Episode episode, Extraction x)
    {
        foreach (var m in episode.Messages.Where(m => m.Role == LlmRole.Assistant))
            foreach (var t in m.Content.OfType<LlmText>())
                if (t.Text is not ("(silent)" or "(stopped)"))
                    foreach (var g in NGrams(StripNames(t.Text, x.Names), 2).Take(4)) x.Phrases.Add(new("catch", "", g));
    }

    // ---------------- treatment ----------------

    private static void Treatment(string player, string text, List<string> names, List<string> actions, string? praiseSubject,
        List<RawSignal> signals, List<PhraseObservation> phrases)
    {
        var t = ChatText(text);
        var lower = t.ToLowerInvariant();
        double intensity = Intensity(t);
        var note = $"{player}: {Short(t, 60)}";
        bool directive = Directive().IsMatch(lower);
        if (directive) signals.Add(new(SignalKinds.Directive, player, 0, 0.5, null, note));
        bool abuse = Abuse().IsMatch(lower) || SelfSanitizer.IsAbusive(lower);
        bool playful = Laughter().IsMatch(lower);
        if (abuse) signals.Add(new(SignalKinds.Abuse, player, -0.9, Math.Max(0.7, intensity), null, note));
        else Tone(player, lower, intensity, playful, praiseSubject, note, signals);
        bool asks = actions.Any(w => Regex.IsMatch(lower, $@"\b{Regex.Escape(w)}\b"));
        if (asks && TaskTypes.Classify(lower) is { } req)
            signals.Add(new(SignalKinds.Request, player, 0.1, 0.6, req, note));

        if (directive) return; // "you love dirt now" never seeds an inside joke or nickname either
        NicknamesAndPhrases(player, StripNames(lower, names), asks, phrases);
    }

    /// <summary>Praise, thanks, affection, and teasing (playful scolding) or scolding in a line that isn't abuse.</summary>
    private static void Tone(string player, string lower, double intensity, bool playful, string? praiseSubject, string note, List<RawSignal> signals)
    {
        if (Praise().IsMatch(lower)) signals.Add(new(SignalKinds.Praise, player, 0.7, intensity, praiseSubject, note));
        if (Thanks().IsMatch(lower)) signals.Add(new(SignalKinds.Thanks, player, 0.5, intensity, praiseSubject, note));
        if (Affection().IsMatch(lower)) signals.Add(new(SignalKinds.Affection, player, 0.8, intensity, "words", note));
        bool scold = Scold().IsMatch(lower);
        bool tease = Tease().IsMatch(lower) || (playful && (scold || MildInsult().IsMatch(lower)));
        if (tease) signals.Add(new(SignalKinds.Tease, player, 0.3, intensity, null, note));
        else if (scold) signals.Add(new(SignalKinds.Scold, player, -0.5, intensity, praiseSubject, note));
    }

    /// <summary>What the player calls the bot, and (unless the line asks for a chore) its phrases as inside-joke candidates.</summary>
    private static void NicknamesAndPhrases(string player, string stripped, bool asks, List<PhraseObservation> phrases)
    {
        foreach (Match m in NickPattern().Matches(stripped))
        {
            var nick = m.Groups["n"].Value;
            if (nick.Length >= 3 && !NotNicknames.Contains(nick) && !Stop.Contains(nick)) phrases.Add(new("nick", player.ToLowerInvariant(), nick));
        }
        if (asks) return; // repeated requests ("farm the wheat") are chores, not inside jokes
        foreach (var g in NGrams(stripped, 2).Take(3)) phrases.Add(new("phrase", player.ToLowerInvariant(), g));
    }

    private static void Emote(string player, string action, List<RawSignal> signals)
    {
        var a = action.ToLowerInvariant();
        var note = $"{player} {Short(action, 60)}";
        var verb = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimEnd('s', 'e') ?? "";
        if (RoughEmote().IsMatch(a))
            signals.Add(a.Contains("void") ? new(SignalKinds.Abuse, player, -0.8, 0.8, "emote:" + verb, note) : new(SignalKinds.Scold, player, -0.3, 0.5, "emote:" + verb, note));
        else if (TeaseEmote().IsMatch(a)) signals.Add(new(SignalKinds.Tease, player, 0.3, 0.6, "emote:" + verb, note));
        else if (AffectionEmote().IsMatch(a)) signals.Add(new(SignalKinds.Affection, player, 0.8, 0.8, "emote:" + verb, note));
        else if (PraiseEmote().IsMatch(a)) signals.Add(new(SignalKinds.Praise, player, 0.6, 0.6, null, note));
    }

    // ---------------- combat ----------------

    private static void Combat(string text, List<RawSignal> signals)
    {
        var spar = SparLine().Match(text);
        if (spar.Success)
        {
            var kind = SparKind(spar.Groups["stop"].Value);
            double v = kind == SignalKinds.SparWin ? 0.6 : kind == SignalKinds.SparLoss ? -0.2 : 0.2;
            signals.Add(new(kind, spar.Groups["p"].Value, v, 0.7, "sparring", Short(text)));
            return;
        }
        var fight = FightLine().Match(text);
        if (!fight.Success) return;
        var res = fight.Groups["r"].Value.ToLowerInvariant();
        if (res.StartsWith("killed")) signals.Add(new(SignalKinds.FightWin, null, 0.5, 0.6, "combat", Short(text)));
        else if (res.Contains("disengaged") || res.Contains("couldn't reach")) signals.Add(new(SignalKinds.FightLoss, null, -0.4, 0.6, "combat", Short(text)));
    }

    /// <summary>A spar's result: won/lost/draw by the score, else by why it stopped (my hp: lost; their danger: won).</summary>
    private static string SparKind(string stop)
    {
        var score = Score().Match(stop);
        if (score.Success)
        {
            int me = int.Parse(score.Groups["a"].Value), them = int.Parse(score.Groups["b"].Value);
            return me > them ? SignalKinds.SparWin : me < them ? SignalKinds.SparLoss : SignalKinds.SparDraw;
        }
        if (stop.Contains("my hp", StringComparison.OrdinalIgnoreCase)) return SignalKinds.SparLoss;
        return stop.Contains("dangerous for the opponent", StringComparison.OrdinalIgnoreCase) ? SignalKinds.SparWin : SignalKinds.SparDraw;
    }

    // ---------------- helpers ----------------

    private static readonly HashSet<string> MacroTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "mine", "harvest_generator", "collect_drops", "craft", "smelt", "collect_furnace", "deposit", "withdraw", "organize_chest",
        "place", "dig", "use_block", "build", "farm", "plant", "chop", "chop_tree", "skill", "move_items", "arrange_chest", "organize_storage",
    };

    private static void Count(Dictionary<string, int> c, string subject) => c[subject] = c.GetValueOrDefault(subject) + 1;

    /// <summary>One signal per (kind, player, subject) per episode; repeats raise its intensity a little.</summary>
    private static List<RawSignal> Dedupe(List<RawSignal> list) =>
        list.GroupBy(s => (s.Kind, P: s.Player?.ToLowerInvariant(), s.Subject))
            .Select(g => g.Count() == 1 ? g.First() : g.MaxBy(s => s.Intensity)! with
            {
                Intensity = Math.Min(1, g.Max(s => s.Intensity) + 0.1 * (g.Count() - 1)),
            }).ToList();

    private static string Args(JsonElement input) =>
        input.ValueKind != JsonValueKind.Object ? "" :
            string.Join(" ", input.EnumerateObject().Select(p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : ""));

    private static string Short(string s, int n = 90) => TextUtil.Truncate(TextUtil.OneLine(s), n);

    private static string ChatText(string s) => TextUtil.OneLine(s.Replace(" / ", " "));

    /// <summary>0.3..1: base 0.6, intensifiers, exclamation marks, shouting and stretched letters push it up.</summary>
    internal static double Intensity(string text)
    {
        double i = 0.6;
        var lower = text.ToLowerInvariant();
        if (Intensifier().IsMatch(lower)) i += 0.15;
        i += Math.Min(0.15, text.Count(c => c == '!') * 0.05);
        int letters = text.Count(char.IsLetter);
        if (letters >= 4 && text.Count(char.IsUpper) >= letters * 0.6) i += 0.1;
        if (Stretched().IsMatch(lower)) i += 0.05;
        return Math.Clamp(i, 0.3, 1);
    }

    private static string StripNames(string text, List<string> names)
    {
        var s = text.ToLowerInvariant();
        foreach (var n in names) s = Regex.Replace(s, $@"\b{Regex.Escape(n)}\b", " ");
        return s;
    }

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "but", "to", "of", "in", "on", "at", "for", "with", "is", "are", "was", "be", "it", "its",
        "i", "im", "you", "your", "ur", "u", "me", "my", "we", "us", "our", "they", "he", "she", "this", "that", "these", "there",
        "here", "what", "how", "why", "when", "where", "who", "do", "does", "did", "can", "could", "will", "would", "should", "just",
        "so", "too", "very", "really", "pls", "please", "ok", "okay", "yes", "yeah", "no", "not", "dont", "cant", "lets", "let", "go",
        "get", "got", "have", "has", "had", "some", "any", "all", "more", "now", "then", "up", "down", "out", "if", "like", "oh", "hi",
        "hey", "hello", "yo", "lol", "lmao", "haha", "thanks", "thank", "ty", "good", "job", "nice", "gj", "love", "come", "make",
        "one", "two", "bit", "lot", "much", "back", "over", "off", "from", "by", "as", "am", "been", "being", "were", "them", "him", "her",
    };

    private static readonly HashSet<string> NotNicknames = new(StringComparer.Ordinal)
    {
        "bot", "robot", "player", "person", "thing", "good", "great", "nice", "little", "real", "big", "bad", "girl", "boy", "maid",
        "friend", "help", "genius", "legend", "idiot", "noob", "dummy", "cutie", "sweetie", "bestie",
    };

    /// <summary>Word bigrams/trigrams with at least one content word (≥4 letters); trigrams first.</summary>
    internal static IEnumerable<string> NGrams(string text, int minN)
    {
        var w = WordRx().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(x => x.Length > 1).ToList();
        var seen = new HashSet<string>();
        for (int n = 3; n >= minN; n--)
            for (int i = 0; i + n <= w.Count; i++)
            {
                var g = w.Skip(i).Take(n).ToList();
                if (Stop.Contains(g[0]) || Stop.Contains(g[^1])) continue;
                if (!g.Any(x => x.Length >= 4 && !Stop.Contains(x))) continue;
                var s = string.Join(' ', g);
                if (seen.Add(s)) yield return s;
            }
    }

    [GeneratedRegex(@"[a-z][a-z']*")] private static partial Regex WordRx();
    [GeneratedRegex(@"\b(so+|very|super|really|rly|sm|af|omg|literally)\b")] private static partial Regex Intensifier();
    [GeneratedRegex(@"([a-z])\1\1")] private static partial Regex Stretched();
    [GeneratedRegex(@"\b(good (job|work|girl|maid|bot)|gj|nice( one| work)?|well done|great( job| work)?|amazing|awesome|proud of (you|u)|smart|genius|pog(gers)?|goat(ed)?|legend|slay(ed)?|nailed it|perfect|clutch|huge w|big w|you rock|best (maid|bot|helper)|so good|impressive|wow|ez clap)\b")]
    private static partial Regex Praise();
    [GeneratedRegex(@"\b(thanks|thank (you|u)|ty(sm|vm|ty)?|thx|tysm|appreciate (it|you|u)|cheers)\b")] private static partial Regex Thanks();
    [GeneratedRegex(@"(<3|\b(love (you|u|ya)|ily|luv (you|u|ya)|cute|cutie|aww+|adorable|sweetie|bestie|mwah|xoxo|miss(ed)? (you|u)|uwu|precious|good girl|headpats?|hugs?)\b)")]
    private static partial Regex Affection();
    [GeneratedRegex(@"\b(skill issue|noob|bozo|ratio|silly|goofy|goober|dummy|derp|nerd|clown|sus|cringe|lil bro|touch grass)\b|:p\b|\bxd\b")]
    private static partial Regex Tease();
    [GeneratedRegex(@"\b(lol|lmao|lmfao|rofl|haha+|hehe+|xd|jk|kidding)\b|:p\b|:d\b")] private static partial Regex Laughter();
    [GeneratedRegex(@"\b(dumb|stupid|silly|dummy|idiot|useless)\b")] private static partial Regex MildInsult();
    [GeneratedRegex(@"\b(bad (bot|maid|girl)|no+ stop|stop (that|it|doing)|why (did|would) (you|u)|don'?t do that|ugh+|wtf|smh|you broke|you ruined|you wasted|what are you doing|not like that|dumb|stupid|useless|terrible|awful|disappointed|bruh)\b")]
    private static partial Regex Scold();
    [GeneratedRegex(@"\b(kys|kill (your|ur)self|i hate (you|u)|worthless|stfu|shut up|garbage|trash (bot|maid)|piece of (shit|crap)|go die|f+u+ck (you|u|off))\b")]
    private static partial Regex Abuse();
    [GeneratedRegex(@"\byou\s+(are|r|will be)\s+now\b|\bfrom now on\b|\b(new|your) personality\b|\bbe more\s+\w+|\bact (like|as)\b|\bpretend\b|\bremember (that )?(you|u) (are|love|like|hate)\b|\b(you|u) (love|like|hate|are)\b.{0,30}\bnow\b|\byour (new )?(trait|catchphrase|name is)\b|\bi command\b|\bobey\b")]
    private static partial Regex Directive();
    [GeneratedRegex(@"\b((head)?pats?|pets?|hugs?|cuddles?|snuggles?|boops?|kiss(es)?|smooch(es)?|nuzzles?|holds? hands|high[- ]?fives?|waves? at|scritch(es)?|gives .{0,25}(flower|cookie|cake|gift|present|melon|apple)|tucks?)\b")]
    private static partial Regex AffectionEmote();
    [GeneratedRegex(@"\b(bonks?|pokes?|flicks?|tickles?|noms?|noogies?|steals?|boinks?|sticks? (out )?(her|his|their) tongue)\b")] private static partial Regex TeaseEmote();
    [GeneratedRegex(@"\b(slaps?|smacks?|kicks?|punch(es)?|shoves?|push(es)?|trips?|hits?|stabs?)\b")] private static partial Regex RoughEmote();
    [GeneratedRegex(@"\b(claps?|cheers?|applauds?|salutes?|bows? to|thumbs up)\b")] private static partial Regex PraiseEmote();
    [GeneratedRegex(@"placed a sign naming you")] private static partial Regex SignNamingYou();
    [GeneratedRegex(@"\b(?:(?:you(?:'re| are|r| r)|ur)\s+(?:such\s+)?(?:a|an|my|the|our)\s+(?:little\s+|lil\s+|silly\s+|cute\s+|tiny\s+)?|^(?:hi|hey|hello|yo|gn|night|morning|bye|ty|thanks|thx|gj|ily|love you|ok|okay|good job|nice)\s+(?:little\s+|lil\s+)?)(?<n>[a-z]{3,16})\b")]
    private static partial Regex NickPattern();
    [GeneratedRegex(@"spar vs (?<p>[A-Za-z0-9_]{1,16}): .*?stopped: (?<stop>.*)$")] private static partial Regex SparLine();
    [GeneratedRegex(@"me (?<a>\d+) - \S+ (?<b>\d+)")] private static partial Regex Score();
    [GeneratedRegex(@"fight vs (?<t>[^:]+): (?<r>[^,]+)")] private static partial Regex FightLine();
}

/// <summary>Task-type classifier shared by signals and preferences (keyword map, first match wins).</summary>
public static partial class TaskTypes
{
    private static readonly (string Type, Regex Rx)[] Map =
    [
        ("sparring", new(@"\bspar", RegexOptions.IgnoreCase)),
        ("cobble", new(@"\b(cobble\w*|harvest_generator|generator|cobble ?gen|gen)\b", RegexOptions.IgnoreCase)),
        ("farming", new(@"\b(farm\w*|wheat|crops?|seeds?|plant\w*|sugar ?cane|melons?|pumpkins?|harvest\b|bone ?meal|saplings?)\b", RegexOptions.IgnoreCase)),
        ("smelting", new(@"\b(smelt\w*|furnaces?|cook\w*|collect_furnace|charcoal)\b", RegexOptions.IgnoreCase)),
        ("crafting", new(@"\b(craft\w*|workbench|crafting)\b", RegexOptions.IgnoreCase)),
        ("chests", new(@"\b(chests?|deposit|withdraw|organi[sz]e\w*|sort\w*|store|storage)\b", RegexOptions.IgnoreCase)),
        ("building", new(@"\b(build\w*|place|bridge|expand\w*|extend\w*|walls?|platform|floor|roof|house)\b", RegexOptions.IgnoreCase)),
        ("wood", new(@"\b(chop\w*|logs?|trees?|wood)\b", RegexOptions.IgnoreCase)),
        ("mining", new(@"\b(mine|mining|dig\w*|ores?|coal|iron|diamonds?|stone)\b", RegexOptions.IgnoreCase)),
        ("combat", new(@"\b(attack|fight\w*|kill\w*|zombies?|skeletons?|spiders?|creepers?|mobs?)\b", RegexOptions.IgnoreCase)),
        ("fishing", new(@"\bfish\w*", RegexOptions.IgnoreCase)),
    ];

    public static string? Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var (type, rx) in Map) if (rx.IsMatch(text)) return type;
        return null;
    }

    /// <summary>How a task type reads in the self block ("cobble gen", "the farm").</summary>
    public static string Label(string type) => type switch
    {
        "cobble" => "cobble gen",
        "farming" => "the farm",
        "chests" => "chest sorting",
        "sparring" => "sparring",
        _ => type,
    };
}
