using MineClaw.Agent.Memory;
using MineClaw.Bot.Demo;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Perception;
using MineClaw.Bot.Tests.WorldIndex;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;
using WPos = MineClaw.World.BlockPos;

namespace MineClaw.Bot.Tests;

/// <summary>Second live session: gestures as pointing, dual cobble generators, claim-aware cells, one prompt, loose answers.</summary>
public class LiveSession2BotTests
{
    /// <summary>Dual generator along X at y 64: water | west cell | lava | east cell | water, on a stone floor.</summary>
    private static (WPos West, WPos East) DualGen(WorldState w)
    {
        for (int x = -2; x <= 7; x++) for (int z = -3; z <= 4; z++) w.SetBlock(x, 63, z, Blocks.Stone, 0);
        w.SetBlock(0, 64, 0, Blocks.Water, 0);
        w.SetBlock(1, 64, 0, Blocks.Cobblestone, 0);
        w.SetBlock(2, 64, 0, Blocks.Lava, 0);
        w.SetBlock(3, 64, 0, Blocks.Cobblestone, 0);
        w.SetBlock(4, 64, 0, Blocks.Water, 0);
        return (new WPos(1, 64, 0), new WPos(3, 64, 0));
    }

    /// <summary>Fox stands 2.5 blocks south of <paramref name="cell"/> looking north and down at it.</summary>
    private static Entity FoxLookingAt(WorldState w, WPos cell, int held = 0) =>
        w.Entities.SpawnPlayer(7, "PoofimaFox", new Vec3d(cell.X + 0.5, 64, cell.Z + 2.5), 180, 29, held);

    // ---------------------------------------------------------------- 1: gestures

    [Fact]
    public void A_punch_is_pointing_and_lands_in_the_turn_context()
    {
        var (h, c) = TestBot.Create();
        var (_, east) = DualGen(c.World);
        FoxLookingAt(c.World, east);
        c.RaiseSwing(7);
        c.RaiseSwing(7); // digging swings at the same block: one gesture
        var line = h.Runtime.Agent.TurnPerception!.Invoke();
        Assert.NotNull(line);
        Assert.StartsWith("[pointing] PoofimaFox punched the east cell of the gen, right next to the lava [3,64,0]", line);
        Assert.Single(h.Gestures!.Recent());
        // A punched generator cell is a taught dig cell: never protected.
        Assert.True(h.DigCells!.IsDigCell(east));
    }

    [Fact]
    public void Right_click_uses_are_not_pointing_and_old_gestures_age_out()
    {
        var c = new FakeGameClient();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var (west, _) = DualGen(c.World);
        var g = new GestureTracker(c, time: clock);
        FoxLookingAt(c.World, west, Items.WaterBucket);
        Assert.Null(g.Observe(7));
        c.World.Entities.SetHeldItem(7, 0);
        Assert.NotNull(g.Observe(7));
        Assert.NotNull(g.Line());
        clock.Seconds(21);
        Assert.Null(g.Line());
        Assert.Contains("PoofimaFox punched the west cell", g.Older(clock.GetUtcNow() - TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Demo_records_a_punched_block_as_a_step_and_punches_pass_through_lava()
    {
        var c = new FakeGameClient();
        var w = c.World;
        for (int x = -2; x <= 6; x++) for (int z = -2; z <= 2; z++) w.SetBlock(x, 63, z, Blocks.Stone, 0);
        w.SetBlock(3, 64, 0, Blocks.Lava, 0);       // lava in front...
        w.SetBlock(4, 64, 0, Blocks.Cobblestone, 0); // ...of the regenerating brick
        var rec = new DemonstrationRecorder(c) { PollInterval = TimeSpan.FromMilliseconds(20) };
        w.Entities.SpawnPlayer(7, "PoofimaFox", new Vec3d(2.5, 64, 0.5), -90, 20, 0);
        await using var session = await rec.StartAsync("PoofimaFox", null, CancellationToken.None);
        c.RaiseSwing(7);
        Assert.Equal(1, session.ActionCount);
        Assert.NotNull(session.LastActivity);
        var steps = DemoSummary.Summarize(await session.StopAsync(CancellationToken.None));
        Assert.Contains("punched cobblestone at 4,64,0", steps);
        Assert.DoesNotContain(steps, s => s.Contains("lava at"));
    }

    // ---------------------------------------------------------------- 2/3: watch_demo and ask_stand in play

    [Fact]
    public async Task Watch_demo_ends_on_did_it_and_says_nothing_when_the_brain_already_asked()
    {
        var (h, c) = TestBot.Create();
        DualGen(c.World);
        FoxLookingAt(c.World, new WPos(3, 64, 0));
        h.Runtime.Chat.MarkTurnStart();
        await h.Runtime.Chat.SayAsync("ooh show me how you mine it foxy");
        h.Runtime.Chat.NoteModelSpeech();
        int said = c.Said.Count;
        var run = h.Runtime.Tools.ExecuteAsync("watch_demo", TestBot.J(new { player = "PoofimaFox", structure = "dual cobble generator" }), CancellationToken.None);
        await Task.Delay(150);
        c.RaiseSwing(7);
        await Task.Delay(50);
        h.Runtime.Chat.OnServerChat("<PoofimaFox> did it");
        var r = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(r.Ok, r.Text);
        Assert.Contains("punched cobblestone at", r.Text);
        Assert.Equal(said, c.Said.Count); // no duplicate "say done when finished" line
    }

    [Fact]
    public async Task Ask_stand_here_is_the_cell_registers_the_dig_cell_it_faces()
    {
        var (h, c) = TestBot.Create();
        var (west, _) = DualGen(c.World);
        FoxLookingAt(c.World, west);
        var run = h.Runtime.Tools.ExecuteAsync("ask_stand", TestBot.J(new { player = "PoofimaFox" }), CancellationToken.None);
        await Task.Delay(100);
        h.Runtime.Chat.OnServerChat("<PoofimaFox> here is the cell");
        var r = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(r.Ok, r.Text);
        Assert.Contains("dig cell 1,64,0 (the west cell of the gen", r.Text);
        Assert.True(h.DigCells!.IsDigCell(west));
    }

    // ---------------------------------------------------------------- 6: dual generator protection and claims

    [Fact]
    public void Taught_box_never_protects_its_dig_cells()
    {
        var (h, c) = TestBot.Create();
        var (west, east) = DualGen(c.World);
        h.Runtime.Memory.UpsertStructure("dual cobble generator", s =>
        {
            s.Kind = StructureKinds.CobbleGenerator;
            s.Min = new MineClaw.Agent.Memory.BlockPos(0, 63, -1);
            s.Max = new MineClaw.Agent.Memory.BlockPos(4, 64, 1);
            s.KeyBlocks.Add(new KeyBlock { Label = "mine_here", X = east.X, Y = east.Y, Z = east.Z });
        });
        h.Protection.Invalidate();
        h.DigCells!.Invalidate();
        Assert.True(c.IsProtected!(new WPos(0, 63, 0), Blocks.Stone)); // the gen's floor: part of the taught build
        Assert.False(c.IsProtected!(east, Blocks.Cobblestone));        // taught key block "mine_here"
        Assert.True(c.IsProtected!(west, Blocks.Cobblestone));         // not taught yet...
        FoxLookingAt(c.World, west);
        c.RaiseSwing(7);                                                // ...Fox punches it: "this one"
        Assert.False(c.IsProtected!(west, Blocks.Cobblestone));
    }

    [Fact]
    public void Indexer_finds_both_cells_and_never_walls_off_the_second()
    {
        using var f = new IndexFixture();
        f.Island(0, 0, 4);
        f.Set(0, 64, 0, Blocks.Water);
        f.Set(1, 64, 0, Blocks.Cobblestone);
        f.Set(2, 64, 0, Blocks.Lava);
        f.Set(3, 64, 0, Blocks.Cobblestone);
        f.Set(4, 64, 0, Blocks.Water);
        f.Fill(0, 64, 1, 4, 64, 1, Blocks.Cobblestone);  // the wall south of the channel
        f.Fill(0, 64, -1, 4, 64, -1, Blocks.Cobblestone); // and north
        f.Pump();
        Assert.False(f.Indexer.IsProtected(new WPos(1, 64, 0), Blocks.Cobblestone));
        Assert.False(f.Indexer.IsProtected(new WPos(3, 64, 0), Blocks.Cobblestone));
        Assert.True(f.Indexer.IsProtected(new WPos(2, 64, 1), Blocks.Cobblestone)); // a real wall holding the lava
        Assert.Equal(2, f.Indexer.GeneratorCellsNear(new WPos(2, 64, 0)).Count);

        // A player digs the second cell and it refills: confirmed by observed regen, whoever dug it.
        f.Set(3, 64, 0, 0);
        f.Clock.Seconds(1);
        f.Set(3, 64, 0, Blocks.Cobblestone);
        f.Pump();
        Assert.True(f.Indexer.IsConfirmedDigCell(new WPos(3, 64, 0)));
        Assert.False(f.Indexer.IsConfirmedDigCell(new WPos(1, 64, 0)));
    }

    private sealed class Board(string owner, Dictionary<WPos, string> held) : IClaimBoard
    {
        public string Owner => owner;
        public bool IsFree(ClaimKey key) => !(key.Kind == ClaimKind.Block && held.TryGetValue(key.Min, out var o) && o != owner);
        public Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default) =>
            Task.FromResult(IsFree(key) ? ClaimResult.Granted(owner, DateTimeOffset.MaxValue) : ClaimResult.Refused(held[key.Min], DateTimeOffset.MaxValue));
        public Task ReleaseAsync(ClaimKey key, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_clone_takes_the_free_dig_cell_not_mains()
    {
        var c = new FakeGameClient { Username = "maid_chan_67" };
        var (west, east) = DualGen(c.World);
        var board = new Board("maid_chan_67", new() { [east] = "maid_chan" }); // main is harvesting the east cell
        var h = BotHost.Create(c, TestBot.Options(), null, new NoLlm(), persist: false, claims: board);
        c.Give(36, MineClaw.Data.Items.StonePickaxe, 1); // the gen loop never digs bare-handed
        var r = await h.Run("harvest_generator", new { at = "3,64,0", count = 10 });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("took the free cell 1,64,0", r.Text);
        Assert.Contains("generator 1,64,0 10", c.CallList);

        var (cell, note) = GameToolSet.PickGeneratorCell(new ClaimAwareGameClient(c, new Board("maid_chan_67", new() { [east] = "maid_chan", [west] = "maid_chan_69" })), east, null);
        Assert.Null(cell);
        Assert.StartsWith("busy: every dig cell", note);
    }

    // ---------------------------------------------------------------- 10: relational names

    [Fact]
    public void Block_landmarks_read_like_a_player()
    {
        var w = new FakeGameClient().World;
        var (west, east) = DualGen(w);
        Assert.Equal("the west cell of the gen, right next to the lava", BlockDescriber.Landmark(w, west, null));
        Assert.Equal("the east cell of the gen, right next to the lava", BlockDescriber.Landmark(w, east, null));
        Assert.Equal("the stone under the west cell of the gen, right next to the lava", BlockDescriber.Landmark(w, new WPos(1, 63, 0), null));
        Assert.Null(BlockDescriber.Landmark(w, new WPos(-2, 63, -3), null)); // nothing better than "stone": coordinates stay
    }

    // ---------------------------------------------------------------- spawn protection belief

    [Fact]
    public void Retracting_spawn_protection_forgets_the_brains_notes()
    {
        var (h, _) = TestBot.Create();
        h.Runtime.Memory.AddNote("world", "spawn protection might block us maids from placing blocks near spawn");
        h.Runtime.Memory.AddNote("world", "the gen is south of the house");
        Assert.Equal(1, BotHost.RetractSpawnProtectionNotes(h.Runtime));
        Assert.Single(h.Runtime.Memory.Read(d => d.Notes.ToList()));
    }
}
