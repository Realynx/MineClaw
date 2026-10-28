using MineClaw.Bot.GameTools;
using MineClaw.Bot.Perception;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;
using WPos = MineClaw.World.BlockPos;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-28: every clone ran the same hive script (hive_acff51) hardcoding cell 5,64,7, so they fought over it and the
/// "free cell" fallback invented non-cells: 69 "5,64,7 is taken by another bot; took the free cell 5,65,7; generator: dug
/// 0/1 at 5,65,7", 420 the lava source 4,64,7. Both strays had been "taught" by punches while players mined the gen (main
/// 22:14). The taught cells are exactly 4,64,6, 5,64,7 and 6,64,8, all at y64.
/// </summary>
public class GenCellFallbackTests
{
    private static readonly WPos A = new(4, 64, 6), B = new(5, 64, 7), C = new(6, 64, 8);
    private static readonly WPos StrayAbove = new(5, 65, 7), LavaSource = new(4, 64, 7);

    /// <summary>Fox's gen as the logs describe it: water 5,64,6 and lava 4,64,7 (an updated source: flowing lava meta 0)
    /// feed 4,64,6 and 5,64,7; lava 7,64,8 + water 6,64,9 feed 6,64,8; cobble formed over 5,64,7 with lava and water beside it.</summary>
    private static void Gen(WorldState w)
    {
        for (int x = 0; x <= 10; x++) for (int z = 2; z <= 12; z++) w.SetBlock(x, 63, z, Blocks.Stone, 0);
        w.SetBlock(5, 64, 6, Blocks.Water, 0);
        w.SetBlock(4, 64, 7, Blocks.FlowingLava, 0);
        w.SetBlock(7, 64, 8, Blocks.Lava, 0);
        w.SetBlock(6, 64, 9, Blocks.Water, 0);
        foreach (var p in new[] { A, B, C, StrayAbove }) w.SetBlock(p.X, p.Y, p.Z, Blocks.Cobblestone, 0);
        w.SetBlock(4, 65, 7, Blocks.FlowingLava, 2);
        w.SetBlock(5, 65, 6, Blocks.FlowingWater, 1);
    }

    /// <summary>One claim board for the whole hive; a lease stays held (each bot is still mining its cell).</summary>
    private sealed class HiveBoard
    {
        public readonly Dictionary<WPos, string> Held = new();
        public IClaimBoard For(string owner) => new View(this, owner);

        private sealed class View(HiveBoard b, string owner) : IClaimBoard
        {
            public string Owner => owner;
            public bool IsFree(ClaimKey key)
            {
                lock (b.Held) return !(key.Kind == ClaimKind.Block && b.Held.TryGetValue(key.Min, out var o) && o != owner);
            }
            public Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default)
            {
                lock (b.Held)
                {
                    if (key.Kind == ClaimKind.Block && b.Held.TryGetValue(key.Min, out var o) && o != owner)
                        return Task.FromResult(ClaimResult.Refused(o, DateTimeOffset.MaxValue));
                    if (key.Kind == ClaimKind.Block) b.Held[key.Min] = owner;
                    return Task.FromResult(ClaimResult.Granted(owner, DateTimeOffset.MaxValue));
                }
            }
            public Task ReleaseAsync(ClaimKey key, CancellationToken ct = default) => Task.CompletedTask;
        }
    }

    private static (BotHost H, FakeGameClient C) Bot(HiveBoard board, string name, double x, double z)
    {
        var c = new FakeGameClient { Username = name };
        Gen(c.World);
        c.World.Player.Physics.Teleport(new Vec3d(x, 64, z), 0, 0);
        c.World.Player.SyncFromPhysics();
        c.Give(36, Items.StonePickaxe, 1);
        var h = BotHost.Create(c, TestBot.Options(), new BotOptions { GeneratorQueueSeconds = 0 }, new NoLlm(), persist: false, claims: board.For(name));
        // The taught cells, plus the two strays the punches added.
        foreach (var p in new[] { A, B, C, StrayAbove, LavaSource }) h.DigCells!.Register(p, "kiwi");
        return (h, c);
    }

    private static WPos? Dug(FakeGameClient c)
    {
        var line = c.CallList.ToList().LastOrDefault(l => l.StartsWith("generator ", StringComparison.Ordinal));
        if (line == null) return null;
        var xyz = line.Split(' ')[1].Split(',').Select(int.Parse).ToArray();
        return new WPos(xyz[0], xyz[1], xyz[2]);
    }

    [Fact]
    public async Task Three_bots_with_the_same_hardcoded_cell_each_get_a_distinct_taught_cell_never_a_non_cell()
    {
        var board = new HiveBoard();
        var dug = new List<WPos>();
        string? last = null;
        foreach (var (name, x) in new[] { ("maid_chan_67", 5.5), ("maid_chan_69", 4.5), ("maid_chan_420", 6.5) })
        {
            var (h, c) = Bot(board, name, x, 4.5);
            var r = await h.Run("harvest_generator", new { at = "5,64,7", count = 1 });
            Assert.True(r.Ok, r.Text);
            last = r.Text;
            dug.Add(Dug(c) ?? throw new Xunit.Sdk.XunitException("nothing dug: " + r.Text));
        }
        Assert.Equal(3, dug.Distinct().Count());
        Assert.All(dug, p => Assert.Contains(p, new[] { A, B, C }));
        Assert.DoesNotContain("5,65,7", last);

        // A fourth bot: every taught cell is held, so it waits / does another job; it never invents one.
        var (h4, c4) = Bot(board, "maid_chan_1337", 5.5, 4.5);
        var r4 = await h4.Run("harvest_generator", new { at = "5,64,7", count = 1 });
        Assert.Null(Dug(c4));
        Assert.Contains("every dig cell", r4.Text);
        Assert.DoesNotContain("5,65,7", r4.Text);
        Assert.DoesNotContain("4,64,7", r4.Text);
    }

    [Fact]
    public async Task Three_bots_with_cell_auto_each_claim_a_distinct_taught_cell()
    {
        var board = new HiveBoard();
        var dug = new List<WPos>();
        foreach (var (name, x) in new[] { ("maid_chan_67", 5.5), ("maid_chan_69", 5.5), ("maid_chan_420", 5.5) })
        {
            var (h, c) = Bot(board, name, x, 4.5);
            var r = await h.Run("harvest_generator", new { at = "auto", count = 1 });
            Assert.True(r.Ok, r.Text);
            Assert.Contains("auto: cell", r.Text);
            dug.Add(Dug(c)!.Value);
        }
        Assert.Equal(new[] { A, B, C }.OrderBy(p => p.X), dug.OrderBy(p => p.X));
    }

    [Fact]
    public void Fallback_candidates_are_taught_cells_on_the_asked_layer_never_the_block_over_a_cell_or_the_lava()
    {
        var board = new HiveBoard();
        board.Held[B] = "maid_chan";
        var (h, c) = Bot(board, "maid_chan_69", 5.5, 4.5);
        var client = new ClaimAwareGameClient(c, board.For("maid_chan_69"));
        var cells = GameToolSet.FallbackCells(client, B, p => [A, C, StrayAbove, LavaSource, new WPos(5, 64, 8)]);
        Assert.Equal(new[] { A, C }.OrderBy(p => p.X), cells.OrderBy(p => p.X));
        Assert.Equal("it's the gen's lava source, not a cell", BlockDescriber.GenCellRejection(c.World, LavaSource));
    }

    [Fact]
    public void Stored_cells_that_are_the_gens_lava_or_water_or_a_build_block_are_purged_valid_ones_stay()
    {
        // Startup 10-28 after a85f8cb: "(5, 65, 7) purged: off the gen's dig layer y64 (4 known cells there)": the 4th y64
        // entry was kiwi's punch at the lava source 4,64,7.
        var w = new FakeGameClient().World;
        Gen(w);
        w.SetBlock(6, 64, 8, 0, 0); // just dug: air, still a cell
        var planks = new WPos(9, 64, 9);
        w.SetBlock(planks.X, planks.Y, planks.Z, Blocks.Planks, 0);
        var water = new WPos(5, 64, 6);
        var cells = new DigCells(null);
        foreach (var p in new[] { A, B, C, LavaSource, water, planks }) cells.Register(p, "kiwi");
        var purged = cells.PurgeNonCells(p => (w.GetBlock(p), w.GetMeta(p)));
        Assert.Equal(new[] { LavaSource, water, planks }.OrderBy(p => p.X), purged.Select(t => t.Cell).OrderBy(p => p.X));
        Assert.Contains(purged, t => t.Cell == LavaSource && t.Why == "it's the gen's lava");
        Assert.True(cells.IsDigCell(A) && cells.IsDigCell(B) && cells.IsDigCell(C));
        Assert.False(cells.IsDigCell(LavaSource));
    }

    [Fact]
    public void Stray_cells_off_the_dig_layer_are_purged_and_new_punches_there_are_refused()
    {
        var cells = new DigCells(null);
        foreach (var p in new[] { A, B, C, StrayAbove, new WPos(4, 65, 6) }) cells.Register(p, "PoofimaFox");
        var purged = cells.PurgeOffLayer();
        Assert.Equal(new[] { new WPos(4, 65, 6), StrayAbove }.OrderBy(p => p.X), purged.Select(t => t.Cell).OrderBy(p => p.X));
        Assert.All(purged, t => Assert.Contains("off the gen's dig layer y64", t.Why));
        Assert.False(cells.IsDigCell(StrayAbove));
        Assert.True(cells.IsDigCell(B));
        Assert.NotNull(cells.OffLayer(StrayAbove));
        Assert.Null(cells.OffLayer(new WPos(7, 64, 7)));
    }
}

/// <summary>Item 3 (live 10-27/28): an enclosed cell says exactly what boxes it in and asks once; a yield drop names the change
/// against the gen's taught layout instead of retracting a right cell.</summary>
public class GenHealthTests
{
    private static readonly WPos A = new(4, 64, 6);

    private static void Gen(WorldState w)
    {
        for (int x = 0; x <= 10; x++) for (int z = 2; z <= 12; z++) w.SetBlock(x, 63, z, Blocks.Stone, 0);
        w.SetBlock(5, 64, 6, Blocks.Water, 0);
        w.SetBlock(4, 64, 7, Blocks.Lava, 0);
        w.SetBlock(A.X, A.Y, A.Z, Blocks.Cobblestone, 0);
    }

    [Fact]
    public void A_yield_drop_names_the_lava_that_is_gone_from_the_taught_layout()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        Gen(c.World);
        h.Runtime.Memory.UpsertStructure("cobble gen", s => { s.Kind = MineClaw.Agent.Memory.StructureKinds.CobbleGenerator; s.Min = new(3, 63, 5); s.Max = new(7, 64, 9); });
        h.DigCells!.Register(A, "PoofimaFox"); // the teach records lava 4,64,7 + water 5,64,6 as the layout
        Assert.Null(GenLayout.Diagnose(c.World, h.Runtime.Memory, 0, A)); // as taught: the cell itself would be the suspect
        c.World.SetBlock(4, 64, 7, Blocks.Obsidian, 0);                    // the lava source got covered: obsidian
        var why = GenLayout.Diagnose(c.World, h.Runtime.Memory, 0, A);
        Assert.Equal("the lava at 4,64,7 is gone (now obsidian; taught layout had it)", why);
    }

    [Fact]
    public async Task The_gen_loop_reports_the_cause_and_keeps_the_right_cell_taught()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        c.Give(36, Items.StonePickaxe, 1);
        c.Results["generator"] = () => MineClaw.Client.ActionResult.Fail("4,64,6 isn't regenerating cobble for me (yield 12 %: 1 cobblestone from 8 digs), need the right cell (dug 8, +1)", 8);
        WPos? retracted = null;
        var loop = new GeneratorLoop(c, new GeneratorSupport(BadCell: (p, _) => retracted = p, Diagnose: _ => "the water at 5,64,6 is gone (now air; taught layout had it)"))
            { RetryPause = TimeSpan.Zero };
        var r = await loop.RunAsync(A, 64, null, default);
        Assert.False(r.Ok);
        Assert.Contains("cause: the water at 5,64,6 is gone", r.Message);
        Assert.Null(retracted);
    }

    [Fact]
    public void An_enclosed_cell_is_asked_about_once_then_reported_as_already_asked()
    {
        var cell = new WPos(40, 64, 40);
        var first = GameToolSet.AskOnceIfEnclosed(Agent.Tools.ToolResult.Error(
            "can't get to a safe spot at the generator: 40,64,40 is enclosed (N water, S lava, E planks wall, W void, above: planks; no standable spot within 8; no path); ask a player once where to stand"), cell);
        Assert.StartsWith("needs_input: can't get to a safe spot at the generator: 40,64,40 is enclosed (N water, S lava, E planks wall, W void", first.Text);
        Assert.EndsWith("Where should I stand to dig 40,64,40?", first.Text);
        var again = GameToolSet.AskOnceIfEnclosed(Agent.Tools.ToolResult.Error(
            "can't get to a safe spot at the generator: 40,64,40 is enclosed (N water; x); ask a player once where to stand"), cell);
        Assert.DoesNotContain("needs_input", again.Text);
        Assert.Contains("already asked where to stand", again.Text);
    }
}
