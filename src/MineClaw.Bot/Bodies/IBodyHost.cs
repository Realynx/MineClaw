using MineClaw.Agent.Events;
using MineClaw.World;

namespace MineClaw.Bot.Bodies;

/// <summary>Main's view of the server's player slots: max players from its login packet and its tab list.</summary>
public sealed record BodySlots(int MaxPlayers, IReadOnlyCollection<string> Online);

/// <summary>A slot decision (the hive's SlotPolicy behind <see cref="IBodyHost.CanSpawn"/>).</summary>
public sealed record BodySlotDecision(bool Ok, string Reason);

/// <summary>A live clone body as the slot policy sees it (0..1 progress; the furthest from finishing yields first).</summary>
public sealed record BodyProgress(string Name, double Progress, DateTimeOffset Started);

/// <summary>The despawn deposit's outcome (the hive's DepositProcedure behind <see cref="IBodyHost.DepositAsync"/>).</summary>
public sealed record BodyDeposit(bool Empty, int Remaining, string Text)
{
    public static BodyDeposit Nothing(int remaining, string why) => new(remaining == 0, remaining, why);
}

/// <summary>
/// What the <see cref="BodyRegistry"/> needs from the process (one brain, many bodies, step 2): building a clone body, its
/// connection, main's tab list and slot policy, the logout deposit (main as the hand-off receiver), stranded items and void
/// spots in the shared db, and main's chat. The App wires the real ones (Hive's SlotPolicy, DepositProcedure, HiveStore);
/// tests use fakes.
/// </summary>
public interface IBodyHost
{
    /// <summary>Builds the body for a clone account (its client and per-body wiring). Called once per account; the body is
    /// reused for every later spawn of that account.</summary>
    Body Create(string name);

    /// <summary>Logs the body in (throws on failure; the runner retries with backoff).</summary>
    Task ConnectAsync(IBody body, CancellationToken ct);

    /// <summary>Runs until the body's connection ends (or <paramref name="ct"/>): lost, kicked, or someone else's login.</summary>
    Task<BodyExit> RunAsync(IBody body, CancellationToken ct);

    /// <summary>Logs the body out (a clean quit; no reconnect).</summary>
    Task DisconnectAsync(IBody body, string reason);

    /// <summary>Main's tab list (max players, everyone online).</summary>
    BodySlots Slots();

    /// <summary>May one more body log in now (<paramref name="pending"/> are logging in already)?</summary>
    BodySlotDecision CanSpawn(int pending);

    /// <summary>Bodies that must log out, in order, so the offline regulars' slots are free again.</summary>
    IReadOnlyList<string> MustYield(IReadOnlyList<BodyProgress> running);

    /// <summary>Empties the body's inventory into storage (main takes what doesn't fit). The report says what is left.</summary>
    Task<BodyDeposit> DepositAsync(IBody body, TimeSpan timeout, CancellationToken ct);

    /// <summary>Items left on a logged-out account (recorded in the shared db). <paramref name="remaining"/> 0 = clear the record.</summary>
    void Stranded(IBody body, string reason, int remaining) { }

    /// <summary>Where this account was left holding over the void (its saved login spot), or null.</summary>
    string? VoidSpot(string name) => null;
    void MarkVoid(string name, Vec3d at) { }
    void ClearVoid(string name) { }

    /// <summary>Trusted players (who to ask for a /tp).</summary>
    IReadOnlyList<string> Trusted() => [];

    /// <summary>Says a line on main's account (the only chat sender).</summary>
    Task SayAsync(string text) => Task.CompletedTask;

    /// <summary>An event for the brain.</summary>
    void Post(GameEvent e);
}
