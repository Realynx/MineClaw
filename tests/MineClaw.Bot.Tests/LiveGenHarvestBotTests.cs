using MineClaw.Agent;
using MineClaw.Agent.Rag;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Perception;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;
using MPos = MineClaw.Agent.Memory.BlockPos;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-27 21:12-21:19 at the three-cell gen (4,64,6 / 5,64,7 / 6,64,8): one stand passed for every cell, "store":true read as
/// a chest name, leftovers not deposited, kiwi's vague "never mine the middle cell" note, and the yield guard retracting real cells.
/// </summary>
public class LiveGenHarvestBotTests
{
    private static readonly BlockPos West = new(4, 64, 6), Middle = new(5, 64, 7), East = new(6, 64, 8);

    private static AgentOptions Trusted()
    {
        var o = TestBot.Options();
        o.Self.TrustedPlayers = ["PoofimaFox", "kiwi"];
        return o;
    }

    private static void DualGen(AgentRuntime rt) =>
        rt.Memory.UpsertStructure("dual cobble generator", s =>
        {
            s.Kind = "cobble_generator";
            s.Min = new MPos(3, 63, 5);
            s.Max = new MPos(7, 66, 9);
            s.Steps.AddRange(["stood at 7,63,8 facing cobblestone", "punched cobblestone at 6,64,8 (x2)"]);
            s.KeyBlocks.Add(new MineClaw.Agent.Memory.KeyBlock { Label = "mine_here", X = 6, Y = 64, Z = 8 });
        });

    [Fact]
    public void Stand_reach_matches_the_live_layout()
    {
        var w = new WorldState();
        Assert.False(GeneratorStand.Reaches(w, new BlockPos(3, 64, 4), East)); // live: "6,64,8 unreachable from stand 3,64,4"
        Assert.True(GeneratorStand.Reaches(w, new BlockPos(3, 64, 4), West));
        Assert.True(GeneratorStand.Reaches(w, new BlockPos(7, 63, 8), East));  // the demo's stand in the hole
    }

    [Fact]
    public async Task A_stand_that_cant_reach_the_cell_falls_back_to_the_cells_own_taught_stand()
    {
        var (h, c) = TestBot.Create();
        DualGen(h.Runtime);
        c.Give(36, Items.StonePickaxe, 1);
        var r = await h.Run("harvest_generator", new { at = "dual cobble generator", count = 20, stand = "3,64,4" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("generator 6,64,8 20 stand 7,63,8", c.CallList);
        Assert.Contains("stand 3,64,4 can't reach 6,64,8: using its taught stand 7,63,8", r.Text);
    }

    [Fact]
    public async Task Store_true_means_the_default_store_not_a_chest_named_true()
    {
        var (h, c) = TestBot.Create();
        DualGen(h.Runtime);
        c.Give(36, Items.StonePickaxe, 1);
        // Clone 69 at 21:53:19: do{goto, then harvest_generator{store:true}} → "unknown chest 'true'".
        var r = await h.Run("harvest_generator", new { at = "dual cobble generator", count = 8, store = true });
        Assert.DoesNotContain("unknown chest", r.Text);
        Assert.True(r.Ok, r.Text);
        Assert.True((await h.Run("harvest_generator", new { at = "dual cobble generator", count = 8, store = "true" })).Ok);
        Assert.True((await h.Run("harvest_generator", new { at = "dual cobble generator", count = 8, store = "14,64,-5" })).Ok);
        var bad = await h.Run("harvest_generator", new { at = "dual cobble generator", count = 8, store = "the blue box" });
        Assert.False(bad.Ok);
        Assert.Contains("store takes true, x,y,z or a chest/waypoint name", bad.Text);
    }

    [Fact]
    public async Task Loop_deposits_what_it_has_at_the_end_even_short_of_the_target()
    {
        // Live 21:12: "Chest 14,64,-5 not topped up (have 8 cobble in inventory)".
        var (_, c) = TestBot.Create();
        c.Give(36, Items.StonePickaxe, 1);
        c.Results["generator"] = () => ActionResult.Success("generator: dug 8, +8 cobblestone (have 8); generator at 4,64,6 stopped producing (now 4,64,6 air): no water next to 4,64,6 any more", 8);
        c.Effects["generator"] = () => c.Give(10, Blocks.Cobblestone, 8);
        c.Effects["deposit"] = () => c.World.Inventory.SetSlot(0, 10, ItemStack.Empty);
        var store = new BlockPos(14, 64, -5);
        var r = await new GeneratorLoop(c) { RetryPause = TimeSpan.Zero }.RunAsync(West, 64, store, default);
        Assert.False(r.Ok);
        Assert.Contains("deposit 14,64,-5 filter=True", c.CallList);
        Assert.Contains("deposited", r.Message);
        Assert.Equal(0, c.World.Inventory.CountItem(Blocks.Cobblestone));
    }

    [Fact]
    public async Task The_loop_rotates_all_cells_through_the_client()
    {
        var (_, c) = TestBot.Create();
        c.Give(36, Items.StonePickaxe, 1);
        var r = await new GeneratorLoop(c) { RetryPause = TimeSpan.Zero }.RunAsync(West, 4, null, default, null, [Middle, East]);
        Assert.True(r.Ok, r.Message);
        Assert.Contains(c.CallList, x => x.StartsWith("generator 4,64,6", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ never-break on gen cells

    private static (BotHost H, FakeGameClient C) Host()
    {
        var c = new FakeGameClient();
        var h = BotHost.Create(c, Trusted(), null, new NoLlm(), persist: false);
        h.ProtectionCommands!.GenCells = () => [West, Middle, East];
        return (h, c);
    }

    /// <summary>Exactly what the live db held (structure "cobble_generator" taught by kiwi at 15:55).</summary>
    private static void KiwiNote(AgentRuntime rt) =>
        rt.Memory.UpsertStructure("cobble_generator", s =>
        {
            s.Kind = "unknown";
            s.TaughtBy = "kiwi";
            s.Steps.AddRange(["stand 3,64,4", "dig only 4,64,6 and 6,64,8", "never mine the middle cell 5,64,7 (kiwi)"]);
        });

    [Fact]
    public void A_vague_dont_mine_the_middle_one_marks_no_gen_cell_but_exact_coordinates_do()
    {
        var (h, c) = Host();
        c.RaiseChat("<kiwi> claudechan don't mine the middle one");
        Assert.Null(h.ProtectionMarks!.NeverAt(Middle, 0));
        c.RaiseChat("<kiwi> claudechan don't mine 5,64,7");
        Assert.NotNull(h.ProtectionMarks.NeverAt(Middle, 0));
    }

    [Fact]
    public void An_area_never_mark_doesnt_cover_the_gens_regen_cells()
    {
        var (h, _) = Host();
        h.DigCells.Register(Middle, "PoofimaFox");
        h.ProtectionMarks!.Add(MarkKind.Never, 0, [(new BlockPos(3, 63, 5), new BlockPos(7, 66, 9))], "the gen", "kiwi", null, false);
        Assert.Null(h.ProtectionMarks.NeverAt(Middle, 0));
        Assert.NotNull(h.ProtectionMarks.NeverAt(new BlockPos(3, 64, 6), 0)); // the housing wall stays marked
    }

    [Fact]
    public void Trusted_you_can_mine_the_middle_one_clears_the_note_and_the_mark()
    {
        var (h, c) = Host();
        KiwiNote(h.Runtime);
        h.ProtectionMarks!.Add(MarkKind.Never, 0, [(Middle, Middle)], "the middle generator cell 5,64,7", "kiwi", null, true);
        c.RaiseChat("<PoofimaFox> claudechan you can mine the middle one");
        var s = h.Runtime.Memory.Read(d => d.Structures.First(x => x.Name == "cobble_generator"));
        Assert.DoesNotContain(s.Steps, x => x.Contains("never mine the middle", StringComparison.Ordinal));
        Assert.DoesNotContain(s.Steps, x => x.StartsWith("dig only", StringComparison.Ordinal));
        Assert.Contains(s.History, x => x.Contains("per PoofimaFox", StringComparison.Ordinal) && x.Contains("never mine the middle cell", StringComparison.Ordinal));
        Assert.Null(h.ProtectionMarks.NeverAt(Middle, 0));
        Assert.True(h.DigCells.IsDigCell(Middle));
    }

    [Fact]
    public void Migration_unmarks_a_confirmed_regen_cell_once()
    {
        var (h, _) = Host();
        KiwiNote(h.Runtime);
        h.Runtime.Observations.Upsert(new Observation("cobble_generator", "4,64,6", "cobble generator")
        {
            Bounds = Box.Of(4, 64, 6, 6, 64, 8),
            FieldsJson = """{"dig":[[4,64,6],[5,64,7],[6,64,8]],"stand":[3,64,4],"confirmed":true,"regens":34}""",
        });
        var lines = new List<string>();
        var fix = new DigCellCorrections(h.DigCells, h.Runtime.Memory) { Marks = h.ProtectionMarks };
        fix.Changed += lines.Add;
        Assert.Equal(1, fix.UnmarkConfirmedGenCells(h.Runtime.Observations, "PoofimaFox"));
        Assert.Contains(lines, l => l.StartsWith("unmarked gen cell 5,64,7 per PoofimaFox", StringComparison.Ordinal));
        var s = h.Runtime.Memory.Read(d => d.Structures.First(x => x.Name == "cobble_generator"));
        Assert.Equal(["stand 3,64,4"], s.Steps);
        Assert.Equal(0, fix.UnmarkConfirmedGenCells(h.Runtime.Observations, "PoofimaFox")); // idempotent
    }

    // ------------------------------------------------------------------ several bots on one gen

    /// <summary>One shared lease table for every bot (the hive's claim board, in-process).</summary>
    private sealed class SharedBoard(string owner, Dictionary<BlockPos, string> held) : IClaimBoard
    {
        public string Owner => owner;
        public bool IsFree(ClaimKey key)
        {
            lock (held) return key.Kind != ClaimKind.Block || !held.TryGetValue(key.Min, out var o) || o == owner;
        }
        public Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default)
        {
            lock (held)
            {
                if (key.Kind == ClaimKind.Block && held.TryGetValue(key.Min, out var o) && o != owner)
                    return Task.FromResult(ClaimResult.Refused(o, DateTimeOffset.MaxValue));
                if (key.Kind == ClaimKind.Block) held[key.Min] = owner;
                return Task.FromResult(ClaimResult.Granted(owner, DateTimeOffset.MaxValue));
            }
        }
        public Task ReleaseAsync(ClaimKey key, CancellationToken ct = default)
        {
            lock (held) if (held.TryGetValue(key.Min, out var o) && o == owner) held.Remove(key.Min);
            return Task.CompletedTask;
        }
    }

    private static void LiveGenInto(WorldState w)
    {
        string? path = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && path == null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "tests", "fixtures", "live-gen", "gen.txt"))) path = Path.Combine(d.FullName, "tests", "fixtures", "live-gen", "gen.txt");
        foreach (var line in File.ReadLines(path!))
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            w.SetBlock(f[0], f[1], f[2], f[3], f[4]);
        }
    }

    [Fact]
    public async Task Three_bots_take_three_cells_and_a_fourth_waits_instead_of_failing()
    {
        // Live 21:44: main, 67 and 1337 on the gen; 67 failed "my clone partner was using the cell at 4,64,6 at the same time".
        var held = new Dictionary<BlockPos, string>();
        using var gate = new ManualResetEventSlim(false);
        var bots = new List<(BotHost H, FakeGameClient C)>();
        foreach (var name in new[] { "maid_chan", "maid_chan_67", "maid_chan_1337", "maid_chan_69" })
        {
            var c = new FakeGameClient { Username = name };
            LiveGenInto(c.World);
            c.Give(36, Items.StonePickaxe, 1);
            c.Effects["generator"] = () => gate.Wait(TimeSpan.FromSeconds(20)); // each bot keeps its cell while it works
            var h = BotHost.Create(c, TestBot.Options(), new BotOptions { GeneratorQueueSeconds = 1 }, new NoLlm(), persist: false,
                claims: new SharedBoard(name, held));
            bots.Add((h, c));
        }
        var running = new List<Task<MineClaw.Agent.Tools.ToolResult>>();
        for (int i = 0; i < 3; i++)
        {
            var (h, _) = bots[i];
            running.Add(Task.Run(() => h.Run("harvest_generator", new { at = "4,64,6", count = 8 })));
            int want = i + 1;
            Assert.True(SpinWait.SpinUntil(() => { lock (held) return held.Count == want; }, 5000), $"bot {i} took no cell");
        }
        lock (held)
        {
            Assert.Equal(3, held.Values.Distinct().Count());
            Assert.Equal(new[] { West, Middle, East }.OrderBy(p => p.X), held.Keys.OrderBy(p => p.X));
        }
        var fourth = await bots[3].H.Run("harvest_generator", new { at = "4,64,6", count = 8 });
        Assert.True(fourth.Ok, fourth.Text);
        Assert.StartsWith("standing by:", fourth.Text);
        Assert.DoesNotContain(bots[3].C.CallList, x => x.StartsWith("generator", StringComparison.Ordinal));
        gate.Set();
        foreach (var t in running) Assert.True((await t).Ok);
        var cells = bots.Take(3).Select(b => b.C.CallList.First(x => x.StartsWith("generator", StringComparison.Ordinal)).Split(' ')[1]).ToList();
        Assert.Equal(3, cells.Distinct().Count());
    }
}
