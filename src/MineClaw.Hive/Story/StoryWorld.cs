using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Hive.Story;

/// <summary>Something the colony sees: a drop, an animal or monster, a player.</summary>
public sealed record StoryEntity(int Id, string Kind, string Name, double X, double Y, double Z, bool Hostile = false, int ItemId = -1, int ItemMeta = 0,
    int Count = 0)
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
public sealed record PawnLive(bool Online, int Hp, int Food, double X, double Y, double Z, int Dim, string? Held, IReadOnlyList<Stack> Inventory,
    bool ScriptRunning, int FreeSlots = 36)
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
}

/// <param name="Hosted">The account is one of the colony's bodies right now (online, or its login is under way / looping).</param>
/// <param name="Why">Why it isn't in game, when known.</param>
public sealed record PawnSeat(bool Hosted, string? Why);

/// <summary><see cref="IStoryWorld"/> over a client's <see cref="WorldState"/>.</summary>
public sealed class WorldStateStoryWorld(Func<WorldState?> world, IGameData data) : IStoryWorld
{
    public IGameData Data => data;
    public long TimeOfDay => world()?.TimeOfDay ?? 0;
    public int Height => world()?.WorldHeight ?? 128;
    public int Dimension => world()?.Dimension ?? 0;
    public bool Loaded(int x, int z) => world()?.IsLoaded(x, z) ?? false;
    public int? NightLight(int x, int y, int z) => world() is { } w ? w.Light.LightLevel(x, y, z, LightEngine.NightSubtracted) : null;

    public (int Id, int Meta) Block(int x, int y, int z) => world() is { } w ? (w.GetBlock(x, y, z), w.GetMeta(x, y, z)) : (0, 0);

    public IReadOnlyList<StoryEntity> Entities()
    {
        if (world() is not { } w) return [];
        var list = new List<StoryEntity>();
        foreach (var e in w.Entities.All())
        {
            switch (e.Kind)
            {
                case EntityKind.Item when e.Item is { IsEmpty: false } it:
                    list.Add(new StoryEntity(e.Id, "item", data.Item(it.Id).Name, e.Position.X, e.Position.Y, e.Position.Z, false, it.Id, it.Damage, it.Count));
                    break;
                case EntityKind.Mob:
                    list.Add(new StoryEntity(e.Id, "mob", e.DisplayName.ToLowerInvariant(), e.Position.X, e.Position.Y, e.Position.Z, e.IsHostileMob));
                    break;
                case EntityKind.Player:
                    list.Add(new StoryEntity(e.Id, "player", e.DisplayName, e.Position.X, e.Position.Y, e.Position.Z));
                    break;
            }
        }
        return list;
    }
}
