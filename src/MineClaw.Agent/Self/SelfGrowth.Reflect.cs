using System.Text;

namespace MineClaw.Agent.Self;

/// <summary>Result of checking that a proposed personality change is backed by real signals.</summary>
public sealed record Grounding(bool Ok, string Why, IReadOnlyList<SelfSignal> Evidence)
{
    public string Refs => string.Join(",", Evidence.Select(s => s.Ref));
}

public sealed partial class SelfGrowth
{
    /// <summary>
    /// The aggregates as reflection input, each value followed by the ids of the signals behind it (s12 s40), so proposals can
    /// cite them. Bounded; no raw chat. Directives (players telling the bot what to become) are never listed.
    /// </summary>
    public string DescribeForReflection(int maxChars = 1600)
    {
        var sb = new StringBuilder();
        var m = Mood();
        sb.Append($"mood: cheer {m.Cheer:0.00} ({SignalIds("mood:cheer")}), confidence {m.Confidence:0.00} ({SignalIds("mood:confidence")}), " +
                  $"energy {m.Energy:0.00}, affection {m.Affection:0.00} ({SignalIds("mood:affection")})\n");
        var people = KnownPlayers(6);
        if (people.Count > 0) sb.Append("people:\n");
        foreach (var p in people) sb.Append(Tools.TextUtil.Truncate(PersonReflection(p), 330)).Append('\n');
        var prefs = Preferences().Where(p => p.Done >= 1).Take(6).ToList();
        if (prefs.Count > 0) sb.Append("activities:\n");
        foreach (var p in prefs)
            sb.Append($"- {p.Subject}: done {p.Done:0.#}, ok {p.Ok:0.#}, failed {p.Fail:0.#}, praised {p.Praise:0.##} -> like {p.Like:0.00}" +
                      $"{(p.IsPro ? ", mastered" : "")} ({SignalIds($"pref:{p.Subject}:done", 2)} {SignalIds($"pref:{p.Subject}:praise", 2)})\n");
        var firsts = Store.Recent(Scope, null, 60).Where(s => s.Kind is SignalKinds.FirstTime or SignalKinds.Death or SignalKinds.SparWin
            or SignalKinds.Catchphrase or SignalKinds.NearFall).Take(8).ToList();
        if (firsts.Count > 0) sb.Append("moments: ").Append(string.Join("; ", firsts.Select(s => $"s{s.Id} {s.Note ?? s.Kind}"))).Append('\n');
        var text = sb.ToString().TrimEnd();
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    /// <summary>The ids of the signals behind an aggregate ("s12 s40").</summary>
    private string SignalIds(string what, int n = 3) => string.Join(" ", Explain(what, n).Select(c => "s" + c.SignalId));

    private string PersonReflection(string p)
    {
        var r = Relationship(p);
        var line = new StringBuilder($"- {p} [{(r.Regular ? "regular" : "not a regular, weighs less")}, {r.Episodes} eps]: ");
        line.Append($"affinity {r.Affinity:0.00} ({SignalIds($"rel:{p}:affinity")}), playful {r.Playful:0.00} ({SignalIds($"rel:{p}:playful", 2)}), trust {r.Trust:0.00}");
        var treats = r.Treatment.Where(t => t.Value >= 0.3).OrderByDescending(t => t.Value).Take(3).ToList();
        if (treats.Count > 0) line.Append("; treats you: " + string.Join(", ", treats.Select(t => $"{t.Key} {t.Value:0.0} ({SignalIds($"rel:{p}:t_{t.Key}", 2)})")));
        if (r.TopRequest is { } q) line.Append($"; asks you for {q} ({SignalIds($"rel:{p}:req:{q}", 2)})");
        foreach (var s in Store.Recent(Scope, null, 2, p, SignalKinds.Nickname)) line.Append($"; calls you \"{s.Subject}\" (s{s.Id})");
        foreach (var s in Store.Recent(Scope, null, 2, p, SignalKinds.InsideJoke)) line.Append($"; joke \"{s.Subject}\" (s{s.Id})");
        return line.ToString();
    }

    /// <summary>
    /// A reflection change must be grounded: add/adjust/reinforce cite at least one real signal of this scope (directives
    /// never count). Person entries cite that person's signals. New traits/likes/dislikes/opinions/jokes need evidence from
    /// several distinct episodes and enough weight (strangers count less), so one chat can't create a trait.
    /// </summary>
    public Grounding Ground(SelfChange c, IReadOnlyList<long> cited)
    {
        var op = c.Op.Trim().ToLowerInvariant();
        if (op == "remove") return new(true, "ok", []);
        var ev = Store.ByIds(cited).Where(s => s.Scope == Scope && s.Kind != SignalKinds.Directive).ToList();
        if (ev.Count == 0) return new(false, "cites no valid signal ids", []);
        if (c.Section is SelfSection.Relationship or SelfSection.Nickname && c.Player != null)
        {
            ev = ev.Where(s => string.Equals(s.Player, c.Player, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ev.Count == 0) return new(false, $"no cited signal involves {c.Player}", []);
        }
        return NewEntryRefusal(op, c, ev) ?? new(true, "ok", ev);
    }

    /// <summary>A new trait/like/dislike/opinion/joke without evidence from enough episodes and weight, else null.</summary>
    private Grounding? NewEntryRefusal(string op, SelfChange c, List<SelfSignal> ev)
    {
        if (op != "add" || c.Section is not (SelfSection.Trait or SelfSection.Like or SelfSection.Dislike or SelfSection.Opinion or SelfSection.Joke))
            return null;
        int episodes = ev.Select(s => s.Episode).Distinct().Count();
        double weight = ev.Sum(s => s.Weight);
        if (episodes < Math.Max(1, _o.MinEvidenceEpisodes))
            return new(false, $"needs more observations (signals from {episodes} episode(s), {_o.MinEvidenceEpisodes} required)", ev);
        if (weight < _o.MinEvidenceWeight) return new(false, "evidence too weak (mostly non-regulars)", ev);
        return null;
    }
}
