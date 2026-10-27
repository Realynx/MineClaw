using System.Text.RegularExpressions;

namespace MineClaw.Agent.Brainstem;

/// <summary>What a chat line asks for, as far as deterministic C# can tell.</summary>
public enum IntentKind
{
    None,
    // ---- reflexes: the WHOLE line is the command (zero LLM)
    Stop,
    Come,
    Follow,
    Gesture,
    LookAtMe,
    Give,
    // ---- speculative first steps: a clear job; the brain still plans it
    SpecGenerator,
    SpecTree,
    SpecToSpeaker,
    SpecBring,
    SpecPlace,
}

/// <summary>A matched intent. <see cref="Arg"/>: gesture move, item, place; <see cref="Count"/>: give count (null = default).</summary>
public sealed record Intent(IntentKind Kind, string? Arg = null, int? Count = null, bool All = false)
{
    public static readonly Intent None = new(IntentKind.None);
    public bool IsReflex => Kind is IntentKind.Stop or IntentKind.Come or IntentKind.Follow or IntentKind.Gesture or IntentKind.LookAtMe or IntentKind.Give;
    public bool IsSpeculative => Kind is IntentKind.SpecGenerator or IntentKind.SpecTree or IntentKind.SpecToSpeaker or IntentKind.SpecBring or IntentKind.SpecPlace;

    /// <summary>Config name of a reflex ("stop", "come", "follow", "gesture", "look", "give").</summary>
    public string ReflexName => Kind switch
    {
        IntentKind.Stop => "stop", IntentKind.Come => "come", IntentKind.Follow => "follow", IntentKind.Gesture => "gesture",
        IntentKind.LookAtMe => "look", IntentKind.Give => "give", _ => "",
    };
}

/// <summary>
/// Conservative, deterministic intent matching on one player's chat line (C#, zero tokens).
/// <para><b>Reflexes</b> fire only when the whole line — after stripping the bot's name, "please", "ok", punctuation — is one
/// of a few exact command shapes. Anything else (extra words, a question, a negation, an unknown item) goes to the brain:
/// "wait that's the bridge", "don't stop", "give me a hand", "jump in the hole", "can I come?" never fire.</para>
/// <para><b>Speculative</b> intents are clear jobs inside a longer line ("grab some cobble and come here"): the brainstem may
/// start walking toward the obvious place while the brain plans. Questions about how/why and negations never speculate.</para>
/// </summary>
public static class IntentMatcher
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex Filler = new(@"^(?:(?:ok(?:ay)?|hey|yo|pls|plz|please|now|quick(?:ly)?|real quick|right now|for me|just)\s+)+|(?:\s+(?:pls|plz|please|now|right now|for me|thanks|thx|ty|real quick|quick(?:ly)?))+$", O);

    private static readonly Regex StopRx = new(@"^(?:stop|halt|freeze|hold on|hold up|wait|wait up|wait for me|stop it|stop that|stop moving|stop walking|stop following(?: me)?|stop mining|stop digging|stop chopping|cancel(?: that| it)?|stop everything|everyone stop|both of you stop|all stop)$", O);
    /// <summary>"oh my god both of you stop" / "ok guys stop": a short exclamation that ENDS in stop (no negation/question).</summary>
    private static readonly Regex TrailingStopRx = new(@"^(?:[a-z']+ ){1,6}stop(?: it| now| that)?$", O);
    private static readonly Regex NotAStop = new(@"\b(?:don'?t|dont|do not|never|can'?t|won'?t|wouldn'?t|shouldn'?t|didn'?t|not|no need|bus|pit|full|door|non|where'?s?|what'?s?|why|how|when|who|which)\b", O);
    private static readonly Regex ComeRx = new(@"^(?:come|come here|come to me|come over|come over here|get over here|over here|come back|come back here)$", O);
    private static readonly Regex FollowRx = new(@"^(?:follow me|follow|come with me|follow along)$", O);
    private static readonly Regex GestureRx = new(@"^(?:(?<m>jump|spin|wave|crouch|sneak|nod|dance)(?: (?:again|around|once|twice))?|do a (?<m>jump|spin|wave|dance)|(?<m>wave) (?:hi|hello|at me))$", O);
    private static readonly Regex LookRx = new(@"^(?:look at me|look here|look over here|face me|eyes on me)$", O);
    private static readonly Regex GiveRx = new(@"^(?:(?:give|toss|throw|pass|hand)(?: me)?|drop(?: me)?)(?: (?<n>\d{1,3}|all (?:of your|of the|your|the)|a stack of|a few|all|an|a|one|some|your|the))? (?<item>[a-z][a-z_ ]{1,30}?)(?: (?<n2>\d{1,3}))?$", O);

    private static readonly Regex Negation = new(@"\b(?:don'?t|do not|dont|never|no need|not now|nvm|never ?mind|stop|cancel|forget it)\b", O);
    private static readonly Regex QuestionStart = new(@"^(?:how|why|what|where|when|who|which|is|are|was|were|do|does|did|should|shall|would|could (?:i|we)|can (?:i|we)|may i|have you|has)\b", O);
    private static readonly Regex GenRx = new(@"\b(?:mine|get|grab|gather|farm|harvest|collect|fetch|work|run|dig)\b.{0,30}\b(?:cobble ?stone|cobble|stone|(?:cobble )?gen(?:erator)?)\b", O);
    private static readonly Regex TreeRx = new(@"\b(?:chop|cut|get|grab|gather|harvest|collect|fetch|punch)\b.{0,25}\b(?:wood|logs?|trees?|planks?)\b", O);
    private static readonly Regex ComeAnyRx = new(@"\b(?:come (?:here|to me|over(?: here)?|back(?: here)?)|get over here|follow me|come with me)\b", O);
    private static readonly Regex BringRx = new(@"\b(?:bring|fetch|get|grab|give) me (?:(?:\d{1,3}|a|an|some|the|your|a stack of|a few|more) )?(?<item>[a-z][a-z_]{2,20}(?: [a-z_]{3,15})?)", O);
    private static readonly Regex PlaceRx = new(@"\bgo (?:back )?(?:to|over to|stand (?:at|by)) (?:the |my |our |your )?(?<place>[a-z0-9_][a-z0-9_ ]{1,30})$", O);

    /// <summary>Words that make a line a plan, not a simple command (multi-step, long-running, building, teaching, hive work).</summary>
    private static readonly Regex Planning = new(@"\b(?:then|after(?:wards)?|all night|until|build|expand|extend|design|plan|routine|script|teach|learn|clone|clones|hive|both|everyone|each|organi[sz]e|sort|craft|smelt|cook|bridge|farm)\b|[,;]", O);
    private static readonly Regex ActionWord = new(@"\b(?:mine|dig|build|craft|smelt|cook|fetch|bring|collect|gather|follow|place|plant|farm|chop|kill|deposit|store|expand|extend|go|come|get|grab|give|drop|make|put|move|take|harvest|work)\b", O);

    /// <summary>Normalizes a line: lower case, bot name/aliases and filler stripped, punctuation trimmed, spaces collapsed.</summary>
    public static string Normalize(string text, IEnumerable<string>? names = null)
    {
        var s = (text ?? "").Trim().ToLowerInvariant();
        s = Regex.Replace(s, @"[!.~]+$", "").Trim();
        foreach (var n in (names ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).OrderByDescending(n => n.Length))
        {
            var nm = Regex.Escape(n.Trim().ToLowerInvariant());
            s = Regex.Replace(s, $@"^@?{nm}\b[\s,:]*|[\s,]*\b{nm}$", "", O).Trim();
        }
        s = Regex.Replace(s, @"\s+", " ");
        for (int i = 0; i < 3; i++)
        {
            var t = Filler.Replace(s, "").Trim();
            if (t == s) break;
            s = t;
        }
        return s.Trim(' ', ',', '!', '.', '~');
    }

    /// <summary>
    /// The reflex this line is, or <see cref="Intent.None"/>. Only exact command shapes; a "?" or any extra word = None.
    /// <paramref name="isItem"/> confirms a give/drop item name (null = never match give).
    /// </summary>
    public static Intent Reflex(string text, IEnumerable<string>? names = null, Func<string, bool>? isItem = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains('?')) return Intent.None;
        var s = Normalize(text, names);
        if (s.Length == 0 || s.Length > 48) return Intent.None;
        if (StopRx.IsMatch(s)) return new Intent(IntentKind.Stop);
        if (TrailingStopRx.IsMatch(s) && !NotAStop.IsMatch(s)) return new Intent(IntentKind.Stop);
        if (ComeRx.IsMatch(s)) return new Intent(IntentKind.Come);
        if (FollowRx.IsMatch(s)) return new Intent(IntentKind.Follow);
        if (LookRx.IsMatch(s)) return new Intent(IntentKind.LookAtMe);
        if (GestureRx.Match(s) is { Success: true } g)
        {
            var m = g.Groups["m"].Value.ToLowerInvariant() switch { "sneak" => "crouch", "nod" => "look", "dance" => "spin", var x => x };
            return new Intent(IntentKind.Gesture, m);
        }
        if (isItem != null && GiveRx.Match(s) is { Success: true } gm)
        {
            var item = gm.Groups["item"].Value.Trim();
            var n = gm.Groups["n"].Success ? gm.Groups["n"].Value : gm.Groups["n2"].Success ? gm.Groups["n2"].Value : null;
            // "give me a hand", "drop it", "give me some space": not an item we hold → the brain decides.
            if (item is "it" or "that" or "this" or "them" or "those" or "everything" or "stuff" or "all" || !isItem(item)) return Intent.None;
            bool all = n != null && n.StartsWith("all", StringComparison.Ordinal);
            int? count = n != null && int.TryParse(n, out var k) ? Math.Clamp(k, 1, 256) : n is "a" or "an" or "one" ? 1 : null;
            if (n is "some" or "a few" or "your" or "the" or "a stack of") count = n == "a stack of" ? 64 : null;
            return new Intent(IntentKind.Give, item, count, all);
        }
        return Intent.None;
    }

    /// <summary>
    /// A clear job worth a speculative first step (walk only), or None. Questions about how/what, negations and
    /// "not to me" lines never speculate.
    /// </summary>
    public static Intent Speculative(string text, IEnumerable<string>? names = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return Intent.None;
        var s = Normalize(text, names);
        if (s.Length == 0 || Negation.IsMatch(s) || QuestionStart.IsMatch(s)) return Intent.None;
        if (ComeAnyRx.IsMatch(s)) return new Intent(IntentKind.SpecToSpeaker); // "grab cobble and come here": the player first
        if (BringRx.Match(s) is { Success: true } b) return new Intent(IntentKind.SpecBring, b.Groups["item"].Value.Trim());
        if (GenRx.IsMatch(s)) return new Intent(IntentKind.SpecGenerator);
        if (TreeRx.IsMatch(s)) return new Intent(IntentKind.SpecTree);
        if (PlaceRx.Match(s) is { Success: true } p) return new Intent(IntentKind.SpecPlace, p.Groups["place"].Value.Trim());
        return Intent.None;
    }

    /// <summary>
    /// True when a chat batch needs only a quick reaction: a reflex, one clear short job, or short small talk — no planning
    /// words, no multi-step markers. Decides the cheaper effort for the turn.
    /// </summary>
    public static bool IsSimple(IReadOnlyList<string> lines, IEnumerable<string>? names = null)
    {
        if (lines.Count == 0 || lines.Count > 2) return false;
        var all = string.Join(" ", lines.Select(l => Normalize(l, names)));
        int words = all.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words == 0 || words > 12 || Planning.IsMatch(all)) return false;
        if (lines.All(l => Reflex(l, names, _ => true).IsReflex)) return true;
        if (Speculative(all).IsSpeculative && words <= 10) return true;
        return !ActionWord.IsMatch(all) && words <= 10; // small talk
    }
}
