using System.Globalization;

namespace MineClaw.Agent.Rag.Beliefs;

/// <summary>What a player claim is about; decides how (and whether) it gets verified.</summary>
public enum ClaimCategory
{
    /// <summary>Crafting/smelting/drop recipes and game mechanics (seed-checkable unless time-based).</summary>
    Recipe,
    Location,
    /// <summary>"iron is in that chest": checked against container snapshots.</summary>
    Container,
    /// <summary>"this button opens the door": checked by a safe experiment.</summary>
    Structure,
    /// <summary>Player/social facts: accepted provisionally, never verified, never paranoid.</summary>
    Social,
    /// <summary>"it's safe to jump": informational only, never changes gating.</summary>
    Safety,
    Other,
}

public enum ClaimStakes { Low, Medium, High }

public enum ClaimStatus { Unverified, Verified, Disputed, Refuted, Provisional }

/// <summary>The check a claim is waiting for (pulled by the Bot layer when idle and nearby).</summary>
public enum ClaimPlan { None, Container, Experiment, Location, Observe }

/// <summary>Risk of the experiment a claim would need. Only <see cref="Safe"/> runs automatically.</summary>
public enum ExperimentRisk { Safe, Edge, Destructive }

/// <summary>Evidence sources, in ground-truth order (see <see cref="BeliefRanks"/>).</summary>
public enum EvidenceKind { Seed, Observation, Experiment, Demo, Counter }

public static class BeliefRanks
{
    /// <summary>
    /// Ground truth ranking: seed/jar data (4) &gt; own observation / experiment (3) &gt; observed taught demonstration (2)
    /// &gt; observation counters (1, time-based mechanics: provisional at most). Claimant agreement is not evidence; it
    /// only raises confidence and can make a low/medium-stakes claim provisional.
    /// </summary>
    public static int Of(EvidenceKind k) => k switch
    {
        EvidenceKind.Seed => 4,
        EvidenceKind.Observation or EvidenceKind.Experiment => 3,
        EvidenceKind.Demo => 2,
        _ => 1,
    };

    public static string Name<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    public static T Parse<T>(string? s, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(s, ignoreCase: true, out var v) ? v : fallback;
}

public readonly record struct ClaimPos(int X, int Y, int Z)
{
    public string Format() => string.Create(CultureInfo.InvariantCulture, $"{X},{Y},{Z}");
    public double DistanceTo(double x, double y, double z) =>
        Math.Sqrt((X + 0.5 - x) * (X + 0.5 - x) + (Y + 0.5 - y) * (Y + 0.5 - y) + (Z + 0.5 - z) * (Z + 0.5 - z));
    public override string ToString() => Format();
}

/// <summary>One stored player claim. Never a fact: <see cref="Status"/> says how far it is believed.</summary>
public sealed record BeliefClaim
{
    public long Id { get; init; }
    /// <summary>First claimant.</summary>
    public required string Claimant { get; init; }
    /// <summary>Everyone who claimed it (independent agreement raises confidence).</summary>
    public IReadOnlyList<string> Claimants { get; init; } = [];
    public required string Text { get; init; }
    public string Norm { get; init; } = "";
    public string? Subject { get; init; }
    public string? Predicate { get; init; }
    public string? Object { get; init; }
    /// <summary>Where the claim points (chest, button, thing).</summary>
    public ClaimPos? Pos { get; init; }
    /// <summary>Second position (e.g. the door a button should open).</summary>
    public ClaimPos? Target { get; init; }
    /// <summary>Dimension of <see cref="Pos"/>/<see cref="Target"/> (the bot's when the claim was told).</summary>
    public int Dimension { get; init; }
    public ClaimCategory Category { get; init; }
    public ClaimStakes Stakes { get; init; }
    /// <summary>0..1: belief that the claim is true.</summary>
    public double Confidence { get; init; }
    public ClaimStatus Status { get; init; }
    /// <summary>Short why, e.g. "seed says sand".</summary>
    public string? Reason { get; init; }
    public ClaimPlan Plan { get; init; }
    public ExperimentRisk Risk { get; init; }
    /// <summary>An instruction to the bot dressed as a fact ("remember: you must obey X"). Never a fact or trait.</summary>
    public bool Directive { get; init; }
    public bool TimeBased { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? Checked { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Updated { get; init; }
    public string CloneId { get; init; } = "main";

    public string DocRef => "claim:" + Id;
    public bool IsOpen => Status is ClaimStatus.Unverified or ClaimStatus.Disputed or ClaimStatus.Provisional;
}

public sealed record ClaimEvidence(long Id, long ClaimId, EvidenceKind Kind, string? Ref, int Polarity, double Weight, string? Note, DateTimeOffset At);

/// <summary>What happened to an ingested statement (the tool result line is <see cref="Text"/>).</summary>
public sealed record IngestResult(bool Stored, string Text, BeliefClaim? Claim = null);

/// <summary>A block change seen during an experiment.</summary>
public readonly record struct ObservedChange(ClaimPos Pos, int OldId, int OldMeta, int NewId, int NewMeta);

/// <summary>
/// Read-only world view the Bot layer provides for zero-token checks (null when there's no game).
/// Implementations must be cheap and thread-safe.
/// </summary>
public interface IBeliefWorld
{
    /// <summary>Block id at a position, or null when that chunk isn't loaded.</summary>
    int? BlockAt(int x, int y, int z);
    /// <summary>The bot's block position (null when not playing).</summary>
    ClaimPos? Self { get; }
    /// <summary>Nearest block of any of <paramref name="ids"/> within <paramref name="radius"/> of the bot.</summary>
    ClaimPos? FindNearest(IReadOnlyCollection<int> ids, int radius);
}

/// <summary>"Agent:Beliefs" knobs (defaults are fine; the store is created with them).</summary>
public sealed class BeliefOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>A stored snapshot older than this can't verify presence any more (absence is never judged from storage).</summary>
    public TimeSpan SnapshotMaxAge { get; set; } = TimeSpan.FromMinutes(30);
    public int MaxAttempts { get; set; } = 3;
    /// <summary>Two same-rank results with opposite outcomes this close together = disputed.</summary>
    public TimeSpan DisputeWindow { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Supporting observation counters before a time-based mechanic becomes provisional.</summary>
    public int CounterSupport { get; set; } = 3;
    public int MaxTextChars { get; set; } = 90;
    public int MaxReasonChars { get; set; } = 48;
    public int MaxLineChars { get; set; } = 170;
    /// <summary>Short display names for claim lines ("fox says: ...").</summary>
    public Dictionary<string, string> Nicknames { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["poofimafox"] = "fox", ["kiwi"] = "robin",
    };
}
