namespace MineClaw.Agent.Self;

/// <summary>Short-term mood, each dimension in [-1, 1] (0 = neutral baseline it relaxes back to over hours).</summary>
public sealed record MoodState(double Cheer, double Confidence, double Energy, double Affection)
{
    public static readonly MoodState Neutral = new(0, 0, 0, 0);

    /// <summary>A few words ("giddy, kinda sleepy"), "" when neutral. Quantized so it rarely changes turn to turn.</summary>
    public string Words()
    {
        var w = new List<string>();
        Bands.AddFirst(w, Cheer, CheerBands);
        Bands.AddFirst(w, Confidence, ConfidenceBands);
        Bands.AddFirst(w, Energy, EnergyBands);
        Bands.AddFirst(w, Affection, AffectionBands);
        return string.Join(", ", w);
    }

    private static readonly Bands.Band[] CheerBands =
        [new(0.45, true, "giddy"), new(0.18, true, "cheery"), new(-0.45, false, "pretty down"), new(-0.18, false, "a bit down")];
    private static readonly Bands.Band[] ConfidenceBands =
        [new(0.45, true, "cocky"), new(0.2, true, "confident"), new(-0.4, false, "shaken"), new(-0.2, false, "unsure of yourself")];
    private static readonly Bands.Band[] EnergyBands = [new(-0.4, false, "sleepy"), new(-0.2, false, "kinda tired")];
    private static readonly Bands.Band[] AffectionBands = [new(0.4, true, "clingy"), new(0.2, true, "feeling loved")];
}

/// <summary>Threshold bands for the self model's words: the first band a value reaches names it.</summary>
internal static class Bands
{
    /// <param name="Above">True: the value is at least <paramref name="At"/>; false: at most.</param>
    internal readonly record struct Band(double At, bool Above, string Word);

    /// <summary>Adds the word of the first band <paramref name="value"/> falls in (none when it falls in no band).</summary>
    public static void AddFirst(List<string> words, double value, Band[] bands)
    {
        foreach (var b in bands)
        {
            if (b.Above ? value >= b.At : value <= b.At)
            {
                words.Add(b.Word);
                return;
            }
        }
    }
}

/// <summary>How the bot relates to one player. All values bounded; every one explainable by its signals (SelfGrowth.Explain).</summary>
public sealed record RelationshipState(string Player)
{
    /// <summary>[-0.5 floor, 1]: warmth toward them. Below 0 = wary (never hostile).</summary>
    public double Affinity { get; init; }
    /// <summary>[-0.5 floor, 1]: how much their claims are believed (belief system feeds claim_outcome).</summary>
    public double Trust { get; init; }
    /// <summary>[0, 1]: banter level.</summary>
    public double Playful { get; init; }
    /// <summary>Decayed treatment counts (praise, thanks, tease, scold, affection, abuse).</summary>
    public IReadOnlyDictionary<string, double> Treatment { get; init; } = new Dictionary<string, double>();
    /// <summary>Task types they ask for, decayed counts.</summary>
    public IReadOnlyDictionary<string, double> Requests { get; init; } = new Dictionary<string, double>();
    public IReadOnlyList<string> InsideJokes { get; init; } = [];
    public string? Nickname { get; init; }
    public IReadOnlyList<string> Milestones { get; init; } = [];
    public int Episodes { get; init; }
    public bool Regular { get; init; }

    public string? Dominant => Treatment.Where(t => t.Value >= 0.6).OrderByDescending(t => t.Value).Select(t => t.Key).FirstOrDefault();
    public string? TopRequest => Requests.Where(t => t.Value >= 1.0).OrderByDescending(t => t.Value).Select(t => t.Key).FirstOrDefault();

    /// <summary>Compact relationship line ("fave tease target, super warm, trusts you w/ the farm"), "" if nothing notable.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        var dominant = Dominant;
        if (dominant != null && DominantWords.TryGetValue(dominant, out var how)) parts.Add(how);
        Bands.AddFirst(parts, Affinity, AffinityBands);
        if (Playful >= 0.5 && dominant != SignalKinds.Tease) parts.Add("banter buddy");
        if (TopRequest is { } r) parts.Add($"trusts you w/ {TaskTypes.Label(r)}");
        Bands.AddFirst(parts, Trust, TrustBands);
        if (Nickname != null) parts.Add($"calls you \"{Nickname}\"");
        if (InsideJokes.Count > 0) parts.Add($"joke: \"{InsideJokes[0]}\"");
        return string.Join(", ", parts);
    }

    private static readonly Dictionary<string, string> DominantWords = new(StringComparer.Ordinal)
    {
        [SignalKinds.Tease] = "fave tease target", [SignalKinds.Praise] = "hypes you up", [SignalKinds.Thanks] = "always says ty",
        [SignalKinds.Affection] = "gives you headpats", [SignalKinds.Scold] = "strict with you", [SignalKinds.Abuse] = "been rude lately",
    };

    private static readonly Bands.Band[] AffinityBands =
        [new(0.6, true, "bestie"), new(0.3, true, "warm"), new(-0.15, false, "you're wary of them (stay polite)")];
    private static readonly Bands.Band[] TrustBands = [new(-0.2, false, "double-check their claims"), new(0.4, true, "their info checks out")];
}

/// <summary>An emerging preference: activity × outcome × treatment for one task type.</summary>
public sealed record PreferenceState(string Subject, double Done, double Ok, double Fail, double Praise)
{
    /// <summary>[-1, 1]: does it like doing this (done a lot, goes well, gets praised).</summary>
    public double Like => Math.Tanh((0.08 * Done + 0.25 * Ok - 0.4 * Fail + 0.8 * Praise) / 2.0);
    /// <summary>Success rate when there's enough history, else null.</summary>
    public double? Mastery => Ok + Fail >= 4 ? Ok / (Ok + Fail) : null;

    public bool IsLike => Like >= 0.35 && Done >= 4;
    public bool IsDislike => Like <= -0.3 && Done >= 3;
    public bool IsPro => Mastery is >= 0.8 && Ok >= 5;
}
