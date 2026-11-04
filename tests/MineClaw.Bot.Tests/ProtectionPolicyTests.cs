using MineClaw.Agent;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Worker;
using MineClaw.Bot.Game;
using MineClaw.Bot.Memory;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.World;
using MPos = MineClaw.Agent.Memory.BlockPos;

namespace MineClaw.Bot.Tests;

/// <summary>
/// The protection policy (ARCHITECTURE.md "Protection policy"), replaying live 2026-10-27:
/// 15:33-15:35 the bot nerd-poled up with logs inside the guessed "main house" box and couldn't mine back down ("log at
/// 6,69,-6 is protected", twice, then a script, then asking players); 15:55 "mine cobble" dug the island floor next to the
/// dual gen. Own/hive placements override soft protection (persisted, shared), trusted commands unlock soft protection,
/// hard marks and hard safety never yield, refusals say why + how to unlock, and are counted.
/// </summary>
public class ProtectionPolicyTests
{
    // ------------------------------------------------------------------ harness (real client + fake server)

    /// <summary>
    /// Tick for the pillar scenarios. A pillar jump leaves ~6 ticks to send the placement under the feet; at 5 ms ticks that is
    /// ~30 ms of wall time, which a CPU-saturated full test run (every class's FakeServer ticking in parallel, the tick loop
    /// bursting to catch up) regularly overran: "path step Place at 6,6x,-6 failed: no visible support face" in full runs only
    /// (reproduced alone with every core busy). 20 ms ticks keep the window at ~120 ms; the protection assertions are unchanged.
    /// </summary>
    private const int PillarTickMs = 20;

    private static async Task<(MinecraftClient C, FakeServer S)> StartAsync(Action<FakeServer> setup, string username = "maid_chan", int tickMs = 5)
    {
        var server = new FakeServer();
        setup(server);
        var o = new ClientOptions
        {
            Username = username, TickInterval = TimeSpan.FromMilliseconds(tickMs), ChatInterval = TimeSpan.Zero,
            RespawnDelay = TimeSpan.FromMilliseconds(10), ConnectionFactory = server.CreateClientConnection, HostileAlertRadius = 0,
        };
        var client = new MinecraftClient(o);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);
        await Until(() => client.World.Player.OnGround, 3000);
        return (client, server);
    }

    private static async Task<bool> Until(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(5);
        }
        return cond();
    }

    private static ItemStack Stack(int id, int n) => new((short)id, (byte)n, 0);

    /// <summary>The live house area: a platform at y63 inside the "main house" box 0,63,-16..15,71,-1, a planks wall.</summary>
    private static void HouseArea(FakeServer s)
    {
        s.Blocks.Clear();
        for (int x = 3; x <= 9; x++)
        for (int z = -9; z <= -3; z++)
            s.Blocks[(x, 63, z)] = Blocks.Planks;
        s.Blocks[(9, 64, -6)] = Blocks.Planks;
        s.SpawnAt = (5.5, 64, -5.5);
        s.ConsumeOnPlace = true;
        s.Inventory[36] = Stack(Blocks.Log, 12); // the live pillar was oak logs
    }

    private static AgentOptions Options()
    {
        var o = TestBot.Options();
        o.Self.TrustedPlayers = ["PoofimaFox", "kiwi"];
        return o;
    }

    private static void MarkMainHouse(AgentRuntime rt) =>
        // Exactly what the live brain saved at 14:05: mark_structure "main house" [house] 0,63,-16..15,71,-1 "Don't break" (conf 0.4).
        rt.Memory.UpsertStructure("main house", s =>
        {
            s.Kind = "house";
            s.Min = new MPos(0, 63, -16);
            s.Max = new MPos(15, 71, -1);
            s.Note = "guessed house, awaiting player confirmation. Don't break.";
            s.Confidence = 0.4;
        });

    // ------------------------------------------------------------------ rule 1: own pillar (the live sequence)

    [Fact]
    public async Task Replay_15_33_the_bot_mines_its_own_log_pillar_inside_the_main_house_box()
    {
        var (c, s) = await StartAsync(HouseArea, tickMs: PillarTickMs);
        await using var _ = c;
        using var __ = s;
        using var h = BotHost.Create(new MinecraftGameClient(c), Options(), null, new NoLlm(), persist: false);
        MarkMainHouse(h.Runtime);
        h.Protection.Invalidate();
        // Someone else's block in the box: soft-protected, and the refusal says why and how to unlock it.
        var wall = await c.DigBlockAsync(9, 64, -6);
        Assert.False(wall.Ok);
        Assert.Contains("part of the main house build", wall.Message);
        Assert.Contains("say 'you can break it'", wall.Message);

        // 15:33:11 "placed while moving (bridge/pillar block)": goto pillars up with logs.
        var up = await c.GotoAsync(new GoalBlock(6, 70, -6), GotoOptions.Default with { AllowPlace = true, ScaffoldBudget = 10 });
        Assert.True(up.Ok, up.Message);
        for (int y = 64; y <= 69; y++) Assert.False(h.Protection.IsProtected(new BlockPos(6, y, -6), Blocks.Log), $"6,{y},-6 still protected");

        // 15:34:47 "dig failed 2x the same way: log at 6,69,-6 is protected" → now it mines down, one step at a time.
        for (int y = 69; y >= 64; y--)
        {
            var r = await c.DigBlockAsync(6, y, -6);
            Assert.True(r.Ok, r.Message);
            Assert.True(await Until(() => c.World.Player.OnGround && c.World.Player.Position.Y < y + 0.5), $"didn't step down from {y}");
        }
        Assert.Equal(64, (int)Math.Floor(c.World.Player.Position.Y + 1e-6));
        Assert.True(h.ProtectionTelemetry!.Counts.GetValueOrDefault("structure") >= 1); // the wall refusal was counted
    }

    // ------------------------------------------------------------------ rule 1: persistence + hive

    private sealed class HouseBox : IProtectedRegionSource
    {
        public IReadOnlyList<(BlockPos Min, BlockPos Max)> ProtectedRegions() => [(new BlockPos(0, 63, -16), new BlockPos(15, 71, -1))];
        public IReadOnlyList<(BlockPos Min, BlockPos Max, string? Label)> LabeledRegions() =>
            [(new BlockPos(0, 63, -16), new BlockPos(15, 71, -1), "main house")];
    }

    private static Protection Policy(MinecraftClient c)
    {
        var p = new Protection(new BotOptions(), new HouseBox(), new NoMemory()) { Placed = c.World.Placed, Dimension = () => c.World.Dimension };
        c.IsProtected = p.IsProtected;
        c.SoftOverride = p.OverridesSoft;
        c.ExplainProtection = p.Explain;
        return p;
    }

    private static async Task<Dictionary<(int, int, int), int>> BuildPillarAsync(string dbPath)
    {
        var (c, s) = await StartAsync(HouseArea, tickMs: PillarTickMs);
        await using var _ = c;
        using var __ = s;
        using var db = AgentDb.Open(dbPath);
        using var store = new PlacedBlockStore(db, c.World.Placed);
        Policy(c);
        var up = await c.GotoAsync(new GoalBlock(6, 70, -6), GotoOptions.Default with { AllowPlace = true, ScaffoldBudget = 10 });
        Assert.True(up.Ok, up.Message);
        store.Flush();
        Assert.Equal(6, store.Rows);
        return new Dictionary<(int, int, int), int>(s.Blocks);
    }

    private static async Task MineDownAfterRestartAsync(string dbPath, Dictionary<(int, int, int), int> world, string username)
    {
        var (c, s) = await StartAsync(srv =>
        {
            HouseArea(srv);
            foreach (var kv in world) srv.Blocks[kv.Key] = kv.Value;
            srv.SpawnAt = (6.5, 70, -5.5); // where the bot was when the session ended: on top of the pillar
        }, username, PillarTickMs);
        await using var _ = c;
        using var __ = s;
        using var db = AgentDb.Open(dbPath);
        using var store = new PlacedBlockStore(db, c.World.Placed); // loads the shared rows
        var policy = Policy(c);
        Assert.True(await Until(() => c.World.GetBlock(6, 69, -6) == Blocks.Log));
        Assert.False(policy.IsProtected(new BlockPos(6, 69, -6), Blocks.Log));
        Assert.True(policy.IsProtected(new BlockPos(9, 64, -6), Blocks.Planks)); // the house itself stays protected
        for (int y = 69; y >= 64; y--)
        {
            var r = await c.DigBlockAsync(6, y, -6);
            Assert.True(r.Ok, $"{username}: {r.Message}");
            Assert.True(await Until(() => c.World.Player.OnGround && c.World.Player.Position.Y < y + 0.5));
        }
        store.Flush();
        Assert.Equal(0, store.Rows); // dug: no longer anyone's placed blocks
    }

    [Fact]
    public async Task The_bot_mines_its_own_pillar_after_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mineclaw-placed-{Guid.NewGuid():N}.db");
        try
        {
            var world = await BuildPillarAsync(path);
            await MineDownAfterRestartAsync(path, world, "maid_chan");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); TryDelete(path); }
    }

    [Fact]
    public async Task A_clone_mines_mains_scaffolding_through_the_shared_db()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mineclaw-placed-{Guid.NewGuid():N}.db");
        try
        {
            var world = await BuildPillarAsync(path);         // main built it
            await MineDownAfterRestartAsync(path, world, "maid_chan_67"); // the clone takes it down
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); TryDelete(path); }
    }

    [Fact]
    public void Placed_rows_written_by_one_process_reach_another_on_sync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mineclaw-placed-{Guid.NewGuid():N}.db");
        try
        {
            using var dbMain = AgentDb.Open(path);
            using var dbClone = AgentDb.Open(path);
            var main = new PlacedBlockLog();
            var clone = new PlacedBlockLog();
            using var sMain = new PlacedBlockStore(dbMain, main);
            using var sClone = new PlacedBlockStore(dbClone, clone);
            main.Note(new BlockPos(1, 70, 1), 0, Blocks.Cobblestone, "maid_chan", DateTimeOffset.UtcNow);
            sMain.Flush();
            Assert.True(sClone.Sync());
            Assert.True(clone.IsPlaced(new BlockPos(1, 70, 1), 0, Blocks.Cobblestone));
            Assert.Equal("maid_chan", clone.Get(new BlockPos(1, 70, 1), 0)!.Bot);
            Assert.False(clone.IsPlaced(new BlockPos(1, 70, 1), 0, Blocks.Planks)); // someone replaced it: not ours
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); TryDelete(path); }
    }

    private static void TryDelete(string path)
    {
        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ rules 3-6 on a host (chat, gestures, marks, telemetry)

    private static (BotHost H, FakeGameClient C) Host()
    {
        var c = new FakeGameClient();
        var h = BotHost.Create(c, Options(), null, new NoLlm(), persist: false);
        c.Floor(64, 10, Blocks.Planks);
        h.Runtime.Memory.UpsertStructure("main house", s =>
        {
            s.Kind = "house";
            s.Min = new MPos(-8, 64, -8);
            s.Max = new MPos(8, 70, 8);
        });
        for (int y = 65; y <= 67; y++) c.World.SetBlock(2, y, 2, Blocks.Planks, 0);
        h.Protection.Invalidate();
        return (h, c);
    }

    [Theory]
    [InlineData("maid_chan: you can break it: one unripe wheat next to each water hole")]
    [InlineData("maid_chan, you can break the wheat next to the water holes")]
    public async Task A_trusted_player_can_permit_named_wheat_without_a_pointing_gesture(string text)
    {
        var (c, s) = await StartAsync(srv =>
        {
            srv.Blocks[(9, 63, 8)] = Blocks.Farmland;
            srv.Blocks[(9, 64, 8)] = Blocks.Crops;
            srv.Blocks[(10, 63, 8)] = Blocks.Water;
            srv.Blocks[(8, 63, 10)] = Blocks.Farmland;
            srv.Blocks[(8, 64, 10)] = Blocks.Crops; // unrelated wheat: not by a hole
        }, "maid_chan");
        await using var _ = c;
        using var __ = s;
        var options = Options();
        options.Self.TrustedPlayers = ["player_one"];
        options.Chat.DisplayName = "maid_chan";
        using var h = BotHost.Create(new MinecraftGameClient(c), options, null, new NoLlm(), persist: false);
        var wheat = new BlockPos(9, 64, 8);
        Assert.True(h.Protection.IsProtected(wheat, Blocks.Crops));
        s.Send(new MineClaw.Protocol.Packets.ChatPacket("<player_one> " + text));
        Assert.True(await Until(() => !h.Protection.IsProtected(wheat, Blocks.Crops), 3000),
            "trusted named permit was ignored: " + string.Join(" | ", h.Runtime.Events.Snapshot().Select(e => e.Text)));
        Assert.True(h.Protection.IsProtected(new BlockPos(8, 64, 10), Blocks.Crops));
        Assert.True((await c.DigBlockAsync(9, 64, 8)).Ok);
        Assert.Equal(Blocks.Farmland, c.World.GetBlock(9, 63, 8));
        Assert.Null(h.ProtectionMarks!.PermitAt(new BlockPos(10, 63, 8), c.World.Dimension));
    }

    [Theory]
    [InlineData("unripe")]
    [InlineData("ripe")]
    [InlineData("growing")]
    public void Named_wheat_permits_filter_every_growth_stage_by_metadata(string qualifier)
    {
        var (h, c) = Host();
        using var _ = h;
        var crops = Enumerable.Range(0, 8).Select(age => (Pos: new BlockPos(age % 4 - 2, 65, age / 4 - 1), Age: age)).ToArray();
        foreach (var (p, age) in crops)
        {
            c.World.SetBlock(p.X, p.Y - 1, p.Z, Blocks.Farmland, 0);
            c.World.SetBlock(p.X, p.Y, p.Z, Blocks.Crops, age);
            // Ripe wheat is normally harvestable; individual never-break marks make every stage need a permit.
            h.ProtectionMarks!.Add(MarkKind.Never, c.World.Dimension, [(p, p)], "wheat", "PoofimaFox", null, false);
            Assert.True(h.Protection.IsProtected(p, Blocks.Crops));
        }
        c.RaiseChat($"<PoofimaFox> {c.Username} you can break the {qualifier} wheat");
        foreach (var (p, age) in crops)
        {
            bool selected = qualifier == "ripe" ? age == 7 : age < 7;
            Assert.Equal(selected, h.ProtectionMarks!.PermitAt(p, c.World.Dimension) != null);
            Assert.Equal(!selected, h.Protection.IsProtected(p, Blocks.Crops));
            Assert.Null(h.ProtectionMarks.PermitAt(p.Down(), c.World.Dimension));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void One_wheat_next_to_each_water_hole_permits_one_crop_per_separate_hole(bool connectedWaterCells)
    {
        var (h, c) = Host();
        using var _ = h;
        var holes = new[] { new BlockPos(-3, 64, 1), new BlockPos(2, 64, 1) };
        var groups = holes.Select(p => new[] { p.Up().Offset(BlockFace.North),
            connectedWaterCells ? p.Up().Offset(BlockFace.East).Offset(BlockFace.North) : p.Up().Offset(BlockFace.South) }).ToArray();
        foreach (var hole in holes)
        {
            c.World.SetBlock(hole.X, hole.Y, hole.Z, Blocks.Water, 0);
            if (connectedWaterCells) c.World.SetBlock(hole.X + 1, hole.Y, hole.Z, Blocks.Water, 0);
        }
        var unrelated = new BlockPos(0, 65, -2);
        foreach (var p in groups.SelectMany(g => g).Append(unrelated))
        {
            c.World.SetBlock(p.X, p.Y - 1, p.Z, Blocks.Farmland, 0);
            c.World.SetBlock(p.X, p.Y, p.Z, Blocks.Crops, 2);
            Assert.True(h.Protection.IsProtected(p, Blocks.Crops));
        }
        c.RaiseChat($"<PoofimaFox> {c.Username} you can break one unripe wheat next to each water hole");
        foreach (var group in groups)
        {
            Assert.Single(group, p => h.ProtectionMarks!.PermitAt(p, c.World.Dimension) != null);
            Assert.Single(group, p => !h.Protection.IsProtected(p, Blocks.Crops));
        }
        Assert.True(h.Protection.IsProtected(unrelated, Blocks.Crops));
        foreach (var p in groups.SelectMany(g => g)) Assert.Null(h.ProtectionMarks!.PermitAt(p.Down(), c.World.Dimension));
        foreach (var hole in holes)
        {
            Assert.Null(h.ProtectionMarks!.PermitAt(hole, c.World.Dimension));
            Assert.Null(h.ProtectionMarks.PermitAt(hole.Offset(BlockFace.East), c.World.Dimension));
        }
    }

    private static readonly BlockPos Wall = new(2, 66, 2);

    /// <summary>A player at (2.5, 65, 5.5) punch-pointing at the wall block.</summary>
    private static void Point(BotHost h, FakeGameClient c, int entityId, string name)
    {
        var feet = new Vec3d(2.5, 65, 5.5);
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(feet + new Vec3d(0, PlayerPhysics.EyeHeight, 0), Wall.Center);
        if (c.World.Entities.Get(entityId) == null) c.World.Entities.SpawnPlayer(entityId, name, feet, yaw, pitch);
        h.Gestures!.Observe(entityId); // a repeat at the same block just refreshes the gesture
        Assert.Contains(h.Gestures.Recent(), g => g.Player == name && g.Pos == Wall);
    }

    [Fact]
    public void A_trusted_break_that_with_a_punch_unlocks_a_soft_protected_block()
    {
        var (h, c) = Host();
        Assert.True(h.Protection.IsProtected(Wall, Blocks.Planks));
        Point(h, c, 7, "kiwi");
        c.RaiseChat("<kiwi> claudechan break that");
        Assert.False(h.Protection.IsProtected(Wall, Blocks.Planks));
        Assert.False(h.Protection.IsProtected(Wall.Up(), Blocks.Planks)); // the pointed column
        Assert.True(h.Protection.IsProtected(new BlockPos(3, 64, 3), Blocks.Planks)); // the rest of the house still protected
    }

    [Fact]
    public void An_untrusted_players_command_unlocks_nothing()
    {
        var (h, c) = Host();
        Point(h, c, 8, "stranger");
        c.RaiseChat("<stranger> claudechan break that");
        c.RaiseChat("<stranger> claudechan you can break it");
        Assert.True(h.Protection.IsProtected(Wall, Blocks.Planks));
    }

    [Fact]
    public void Never_break_marks_are_hard_until_a_trusted_player_unmarks_them()
    {
        var (h, c) = Host();
        Point(h, c, 8, "stranger");
        c.RaiseChat("<stranger> claudechan never break that");
        var v = h.Protection.Check(Wall, Blocks.Planks);
        Assert.Equal(ProtectionLevel.Hard, v.Level);
        Assert.Equal("never_break", v.Reason);
        // A command doesn't override a hard mark...
        Point(h, c, 7, "kiwi");
        c.RaiseChat("<kiwi> claudechan break that");
        Assert.True(h.Protection.IsProtected(Wall, Blocks.Planks));
        // ...a trusted "you can break it" unmarks it (and permits it).
        Point(h, c, 7, "kiwi");
        c.RaiseChat("<kiwi> claudechan you can break it");
        Assert.False(h.Protection.IsProtected(Wall, Blocks.Planks));
    }

    [Fact]
    public void You_can_break_it_right_after_a_refusal_unlocks_the_refused_block()
    {
        var (h, c) = Host();
        var v = h.Protection.Explain(Wall, Blocks.Planks); // the client asks this when it refuses
        var text = ProtectionText.Refusal("planks at 2,66,2", v);
        Assert.Contains("part of the main house build", text);
        Assert.Contains("say 'you can break it'", text);
        c.RaiseChat("<PoofimaFox> claudechan you can break it");
        Assert.False(h.Protection.IsProtected(Wall, Blocks.Planks));
    }

    /// <summary>A trusted player at (2.5, 65, 5.5) punches the instant-break block at <paramref name="punched"/>: the dig lands before the
    /// swing (vanilla clickMouse), so the block is air when the swing arrives.</summary>
    private static void PunchAndBreak(BotHost h, FakeGameClient c, BlockPos punched)
    {
        var feet = new Vec3d(2.5, 65, 5.5);
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(feet + new Vec3d(0, PlayerPhysics.EyeHeight, 0), punched.Center);
        c.World.Entities.SpawnPlayer(7, "PoofimaFox", feet, yaw, pitch);
        int id = c.World.GetBlock(punched);
        c.World.SetBlock(punched.X, punched.Y, punched.Z, 0, 0);
        h.Gestures!.Observe(7);
        Assert.Contains(h.Gestures.Recent(), g => g.Player == "PoofimaFox" && g.Pos == punched && g.BlockId == id);
    }

    [Fact]
    public void You_can_break_it_after_a_punch_that_broke_the_block_binds_to_that_kind_not_a_stale_refusal()
    {
        // An instant-break target must take precedence over a stale refusal. House torches are soft-protected.
        var (h, c) = Host();
        var torches = new[] { new BlockPos(1, 65, 3), new BlockPos(3, 65, 3), new BlockPos(1, 65, 4) };
        foreach (var t in torches) c.World.SetBlock(t.X, t.Y, t.Z, Blocks.Torch, 5);
        var punched = new BlockPos(2, 65, 3);
        c.World.SetBlock(punched.X, punched.Y, punched.Z, Blocks.Torch, 5);
        h.Protection.Invalidate();
        Assert.All(torches, t => Assert.True(h.Protection.IsProtected(t, Blocks.Torch)));
        h.Protection.Explain(Wall, Blocks.Planks);       // an earlier refusal...
        c.World.SetBlock(Wall.X, Wall.Y, Wall.Z, 0, 0);  // ...whose block is gone since
        PunchAndBreak(h, c, punched);
        c.RaiseChat($"<PoofimaFox> {c.Username} you can break it");
        Assert.All(torches, t => Assert.False(h.Protection.IsProtected(t, Blocks.Torch), $"{t} still protected"));
        Assert.True(h.Protection.IsProtected(new BlockPos(2, 67, 2), Blocks.Planks)); // not the refused wall's column
        Assert.True(h.Protection.IsProtected(new BlockPos(2, 65, 2), Blocks.Planks));
    }

    [Fact]
    public void Punching_wild_tall_grass_and_you_can_break_it_says_it_needs_no_permit()
    {
        var (h, c) = Host();
        c.World.SetBlock(1, 65, 3, Blocks.TallGrass, 1);
        var punched = new BlockPos(2, 65, 3);
        c.World.SetBlock(punched.X, punched.Y, punched.Z, Blocks.TallGrass, 1);
        PunchAndBreak(h, c, punched);
        c.RaiseChat($"<PoofimaFox> {c.Username} you can break it");
        Assert.Contains(c.Said, l => l.Contains("tallgrass") && l.Contains("can already break it"));
        Assert.True(h.Protection.IsProtected(new BlockPos(2, 65, 2), Blocks.Planks)); // nothing else got unlocked
    }

    [Fact]
    public void You_can_break_it_with_only_a_stale_refusal_asks_which_block_instead_of_permitting_nothing()
    {
        var (h, c) = Host();
        h.Protection.Explain(Wall, Blocks.Planks);
        c.World.SetBlock(Wall.X, Wall.Y, Wall.Z, 0, 0); // broken meanwhile
        c.RaiseChat($"<PoofimaFox> {c.Username} you can break it");
        Assert.Contains(c.Said, l => l.Contains("which block"));
        Assert.True(h.Protection.IsProtected(new BlockPos(2, 67, 2), Blocks.Planks));
        Assert.True(h.Protection.IsProtected(new BlockPos(2, 65, 2), Blocks.Planks));
    }

    [Fact]
    public void A_large_area_asks_one_confirmation_line_first()
    {
        var (h, c) = Host();
        c.RaiseChat("<PoofimaFox> claudechan you can break the main house");
        Assert.True(h.Protection.IsProtected(Wall, Blocks.Planks)); // not yet
        Assert.Contains(c.Said, l => l.Contains("say 'yes'"));
        c.RaiseChat("<PoofimaFox> claudechan yes");
        Assert.False(h.Protection.IsProtected(Wall, Blocks.Planks));
        Assert.False(h.Protection.IsProtected(new BlockPos(3, 64, 3), Blocks.Planks));
    }

    [Fact]
    public void Refusals_are_counted_by_reason_and_a_repeat_says_already_asked()
    {
        var (h, _) = Host();
        var first = h.Protection.Explain(Wall, Blocks.Planks);
        var again = h.Protection.Explain(Wall, Blocks.Planks);
        Assert.False(first.Repeat);
        Assert.True(again.Repeat);
        Assert.Contains("don't ask again", ProtectionText.Refusal("planks", again));
        Assert.Equal(2, h.ProtectionTelemetry!.Counts["structure"]);
        Assert.Contains("structure 2", h.ProtectionTelemetry.Summary());
    }

    [Fact]
    public void Containers_open_only_for_an_exact_permit()
    {
        var (h, c) = Host();
        var chest = new BlockPos(-3, 65, -3);
        c.World.SetBlock(chest.X, chest.Y, chest.Z, Blocks.Chest, 0);
        c.World.Placed.Note(chest, 0, Blocks.Chest, "maid_chan", DateTimeOffset.UtcNow); // even one I placed
        Assert.True(h.Protection.IsProtected(chest, Blocks.Chest));
        h.ProtectionMarks!.Add(MarkKind.Permit, 0, [(new BlockPos(-8, 64, -8), new BlockPos(8, 70, 8))], "the main house", "PoofimaFox",
            TimeSpan.FromMinutes(5), exact: false);
        Assert.True(h.Protection.IsProtected(chest, Blocks.Chest)); // an area permit doesn't cover a chest that may hold items
        Assert.False(h.Protection.IsProtected(new BlockPos(3, 64, 3), Blocks.Planks));
        h.ProtectionMarks.Add(MarkKind.Permit, 0, [(chest, chest)], "the chest", "PoofimaFox", TimeSpan.FromMinutes(5), exact: true);
        Assert.False(h.Protection.IsProtected(chest, Blocks.Chest));
    }

    [Fact]
    public void Dont_mine_the_middle_one_is_too_vague_for_a_gen_cell_but_its_coordinates_mark_it_never_break()
    {
        // Live 10-27 21:17 (Fox: all 3 cells are minable): a vague "the middle one" took a regen cell away for good. Only the
        // exact cell named again marks it.
        var (h, c) = Host();
        var cells = new[] { new BlockPos(4, 64, 6), new BlockPos(5, 64, 7), new BlockPos(6, 64, 8) };
        h.ProtectionCommands!.GenCells = () => cells;
        c.RaiseChat("<kiwi> claudechan don't mine the middle one");
        Assert.Null(h.ProtectionMarks!.NeverAt(new BlockPos(5, 64, 7), 0));
        c.RaiseChat("<kiwi> claudechan don't mine 5,64,7");
        Assert.NotNull(h.ProtectionMarks.NeverAt(new BlockPos(5, 64, 7), 0));
        Assert.Null(h.ProtectionMarks.NeverAt(new BlockPos(4, 64, 6), 0));
        Assert.True(h.Protection.IsProtected(new BlockPos(5, 64, 7), Blocks.Cobblestone)); // even a dig cell: hard
    }

    [Fact]
    public void Op_mode_forgets_a_remembered_spawn_protection_region()
    {
        var (h, _) = Host();
        h.Runtime.Observations.Upsert(new Observation("protected_region", "spawn", "spawn protection: can't break blocks within 16 of 0,66,-3")
            { Bounds = Box.Of(-16, 0, -19, 16, 127, 13) });
        h.Runtime.Memory.AddNote("spawn", "spawn protection blocks digging near spawn");
        BotHost.ForgetSpawnProtection(h.Runtime, null);
        Assert.Null(h.Runtime.Observations.Get("protected_region", "spawn"));
        Assert.True(new BotOptions().Protection.ServerOp);
    }

    [Fact]
    public void Parser_reads_the_live_lines()
    {
        Assert.Equal(ProtectionIntentKind.Allow, ProtectionCommands.Parse("you can mine down on it")!.Kind);
        Assert.Null(ProtectionCommands.Parse("you can mine down on it")!.Target);
        Assert.Equal(ProtectionIntentKind.Command, ProtectionCommands.Parse("mine the logs")!.Kind);
        Assert.Equal(ProtectionIntentKind.Never, ProtectionCommands.Parse("don't mine the middle one")!.Kind);
        Assert.Equal(ProtectionIntentKind.Never, ProtectionCommands.Parse("you can't break the gen")!.Kind);
        Assert.Equal("main house", ProtectionCommands.Parse("you can break the main house")!.Target!.Replace("the ", ""));
        Assert.Equal(new BlockPos(14, 64, -5), ProtectionCommands.Parse("you can break the chest at 14,64,-5")!.Coords);
        Assert.Null(ProtectionCommands.Parse("how's it going?"));
        Assert.Equal(ProtectionIntentKind.Confirm, ProtectionCommands.Parse("yes")!.Kind);
    }

    // ------------------------------------------------------------------ rule 5: the worker stops at the first refusal

    [Fact]
    public void The_worker_treats_a_protection_refusal_as_final()
    {
        Assert.True(WorkerAgent.IsProtectionRefusal(ProtectionText.Refusal("log at 6,69,-6",
            new ProtectionVerdict(ProtectionLevel.Soft, "structure", "part of the main house build"))));
        Assert.True(WorkerAgent.IsProtectionRefusal("dig failed: log at 6,70,-6 is protected"));
        Assert.False(WorkerAgent.IsProtectionRefusal("can't reach log at 6,69,-6"));
    }

    // ------------------------------------------------------------------ live 15:55: mine cobble = the generator cells only

    private static void DualGenWithFloor(FakeServer srv)
    {
        srv.Blocks.Clear();
        for (int x = 1; x <= 11; x++)
        for (int z = 3; z <= 11; z++)
            srv.Blocks[(x, 63, z)] = Blocks.Cobblestone; // the island floor around the gen
        for (int x = 4; x <= 8; x++)
        {
            srv.Blocks[(x, 64, 6)] = Blocks.Cobblestone;
            srv.Blocks[(x, 64, 8)] = Blocks.Cobblestone;
        }
        srv.Blocks[(4, 64, 7)] = Blocks.Lava;
        srv.Blocks[(5, 64, 7)] = Blocks.Cobblestone; // dig cell
        srv.Blocks[(6, 64, 7)] = Blocks.Water;
        srv.Blocks[(7, 64, 7)] = Blocks.Cobblestone; // dig cell
        srv.Blocks[(8, 64, 7)] = Blocks.Lava;
        srv.Regenerate[(5, 64, 7)] = Blocks.Cobblestone;
        srv.Regenerate[(7, 64, 7)] = Blocks.Cobblestone;
        srv.SpawnAt = (6.5, 64, 4.5);
        srv.Inventory[36] = Stack(Items.StonePickaxe, 1);
        srv.DropOnDig = true;
    }

    [Fact]
    public async Task Mine_20_cobble_at_the_dual_gen_digs_only_the_regen_cells_never_the_floor()
    {
        var (c, s) = await StartAsync(DualGenWithFloor);
        await using var _ = c;
        using var __ = s;
        using var h = BotHost.Create(new MinecraftGameClient(c), Options(), null, new NoLlm(), persist: false);
        var r = await h.RunAs(Agent.Llm.ModelTier.Worker, "mine", new { block = "cobblestone", count = 20 });
        var dug = s.Dug.ToList();
        Assert.True(dug.Count >= 20, $"dug {dug.Count}: {r.Text}");
        Assert.All(dug, d => Assert.True(d is { X: 5 or 7, Y: 64, Z: 7 }, $"dug {d.X},{d.Y},{d.Z} (not a dig cell)"));
        for (int x = 1; x <= 11; x++)
        for (int z = 3; z <= 11; z++)
            Assert.Equal(Blocks.Cobblestone, s.Blocks.GetValueOrDefault((x, 63, z)));
        Assert.Contains("generator cell", r.Text);
    }

    [Fact]
    public async Task Mine_cobble_without_a_known_generator_asks_which_cells_and_digs_nothing()
    {
        var (c, s) = await StartAsync(srv =>
        {
            srv.Blocks.Clear();
            for (int x = 3; x <= 11; x++)
            for (int z = 3; z <= 11; z++)
                srv.Blocks[(x, 63, z)] = Blocks.Cobblestone;
            srv.Inventory[36] = Stack(Items.StonePickaxe, 1);
        });
        await using var _ = c;
        using var __ = s;
        using var h = BotHost.Create(new MinecraftGameClient(c), Options(), null, new NoLlm(), persist: false);
        var r = await h.RunAs(Agent.Llm.ModelTier.Worker, "mine", new { block = "cobblestone", count = 5 });
        Assert.False(r.Ok);
        Assert.StartsWith("needs_input:", r.Text);
        Assert.Empty(s.Dug);
    }

    // ------------------------------------------------------------------ clutch blocks before height work (live 15:42, clone 67)

    [Fact]
    public async Task Chop_tree_first_gets_clutch_blocks_from_the_hive()
    {
        var (h, c) = Host();
        h.Acquisition!.RequestHandoff = (ask, _) =>
        {
            c.Give(20, Blocks.Cobblestone, ask.Count);
            return Task.FromResult<string?>("67 tossed cobble");
        };
        var r = await h.RunAs(Agent.Llm.ModelTier.Worker, "chop_tree", new { });
        Assert.True(MineClaw.Bot.GameTools.ClutchBlocks.Held(c) >= MineClaw.Bot.GameTools.ClutchBlocks.Want, r.Text);
        Assert.StartsWith("carrying", r.Text);
    }
}
