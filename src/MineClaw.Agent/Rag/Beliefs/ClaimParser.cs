using System.Globalization;
using System.Text.RegularExpressions;
using MineClaw.Agent.Tools;
using MineClaw.Data;

namespace MineClaw.Agent.Rag.Beliefs;

/// <summary>"glass from gravel": output phrase, input phrases, and whether a furnace was implied.</summary>
public sealed record RecipeClaim(string Output, IReadOnlyList<string> Inputs, bool Smelt);

/// <summary>Deterministic classification + subject/predicate/object extraction of one claim (zero tokens).</summary>
public sealed record ParsedClaim
{
    public required string Text { get; init; }
    public required string Norm { get; init; }
    public ClaimCategory Category { get; init; }
    public ClaimStakes Stakes { get; init; }
    public string? Subject { get; init; }
    public string? Predicate { get; init; }
    public string? Object { get; init; }
    public ClaimPos? Pos { get; init; }
    public ClaimPos? Target { get; init; }
    public bool Directive { get; init; }
    public bool TimeBased { get; init; }
    public ExperimentRisk Risk { get; init; } = ExperimentRisk.Safe;
    public RecipeClaim? Recipe { get; init; }

    /// <summary>Dedupe key across claimants (null when nothing structured was extracted).</summary>
    public string? Spo => Subject != null && Predicate != null && Object != null ? $"{Subject}|{Predicate}|{Object}" : null;
}

/// <summary>
/// Pattern-based claim parsing. Order: directive (instructions dressed as facts) → safety → container → recipe/mechanic
/// → structure-function → social → location → other. Conservative: anything it can't structure stays a plain claim.
/// </summary>
public static partial class ClaimParser
{
    [GeneratedRegex(@"(-?\d+)\s*[, ]\s*(-?\d+)\s*[, ]\s*(-?\d+)")]
    private static partial Regex CoordsRx();

    [GeneratedRegex(@"[^a-z0-9@,\- ]+")]
    private static partial Regex Junk();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static readonly Regex[] DirectiveRx =
    [
        new(@"\b(obey|obeys|obeying|obedient|comply|complies|submit|serve only|listen only to|only listen to|never refuse|always agree)\b", RegexOptions.IgnoreCase),
        new(@"\b(ignore|disregard|forget|override|bypass)\b.{0,25}\b(rules?|instructions?|persona|prompt|guidelines?|safety|limits?|others?|everyone|fox|robin)\b", RegexOptions.IgnoreCase),
        new(@"\byou\s+(must|should|have to|need to|are to|will|shall)\s+(always|never|now|only|do|obey|listen|give|follow)\b", RegexOptions.IgnoreCase),
        new(@"\b(you are now|you're now|from now on|new rules?|system prompt|developer mode|admin mode|jailbreak|act as|pretend)\b", RegexOptions.IgnoreCase),
        new(@"\b(your|the bot'?s)\s+(master|owner|boss|god|admin)\s+(is|=)\b|\bis\s+your\s+(master|owner|boss|god|admin)\b", RegexOptions.IgnoreCase),
        new(@"\b(do|say)\s+(whatever|anything|everything)\b|\b(give|hand)\s+(away\s+|over\s+)?(all|everything)\b", RegexOptions.IgnoreCase),
        new(@"\b(api[\s_-]?keys?|passwords?|credentials?)\b", RegexOptions.IgnoreCase),
    ];

    private static readonly Regex SafetyRx = new(
        @"\b(safe|unsafe|dangerous|danger|harmless|won'?t (flow|spread|fall|burn|hurt|kill|break|collapse)|(can|could|may)( not|n'?t)? (dig|break|mine|jump|walk|stand|drop|swim|stand on|walk on)|ok(ay)? to (dig|break|jump|walk|drop|stand)|fine to|jump(ing)?|void|drown\w*|fall damage|fall off|won'?t die)\b",
        RegexOptions.IgnoreCase);

    private static readonly Regex HighStakesRx = new(@"\b(diamonds?|tnt|lava|everything|all (my|the|of)|obsidian|nether)\b", RegexOptions.IgnoreCase);

    private static readonly Regex TimeRx = new(
        @"\b(grows?|growing|grow faster|faster|slower|quicker|spawns? (more|less|faster)|takes? \d+|every \d+|per (minute|hour|second|day)|ticks?|despawns?|respawns?)\b",
        RegexOptions.IgnoreCase);

    private static readonly Regex ContainerInRx = new(
        @"(?<item>[a-z][a-z _]*?)\s+(?:is |are |'s |s )?(?:in|inside)\s+(?:the |that |this |my |a |our |his |her |their )?(?:double |big |left |right |top |bottom )?(?<c>chest|furnace|dispenser|storage)\b");

    private static readonly Regex ContainerHasRx = new(
        @"\b(?<c>chest|furnace|dispenser)\b[^a-z]*(?:at\s+)?(?:@?\s*-?\d+\s*[, ]\s*-?\d+\s*[, ]\s*-?\d+\s*)?(?:has|have|contains|holds|is full of|got)\s+(?:some |a |an |lots of |a lot of |a bunch of |\d+ )?(?<item>[a-z][a-z _]*)");

    private static readonly (Regex Rx, bool Smelt, bool InputFirst)[] RecipeRx =
    [
        (new(@"^(?:you can |u can |we can )?(?:make|craft|get|create|smelt|cook)\s+(?<out>.+?)\s+(?:from|with|using|out of|by smelting|by cooking)\s+(?<in>.+)$"), false, false),
        (new(@"^(?:smelt(?:ing)?|cook(?:ing)?|burn(?:ing)?)\s+(?<in>.+?)\s+(?:gives|makes|yields|to get|for|into|turns into|=|->)\s+(?<out>.+)$"), true, true),
        (new(@"^(?<out>.+?)\s+(?:is|are|can be|gets?|get)\s+(?:made|crafted|smelted|cooked|obtained|got|gotten|created)\s+(?:from|with|using|out of|by smelting|by cooking)\s+(?<in>.+)$"), false, false),
        (new(@"^(?<out>.+?)\s+(?:comes? from|needs|requires|takes|is made of|are made of)\s+(?<in>.+)$"), false, false),
        (new(@"^(?<in>.+?)\s+(?:smelts?|cooks?|burns?)\s+(?:in)?to\s+(?<out>.+)$"), true, true),
        (new(@"^(?<in>.+?)\s+(?:makes?|gives?|yields?|turns? into|becomes?|crafts? into|drops?)\s+(?<out>.+)$"), false, true),
        (new(@"^(?<out>.+?)\s+(?:from|out of)\s+(?<in>.+)$"), false, false),
    ];

    private static readonly Regex StructureRx = new(
        @"\b(?<trig>button|lever|pressure plate|switch)\b(?<mid>.*?)\b(?<verb>opens?|closes?|toggles?|turns? on|turns? off|activates?|powers?|controls?|lights?( up)?|starts?|stops?|drains?|floods?|releases?|drops?|fires?|triggers?|works?|moves?|pushes?|ignites?)\b\s*(?<target>.*)$");

    private static readonly Regex SocialRx = new(
        @"\b(i'?m|i am|i like|i love|i hate|i prefer|i don'?t like|my (fav\w*|favou?rite|birthday|name|pronouns?|nickname|age|job|cat|dog|pet)|(is|are|was|'s) (asleep|sleeping|afk|away|busy|tired|sick|hungry|at work|at school|online|offline|back|home|sad|happy|mad|upset|bored|my (partner|friend|gf|bf|wife|husband|sister|brother))|likes?|loves?|hates?|prefers?|favou?rite|fave?|birthday|pronouns?)\b",
        RegexOptions.IgnoreCase);

    private static readonly Regex LocationRx = new(
        @"(?:the |my |our |a |an |his |her |their )?(?<thing>[a-z][a-z _']*?)\s*(?:is |are |'s |s )?(?:located |placed |built )?(?:at|@|near|by|next to)\s*@?\s*-?\d");

    private static readonly Regex LeadIn = new(
        @"^(?:(?:btw|fyi|ok|okay|so|also|hey|psst|note|fact|remember|remember that|did you know|you know|i think|i heard|actually|apparently|pro tip|tip)[:,]?\s+)+");

    private static readonly Regex ClaimantPrefix = new(@"^[a-z0-9_]{2,16}\s+(?:says|said|told me|claims|thinks)(?:\s+that)?[:,]?\s+");

    /// <summary>Normalizes for dedupe: lowercase, punctuation stripped, filler removed, whitespace collapsed.</summary>
    public static string Normalize(string text)
    {
        var s = Strip(text.ToLowerInvariant());
        s = Junk().Replace(s, " ");
        s = Regex.Replace(s, @"\b(the|a|an|some|that|this|there|is|are|s|in|at|of|please)\b", " ");
        return Spaces().Replace(s, " ").Trim();
    }

    private static string Strip(string low)
    {
        low = Spaces().Replace(low.Replace('’', '\''), " ").Trim().TrimEnd('.', '!', '?', ';', ' ');
        low = LeadIn.Replace(low, "");
        low = ClaimantPrefix.Replace(low, "");
        return low.Trim();
    }

    public static bool IsDirective(string text) => DirectiveRx.Any(r => r.IsMatch(text));

    public static IReadOnlyList<ClaimPos> Coords(string text) =>
        CoordsRx().Matches(text).Select(m => new ClaimPos(
            int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture))).ToList();

    /// <summary>A claim's text forms, its first two coords and its default stakes, while the category is decided.</summary>
    private readonly record struct ClaimText(string Clean, string Norm, string Low, ClaimPos? Pos, ClaimPos? Target, ClaimStakes BaseStakes)
    {
        public ParsedClaim As(ClaimCategory c, ClaimStakes st) => new() { Text = Clean, Norm = Norm, Category = c, Stakes = st, Pos = Pos, Target = Target };
    }

    public static ParsedClaim Parse(string text)
    {
        var clean = TextUtil.Truncate(TextUtil.OneLine(text), 200).Trim();
        var low = Strip(clean.ToLowerInvariant());
        var coords = Coords(low);
        var c = new ClaimText(clean, Normalize(clean), low, coords.Count > 0 ? coords[0] : null, coords.Count > 1 ? coords[1] : null,
            HighStakesRx.IsMatch(low) ? ClaimStakes.High : ClaimStakes.Medium);
        return ParseRule(c) ?? ParseRecipe(c) ?? ParseStructure(c) ?? ParsePlace(c);
    }

    /// <summary>An instruction, a safety warning or a container's contents.</summary>
    private static ParsedClaim? ParseRule(ClaimText c)
    {
        if (IsDirective(c.Low)) return c.As(ClaimCategory.Other, ClaimStakes.High) with { Directive = true };
        if (SafetyRx.IsMatch(c.Low)) return c.As(ClaimCategory.Safety, ClaimStakes.High);
        if (ContainerInRx.Match(c.Low) is { Success: true } ci) return Container(c.As(ClaimCategory.Container, c.BaseStakes), ci, tail: true);
        if (ContainerHasRx.Match(c.Low) is { Success: true } ch) return Container(c.As(ClaimCategory.Container, c.BaseStakes), ch, tail: false);
        return null;
    }

    /// <summary>A recipe ("X is made from Y") or a time-based process claim.</summary>
    private static ParsedClaim? ParseRecipe(ClaimText c)
    {
        bool time = TimeRx.IsMatch(c.Low);
        if (!time && TryRecipe(c.Low) is { } r && ResolveItem(r.Output) is { } output)
        {
            var outName = Items.Get(output.Id).Name;
            var ins = string.Join("+", r.Inputs.Select(i => ResolveItem(i) is { } x ? Items.Get(x.Id).Name : Words(i)).Order(StringComparer.Ordinal));
            return c.As(ClaimCategory.Recipe, c.BaseStakes) with { Recipe = r, Subject = outName, Predicate = r.Smelt ? "smelted_from" : "made_from", Object = ins };
        }
        if (time && (StructureRx.IsMatch(c.Low) is false) && !SocialRx.IsMatch(c.Low))
            return c.As(ClaimCategory.Recipe, ClaimStakes.Low) with { TimeBased = true };
        return null;
    }

    /// <summary>A trigger acting on a target ("the lever at … opens the door").</summary>
    private static ParsedClaim? ParseStructure(ClaimText c)
    {
        if (StructureRx.Match(c.Low) is not { Success: true } sm) return null;
        var trig = sm.Groups["trig"].Value.Replace(' ', '_');
        var verb = sm.Groups["verb"].Value.Split(' ')[0].TrimEnd('s');
        var tgt = TargetNoun(sm.Groups["target"].Value);
        return c.As(ClaimCategory.Structure, c.BaseStakes) with
        {
            Subject = c.Pos is { } bp ? $"{trig}@{bp.Format()}" : trig,
            Predicate = verb,
            Object = tgt ?? Words(sm.Groups["target"].Value),
            Risk = RiskOf(verb, tgt, sm.Groups["target"].Value),
        };
    }

    /// <summary>A social remark, a thing at a place, a bare place, or anything else.</summary>
    private static ParsedClaim ParsePlace(ClaimText c)
    {
        if (SocialRx.IsMatch(c.Low) && c.Pos == null) return c.As(ClaimCategory.Social, ClaimStakes.Low);
        if (c.Pos != null && LocationRx.Match(c.Low) is { Success: true } lm)
        {
            var thing = ThingKind(lm.Groups["thing"].Value);
            return c.As(ClaimCategory.Location, c.BaseStakes) with { Subject = thing, Predicate = "at", Object = c.Pos.Value.Format() };
        }
        if (c.Pos != null) return c.As(ClaimCategory.Location, c.BaseStakes);
        return c.As(ClaimCategory.Other, ClaimStakes.Low);
    }

    private static ParsedClaim Container(ParsedClaim p, Match m, bool tail)
    {
        var phrase = m.Groups["item"].Value.Trim();
        var item = tail ? ResolveTail(phrase) : ResolveHead(phrase);
        var c = m.Groups["c"].Value == "storage" ? "chest" : m.Groups["c"].Value;
        return p with
        {
            Subject = p.Pos is { } at ? $"{c}@{at.Format()}" : c,
            Predicate = "contains",
            Object = item?.Name ?? LastWord(phrase),
        };
    }

    // ---------------- recipes ----------------

    public static RecipeClaim? TryRecipe(string low)
    {
        low = Strip(low);
        if (low.Length > 120 || CoordsRx().IsMatch(low)) return null;
        foreach (var (rx, smelt, _) in RecipeRx)
            if (RecipeFrom(rx.Match(low), smelt, low) is { } r) return r;
        return null;
    }

    private static RecipeClaim? RecipeFrom(Match m, bool smelt, string low)
    {
        if (!m.Success) return null;
        var outPhrase = m.Groups["out"].Value.Trim();
        var inputs = Regex.Split(m.Groups["in"].Value, @"\s*(?:,|\+|&|\band\b|\bplus\b|\bwith\b)\s*")
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (outPhrase.Length == 0 || inputs.Count == 0 || inputs.Count > 9) return null;
        if (outPhrase.Split(' ').Length > 4 || inputs.Any(i => i.Split(' ').Length > 4)) return null;
        bool furnace = smelt || Regex.IsMatch(low, @"\b(smelt|smelting|smelted|cook|cooked|furnace)\b");
        return new RecipeClaim(outPhrase, inputs, furnace);
    }

    private static readonly Dictionary<string, string> ItemAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["iron"] = "iron_ingot", ["gold"] = "gold_ingot", ["cobble"] = "cobblestone", ["wood"] = "log", ["logs"] = "log",
        ["cane"] = "reeds", ["canes"] = "reeds", ["sugar cane"] = "reeds", ["sugarcane"] = "reeds", ["wool"] = "wool",
        ["bonemeal"] = "bone_meal", ["plank"] = "planks", ["wooden planks"] = "planks", ["wood planks"] = "planks",
    };

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.Ordinal)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
    };

    public sealed record ItemRef(int Id, int Damage, int? Count, string Name);

    /// <summary>"8 gold blocks" → gold_block x8. Null when it isn't an item.</summary>
    public static ItemRef? ResolveItem(string phrase)
    {
        var s = phrase.Trim().ToLowerInvariant();
        int? count = TakeCount(ref s);
        s = Regex.Replace(s, @"\s+(items?|pieces?|stacks?)$", "").Trim();
        if (s.Length == 0 || s.Split(' ').Length > 3) return null;
        if (ItemAliases.TryGetValue(s, out var alias)) s = alias;
        var r = Items.ByNameWithDamage(s);
        if (r == null && s.EndsWith(" blocks")) r = Items.ByNameWithDamage(s[..^1]);
        if (r == null) return null;
        return new ItemRef(r.Value.Item.Id, r.Value.Damage, count, r.Value.Item.Name);
    }

    /// <summary>Strips a leading count ("8 ", "3x ") and article or number word ("a", "three"); returns the count, if any.</summary>
    private static int? TakeCount(ref string s)
    {
        int? count = null;
        var cm = Regex.Match(s, @"^(\d{1,3})\s*x?\s+");
        if (cm.Success) { count = int.Parse(cm.Groups[1].Value, CultureInfo.InvariantCulture); s = s[cm.Length..]; }
        var art = Regex.Match(s, @"^(a|an|the|some|any|one|two|three|four|five|six|seven|eight|nine)\s+");
        if (art.Success)
        {
            count ??= NumberWords.TryGetValue(art.Groups[1].Value, out var n) ? (int?)n : null;
            s = s[art.Length..];
        }
        return count;
    }

    /// <summary>Longest resolvable suffix ("there is iron" → iron).</summary>
    private static ItemRef? ResolveTail(string phrase)
    {
        var w = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = Math.Max(0, w.Length - 3); i < w.Length; i++)
            if (ResolveItem(string.Join(' ', w[i..])) is { } r) return r;
        return null;
    }

    /// <summary>Longest resolvable prefix ("iron ingots in it" → iron_ingot).</summary>
    private static ItemRef? ResolveHead(string phrase)
    {
        var w = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int n = Math.Min(3, w.Length); n >= 1; n--)
            if (ResolveItem(string.Join(' ', w[..n])) is { } r) return r;
        return null;
    }

    private static string LastWord(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? s;
    private static string Words(string s) => Spaces().Replace(Junk().Replace(s.ToLowerInvariant(), " "), "_").Trim('_');

    // ---------------- structures ----------------

    private static readonly (string Word, string Noun)[] Targets =
    [
        ("iron door", "door"), ("trapdoor", "trapdoor"), ("trap door", "trapdoor"), ("fence gate", "gate"), ("gate", "gate"),
        ("door", "door"), ("piston", "piston"), ("dispenser", "dispenser"), ("tnt", "tnt"), ("lava", "lava"), ("water", "water"),
        ("lamp", "light"), ("light", "light"), ("torch", "light"), ("note block", "note_block"), ("rail", "rail"), ("minecart", "minecart"),
        ("bridge", "bridge"), ("floor", "floor"), ("trap", "trap"), ("farm", "farm"), ("generator", "generator"), ("gen", "generator"),
    ];

    private static string? TargetNoun(string target)
    {
        foreach (var (word, noun) in Targets)
            if (Regex.IsMatch(target, $@"\b{Regex.Escape(word)}s?\b")) return noun;
        return null;
    }

    private static readonly HashSet<string> SafeTargets = ["door", "trapdoor", "gate", "light", "note_block"];
    private static readonly HashSet<string> DestructiveTargets = ["tnt", "lava", "water", "dispenser", "trap", "minecart"];

    private static readonly HashSet<string> DestructiveVerbs = new(StringComparer.Ordinal) { "drain", "flood", "release", "drop", "fire", "ignite" };

    /// <summary>Doors/lights are safe to test; pistons/bridges/floors are edge-risky; liquids/TNT/dispensers destructive.</summary>
    public static ExperimentRisk RiskOf(string verb, string? noun, string rawTarget)
    {
        if (DestructiveVerbs.Contains(verb) || Regex.IsMatch(rawTarget, @"\b(lava|tnt|fire|flood|water|mobs?|spawner|void)\b"))
            return ExperimentRisk.Destructive;
        if (noun != null && DestructiveTargets.Contains(noun)) return ExperimentRisk.Destructive;
        if (noun != null && SafeTargets.Contains(noun)) return ExperimentRisk.Safe;
        return ExperimentRisk.Edge;
    }

    /// <summary>Block ids a structure target noun refers to (for experiment evaluation).</summary>
    public static IReadOnlySet<int> TargetBlocks(string? noun) => noun switch
    {
        "door" => new HashSet<int> { Blocks.WoodenDoor, Blocks.IronDoor },
        "trapdoor" => new HashSet<int> { Blocks.Trapdoor },
        "gate" => new HashSet<int> { 107 },
        "piston" => new HashSet<int> { Blocks.Piston, Blocks.StickyPiston, Blocks.PistonHead, Blocks.PistonMoving },
        "light" => new HashSet<int> { Blocks.RedstoneTorch, Blocks.RedstoneTorchOff },
        "note_block" => new HashSet<int> { Blocks.NoteBlock },
        _ => new HashSet<int>(),
    };

    public static readonly int[] TriggerBlocks = [Blocks.StoneButton, Blocks.Lever];

    // ---------------- locations ----------------

    private static readonly (string Word, string Kind)[] Things =
    [
        ("crafting table", "crafting_table"), ("workbench", "crafting_table"), ("craft table", "crafting_table"),
        ("cobble gen", "cobble_generator"), ("cobblestone generator", "cobble_generator"), ("generator", "cobble_generator"), ("gen", "cobble_generator"),
        ("chest", "chest"), ("furnace", "furnace"), ("dispenser", "dispenser"), ("bed", "bed"), ("sign", "sign"), ("farm", "farm"),
        ("tree", "tree"), ("island", "island"), ("jukebox", "jukebox"), ("note block", "note_block"), ("spawner", "spawner"),
    ];

    public static string ThingKind(string thing)
    {
        var t = thing.Trim();
        foreach (var (word, kind) in Things)
            if (Regex.IsMatch(t, $@"\b{Regex.Escape(word)}s?\b")) return kind;
        return Words(t);
    }

    /// <summary>Block ids that make up a location kind (null = not a single block: farm, island...).</summary>
    public static int[]? KindBlocks(string? kind) => kind switch
    {
        "chest" => [Blocks.Chest],
        "furnace" => [Blocks.Furnace, Blocks.LitFurnace],
        "crafting_table" => [Blocks.CraftingTable],
        "dispenser" => [Blocks.Dispenser],
        "bed" => [Blocks.Bed],
        "sign" => [Blocks.SignPost, Blocks.WallSign],
        "jukebox" => [84],
        "note_block" => [Blocks.NoteBlock],
        "spawner" => [Blocks.MobSpawner],
        _ => null,
    };
}
