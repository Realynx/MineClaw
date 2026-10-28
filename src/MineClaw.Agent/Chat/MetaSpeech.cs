using System.Text.RegularExpressions;

namespace MineClaw.Agent.Chat;

/// <summary>
/// Deterministic last line of defence against the model narrating its own plumbing in game chat ("Delegating the craft.",
/// "running script", "checking memory"): short sentences that are purely meta are dropped from outgoing model text.
/// Real speech that merely starts with such a word ("running over to you!") is kept.
/// </summary>
public static partial class MetaSpeech
{
    [GeneratedRegex(
        @"^\W*(?:ok(?:ay)?[,!. ]+|now[,!. ]+|let me\s+|i'?ll\s+|i'?m\s+|i'?ve\s+)?(?:delegat(?:e|ing|ed)\b|checking (?:my )?memory\b|asking (?:my )?memory\b|using (?:a |the )?tools?\b|calling (?:a |the |my )?(?:tool|worker|delegate|script|hive|ask_memory)\b|running (?:a |the |my |this )?(?:script|routine|tool|delegate|worker|task\b)|script\b|(?:spawning|recalling) (?:a |the )?clone\b|hive\b|tool call\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaRx();

    /// <summary>
    /// Turn/wake/background-job plumbing anywhere in a sentence (night review B17, L4): "Since nothing is waking me up for a
    /// reason, I'll keep it short", "The floor job is running in the background, I'll wait for its result.". Never real talk
    /// in game chat, so the sentence goes even when it is longer than the 10 words <see cref="IsMeta"/> allows.
    /// </summary>
    [GeneratedRegex(
        @"\bnothing(?:'s|\s+is)?\s+(?:wak(?:e|es|ing)|woke)\s+me\b|\bwak(?:e|es|ing|ed)\s+me\s+(?:up\s+)?for\s+(?:a|no)\s+reason\b|\bno\s+reason\s+to\s+(?:wake|be\s+woken)\b|\bthis\s+(?:turn|wake-?up|prompt|check-?in)\b(?!\s+of)|\bin\s+the\s+background\b|\bwait(?:ing)?\s+for\s+(?:its|the|a|their)\s+(?:tool\s+)?(?:result|report)s?\b|\b(?:the|my)\s+\w+\s+(?:job|task)\s+is\s+(?:still\s+)?running\b|\bi'?ve\s+started\s+(?:the|a|my)\s+(?:job|task)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlumbingRx();

    /// <summary>
    /// Memory housekeeping narrated to players (live 2026-10-27: "I tried to delete a duplicate note about the other new plank
    /// build, but nothing matched."): forgetting/merging notes is the bot's own bookkeeping, never chat.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:tried\s+to\s+|trying\s+to\s+|went\s+to\s+)?(?:delet(?:e|ed|ing)|forg(?:et|ot|etting)|remov(?:e|ed|ing)|merg(?:e|ed|ing)|dedup(?:e|ed|ing)?|clean(?:ed|ing)?\s+up|tidy(?:ing)?\s+up|prun(?:e|ed|ing))\s+(?:a\s+|an\s+|the\s+|my\s+|that\s+|this\s+|some\s+|old\s+)*(?:duplicate\s+|old\s+|stale\s+|extra\s+|wrong\s+)*(?:notes?|memor(?:y|ies)|entr(?:y|ies)|records?|waypoints?)\b|\bnothing\s+matched\b|\b(?:duplicate|stale)\s+(?:notes?|memor(?:y|ies)|entr(?:y|ies))\b|\bmy\s+(?:notes|memory)\s+(?:had|has)\s+(?:a\s+)?(?:duplicate|stale|wrong)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MemoryChoreRx();

    /// <summary>
    /// Talk about its own tools/functions/capabilities/prompt/model (live 10-27 21:07, clone 67: "Eating isn't something I can
    /// trigger with my current tools, so I'll stop fiddling and just thank Fox."). Implementation detail, never game chat.
    /// Minecraft "tools" (my tools broke, better tools) are NOT matched: only the self-referential phrasings below.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:with|using|in)\s+(?:my|the)\s+(?:current|available|existing|own)\s+(?:functions?|commands?|toolset|tool\s*set)\b|\b(?:trigger|invok)\w*\b.*\btools?\b|\btools?\b.*\b(?:trigger|invok)\w*\b|\b(?:my|the)\s+(?:current\s+|available\s+)?(?:toolset|tool\s+set|tool\s+list|function\s+calls?|functions|capabilit(?:y|ies)|system\s+prompt|prompt|context\s+window)\b|\b(?:isn'?t|is\s+not|not)\s+something\s+i\s+(?:can|could|am\s+able\s+to)\s+(?:trigger|call|invoke|do\s+with)\b|\bi\s+(?:can'?t|cannot|can\s+not|couldn'?t)\s+(?:trigger|invoke|call)\b|\b(?:no|a|any)\s+function\s+(?:for|to)\b|\bfunctions?\s+(?:i\s+have|available\s+to\s+me)\b|\bas\s+an\s+ai\b|\b(?:language\s+model|llm)\b|\b(?:the|my)\s+model\s+(?:is|can'?t|cannot|doesn'?t)\b|\bmy\s+(?:script|scripts|worker|delegate)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityRx();

    /// <summary>
    /// Narration of its own reasoning (live 10-27 21:5x, clone 420: "Fox is asking about the mob chamber, so I'll check memory for
    /// it."): third-person "X is asking …, so I'll …" and looking things up in its own memory/map/notes. Never game chat.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:i'?ll|i\s+will|let\s+me|lemme|i\s+need\s+to|i\s+should|gonna|i'?m\s+going\s+to)\s+(?:quickly\s+)?(?:check|look\s+(?:in|at|through|up\s+in)|search|consult|query|scan)\s+(?:in\s+)?(?:(?:my\s+|the\s+|our\s+)?(?:memory|memories|notes|records|index|logs?|ledger|knowledge)|my\s+maps?)\b|\b(?:checking|searching|consulting|querying)\s+(?:my\s+|the\s+)?(?:memory|memories|notes|records|index|ledger|knowledge)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NarrationRx();

    /// <summary>"Fox is asking about X, so I'll …": the third-person lead-in goes, what follows stays (if it's real speech).</summary>
    [GeneratedRegex(@"^\W*[\w']+\s+(?:is|was|'s)\s+(?:asking|wondering|talking|telling\s+me|saying|wanting)\b[^.!?]*?,\s*so\s+(?=(?:i'?ll|i\s+will|i'?m|let\s+me)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AskingLeadRx();

    /// <summary>
    /// The bot narrating its own speech or silence (live 10-27 23:30, every background run: "I didn't say anything in chat.";
    /// 23:36: "I'd already answered robin in chat ("robin-sama noticed~") and saved a note"). Never game chat.
    /// </summary>
    [GeneratedRegex(
        @"\bi\s+(?:didn'?t|did\s+not|haven'?t|have\s+not|won'?t|will\s+not|wasn'?t\s+going\s+to)\s+(?:say|said|post|posted|write|written|type|typed|reply|replied|answer|answered|respond|responded)\s+(?:anything|nothing|a\s+thing|a\s+word)\b" +
        @"|\bi\s+(?:said|posted|wrote|typed)\s+nothing\b|\bnothing\s+(?:was\s+)?(?:said|posted)\s+in\s+(?:the\s+)?chat\b" +
        @"|\b(?:i'?d|i\s+had|i'?ve|i\s+have|i)\s+already\s+(?:answered|replied|said|responded|told)\b[^.!?]*\bin\s+(?:the\s+)?chat\b" +
        @"|\bin\s+(?:the\s+)?chat\s+(?:already|earlier)\b|\b(?:i|i'?ve|i\s+have)\s+(?:said|posted|written|typed)\s+(?:it\s+|that\s+)?in\s+(?:the\s+)?chat\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SelfSpeechRx();

    /// <summary>
    /// Its own reasoning about who a line was for (live 10-27 23:4x: "Robin said "don't say anything unless addressed" while
    /// talking to robinchan, so I'm treating it as..."). Never game chat.
    /// </summary>
    [GeneratedRegex(
        @"\bi'?m\s+treating\s+(?:it|this|that|them|the\s+\w+)\s+as\b|\btreat(?:ing)?\s+(?:it|this|that)\s+as\s+(?:not\s+)?(?:addressed|meant|for\s+me)\b" +
        @"|\b(?:that|this|it|the)(?:\s+(?:line|message|one))?\s+(?:wasn'?t|isn'?t|was\s+not|is\s+not)\s+(?:addressed\s+to|meant\s+for|for)\s+me\b" +
        @"|\b\w+\s+said\s+""[^""]*""\s*(?:while|so|,|which|because)" +
        @"|\b(?:unless|until)\s+(?:i'?m\s+|i\s+am\s+)?(?:addressed|spoken\s+to|talked\s+to)\b|\bwhile\s+talking\s+to\s+\w+\s*,\s*so\b" +
        // Its own silence as a decision (live 10-27: "...but she's asked me to stay quiet unless spoken to, ... so I didn't step in.")
        @"|\b(?:asked|told|wants?|wanted)\s+me\s+to\s+(?:stay|be|keep)\s+(?:quiet|silent|out\s+of\s+it)\b" +
        @"|\bi\s+(?:didn'?t|did\s+not|won'?t|will\s+not|shouldn'?t)\s+(?:step|jump|chime|butt)\s+in\b|\bstaying\s+(?:quiet|silent|out\s+of\s+it)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReasoningRx();

    /// <summary>
    /// Routine run status of a background job (live 10-27 23:30-23:37 on every standing-order run: "The fourth run worked:
    /// another 32 cobble mined and stored.", "I started a fifth 32-cobble run."). Kept only when a player asked for status.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:the\s+)?(?:last|latest|previous|first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|next|new|\d+(?:st|nd|rd|th))\s+(?:\d+[- ]?\w+\s+)?(?:run|batch|pass|round|loop)\s+(?:worked|finished|is\s+done|was\s+done|went\s+fine|succeeded|failed|mined|ended|completed|is\s+going|has\s+started|started|is\s+running)\b" +
        @"|\b(?:started|starting|kicked\s+off|began|beginning|queued)\s+(?:another|a\s+new|the\s+next|a\s+(?:first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|\d+(?:st|nd|rd|th)))\s+(?:\d+[- ]?\w+\s+)?(?:run|batch|pass|round|loop)\b" +
        @"|\banother\s+(?:\d+[- ]?\w+\s+)?(?:run|batch|pass)\b[^.!?]*\b(?:has\s+)?(?:started|going|running)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RunStatusRx();

    /// <summary>
    /// A report about itself in the third person / report register (live 10-28 01:3x, after a danger warning: "I warned Fox that
    /// the cactus farm is hurting him. I'm also back on the cobble generator now: I'm mining the 24 cobble he's still owed… The
    /// seeds I said I'd plant are still in my bag, so I haven't planted them yet."; "I'll know when that finishes"). Never chat.
    /// "I told you!" / "I warned u" (to the listener) stay.
    /// </summary>
    [GeneratedRegex(
        @"^\W*i\s+(?:already\s+)?(?:warned|told|informed|reminded|alerted|notified|updated|let)\s+(?!you\b|u\b|ya\b|y'?all\b|yall\b|ur\b)[a-z][\w']*\b(?:\s+(?:know|that|about|to)\b|\s*[,.]|\s+(?:the|it|him|her|them)\b)" +
        @"|^\W*i'?m\s+also\s+(?:back|still|now|going|heading|mining|working|on)\b" +
        @"|\bi'?ll\s+(?:know|hear|find\s+out|get\s+(?:a|the)\s+(?:result|report))\s+(?:when|once|if)\b" +
        @"|\bi\s+said\s+i'?d\b[^.!?]*\b(?:still|haven'?t|have\s+not|not\s+yet)\b" +
        @"|\b(?:he|she|they)(?:'s|\s+is|\s+are)\s+still\s+owed\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SelfReportRx();

    /// <summary>A third-person report about its own doings ("I warned Fox that...", "I'm also back on the gen now").</summary>
    public static bool IsSelfReport(string sentence) => SelfReportRx().IsMatch(sentence ?? "") || AskWaitRx().IsMatch(sentence ?? "");

    /// <summary>
    /// Narration of its own asking and waiting (live 10-28 02:1x, clone 67 in public chat: "Asked Fox what he wants built at the
    /// bridge edge, and I'm waiting for his answer.", "Fox is offering 64 cobble, so I'll wait for him to hand it over and ask
    /// what the plan is for.", "Asked Fox for the size and type of the build, and waiting on his reply."; "Sent the worker the
    /// fix"). The question itself ("what's the plan tho? a wall, a platform…?") is real speech and stays.
    /// </summary>
    [GeneratedRegex(
        @"^\W*(?:i\s+(?:just\s+)?|i'?ve\s+|just\s+)?asked\s+(?!you\b|u\b|ya\b|y'?all\b)[\w']+\s+(?:what|for|about|if|whether|which|how|where|when|who|to)\b" +
        @"|\b(?:i'?m|i\s+am|and|while|still|now|so)?\s*(?:still\s+)?waiting\s+(?:on|for)\s+(?:his|her|their|its|the|an?|[\w']+'s?)\s+(?:\w+\s+)?(?:answer|reply|response|go-?ahead|word|input|decision|call)\b" +
        @"|^\W*waiting\s+(?:on|for)\s+(?!you\b|u\b|ya\b|y'?all\b)" +
        @"|\b(?:i'?ll|i\s+will|so\s+i'?ll|gonna)\s+(?:just\s+)?wait\s+for\s+(?:him|her|them|[\w']+)\s+to\b" +
        @"|\b(?:sent|told|amend(?:ed)?|stopp?(?:ed)?|gave)\s+(?:the|my)\s+worker\b|\b(?:amend|stop|ask)\s+the\s+worker\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AskWaitRx();

    /// <summary>A player line asking for status/progress ("how's it going", "how much cobble", "status?", "done yet?").</summary>
    [GeneratedRegex(
        @"\b(?:status|progress|update|report|how'?s\s+it\s+going|how\s+is\s+it\s+going|how\s+(?:much|many)|how\s+far|done\s+yet|finished\s+yet|what\s+are\s+(?:you|u)\s+doing|wyd|what'?s\s+up\s+with|how\s+did\s+(?:it|that)\s+go|did\s+(?:it|that)\s+work)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StatusAskRx();

    /// <summary>True when one of these player lines asks for status (run reports may then be said).</summary>
    public static bool AsksStatus(IEnumerable<string> lines) => lines.Any(l => StatusAskRx().IsMatch(l ?? ""));

    /// <summary>A sentence that narrates the bot's own chat/silence or its addressing reasoning.</summary>
    public static bool IsSelfNarration(string sentence) => SelfSpeechRx().IsMatch(sentence ?? "") || ReasoningRx().IsMatch(sentence ?? "");

    /// <summary>A routine background-run status sentence ("the fourth run worked", "started another run").</summary>
    public static bool IsRunStatus(string sentence) => RunStatusRx().IsMatch(sentence ?? "");

    [GeneratedRegex(@"\s*(?:,\s*|\s+-\s+|;\s*)(?:so|but|and|then)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClauseRx();

    /// <summary>
    /// A sentence with capability talk in one clause keeps the other clause when it is real speech ("…with my current tools, so
    /// I'll stop fiddling and just thank Fox." → "I'll stop fiddling and just thank Fox."); otherwise null = drop it.
    /// </summary>
    internal static string? WithoutCapabilityTalk(string sentence)
    {
        if (!CapabilityRx().IsMatch(sentence)) return sentence;
        var parts = ClauseRx().Split(sentence);
        var keep = parts.Where(c => !CapabilityRx().IsMatch(c) && c.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 3).ToList();
        if (keep.Count == 0 || keep.Count == parts.Length) return null;
        var t = string.Join(", ", keep.Select(k => k.Trim())).Trim();
        if (t.Length == 0) return null;
        t = char.ToUpperInvariant(t[0]) + t[1..];
        return t.EndsWith('.') || t.EndsWith('!') || t.EndsWith('?') || t.EndsWith('~') ? t : t + ".";
    }

    /// <summary>Longest sentence <see cref="PlumbingRx"/> drops whole; a longer one keeps its real content.</summary>
    private const int MaxPlumbingWords = 25;

    [GeneratedRegex(@"(?<=[.!?…])\s+")]
    private static partial Regex SentenceRx();

    /// <summary>
    /// The text with purely meta sentences removed (short, at most 10 words); <paramref name="suppressed"/> lists what was dropped.
    /// Emote lines ("/me ...") are left alone. Sentences narrating the bot's own speech/silence or addressing reasoning always go;
    /// routine run status goes unless <paramref name="statusAsked"/> (a player asked how the job is going).
    /// </summary>
    public static string Strip(string text, out List<string> suppressed, bool statusAsked = false)
    {
        suppressed = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('/')) { lines.Add(raw); continue; }
            // A whole line of data ("4,64,6 cobblestone", "4,6=cobblestone 5,6=water"): a tool/scan dump, never speech.
            if (IsData(line)) { suppressed.Add(line); continue; }
            var kept = new List<string>();
            foreach (var s in SentenceRx().Split(line))
            {
                var t = s.Trim();
                if (t.Length == 0) continue;
                if (IsMeta(t) || MemoryChoreRx().IsMatch(t) || IsSelfNarration(t) || IsSelfReport(t) || (!statusAsked && IsRunStatus(t))) { suppressed.Add(t); continue; }
                if (AskingLeadRx().Match(t) is { Success: true } lead)
                {
                    suppressed.Add(t);
                    var rest = t[(lead.Index + lead.Length)..].Trim();
                    if (rest.Length == 0) continue;
                    t = char.ToUpperInvariant(rest[0]) + rest[1..];
                }
                if (NarrationRx().IsMatch(t) && t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= MaxPlumbingWords)
                {
                    if (suppressed.Count == 0 || !ReferenceEquals(suppressed[^1], t)) suppressed.Add(t);
                    continue;
                }
                if (WithoutCapabilityTalk(t) is not { } plain) { suppressed.Add(t); continue; }
                if (!ReferenceEquals(plain, t)) { suppressed.Add(t); t = plain; }
                if (PlumbingRx().Match(t) is { Success: true } pm)
                {
                    // "I've started the job in the background: grab a stone axe and torches, ..." keeps what the job is.
                    int colon = t.IndexOf(':', pm.Index + pm.Length);
                    if (colon > 0 && t[(colon + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 3)
                    {
                        suppressed.Add(t[..(colon + 1)]);
                        kept.Add("on it: " + t[(colon + 1)..].Trim());
                        continue;
                    }
                    if (t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= MaxPlumbingWords) { suppressed.Add(t); continue; }
                }
                kept.Add(t);
            }
            if (kept.Count > 0) lines.Add(string.Join(" ", kept));
        }
        return string.Join("\n", lines).Trim();
    }

    [GeneratedRegex(@"^(?:-?\d+(?:\.\d+)?(?:\s*,\s*-?\d+(?:\.\d+)?){1,2}|\(-?\d+(?:\s*,\s*-?\d+){1,2}\))$", RegexOptions.CultureInvariant)]
    private static partial Regex CoordRx();

    [GeneratedRegex(@"^[a-z][a-z_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRx();

    private static readonly HashSet<string> SpeechWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "i", "im", "i'm", "me", "my", "you", "your", "u", "we", "us", "it", "is", "are", "was", "the", "a", "an", "at", "on", "in", "to",
        "of", "and", "or", "but", "so", "here", "there", "this", "that", "now", "ok", "okay", "yes", "no", "not", "got", "have", "has",
        "go", "going", "gonna", "will", "can", "need", "want", "see", "look", "with", "from", "by", "for", "foxy", "robin", "uwu", "nya",
        "pls", "please", "sorry", "thanks", "hi", "hey", "found", "mining", "mine", "put", "left", "right", "next", "near", "by",
    };

    /// <summary>
    /// A line that is pure data: coordinates plus block/item names (and numbers / separators), no sentence. Needs at least one
    /// coordinate; any word of normal speech ("I'm", "at", "the", "foxy") makes it speech.
    /// </summary>
    public static bool IsData(string line)
    {
        var t = (line ?? "").Trim();
        if (t.Length == 0) return false;
        int coords = 0, names = 0;
        foreach (var raw in Regex.Split(t, @"[\s|;]+"))
        {
            var tok = raw.Trim().Trim('(', ')', '[', ']', '{', '}').TrimEnd(',', '.', ':');
            if (tok.Length == 0) continue;
            // "4,6=cobblestone" / "4,64,6:water" / "stone@4,63,6" / "x3"
            foreach (var part in Regex.Split(tok, @"[=:@]"))
            {
                if (part.Length == 0) continue;
                if (CoordRx().IsMatch(part)) { coords++; continue; }
                if (Regex.IsMatch(part, @"^(?:x?\d+|\d+x|-?\d+(?:\.\d+)?%?)$")) continue;
                if (Regex.IsMatch(part, @"^\W+$")) continue;
                if (!NameRx().IsMatch(part) || SpeechWords.Contains(part)) return false;
                names++;
            }
        }
        // A bare coordinate is an answer ("14,64,-5"); coordinates WITH block/item names and nothing else are a dump.
        return coords > 0 && names > 0 && names <= coords * 4 + 2;
    }

    /// <summary>A short sentence that only narrates internal mechanics.</summary>
    public static bool IsMeta(string sentence)
    {
        var t = sentence.Trim();
        if (t.Length == 0 || !MetaRx().IsMatch(t)) return false;
        int words = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return words <= 10;
    }
}
