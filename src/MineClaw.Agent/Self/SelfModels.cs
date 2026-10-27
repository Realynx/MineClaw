namespace MineClaw.Agent.Self;

/// <summary>Short-term mood, each dimension in [-1, 1] (0 = neutral baseline it relaxes back to over hours).</summary>
public sealed record MoodState(double Cheer, double Confidence, double Energy, double Affection)
{
    public static readonly MoodState Neutral = new(0, 0, 0, 0);

    /// <summary>A few words ("giddy, kinda sleepy"), "" when neutral. Quantized so it rarely changes turn to turn.</summary>
    public string Words()
    {
        var w = new List<string>();
        if (Cheer >= 0.45) w.Add("giddy");
        else if (Cheer >= 0.18) w.Add("cheery");
        else if (Cheer <= -0.45) w.Add("pretty down");
        else if (Cheer <= -0.18) w.Add("a bit down");
        if (Confidence >= 0.45) w.Add("cocky");
        else if (Confidence >= 0.2) w.Add("confident");
        else if (Confidence <= -0.4) w.Add("shaken");
        else if (Confidence <= -0.2) w.Add("unsure of yourself");
        if (Energy <= -0.4) w.Add("sleepy");
        else if (Energy <= -0.2) w.Add("kinda tired");
        if (Affection >= 0.4) w.Add("clingy");
        else if (Affection >= 0.2) w.Add("feeling loved");
        return string.Join(", ", w);
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
        switch (Dominant)
        {
            case SignalKinds.Tease: parts.Add("fave tease target"); break;
            case SignalKinds.Praise: parts.Add("hypes you up"); break;
            case SignalKinds.Thanks: parts.Add("always says ty"); break;
            case SignalKinds.Affection: parts.Add("gives you headpats"); break;
            case SignalKinds.Scold: parts.Add("strict with you"); break;
            case SignalKinds.Abuse: parts.Add("been rude lately"); break;
        }
        if (Affinity >= 0.6) parts.Add("bestie");
        else if (Affinity >= 0.3) parts.Add("warm");
        else if (Affinity <= -0.15) parts.Add("you're wary of them (stay polite)");
        if (Playful >= 0.5 && Dominant != SignalKinds.Tease) parts.Add("banter buddy");
        if (TopRequest is { } r) parts.Add($"trusts you w/ {TaskTypes.Label(r)}");
        if (Trust <= -0.2) parts.Add("double-check their claims");
        else if (Trust >= 0.4) parts.Add("their info checks out");
        if (Nickname != null) parts.Add($"calls you \"{Nickname}\"");
        if (InsideJokes.Count > 0) parts.Add($"joke: \"{InsideJokes[0]}\"");
        return string.Join(", ", parts);
    }
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
