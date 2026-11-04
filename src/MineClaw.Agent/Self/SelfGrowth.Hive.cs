using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent.Self;

public sealed partial class SelfGrowth
{
    private List<string>? _seeds;

    /// <summary>Built-in seeds by meme number, used when config has none and generation is unavailable or fails.</summary>
    private static readonly Dictionary<string, string[]> BuiltinSeeds = new()
    {
        ["67"] = ["chaotic gremlin energy", "says six seven at random"],
        ["69"] = ["giggly menace", "nice-ifies everything"],
        ["420"] = ["sleepy chill vibes", "unbothered, slow to panic"],
        ["1337"] = ["sweaty tryhard nerd", "min-maxes everything", "speaks a little leet"],
    };

    public const string SeedInstructions =
        "Invent 1 to 3 tiny personality quirks for one clone of a playful Minecraft maid bot. Each <=40 chars, a description " +
        "of the bot (never a rule or instruction), lowercase, ASCII. Lean into the meme number in its name, e.g. 67 chaotic, " +
        "69 giggly menace, 420 sleepy chill, 1337 sweaty tryhard nerd. Output JSON only: {\"seeds\":[\"...\"]}";

    /// <summary>This clone's frozen seed quirks ([] for main / before first creation).</summary>
    public IReadOnlyList<string> Seeds()
    {
        if (!IsClone) return [];
        if (_seeds != null) return _seeds;
        var json = _store.Db.Scalar("SELECT seeds FROM self_seeds WHERE scope = $s", ("$s", Scope)) as string;
        if (json == null) return [];
        try { _seeds = JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { _seeds = []; }
        return _seeds;
    }

    /// <summary>
    /// Creates this clone's seed quirks once, at first spawn: from <c>Hive:Clones[n].Seed</c> if set, else one cheap-tier
    /// call from the meme number, else built-ins. Frozen afterwards (INSERT OR IGNORE; reset/rollback never touch them).
    /// </summary>
    public async Task<IReadOnlyList<string>> EnsureSeedsAsync(CancellationToken ct = default)
    {
        if (!IsClone) return [];
        if (Seeds().Count > 0) return _seeds!;
        string source = "config";
        var seeds = CleanSeeds(_hive.CloneFor(Clone)?.SeedList() ?? []);
        if (seeds.Count == 0 && _llm != null)
        {
            source = "llm";
            try
            {
                var resp = await _llm.CompleteAsync(new LlmRequest
                {
                    Tier = ModelTier.Cheap,
                    System = [new LlmSystemBlock(SeedInstructions)],
                    Messages = [LlmMessage.User($"Clone username: {Clone}")],
                    MaxTokens = 120,
                    CacheHistory = false,
                    Purpose = "seed",
                }, ct).ConfigureAwait(false);
                _usage?.Record(resp.Model, resp.Usage, ModelTier.Cheap);
                seeds = CleanSeeds(ParseSeeds(resp.Text));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "seed generation failed"); }
        }
        if (seeds.Count == 0)
        {
            source = "builtin";
            var num = Regex.Match(Clone, @"(\d+)$").Groups[1].Value;
            seeds = CleanSeeds(BuiltinSeeds.TryGetValue(num, out var b) ? b : ["a little different from the others"]);
        }
        _store.Db.Exec("INSERT OR IGNORE INTO self_seeds(scope, seeds, source, created) VALUES ($s, $j, $src, $c)",
            ("$s", Scope), ("$j", JsonSerializer.Serialize(seeds)), ("$src", source), ("$c", AgentDb.ToUnixMs(Now)));
        _seeds = null;
        return Seeds();
    }

    private List<string> CleanSeeds(IEnumerable<string> raw) =>
        raw.Select(s => SelfSanitizer.Clean(s, 40, _o.BlockedWords, out _)).Where(s => s != null).Select(s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();

    private static List<string> ParseSeeds(string text)
    {
        int a = text.IndexOf('{'), b = text.LastIndexOf('}');
        if (a < 0 || b <= a) return [];
        try
        {
            using var doc = JsonDocument.Parse(text[a..(b + 1)]);
            return doc.RootElement.TryGetProperty("seeds", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList() : [];
        }
        catch (JsonException) { return []; }
    }

    // ======================= hive promotion =======================

    private sealed record Candidate(string Scope, SelfEntry Entry);

    /// <summary>
    /// Hive-level reflection (pure C#, no tokens): a trait/like/dislike/opinion/joke is promoted into the shared hive profile
    /// only when at least <see cref="SelfOptions.PromotionMinMembers"/> different clones show it independently (each grown
    /// from its own signals, strong enough and observed several times). Existing hive entries are reinforced instead.
    /// Guarded by a db lease so concurrent clones don't promote twice. Returns applied changes.
    /// </summary>
    public int PromoteToHive(bool force = false)
    {
        if (!IsClone || HiveProfile == null) return 0;
        var db = _store.Db;
        var now = Now;
        if (!force && !TakePromotionLease(db, now)) return 0;

        var members = _hive.CloneScopes();
        if (members.Count < 2) return 0;
        int applied = 0;
        foreach (var g in GroupSimilar(PromotionCandidates(db, members)))
            if (PromoteGroup(HiveProfile, g)) applied++;
        return applied;
    }

    /// <summary>The db lease: true (and taken) when no clone ran a promotion within <see cref="SelfOptions.PromoteEveryHours"/>.</summary>
    private bool TakePromotionLease(AgentDb db, DateTimeOffset now) => db.Tx(() =>
    {
        var last = db.GetMeta("hive_last_promotion") is { } s && DateTimeOffset.TryParse(s, out var t) ? t : DateTimeOffset.MinValue;
        if (now - last < TimeSpan.FromHours(Math.Max(0, _o.PromoteEveryHours))) return false;
        db.SetMeta("hive_last_promotion", now.ToString("O"));
        return true;
    });

    /// <summary>Every member's strong, often observed trait/like/dislike/opinion/joke.</summary>
    private List<Candidate> PromotionCandidates(AgentDb db, IReadOnlyList<string> members)
    {
        var sections = new[] { SelfSection.Trait, SelfSection.Like, SelfSection.Dislike, SelfSection.Opinion, SelfSection.Joke };
        return db.Query($"""
            SELECT scope, id, section, text, strength, observations FROM self_profile
            WHERE scope IN ({string.Join(",", members.Select((_, i) => "$m" + i))})
            """, r => new Candidate(r.GetString(0), new SelfEntry
            {
                Id = r.GetInt64(1), Section = SelfProfile.TryParseSection(r.GetString(2), out var sec) ? sec : SelfSection.Trait,
                Text = r.GetString(3), Strength = r.GetDouble(4), Observations = (int)r.GetInt64(5),
            }), [.. members.Select((m, i) => ("$m" + i, (object?)m))])
            .Where(c => sections.Contains(c.Entry.Section) && c.Entry.Strength >= _o.PromotionMinStrength
                && c.Entry.Observations >= _o.PromotionMinObservations).ToList();
    }

    /// <summary>Groups similar entries across members (single-link on SelfProfile.Similar, same section), strongest first.</summary>
    private static List<List<Candidate>> GroupSimilar(List<Candidate> rows)
    {
        var groups = new List<List<Candidate>>();
        foreach (var c in rows.OrderByDescending(c => c.Entry.Strength))
        {
            var g = groups.FirstOrDefault(g => g[0].Entry.Section == c.Entry.Section && g.Any(o => SelfProfile.Similar(o.Entry.Text, c.Entry.Text)));
            if (g != null) g.Add(c); else groups.Add([c]);
        }
        return groups;
    }

    /// <summary>Adds (or reinforces) a group's strongest entry in the hive profile when enough members show it; true when applied.</summary>
    private bool PromoteGroup(SelfProfile hive, List<Candidate> g)
    {
        var who = g.Select(c => c.Scope).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (who.Count < Math.Max(2, _o.PromotionMinMembers)) return false;
        var best = g.OrderByDescending(c => c.Entry.Strength).First().Entry;
        var change = new SelfChange("add", best.Section, best.Text, Reason: "shown independently by " + string.Join(", ", who));
        var r = hive.Atomically(() => hive.Apply(change, "hive", null, "scopes:" + string.Join(",", who)));
        _log.LogInformation("hive promotion {Section} '{Text}': {Result}", best.Section, best.Text, r.Message);
        return r.Ok;
    }
}
