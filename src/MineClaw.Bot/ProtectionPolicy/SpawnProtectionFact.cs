using System.Text.Json;
using System.Text.Json.Serialization;
using MineClaw.Agent.Rag;
using MineClaw.Client;
using MineClaw.World;

namespace MineClaw.Bot;

/// <summary>
/// Spawn protection as a world fact in the shared memory (one <c>protected_region</c>/<c>spawn</c> observation per dimension),
/// learned from the world, never configured: the server undid digs/placements near the world spawn → a radius around it
/// (<see cref="MinecraftClient"/> learns it from two independent undone edits). Who it binds is learned too: <see cref="Affects"/> are
/// the accounts whose edits were undone there, <see cref="Exempt"/> the ones whose edits inside it worked (op). An exempt account's
/// success is no evidence against the fact (live 10-28: main is op, its chopping near spawn retracted the region 420 had learned,
/// and every other body went back to digging there); only an affected account editing inside it retracts it for that account,
/// and the fact goes once no affected account is left. Every hive process and body adopts it unless its account is exempt.
/// </summary>
public sealed record SpawnProtectionFact(BlockPos Center, int Radius, int Dimension, IReadOnlyList<string> Affects, IReadOnlyList<string> Exempt,
    string Reason)
{
    public const string Kind = "protected_region";
    public const string Key = "spawn";

    public bool Contains(BlockPos p, int dimension) =>
        dimension == Dimension && Math.Max(Math.Abs(p.X - Center.X), Math.Abs(p.Z - Center.Z)) <= Radius;

    public bool AppliesTo(string account) => !Exempt.Contains(account, StringComparer.OrdinalIgnoreCase);

    /// <summary>The client's form, for an account it applies to.</summary>
    public LearnedProtectedRegion ToRegion(int failures, DateTime learnedAt) => new(Center, Radius, Dimension, failures, "shared: " + Reason, learnedAt);

    /// <summary>
    /// The fact after <paramref name="account"/>'s client learned (or widened) <paramref name="r"/>: the radius is the larger one,
    /// the account binds (it isn't exempt any more).
    /// </summary>
    public static SpawnProtectionFact Learned(SpawnProtectionFact? old, LearnedProtectedRegion r, string account)
    {
        var affects = Add(old?.Affects, account);
        var exempt = (old?.Exempt ?? []).Where(e => !e.Equals(account, StringComparison.OrdinalIgnoreCase)).ToList();
        int radius = Math.Max(r.Radius, old?.Center == r.Center ? old.Radius : 0);
        return new SpawnProtectionFact(r.Center, radius, r.Dimension, affects, exempt, r.Reason);
    }

    /// <summary>
    /// <paramref name="account"/>'s edit at <paramref name="p"/> worked. Outside the region: no change (null, false). Inside, by an
    /// account it binds: that account is free of it now; the fact is retracted (null, true) once nobody it binds is left. Inside, by
    /// an account it never bound: that account is exempt (op), the fact stays for everyone else.
    /// </summary>
    public (SpawnProtectionFact? Fact, bool Changed) AfterSuccess(string account, BlockPos p, int dimension)
    {
        if (!Contains(p, dimension)) return (this, false);
        if (Affects.Contains(account, StringComparer.OrdinalIgnoreCase))
        {
            var left = Affects.Where(a => !a.Equals(account, StringComparison.OrdinalIgnoreCase)).ToList();
            return left.Count == 0 ? (null, true) : (this with { Affects = left, Exempt = Add(Exempt, account) }, true);
        }
        if (Affects.Count == 0) return (null, true); // nobody known it binds (an old fact without its accounts): a success disproves it
        return Exempt.Contains(account, StringComparer.OrdinalIgnoreCase) ? (this, false) : (this with { Exempt = Add(Exempt, account) }, true);
    }

    /// <summary>The account is known to be op (<c>Bot:Protection:ServerOp</c> for main): exempt, the fact stays for the others.</summary>
    public SpawnProtectionFact WithExempt(string account) =>
        Exempt.Contains(account, StringComparer.OrdinalIgnoreCase) ? this
            : this with { Exempt = Add(Exempt, account), Affects = Affects.Where(a => !a.Equals(account, StringComparison.OrdinalIgnoreCase)).ToList() };

    private static List<string> Add(IEnumerable<string>? list, string account)
    {
        var l = (list ?? []).ToList();
        if (!l.Contains(account, StringComparer.OrdinalIgnoreCase)) l.Add(account);
        return l;
    }

    // ================================================================ the observation row

    private sealed record Fields(
        [property: JsonPropertyName("center")] int[] Center,
        [property: JsonPropertyName("radius")] int Radius,
        [property: JsonPropertyName("affects")] List<string>? Affects,
        [property: JsonPropertyName("exempt")] List<string>? Exempt,
        [property: JsonPropertyName("reason")] string? Reason);

    /// <summary>The fact in <paramref name="dimension"/>, or null. A row from before the accounts were recorded reads its box only.</summary>
    public static SpawnProtectionFact? Read(ObservationStore obs, int dimension)
    {
        if (obs.Get(Kind, Key, dimension: dimension) is not { } row) return null;
        Fields? f = null;
        if (row.FieldsJson is { Length: > 0 } json)
            try { f = JsonSerializer.Deserialize<Fields>(json); } catch (JsonException) { }
        if (f is { Center.Length: 3 })
            return new SpawnProtectionFact(new BlockPos(f.Center[0], f.Center[1], f.Center[2]), f.Radius, dimension, f.Affects ?? [], f.Exempt ?? [], f.Reason ?? row.Text);
        if (row.Bounds is not { } b) return null;
        int radius = Math.Max(1, (int)Math.Round((b.MaxX - b.MinX) / 2.0));
        return new SpawnProtectionFact(new BlockPos((int)Math.Round((b.MinX + b.MaxX) / 2.0), 0, (int)Math.Round((b.MinZ + b.MaxZ) / 2.0)), radius, dimension, [], [], row.Text);
    }

    /// <summary>Writes the fact (text for the brain and recall, box for spatial queries, the accounts as fields).</summary>
    public void Write(ObservationStore obs, int worldHeight)
    {
        obs.Upsert(new Observation(Kind, Key, Text())
        {
            Bounds = Box.Of(Center.X - Radius, 0, Center.Z - Radius, Center.X + Radius, worldHeight - 1, Center.Z + Radius),
            Dimension = Dimension,
            Confidence = 0.9,
            FieldsJson = JsonSerializer.Serialize(new Fields([Center.X, Center.Y, Center.Z], Radius, Affects.ToList(), Exempt.ToList(), Reason)),
        });
    }

    public static void Retract(ObservationStore obs, int dimension) => obs.Remove(Kind, Key, tombstone: false, dimension: dimension);

    /// <summary>What the brain reads: who can't edit there and who can.</summary>
    public string Text()
    {
        static string Names(IEnumerable<string> a) => string.Join(", ", a.Select(n => Bodies.Body.ShortName(n, false)));
        var who = Affects.Count > 0 ? $"; undone for {Names(Affects)}" : "";
        var free = Exempt.Count > 0 ? $"; {Names(Exempt)} can edit there (op)" : "";
        return $"spawn protection: the server undoes digs and placements within {Radius} of {Center.X},{Center.Y},{Center.Z} for accounts " +
            $"without op{who}{free} ({Reason})";
    }
}
