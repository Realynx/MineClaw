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
}

/// <summary><see cref="IStoryWorld"/> over a client's <see cref="WorldState"/>.</summary>
public sealed class WorldStateStoryWorld(Func<WorldState?> world, IGameData data) : IStoryWorld
{
    public IGameData Data => data;
    public long TimeOfDay => world()?.TimeOfDay ?? 0;
    public int Height => world()?.WorldHeight ?? 128;
    public int Dimension => world()?.Dimension ?? 0;
    public bool Loaded(int x, int z) => world()?.IsLoaded(x, z) ?? false;

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
