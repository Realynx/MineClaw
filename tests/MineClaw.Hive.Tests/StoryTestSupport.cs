using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>A fixture world for the Story director: blocks by position (stone floor at y 63), entities, the clock.</summary>
internal sealed class FakeStoryWorld(IGameData? data = null) : IStoryWorld
{
    public IGameData Data { get; } = data ?? Beta181GameData.Instance;
    public long TimeOfDay { get; set; } = 6000; // 12:00
    public int Height => 128;
    public System.Collections.Concurrent.ConcurrentDictionary<P3, (int Id, int Meta)> Blocks { get; } = new(); // a test may change a block while a report waits
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
    /// <summary>A pawn's own world view (its connection's), when a test gives it one; else the colony checks with main's.</summary>
    public Dictionary<string, IStoryWorld> Views { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IStoryWorld? ViewOf(string account) => Views.GetValueOrDefault(account);
    public string? StartPawn(string account, string colony) { Started.Add(account); return null; }
    public void Interrupt(string account, string why) => Interrupted.Add(account + ": " + why);
    public List<(string Account, P3 To)> Teleported { get; } = new();
    /// <summary>Non-null: every teleport is refused with this (the director walks the pawn instead).</summary>
    public string? TeleportError;
    public string? Teleport(string account, P3 to, string why)
    {
        if (TeleportError != null) return TeleportError;
        Teleported.Add((account, to));
        return null;
    }
    public IReadOnlyList<Stack>? KnownChest(P3 at) => Chests.TryGetValue(at, out var c) ? c : null;
    /// <summary>Player names that are the hive's own bots.</summary>
    public HashSet<string> Bots { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsBot(string name) => Bots.Contains(name);
    /// <summary>Attack permits in force: (pawn, entity).</summary>
    public HashSet<(string Pawn, int Entity)> Permits { get; } = new();
    public void PermitAttack(string account, int entity, bool on) { if (on) Permits.Add((account, entity)); else Permits.Remove((account, entity)); }
    /// <summary>Fast / Faster: each body's requested multiplier, the cap a test's "server" left, and the Speed effect commands.</summary>
    public Dictionary<string, double> Speeds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> SpeedCaps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Pawn, int Amp)> Effects { get; } = new();
    public bool EffectsAvailable;
    public void SetSpeed(string account, double multiplier) => Speeds[account] = multiplier;
    public PawnSpeed? Speed(string account) => Live(account).Online && Speeds.TryGetValue(account, out var m)
        ? new PawnSpeed(m, Math.Min(m, SpeedCaps.GetValueOrDefault(account, 3)), 1, SpeedCaps.ContainsKey(account) ? 2 : 0) : null;
    public bool SpeedEffectAvailable => EffectsAvailable;
    public string? SpeedEffect(string account, int amplifier, string why) { Effects.Add((account, amplifier)); return null; }
    /// <summary>Pawns released one by one (removed from the colony).</summary>
    public List<string> ReleasedPawns { get; } = new();
    public string? ReleasePawn(string account, string why) { ReleasedPawns.Add(account); Lives.Remove(account); return null; }
    /// <summary>The server's seats (free, kept for regulars); null = unknown.</summary>
    public (int Free, int Kept)? SeatsNow;
    public (int Free, int Kept)? Seats() => SeatsNow;

    /// <summary>A pawn in game with <paramref name="inv"/> and a stone pickaxe (the fixture world's floor is stone, which takes one: a
    /// pawn without is <see cref="Bare"/>).</summary>
    public static PawnLive Live(double x, double y, double z, int hp = 20, int food = 20, params Stack[] inv) =>
        new(true, hp, food, x + 0.5, y, z + 0.5, 0, null, [.. inv, new Stack(274, 0, 1)], true, 35 - inv.Length);

    /// <summary>A pawn in game with exactly <paramref name="inv"/> (no tools unless given).</summary>
    public static PawnLive Bare(double x, double y, double z, params Stack[] inv) =>
        new(true, 20, 20, x + 0.5, y, z + 0.5, 0, null, inv, true, 36 - inv.Length);
}

internal static class StoryRig
{
    /// <summary>A colony with <paramref name="pawns"/> pawns at the origin, all work at priority 3, a home at 0,64,0.</summary>
    public static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Make(int pawns = 1, IGameData? data = null, TimeProvider? time = null)
    {
        var w = new FakeStoryWorld(data);
        var h = new FakeStoryHost(w);
        var c = new StoryColony(new ColonyMeta { Id = "story-1", Name = "Test", Home = new P3(0, 64, 0) }, h, time)
        {
            DoneGrace = TimeSpan.FromMilliseconds(30), // a "done" the world doesn't confirm waits this long (1.5 s live)
        };
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
