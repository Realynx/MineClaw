namespace MineClaw.Hive.Slots;

/// <summary>What the main bot knows about the server's player slots.</summary>
/// <param name="MaxPlayers">From the login packet (0 = unknown → fallback).</param>
/// <param name="Online">Everyone on the player list, hive bots included.</param>
public sealed record SlotFacts(int MaxPlayers, IReadOnlyCollection<string> Online);

/// <summary>A running clone as the slot policy sees it.</summary>
/// <param name="Progress">0..1 as last reported by the clone (0 = unknown / just started).</param>
public sealed record CloneProgress(string Name, double Progress, DateTimeOffset Started);

public sealed record SlotDecision(bool Ok, int Free, int Reserved, string Reason);

/// <summary>
/// Player-slot rules for the hive (server max-players 7, offline mode):
/// <list type="bullet">
/// <item>a clone may spawn only if, after it joins, one slot stays free for EACH regular player who is offline;</item>
/// <item>if the reservation is violated (e.g. the server filled up while a regular was offline), the clone furthest from
///   finishing its task logs out first (lowest progress; among equals, the most recently started).</item>
/// </list>
/// </summary>
public static class SlotPolicy
{
    public static int Capacity(SlotFacts f, int fallback) => f.MaxPlayers > 0 ? f.MaxPlayers : fallback;

    public static int ReservedFor(SlotFacts f, IEnumerable<string> regulars)
    {
        var online = new HashSet<string>(f.Online, StringComparer.OrdinalIgnoreCase);
        return regulars.Distinct(StringComparer.OrdinalIgnoreCase).Count(r => !online.Contains(r));
    }

    /// <param name="pending">Clones launched but not on the player list yet (they will take a slot).</param>
    public static SlotDecision CanSpawn(SlotFacts f, IEnumerable<string> regulars, int pending = 0, int fallbackMax = 7)
    {
        int cap = Capacity(f, fallbackMax);
        int free = cap - f.Online.Count - pending;
        int reserved = ReservedFor(f, regulars);
        if (free - 1 >= reserved) return new SlotDecision(true, free, reserved, $"{free} free, {reserved} kept for regulars");
        return new SlotDecision(false, free, reserved, free <= 0 ? $"server full ({f.Online.Count}/{cap})"
            : $"only {free} slot(s) free and {reserved} kept for offline regulars");
    }

    /// <summary>Clones that must log out (in order) so the offline regulars' slots are free again.</summary>
    public static IReadOnlyList<string> MustYield(SlotFacts f, IEnumerable<string> regulars, IEnumerable<CloneProgress> running, int fallbackMax = 7)
    {
        int cap = Capacity(f, fallbackMax);
        int free = cap - f.Online.Count;
        int reserved = ReservedFor(f, regulars);
        int need = reserved - free;
        if (need <= 0) return [];
        return YieldOrder(running).Take(need).Select(c => c.Name).ToList();
    }

    /// <summary>Furthest from finishing first: lowest progress, then most recently started.</summary>
    public static IEnumerable<CloneProgress> YieldOrder(IEnumerable<CloneProgress> running) =>
        running.OrderBy(c => c.Progress).ThenByDescending(c => c.Started);
}
