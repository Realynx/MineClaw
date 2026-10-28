using MineClaw.Hive.Slots;

namespace MineClaw.Hive.Hierarchy;

/// <summary>The allocator's view of the server and the hive at one moment.</summary>
/// <param name="Slots">Max players + who is online (hive bots included).</param>
/// <param name="Regulars">Regular players: one slot stays free for each while they're offline.</param>
/// <param name="KnownPlayers">Every name seen on the server that isn't ours (chat, tab list, players table): never allocated.</param>
/// <param name="Running">Accounts with a live or starting process/body (busy, not reusable now).</param>
/// <param name="Pending">Launched clones not on the tab list yet (they will take a slot).</param>
public sealed record AllocationFacts(SlotFacts Slots, IReadOnlyCollection<string> Regulars, IReadOnlyCollection<string> KnownPlayers,
    IReadOnlyCollection<string> Running, int Pending = 0);

public sealed record Allocation(string? Name, string? Error, bool Reused = false)
{
    public bool Ok => Name != null;
}

/// <summary>
/// Picks the account for a new clone: a released managed account first (it keeps its saved login spot and history; the most
/// recently used one), else a fresh <c>{prefix}{id}</c> with the lowest free id that isn't quantity-like. Never a name someone else
/// is using or has used on the server, never main's, never longer than 16 characters. Refuses when the player cap (minus the slots
/// kept for offline regulars) or <c>Hive:MaxClones</c> (when &gt; 0) would be exceeded.
/// </summary>
public sealed class AccountAllocator(HiveAccountsConfig cfg, ManagedAccounts managed, string main, int maxClones = 0, int maxPlayersFallback = 7)
{
    public HiveAccountsConfig Config => cfg;

    public Allocation Allocate(AllocationFacts f)
    {
        var running = new HashSet<string>(f.Running, StringComparer.OrdinalIgnoreCase);
        if (maxClones > 0 && running.Count >= maxClones) return new(null, $"clone cap reached ({running.Count}/{maxClones}, Hive:MaxClones)");
        var slot = SlotPolicy.CanSpawn(f.Slots, f.Regulars, f.Pending, maxPlayersFallback);
        if (!slot.Ok) return new(null, "player cap: " + slot.Reason);

        var online = new HashSet<string>(f.Slots.Online, StringComparer.OrdinalIgnoreCase);
        var foreign = new HashSet<string>(f.KnownPlayers, StringComparer.OrdinalIgnoreCase);
        bool Usable(string n) => AccountNames.IsManagedShape(n, cfg, main) && !running.Contains(n) && !online.Contains(n) && !foreign.Contains(n);

        // Reuse first: a released account main already manages.
        var reuse = managed.All().Where(a => !a.Active && Usable(a.Name)).OrderByDescending(a => a.LastUsed).FirstOrDefault();
        if (reuse != null) return new(reuse.Name, null, Reused: true);

        for (int id = Math.Max(0, cfg.FirstId); id <= Math.Max(cfg.FirstId, cfg.LastId); id++)
        {
            if (AccountNames.QuantityLike(id)) continue;
            var name = AccountNames.For(cfg, id);
            if (name.Length > Math.Min(16, cfg.MaxLength)) break;
            if (managed.IsManaged(name) || !Usable(name)) continue;
            return new(name, null);
        }
        return new(null, $"no free account name under {cfg.Prefix} (≤{Math.Min(16, cfg.MaxLength)} chars)");
    }
}
