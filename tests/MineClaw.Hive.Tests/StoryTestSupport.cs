using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>A fixture world for the Story director: blocks by position (stone floor at y 63), entities, the clock.</summary>
internal sealed class FakeStoryWorld(IGameData? data = null) : IStoryWorld
{
    public IGameData Data { get; } = data ?? Beta181GameData.Instance;
    public long TimeOfDay { get; set; } = 6000; // 12:00
    public int Height => 128;
    public Dictionary<P3, (int Id, int Meta)> Blocks { get; } = new();
    public List<StoryEntity> Ents { get; } = new();
    public (int Id, int Meta) Block(int x, int y, int z) => Blocks.TryGetValue(new P3(x, y, z), out var b) ? b : y <= 63 ? (1, 0) : (0, 0);
    public IReadOnlyList<StoryEntity> Entities() => Ents.ToList();
    public void Set(int x, int y, int z, int id, int meta = 0) => Blocks[new P3(x, y, z)] = (id, meta);

    /// <summary>A tree: a log column of <paramref name="h"/> from y 64 with a leaf cap.</summary>
    public void Tree(int x, int z, int h = 4)
    {
        for (int y = 64; y < 64 + h; y++) Set(x, y, z, 17);
        Set(x, 64 + h, z, 18);
    }
}

/// <summary>Pawn bodies as the director sees them: live state per account, starts and interrupts recorded.</summary>
internal sealed class FakeStoryHost(FakeStoryWorld world) : IStoryHost
{
    public IStoryWorld? World => world;
    public Dictionary<string, PawnLive> Lives { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Started { get; } = new();
    public List<string> Interrupted { get; } = new();
    public int Next = 101;
    public Dictionary<P3, List<Stack>> Chests { get; } = new();

    public (IReadOnlyList<string> Accounts, string? Why) Spawn(string colony, int n, IReadOnlyList<string>? prefer)
    {
        var got = new List<string>();
        foreach (var p in prefer ?? []) if (got.Count < n) got.Add(p);
        while (got.Count < n) got.Add("maid_chan_" + Next++);
        foreach (var a in got) Lives.TryAdd(a, Live(0, 64, 0));
        return (got, null);
    }

    public int Release(string colony) => Lives.Count;
    public PawnLive Live(string account) => Lives.TryGetValue(account, out var l) ? l : PawnLive.Offline;
    public string? StartPawn(string account, string colony) { Started.Add(account); return null; }
    public void Interrupt(string account, string why) => Interrupted.Add(account + ": " + why);
    public IReadOnlyList<Stack>? KnownChest(P3 at) => Chests.TryGetValue(at, out var c) ? c : null;

    public static PawnLive Live(double x, double y, double z, int hp = 20, int food = 20, params Stack[] inv) =>
        new(true, hp, food, x + 0.5, y, z + 0.5, 0, null, inv, true, 36 - inv.Length);
}

internal static class StoryRig
{
    /// <summary>A colony with <paramref name="pawns"/> pawns at the origin, all work at priority 3, a home at 0,64,0.</summary>
    public static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Make(int pawns = 1, IGameData? data = null, TimeProvider? time = null)
    {
        var w = new FakeStoryWorld(data);
        var h = new FakeStoryHost(w);
        var c = new StoryColony(new ColonyMeta { Id = "story-1", Name = "Test", Home = new P3(0, 64, 0) }, h, time);
        var (accounts, _) = h.Spawn("story-1", pawns, null);
        c.Joined(accounts);
        foreach (var p in c.Pawns.Values)
        {
            foreach (var t in WorkTypes.All) p.Priorities[t] = 3;
            p.Traits.Clear();
            p.Schedule = Enumerable.Repeat(ScheduleBlocks.Anything, 24).ToArray();
        }
        return (c, h, w);
    }

    public static System.Text.Json.Nodes.JsonObject Next(this StoryColony c, int i = 0) => c.Next(c.Pawns.Keys.ElementAt(i))!;
    public static string P(this StoryColony c, int i = 0) => c.Pawns.Keys.ElementAt(i);
}
