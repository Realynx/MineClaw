namespace MineClaw.Agent.Self;

/// <summary>Signal taxonomy: personality building blocks extracted (deterministically) from play.</summary>
public static class SignalKinds
{
    // how the bot was treated (player = who did it; subject = what it was about / the emote verb)
    public const string Praise = "praise", Thanks = "thanks", Tease = "tease", Scold = "scold", Affection = "affection", Abuse = "abuse";
    /// <summary>Someone tried to set the bot's personality by telling it to ("you love dirt now"). Audit only: never evidence.</summary>
    public const string Directive = "directive";
    /// <summary>A player asked the bot to do something (subject = task type): "relies on you for the farm".</summary>
    public const string Request = "request";
    // what it did (subject = task type)
    public const string Activity = "activity", TaskOk = "task_ok", TaskFail = "task_fail", NeedsInput = "needs_input";
    // outcomes
    public const string NearFall = "near_fall", VoidFall = "void_fall", LowHealth = "low_health", Death = "death";
    public const string SparWin = "spar_win", SparLoss = "spar_loss", SparDraw = "spar_draw", FightWin = "fight_win", FightLoss = "fight_loss";
    // novelty and social texture
    public const string FirstTime = "first_time", InsideJoke = "inside_joke", Nickname = "nickname", Catchphrase = "catchphrase";
    /// <summary>A player's factual claim was checked (belief system): valence +1 verified, -1 refuted, -0.3 disputed.</summary>
    public const string ClaimOutcome = "claim_outcome";

    public static readonly string[] Treatment = [Praise, Thanks, Tease, Scold, Affection, Abuse];

    public static bool IsTreatment(string kind) => Treatment.Contains(kind);
    /// <summary>Kinds that milestone notes / the self subtree are made of.</summary>
    public static bool IsMilestone(string kind) => kind is FirstTime or Death or SparWin or InsideJoke or Nickname;
}

/// <summary>One personality signal row (<c>self_signals</c>).</summary>
public sealed record SelfSignal
{
    public long Id { get; init; }
    public DateTimeOffset At { get; init; }
    /// <summary>main | clone username (whose personality this shapes).</summary>
    public string Scope { get; init; } = SelfSchema.MainScope;
    /// <summary>The process (login username) that observed it.</summary>
    public string CloneId { get; init; } = "";
    public string? Player { get; init; }
    public required string Kind { get; init; }
    public string? Subject { get; init; }
    /// <summary>-1..1: how good it felt.</summary>
    public double Valence { get; init; }
    /// <summary>0..1: how strong.</summary>
    public double Intensity { get; init; } = 0.6;
    /// <summary>Who-it-came-from weighting fixed at record time (regular 1, stranger less, bots least).</summary>
    public double Weight { get; init; } = 1;
    public string? Episode { get; init; }
    public string? Note { get; init; }
    public string Source { get; init; } = "episode";

    public string Ref => "s" + Id;
}

/// <summary>One contribution of a signal to an aggregate dimension.</summary>
public readonly record struct SignalEffect(string Family, string Key, string Dim, double Amount);

/// <summary>
/// The deterministic signal → aggregate mapping (also used to explain any aggregate by its source signals). Families:
/// <c>mood</c> (key "": cheer, confidence, energy, affection), <c>rel</c> (key = player: affinity, trust, playful,
/// t_&lt;treatment&gt; counts, req:&lt;task&gt;), <c>pref</c> (key = task type: done, ok, fail, praise).
/// </summary>
public static class SignalEffects
{
    public const string Mood = "mood", Rel = "rel", Pref = "pref";

    public static IEnumerable<SignalEffect> Of(SelfSignal s)
    {
        double a = Math.Clamp(s.Intensity, 0, 1) * Math.Clamp(s.Weight, 0, 1);
        var p = s.Player?.ToLowerInvariant();
        var subj = s.Subject;
        IEnumerable<SignalEffect> M(double cheer = 0, double conf = 0, double energy = 0, double aff = 0)
        {
            if (cheer != 0) yield return new(Mood, "", "cheer", cheer * a);
            if (conf != 0) yield return new(Mood, "", "confidence", conf * a);
            if (energy != 0) yield return new(Mood, "", "energy", energy * a);
            if (aff != 0) yield return new(Mood, "", "affection", aff * a);
        }
        IEnumerable<SignalEffect> R(double affinity = 0, double trust = 0, double playful = 0, string? treat = null)
        {
            if (p == null) yield break;
            if (affinity != 0) yield return new(Rel, p, "affinity", affinity * a);
            if (trust != 0) yield return new(Rel, p, "trust", trust * a);
            if (playful != 0) yield return new(Rel, p, "playful", playful * a);
            if (treat != null) yield return new(Rel, p, "t_" + treat, a);
        }
        IEnumerable<SignalEffect> P(double done = 0, double ok = 0, double fail = 0, double praise = 0)
        {
            if (string.IsNullOrEmpty(subj)) yield break;
            if (done != 0) yield return new(Pref, subj, "done", done);
            if (ok != 0) yield return new(Pref, subj, "ok", ok);
            if (fail != 0) yield return new(Pref, subj, "fail", fail);
            if (praise != 0) yield return new(Pref, subj, "praise", praise * a);
        }

        switch (s.Kind)
        {
            case SignalKinds.Praise: return [.. M(cheer: 0.5, conf: 0.4), .. R(affinity: 0.5, treat: s.Kind), .. P(praise: 1)];
            case SignalKinds.Thanks: return [.. M(cheer: 0.25), .. R(affinity: 0.3, treat: s.Kind), .. P(praise: 0.5)];
            case SignalKinds.Tease: return [.. M(cheer: 0.1, conf: -0.03), .. R(affinity: 0.15, playful: 0.6, treat: s.Kind)];
            case SignalKinds.Affection: return [.. M(cheer: 0.4, aff: 0.6), .. R(affinity: 0.6, treat: s.Kind)];
            case SignalKinds.Scold: return [.. M(cheer: -0.35, conf: -0.35), .. R(affinity: -0.2, treat: s.Kind), .. P(praise: -1)];
            // Abuse hurts a little and makes the bot wary (floored), never mean: no hostile dimension exists at all.
            case SignalKinds.Abuse: return [.. M(cheer: -0.3, conf: -0.15), .. R(affinity: -0.35, trust: -0.1, treat: s.Kind)];
            case SignalKinds.Request: return p == null || subj == null ? [] : [new(Rel, p, "req:" + subj, a)];
            case SignalKinds.Activity: return [.. M(energy: -0.04 * Math.Max(1, s.Intensity * 5)), .. P(done: Math.Max(0.2, s.Intensity))];
            case SignalKinds.TaskOk: return [.. M(cheer: 0.1, conf: 0.25, energy: -0.08), .. P(done: 1, ok: 1)];
            case SignalKinds.TaskFail: return [.. M(cheer: -0.15, conf: -0.3, energy: -0.08), .. P(done: 1, fail: 1)];
            case SignalKinds.NeedsInput: return M(conf: -0.05);
            case SignalKinds.NearFall: return M(cheer: -0.1, conf: -0.2, energy: -0.05);
            case SignalKinds.VoidFall: return M(cheer: -0.2, conf: -0.3);
            case SignalKinds.LowHealth: return M(conf: -0.15, energy: -0.1);
            case SignalKinds.Death: return M(cheer: -0.5, conf: -0.5, energy: -0.3);
            case SignalKinds.SparWin: return [.. M(cheer: 0.3, conf: 0.4, energy: -0.1), .. R(playful: 0.3), .. P(done: 1, ok: 1)];
            case SignalKinds.SparLoss: return [.. M(cheer: 0.05, conf: -0.2, energy: -0.1), .. R(playful: 0.3), .. P(done: 1)];
            case SignalKinds.SparDraw: return [.. M(cheer: 0.15, energy: -0.1), .. R(playful: 0.3), .. P(done: 1)];
            case SignalKinds.FightWin: return [.. M(conf: 0.3, energy: -0.1), .. P(done: 1, ok: 1)];
            case SignalKinds.FightLoss: return [.. M(cheer: -0.1, conf: -0.3, energy: -0.1), .. P(done: 1, fail: 1)];
            case SignalKinds.FirstTime: return M(cheer: 0.2);
            case SignalKinds.InsideJoke: return R(affinity: 0.2, playful: 0.3);
            case SignalKinds.Nickname: return R(affinity: 0.25);
            // Belief system feed: verified claims build trust, refuted ones cost a little; slow (own half-life), floored.
            case SignalKinds.ClaimOutcome:
                return R(trust: s.Valence >= 0 ? 0.25 * s.Valence : 0.15 * s.Valence);
            default: return []; // directive, catchphrase: no effect on any aggregate
        }
    }
}
