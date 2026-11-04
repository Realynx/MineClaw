using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Hive.Story;

/// <summary>Something the colony sees: a drop, an animal or monster, a player. <paramref name="SeenAt"/>: the last update any body got for
/// it (null = unknown: counts as fresh); <paramref name="FirstSeen"/>: when a body first tracked it (a drop's age: it despawns at 5 min).</summary>
public sealed record StoryEntity(int Id, string Kind, string Name, double X, double Y, double Z, bool Hostile = false, int ItemId = -1, int ItemMeta = 0,
    int Count = 0, DateTimeOffset? SeenAt = null, DateTimeOffset? FirstSeen = null)
{
    public P3 Block => new((int)Math.Floor(X), (int)Math.Floor(Y), (int)Math.Floor(Z));
}

/// <summary>The colony's world view (main's: one world for the colony), read-only. Tests fake it.</summary>
public interface IStoryWorld
{
    IGameData Data { get; }
    /// <summary>"b1.8.1" | "1.12.2".</summary>
    string Version => Data.VersionId;
    long TimeOfDay { get; }
    /// <summary>The server stopped the daylight cycle (the time holds).</summary>
    bool TimeFrozen => false;
    int Height { get; }
    (int Id, int Meta) Block(int x, int y, int z);
    IReadOnlyList<StoryEntity> Entities();
    /// <summary>The view's dimension (0 overworld, -1 nether, 1 end).</summary>
    int Dimension => 0;
    /// <summary>The column at x,z is in the view (an unloaded one reads as air: never a spot to send a pawn to).</summary>
    bool Loaded(int x, int z) => true;
    /// <summary>The light level at x,y,z after dark (0-15; mobs spawn at 7 or less), or null when the view doesn't know.</summary>
    int? NightLight(int x, int y, int z) => null;
}

/// <summary>A live pawn body as the director reads it each time it picks.</summary>
/// <param name="Inventory">Totals by item; a 1.12.2 potion's meta is its potion type id (<see cref="MineClaw.Data.Brewing.TypeId"/>).</param>
/// <param name="XpLevel">The body's experience level (it earns XP mining ore, smelting, killing): enchanting and anvil bills spend it.</param>
/// <param name="Gear">Tools, weapons and armour slot by slot (armour 5..8, bag 9..44) with their enchantments; null = unknown (the
/// director then reads them off <paramref name="Inventory"/>).</param>
/// <param name="HeldSlot">The held hotbar slot as an inventory slot (36..44), or −1.</param>
public sealed record PawnLive(bool Online, int Hp, int Food, double X, double Y, double Z, int Dim, string? Held, IReadOnlyList<Stack> Inventory,
    bool ScriptRunning, int FreeSlots = 36, int XpLevel = 0, IReadOnlyList<GearSlot>? Gear = null, int HeldSlot = -1)
{
    public P3 Block => new((int)Math.Floor(X), (int)Math.Floor(Y), (int)Math.Floor(Z));
    public static readonly PawnLive Offline = new(false, 0, 0, 0, 0, 0, 0, null, [], false, 0);
}

/// <summary>What the director needs from whatever hosts the pawn bodies (main's hive hierarchy and body registry; tests fake it).</summary>
public interface IStoryHost
{
    /// <summary>The colony's world view; null = not in game yet.</summary>
    IStoryWorld? World { get; }
    /// <summary>Spawns up to <paramref name="n"/> script-only bodies for the colony group (accounts it had before first).</summary>
    (IReadOnlyList<string> Accounts, string? Why) Spawn(string colony, int n, IReadOnlyList<string>? prefer);
    /// <summary>Releases the colony group's bodies (deposit, logout, whitelist remove). Returns how many go.</summary>
    int Release(string colony);
    PawnLive Live(string account);
    /// <summary>The pawn body's own world view (what its connection saw), or null: the colony then checks with <see cref="World"/>.</summary>
    IStoryWorld? ViewOf(string account) => null;
    /// <summary>Starts the pawn script on an online body (no model ever): null = started, else why not.</summary>
    string? StartPawn(string account, string colony);
    /// <summary>Cuts the pawn's current action (a new order, drafted, paused): its script stops; the director restarts it.</summary>
    void Interrupt(string account, string why);
    /// <summary>What the bot remembers of a chest (last seen contents), or null.</summary>
    IReadOnlyList<Stack>? KnownChest(P3 at) => null;
    /// <summary>What a body last saw inside a chest and when (any body's look: a player's deposit shows once a body opens it), or null.</summary>
    (IReadOnlyList<Stack> Contents, DateTimeOffset At)? SeenChest(P3 at) => null;
    /// <summary>The host remembers what bodies saw in chests (<see cref="SeenChest"/>): a chest nobody looked into is then worth a look
    /// before a bill waits.</summary>
    bool KnowsChests => false;
    /// <summary>
    /// Brings a pawn to <paramref name="to"/> at once (the bot is op: <c>/tp</c> through the op command path and its blocklist), for
    /// the colony owner's founding or home move. Null = sent; else why not (the director then walks it there).
    /// </summary>
    string? Teleport(string account, P3 to, string why) => "teleport isn't available here";
    /// <summary>Is main in game (the seat facts are real)? The colony retries offline pawns only then.</summary>
    bool InGame => true;
    /// <summary>Whether a pawn's account is hosted (logged in or logging in) and, when it isn't in game, why ("connecting",
    /// "not whitelisted — re-adding").</summary>
    PawnSeat Seat(string account) => new(true, null);
    /// <summary>The server's seats now (free, kept for offline regulars), or null when unknown.</summary>
    (int Free, int Kept)? Seats() => null;
    /// <summary>
    /// Brings exactly these pawn accounts in: a hosted one is whitelisted again and retried now; another logs in, the player cap
    /// permitting (<paramref name="overCap"/>: the regulars' seats too). Never another account. (account, null = started | why not).
    /// </summary>
    /// <summary>One pawn leaves the colony: it deposits, logs out and its account is released (whitelist removal through main).
    /// Null = on its way; else why not.</summary>
    string? ReleasePawn(string account, string why) => "releasing one pawn isn't available here";
    IReadOnlyList<(string Account, string? Why)> Bring(string colony, IReadOnlyList<string> accounts, bool overCap) =>
        accounts.Select(a => (a, (string?)"bringing pawns online isn't available here")).ToList();
    /// <summary>Is this player name one of the hive's own bots (main, a hive, a body): an attack on it is an attack on a bot.</summary>
    bool IsBot(string name) => false;
    /// <summary>
    /// The owner's confirmed order lets this pawn's body fight exactly that player entity (on), or no longer (off). Bodies never fight
    /// a player without it (the client's hard gate).
    /// </summary>
    void PermitAttack(string account, int entity, bool on) { }
    /// <summary>The pawn body's movement multiplier (1 normal, 2 fast, 3 faster; StoryColony.Speed.cs). Only ever a colony pawn's.</summary>
    void SetSpeed(string account, double multiplier) { }
    /// <summary>The pawn body's speed boost as it runs (null = offline or unknown).</summary>
    PawnSpeed? Speed(string account) => null;
    /// <summary>Can the colony ask the server for a Speed effect (1.12.2 with an op account)?</summary>
    bool SpeedEffectAvailable => false;
    /// <summary>
    /// A Speed effect on a pawn through the op command path (<c>/effect &lt;pawn&gt; minecraft:speed 30 &lt;amplifier&gt; true</c>; −1 clears
    /// it): the gate's scoped exception, managed accounts only, the blocklist applies. Null = sent; else why not.
    /// </summary>
    string? SpeedEffect(string account, int amplifier, string why) => "speed effects aren't available here";
}

/// <param name="Hosted">The account is one of the colony's bodies right now (online, or its login is under way / looping).</param>
/// <param name="Why">Why it isn't in game, when known.</param>
public sealed record PawnSeat(bool Hosted, string? Why);

/// <summary>
/// A pawn body's <see cref="PawnLive"/> read off its <see cref="WorldState"/> (the live host's <see cref="IStoryHost.Live"/>). The
/// director asks many times a second per pawn (each pick, the tick, every state the browser polls); the inventory totals and the gear
/// list are kept per body until its <see cref="MineClaw.World.Inventory.InventoryManager.Version"/> moves, the rest is read each time.
/// Thread-safe.
/// </summary>
public sealed class PawnLiveCache
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Pockets> _pockets = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Pockets(WorldState World, long Version, IGameData Data, IReadOnlyList<Stack> Inventory, IReadOnlyList<GearSlot> Gear, int Free);

    public PawnLive Read(string account, WorldState w, IGameData d, bool running)
    {
        long v = w.Inventory.Version; // before the reads: a change while reading moves it, and the next read starts over
        if (!_pockets.TryGetValue(account, out var pk) || !ReferenceEquals(pk.World, w) || pk.Version != v || !ReferenceEquals(pk.Data, d))
            _pockets[account] = pk = new Pockets(w, v, d, InventoryOf(w), GearOf(w, d), w.Inventory.EmptySlotCount());
        var held = w.Inventory.HeldItem(w.Player.HeldSlot);
        var p = w.Player.Position;
        return new PawnLive(true, w.Player.Health, w.Player.Food, p.X, p.Y, p.Z, w.Dimension, held.IsEmpty ? null : d.Item(held.Id).Name, pk.Inventory,
            running, pk.Free, w.Player.Snapshot.XpLevel, pk.Gear, 36 + w.Player.HeldSlot);
    }

    /// <summary>Totals by item over the bag and hotbar (9..44).</summary>
    private static List<Stack> InventoryOf(WorldState w)
    {
        var inv = new List<Stack>();
        for (int slot = 9; slot <= 44; slot++)
        {
            var s = w.Inventory.GetPlayerSlot(slot);
            if (s.IsEmpty) continue;
            // A 1.12.2 potion's type is NBT (its damage is 0): the colony tells water bottles from awkward or swiftness potions by meta.
            int meta = MineClaw.Data.Brewing.IsPotionItem(s.Id) && MineClaw.Data.Brewing.TypeId(s.Tag?.Potion) is >= 0 and var pt ? pt : s.Damage;
            int i = inv.FindIndex(x => x.Id == s.Id && x.Meta == meta);
            if (i >= 0) inv[i] = inv[i] with { Count = inv[i].Count + s.Count }; else inv.Add(new Stack(s.Id, meta, s.Count));
        }
        return inv;
    }

    /// <summary>The body's tools, weapons and armour slot by slot (armour 5..8, bag 9..44) with their enchantments.</summary>
    private static List<GearSlot> GearOf(WorldState w, IGameData d)
    {
        var list = new List<GearSlot>();
        for (int slot = 5; slot <= 44; slot++)
        {
            var s = w.Inventory.GetPlayerSlot(slot);
            if (s.IsEmpty || !StoryTools.IsGear(d, s.Id)) continue;
            list.Add(new GearSlot(slot, s.Id, s.Damage, s.Count, MineClaw.Data.Enchantments.Of(s)));
        }
        return list;
    }
}

/// <summary><see cref="IStoryWorld"/> over a client's <see cref="WorldState"/>.</summary>
public sealed class WorldStateStoryWorld(Func<WorldState?> world, IGameData data) : IStoryWorld
{
    public IGameData Data => data;
    public long TimeOfDay => world()?.TimeOfDay ?? 0;
    public bool TimeFrozen => world()?.TimeFrozen ?? false;
    public int Height => world()?.WorldHeight ?? 128;
    public int Dimension => world()?.Dimension ?? 0;
    public bool Loaded(int x, int z) => world()?.IsLoaded(x, z) ?? false;
    public int? NightLight(int x, int y, int z) => world() is { } w ? w.Light.LightLevel(x, y, z, LightEngine.NightSubtracted) : null;

    public (int Id, int Meta) Block(int x, int y, int z) => world() is { } w ? (w.GetBlock(x, y, z), w.GetMeta(x, y, z)) : (0, 0);

    public IReadOnlyList<StoryEntity> Entities() => world() is { } w ? EntitiesOf(w, data) : [];

    /// <summary>What one body's tracker holds as the colony sees it (drops, mobs, players).</summary>
    internal static List<StoryEntity> EntitiesOf(WorldState w, IGameData data)
    {
        var list = new List<StoryEntity>();
        foreach (var e in w.Entities.All())
        {
            DateTimeOffset seen = DateTime.SpecifyKind(e.UpdatedAt, DateTimeKind.Utc), first = DateTime.SpecifyKind(e.SpawnedAt, DateTimeKind.Utc);
            switch (e.Kind)
            {
                case EntityKind.Item when e.Item is { IsEmpty: false } it:
                    list.Add(new StoryEntity(e.Id, "item", data.Item(it.Id).Name, e.Position.X, e.Position.Y, e.Position.Z, false, it.Id, it.Damage, it.Count,
                        seen, first));
                    break;
                case EntityKind.Mob:
                    list.Add(new StoryEntity(e.Id, "mob", e.DisplayName.ToLowerInvariant(), e.Position.X, e.Position.Y, e.Position.Z, e.IsHostileMob,
                        SeenAt: seen, FirstSeen: first));
                    break;
                case EntityKind.Player:
                    list.Add(new StoryEntity(e.Id, "player", e.DisplayName, e.Position.X, e.Position.Y, e.Position.Z, SeenAt: seen, FirstSeen: first));
                    break;
            }
        }
        return list;
    }
}

/// <summary>
/// The colony's sight: main's world where it has the column live, else any online body (pawns, hive bodies) that has it live.
/// Main alone misses columns only the pawns load (live 11-04: home ~160 blocks from main, chop/cut boxes there designated 0).
/// Entities are merged by id (server-wide ids); time, height and dimension are main's.
/// </summary>
public sealed class MergedStoryWorld(Func<WorldState?> main, Func<IReadOnlyList<WorldState>> others, IGameData data) : IStoryWorld
{
    private IReadOnlyList<WorldState> _others = [];
    private long _othersAt;

    public IGameData Data => data;
    public long TimeOfDay => main()?.TimeOfDay ?? 0;
    public int Height => main()?.WorldHeight ?? 128;
    public int Dimension => main()?.Dimension ?? 0;

    /// <summary>The other bodies in main's dimension, refreshed at most every half second (the picker asks per cell).</summary>
    private IReadOnlyList<WorldState> Others()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _othersAt) < 500) return _others;
        int dim = Dimension;
        IReadOnlyList<WorldState> list;
        try { list = others().Where(w => w.Dimension == dim && !ReferenceEquals(w, main())).ToList(); }
        catch (Exception) { list = []; }
        _others = list;
        Interlocked.Exchange(ref _othersAt, now);
        return list;
    }

    /// <summary>The body that answered the last lookup main couldn't (valid with the <see cref="Others"/> list it came from): the
    /// pickers scan areas cell by cell, so the next cell is nearly always in a column the same body holds. Bodies on one shared chunk
    /// store hold the same column object, so which of them answers doesn't change the answer.</summary>
    private sealed record Hint(IReadOnlyList<WorldState> List, WorldState World);
    private Hint? _hint;

    /// <summary>The live column at x,z: main's first, else the hinted body's, else the first body's that has it; null = nobody.
    /// One dictionary lookup per body asked (live 11-04 every cell near a far home asked main, then up to ten pawns, twice).</summary>
    private ChunkColumn? Col(int x, int z, out WorldState? by)
    {
        int cx = x >> 4, cz = z >> 4;
        var m = main();
        if (m?.GetColumn(cx, cz) is { HasData: true } mc) { by = m; return mc; }
        var list = Others();
        if (_hint is { } h && ReferenceEquals(h.List, list) && h.World.GetColumn(cx, cz) is { HasData: true } hc) { by = h.World; return hc; }
        foreach (var w in list)
            if (w.GetColumn(cx, cz) is { HasData: true } c)
            {
                _hint = new Hint(list, w);
                by = w;
                return c;
            }
        by = null;
        return null;
    }

    /// <summary>The world that has column x,z live (see <see cref="Col"/>); null = nobody.</summary>
    private WorldState? At(int x, int z) => Col(x, z, out var by) != null ? by : null;

    public bool Loaded(int x, int z) => Col(x, z, out _) != null;

    public (int Id, int Meta) Block(int x, int y, int z) =>
        Col(x, z, out _) is { } c && (uint)y < (uint)c.Height ? (c.GetBlock(x & 15, y, z & 15), c.GetMeta(x & 15, y, z & 15)) : (0, 0);

    public int? NightLight(int x, int y, int z) => At(x, z) is { } w ? w.Light.LightLevel(x, y, z, LightEngine.NightSubtracted) : null;

    /// <summary>Every body's entities by id: the freshest copy (the body nearest it gets its moves) with the earliest first sighting
    /// (a drop's age). A mob only one body's tracker still holds keeps that body's last update, so the colony can tell it is stale.</summary>
    public IReadOnlyList<StoryEntity> Entities()
    {
        var byId = new Dictionary<int, int>();
        var list = new List<StoryEntity>();
        void Add(WorldState w)
        {
            foreach (var e in WorldStateStoryWorld.EntitiesOf(w, data))
            {
                if (!byId.TryGetValue(e.Id, out var i)) { byId[e.Id] = list.Count; list.Add(e); continue; }
                var old = list[i];
                var first = old.FirstSeen is { } a && e.FirstSeen is { } b ? (a < b ? a : b) : old.FirstSeen ?? e.FirstSeen;
                list[i] = (e.SeenAt > old.SeenAt ? e : old) with { FirstSeen = first };
            }
        }
        if (main() is { } m) Add(m);
        foreach (var w in Others()) Add(w);
        return list;
    }
}

