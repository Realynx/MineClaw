using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Bot.Memory;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;
using BlockPos = MineClaw.World.BlockPos;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Fox: "It'd be nice if I could tell the AI to memorize structures that are already built if I show it my house, and then it
/// could memorize that and rebuild it." These pin design capture: the region (flood fill over the build, not the terrain or a
/// living tree; corners; the 32 cap; unloaded chunks), the DSL round trip voxel by voxel, the rebuild elsewhere (never on the
/// original), "turn it" at the gate, and the memory/protection link.
/// </summary>
public class StructureCaptureTests
{
    private static readonly BlockPos Feet = new(3, 64, 2);
    private static readonly BlockPos Doorstep = new(3, 63, 5);

    /// <summary>Grass on dirt (y 63/62) over x -30..12, z -10..12; chunks x -48..47, z -32..31 loaded.</summary>
    internal static WorldState Terrain()
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-48, 0, -32, 96, 128, 64, new byte[96 * 128 * 64], null, null, null);
        for (int x = -30; x <= 12; x++)
            for (int z = -10; z <= 12; z++)
            {
                w.SetBlock(x, 62, z, Blocks.Dirt, 0);
                w.SetBlock(x, 63, z, Blocks.Grass, 0);
            }
        return w;
    }

    /// <summary>
    /// Fox's house at x 0..6, z 0..4: a planks floor in the grass (y 63), cobble walls y 64..66 with log corner posts, a door in
    /// the south wall (placed from outside: meta 3), glass windows, wall torches inside (north wall: 3, east wall: 2) and outside
    /// (2), a ladder on the east wall, a chest, a lit furnace, a bed, a cobblestone-slab bench outside, a roof of cobble slabs
    /// between oak-stair eaves (east-/west-ascending). Next to it: a living oak (its trunk touches the west wall), gen water at
    /// the east wall, a separate cobble pillar. Returns every block of the house (bed included).
    /// </summary>
    internal static Dictionary<BlockPos, (int Id, int Meta)> House(WorldState w)
    {
        var house = new Dictionary<BlockPos, (int Id, int Meta)>();
        void Set(int x, int y, int z, int id, int meta = 0)
        {
            w.SetBlock(x, y, z, id, meta);
            var p = new BlockPos(x, y, z);
            if (id == 0) house.Remove(p);
            else house[p] = (id, meta);
        }
        for (int x = 0; x <= 6; x++) for (int z = 0; z <= 4; z++) Set(x, 63, z, Blocks.Planks);
        for (int y = 64; y <= 66; y++)
            for (int x = 0; x <= 6; x++)
                for (int z = 0; z <= 4; z++)
                    if (x is 0 or 6 || z is 0 or 4) Set(x, y, z, Blocks.Cobblestone);
        for (int y = 64; y <= 66; y++) { Set(0, y, 0, Blocks.Log); Set(6, y, 4, Blocks.Log); }
        Set(3, 64, 4, Blocks.WoodenDoor, 3);
        Set(3, 65, 4, Blocks.WoodenDoor, 11);
        Set(0, 65, 2, Blocks.Glass);
        Set(1, 65, 0, Blocks.Glass);
        Set(5, 65, 0, Blocks.Glass);
        Set(2, 65, 1, Blocks.Torch, 3);   // on the north wall
        Set(5, 65, 1, Blocks.Torch, 2);   // on the east wall
        Set(2, 65, 5, Blocks.Torch, 3);   // outside, on the south wall
        Set(5, 64, 3, Blocks.Ladder, 4);  // the east wall behind it
        Set(5, 65, 3, Blocks.Ladder, 4);
        Set(1, 64, 1, Blocks.Chest);
        Set(1, 64, 3, Blocks.LitFurnace, 5);
        Set(3, 64, 1, Blocks.Bed, 1);
        Set(4, 64, 1, Blocks.Bed, 9);
        Set(4, 64, 5, Blocks.Slab, 3);    // a cobblestone-slab bench outside
        for (int z = 0; z <= 4; z++)
        {
            Set(0, 67, z, Blocks.WoodenStairs, 0); // ascending east
            Set(6, 67, z, Blocks.WoodenStairs, 1); // ascending west
            for (int x = 1; x <= 5; x++) Set(x, 67, z, Blocks.Slab, 3);
        }
        // Not the house: a living oak against the west wall, the gen's water at the east wall, a pillar further off.
        for (int y = 64; y <= 68; y++) w.SetBlock(-1, y, 2, Blocks.Log, 0);
        for (int y = 66; y <= 68; y++)
            for (int z = 0; z <= 4; z++)
                for (int x = -3; x <= -1; x++)
                    if (w.GetBlock(x, y, z) == 0) w.SetBlock(x, y, z, Blocks.Leaves, 0);
        w.SetBlock(-1, 69, 2, Blocks.Leaves, 0);
        w.SetBlock(7, 64, 2, Blocks.Water, 0);
        for (int y = 64; y <= 66; y++) w.SetBlock(-10, y, -10, Blocks.Cobblestone, 0);
        w.Player.ServerTeleport(new Vec3d(-20.5, 64, 8.5), 65.62, 0, 0, true);
        return house;
    }

    private static CaptureResult CaptureHouse(WorldState w) =>
        StructureCapture.Capture(new CaptureRequest { Name = "Fox's house", World = w.PlanningView, Seed = Feet, FloorY = Feet.Y - 1 });

    [Fact]
    public void A_house_captures_into_a_DSL_that_parses_back_voxel_for_voxel()
    {
        var w = Terrain();
        var house = House(w);
        var r = CaptureHouse(w);
        Assert.True(r.Ok, r.Refusal);
        Assert.True(r.Exact);
        Assert.Equal(new BlockPos(0, 63, 0), r.Min);
        Assert.Equal(new BlockPos(6, 67, 5), r.Max);
        // Every block of the house but the bed, in its own state (the lit furnace as a furnace with its front kept), and the
        // grass in front of the door as a dirt doorstep (a copy stands one up on its site: the door must still be walkable).
        var expected = house.Where(kv => kv.Value.Id != Blocks.Bed)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Id == Blocks.LitFurnace ? (Blocks.Furnace, kv.Value.Meta) : kv.Value);
        expected[Doorstep] = (Blocks.Dirt, 0);
        Assert.Equal(expected.Keys.OrderBy(p => (p.Y, p.Z, p.X)), r.Cells.Keys.OrderBy(p => (p.Y, p.Z, p.X)));
        foreach (var (p, s) in expected) Assert.Equal(s, r.Cells[p]);
        Assert.Equal(2, r.Skipped["bed"]);
        Assert.Equal(1, r.Substituted["lit furnace as furnace"]);
        Assert.Contains("chest contents not captured", r.Notes);
        Assert.Equal("not captured: 2 bed; 1 lit furnace as furnace; added 1 doorstep (the ground in front of the door) so the copy can be walked into; "
                     + "chest contents not captured", r.Report());

        // The DSL on its own (as the store keeps it): parse, normalize, the same voxels with the same metas at the same place.
        Assert.Contains("origin 0,63,0", r.Dsl);
        Assert.Contains("facing south", r.Dsl);
        Assert.Contains("wooden_door:3", r.Dsl);
        Assert.Contains("oak_stairs:1", r.Dsl);
        Assert.Contains("torch:2", r.Dsl);
        Assert.Contains("ladder:4", r.Dsl);
        Assert.Contains("stone_slab:3", r.Dsl);
        var (plan, issues) = BlueprintNormalizer.FromText(r.Dsl);
        Assert.NotNull(plan);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        Assert.Equal(expected.Count, plan!.Blocks.Count);
        foreach (var (p, s) in expected)
        {
            Assert.True(plan.Blocks.TryGetValue(p, out var b), $"{p} missing");
            Assert.Equal(s, (b!.Id, b.Meta));
        }
        // Drawn air is interior air only (the rooms), never the grass or the tree around it.
        Assert.All(plan.Air, a => Assert.Equal(0, w.GetBlock(a)));
        Assert.Contains(new BlockPos(3, 65, 2), plan.Air);
        Assert.DoesNotContain(new BlockPos(-1, 65, 2), plan.Air);
    }

    [Fact]
    public void The_flood_fill_keeps_to_the_build_and_leaves_terrain_trees_and_fluids()
    {
        var w = Terrain();
        House(w);
        var r = CaptureHouse(w);
        Assert.True(r.Ok, r.Refusal);
        Assert.DoesNotContain(r.Cells.Where(c => c.Key != Doorstep).Select(c => c.Value), s => s.Id is Blocks.Grass or Blocks.Dirt or Blocks.Leaves or Blocks.Water);
        Assert.DoesNotContain(new BlockPos(-1, 64, 2), r.Cells.Keys);    // the living oak's trunk, touching the wall
        Assert.DoesNotContain(new BlockPos(-10, 64, -10), r.Cells.Keys); // the pillar further off
        Assert.Equal((Blocks.Log, 0), r.Cells[new BlockPos(0, 65, 0)]);  // log posts on the planks floor are the build's
        // The same from outside: pointing at the wall from the grass finds the same house (the floor from the grass level).
        var outside = StructureCapture.Capture(new CaptureRequest { Name = "Fox's house", World = w.PlanningView, Seed = new BlockPos(3, 64, 7), FloorY = 63 });
        Assert.True(outside.Ok, outside.Refusal);
        Assert.Equal(r.Cells.Keys.ToHashSet(), outside.Cells.Keys.ToHashSet());
        // With no floor hint, a wall block seed finds the floor below its run (the grass): the planks floor is part of it.
        var pointed = StructureCapture.Capture(new CaptureRequest { Name = "Fox's house", World = w.PlanningView, Seed = new BlockPos(0, 64, 3) });
        Assert.True(pointed.Ok, pointed.Refusal);
        Assert.Equal(r.Cells.Keys.ToHashSet(), pointed.Cells.Keys.ToHashSet());
    }

    [Fact]
    public void A_build_past_32_is_refused_with_a_hint_and_so_are_bigger_corners()
    {
        var w = Terrain();
        for (int x = -36; x <= 3; x++) for (int y = 64; y <= 66; y++) w.SetBlock(x, y, -15, Blocks.Cobblestone, 0);
        var r = StructureCapture.Capture(new CaptureRequest { Name = "wall", World = w.PlanningView, Seed = new BlockPos(1, 64, -14), FloorY = 63 });
        Assert.False(r.Ok);
        Assert.Contains("keeps going past 32×32×32", r.Refusal);
        Assert.Contains("two corners", r.Refusal);
        var big = StructureCapture.Capture(new CaptureRequest { Name = "wall", World = w.PlanningView, Corners = (new BlockPos(-36, 64, -15), new BlockPos(3, 66, -15)) });
        Assert.False(big.Ok);
        Assert.Contains("40×1×3 is bigger than I can memorize (32×32×32)", big.Refusal);
        // 32 exactly is fine.
        var ok = StructureCapture.Capture(new CaptureRequest { Name = "wall", World = w.PlanningView, Corners = (new BlockPos(-28, 64, -15), new BlockPos(3, 66, -15)) });
        Assert.True(ok.Ok, ok.Refusal);
        Assert.Equal(96, ok.Blocks);
    }

    [Fact]
    public void Unloaded_cells_refuse_the_capture_and_stale_ones_are_reported()
    {
        // Chunks x -16..15 only: a box reaching x 20 is a third unknown.
        var w = new WorldState();
        w.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        for (int x = 10; x <= 15; x++) for (int y = 64; y <= 66; y++) w.SetBlock(x, y, 0, Blocks.Cobblestone, 0);
        var r = StructureCapture.Capture(new CaptureRequest { Name = "shed", World = w.PlanningView, Corners = (new BlockPos(10, 64, 0), new BlockPos(20, 66, 2)) });
        Assert.False(r.Ok);
        Assert.Contains("aren't loaded", r.Refusal);
        Assert.Equal(5 * 3 * 3, r.Unknown);
        // The flood from the wall runs into the unloaded chunk at x 16: it may go on there, so it isn't memorized half-seen.
        var flood = StructureCapture.Capture(new CaptureRequest { Name = "shed", World = w.PlanningView, Seed = new BlockPos(12, 64, 1), FloorY = 63 });
        Assert.False(flood.Ok);
        Assert.Contains("aren't loaded: come closer", flood.Refusal);
        // Loaded but only remembered (stale) columns: captured, and said.
        var s = StructureCapture.Capture(new CaptureRequest
        {
            Name = "shed", World = w.PlanningView, Corners = (new BlockPos(10, 64, 0), new BlockPos(15, 66, 0)), Live = (x, _) => x < 13,
        });
        Assert.True(s.Ok, s.Refusal);
        Assert.Equal(9, s.Stale);
        Assert.Contains("9 cells from remembered chunks (stale)", s.Report());
    }

    // ------------------------------------------------------------------ the design, the rebuild, the link

    private sealed record Fixture(BlueprintService S, BlueprintStore Store, WorldState W, SimBuildBody Body, MemoryStore Memory, Dictionary<BlockPos, (int, int)> House);

    private static Dictionary<(int, int), int> Materials() => new()
    {
        [(Blocks.Cobblestone, 0)] = 256, [(Blocks.Planks, 0)] = 128, [(Blocks.Log, 0)] = 16, [(Blocks.Glass, 0)] = 16, [(Items.WoodenDoor, 0)] = 2,
        [(Blocks.Torch, 0)] = 16, [(Blocks.Ladder, 0)] = 8, [(Blocks.Chest, 0)] = 2, [(Blocks.Furnace, 0)] = 2, [(Blocks.WoodenStairs, 0)] = 16,
        [(Blocks.Slab, 3)] = 32, [(Blocks.Dirt, 0)] = 4,
    };

    private static Fixture Setup(AgentDb db)
    {
        var w = Terrain();
        var house = House(w);
        var store = new BlueprintStore(db);
        var body = new SimBuildBody(w, Materials());
        var s = new BlueprintService(new BlueprintOptions(), store, new BlueprintPreview())
        {
            Site = () => BlueprintTests.Site(w), Crew = () => new SingleBodyCrew(body),
            // "here" = where Fox stands (in his house); anywhere else = the open grass west of the tree.
            ResolveSite = (at, _) => (at is null or "here" ? new SitePick(new BlockPos(3, 63, 2), "where PoofImaFox is standing") : new SitePick(new BlockPos(-20, 63, 0), "at " + at), null),
            Trusted = () => ["PoofImaFox"],
            Executor = new ConstructionExecutor(new BlueprintOptions(), store),
        };
        s.StartBuild = (_, _) => null;
        return new Fixture(s, store, w, body, new MemoryStore(db, null), house);
    }

    private static BlueprintRecord Captured(Fixture f, out string text)
    {
        var (rec, t) = DesignCapture.Save(f.S, CaptureHouse(f.W), "PoofImaFox", (r, c) => DesignCapture.Link(f.Memory, f.W, r, c));
        text = t;
        Assert.NotNull(rec);
        return rec!;
    }

    [Fact]
    public async Task A_captured_house_rebuilds_complete_at_a_new_site_but_never_on_top_of_the_original()
    {
        using var db = AgentDb.Open(null);
        var f = Setup(db);
        var rec = Captured(f, out var text);
        Assert.True(text.StartsWith("captured 'Fox's house' as design #1: 7×6×5, 138 blocks (palette: cobblestone, planks, cobblestone slab, oak stairs, oak wood…); bill to rebuild: 49 cobblestone (short 49), 35 planks", StringComparison.Ordinal), text);
        Assert.Contains("; bill to rebuild: ", text);
        Assert.Contains("not captured: 2 bed", text);
        Assert.EndsWith("rebuild elsewhere: design build{id=1,at}", text);
        Assert.True(text.Length <= 600, $"{text.Length}: {text}");
        Assert.Equal(DesignStatus.Captured, rec.Status);
        Assert.Equal("PoofImaFox", rec.AcceptedBy);
        Assert.Equal(new BlockPos(0, 63, 0), rec.Origin);
        Assert.True(rec.HasPicture);
        Assert.Contains(rec.Bill, b => b.Item == "cobblestone" && b.Need == 49);
        // The saved record re-plans at its own spot exactly as the world stands.
        var at = f.S.PlanOf(f.Store.Get(rec.Id)!, out var err)!;
        Assert.Null(err);
        foreach (var b in at.Blocks.Values.Where(b => f.W.GetBlock(b.Pos) is not (Blocks.LitFurnace or Blocks.Grass))) // stand-ins: furnace, dirt doorstep
            Assert.True(DesignChecks.SameState(b, f.W.GetBlock(b.Pos), f.W.GetMeta(b.Pos)), b.ToString());
        // The worker's build{bp} never builds the captured record itself (only a copy elsewhere).
        Assert.Contains("is captured, not accepted", (await f.S.BuildAsync(rec.Id.ToString(), null, null, null, CancellationToken.None)).Text);

        // "build another one here" while standing in it: refused, nothing copied.
        var (none, onTop) = f.S.Reuse(rec, "here", "PoofImaFox");
        Assert.Null(none);
        Assert.Contains("on top of the original Fox's house", onTop);
        Assert.Single(f.Store.List(10));

        var (copy, started) = f.S.Reuse(rec, "over there", "PoofImaFox");
        Assert.NotNull(copy);
        Assert.Equal("building another Fox's house (#2)", started);
        Assert.Equal(new BlockPos(-23, 64, -3), copy!.Origin);
        var (ok, built) = await f.S.BuildAsync(copy.Id.ToString(), null, null, null, CancellationToken.None);
        Assert.True(ok, built);
        Assert.True(built.StartsWith("built #2", StringComparison.Ordinal), built);
        Assert.Empty(f.Body.Unguarded);
        Assert.Equal(Blocks.Dirt, f.W.GetBlock(Doorstep.X - 23, Doorstep.Y + 1, Doorstep.Z - 3));
        var shift = new BlockPos(-23 - 0, 64 - 63, -3 - 0);
        foreach (var (p, (id, meta)) in f.House.Where(kv => kv.Value.Item1 != Blocks.Bed))
        {
            var q = new BlockPos(p.X + shift.X, p.Y + shift.Y, p.Z + shift.Z);
            Assert.Equal(id == Blocks.LitFurnace ? Blocks.Furnace : id, f.W.GetBlock(q));
            Assert.Equal(meta, f.W.GetMeta(q));
        }
        Assert.Equal(DesignStatus.Captured, f.Store.Get(rec.Id)!.Status); // the original design stays as it was
        foreach (var (p, (id, _)) in f.House) Assert.Equal(id, f.W.GetBlock(p)); // and so does the house
    }

    [Fact]
    public async Task Turn_it_at_the_gate_turns_every_facing_with_the_house()
    {
        using var db = AgentDb.Open(null);
        var f = Setup(db);
        var rec = Captured(f, out _);
        var (copy, _) = f.S.Reuse(rec, "over there", "PoofImaFox");
        var plan = f.S.PlanOf(copy!, out _)!;
        var gates = new BuildGates();
        var asked = new List<string>();
        var anchor = new BlockPos(-20, 64, 0);
        var ex = new ConstructionExecutor(new BlueprintOptions { GateMinutes = 1 }, f.Store)
        {
            Gates = gates,
            Ask = q =>
            {
                asked.Add(q);
                var reply = asked.Count == 1 ? BuildGate.Parse("turn it", "PoofImaFox", anchor, 'x')! : BuildGate.Parse("yes", "PoofImaFox", anchor, 'x')!;
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < 400 && !gates.IsWaiting(copy.Id); i++) await Task.Delay(5);
                    gates.Answer(copy.Id, reply);
                });
                return Task.CompletedTask;
            },
        };
        var report = await ex.RunAsync(copy.Id, plan, BuildPart.Whole(plan), f.Body, CancellationToken.None);
        Assert.True(report.Complete, report.Text("house"));
        Assert.Equal(2, asked.Count);
        Assert.Equal(1, report.Plan!.Transform.Quarters);
        var turned = PlanTransform.Apply(plan, report.Plan.Transform);
        Assert.Equal((6, 7), (turned.Max.X - turned.Min.X + 1, turned.Max.Z - turned.Min.Z + 1)); // 7 wide, 6 deep → 6 wide, 7 deep
        foreach (var b in turned.Blocks.Values)
        {
            Assert.Equal(b.Id, f.W.GetBlock(b.Pos));
            Assert.Equal(b.Meta, f.W.GetMeta(b.Pos));
        }
        // Every facing turned a quarter clockwise with the cells: the door (south front → west), the eaves, the torches and the
        // ladder still on their walls.
        var doors = turned.Blocks.Values.Where(b => b.Id == Blocks.WoodenDoor && !b.UpperDoor).ToList();
        Assert.Equal(Dir.East, BuildBlocks.DoorPlacerFacing(Assert.Single(doors).Meta)); // placed from the west, looking east
        Assert.All(turned.Blocks.Values.Where(b => b.Id == Blocks.WoodenStairs), s => Assert.Contains(BuildBlocks.StairsAscend(s.Meta), new[] { Dir.South, Dir.North }));
        foreach (var t in turned.Blocks.Values.Where(b => b.Id == Blocks.Torch))
        {
            var wall = t.Meta switch { 1 => t.Pos.Offset(-1, 0, 0), 2 => t.Pos.Offset(1, 0, 0), 3 => t.Pos.Offset(0, 0, -1), _ => t.Pos.Offset(0, 0, 1) };
            Assert.True(BuildBlocks.NormalCube(f.W.GetBlock(wall)), $"torch {t} has no wall at {wall}");
        }
        foreach (var l in turned.Blocks.Values.Where(b => b.Id == Blocks.Ladder))
        {
            var wall = l.Meta switch { 2 => l.Pos.Offset(0, 0, 1), 3 => l.Pos.Offset(0, 0, -1), 4 => l.Pos.Offset(1, 0, 0), _ => l.Pos.Offset(-1, 0, 0) };
            Assert.True(BuildBlocks.NormalCube(f.W.GetBlock(wall)), $"ladder {l} has no wall at {wall}");
        }
    }

    [Fact]
    public void The_capture_is_Foxs_protected_build_and_my_house_finds_the_design()
    {
        using var db = AgentDb.Open(null);
        var f = Setup(db);
        // A bot once placed the bench outside: once captured it's Fox's, never ours to take back.
        f.W.Placed.Note(new BlockPos(4, 64, 5), 0, Blocks.Slab, "maid_chan", DateTimeOffset.UtcNow);
        var rec = Captured(f, out _);
        Assert.Null(f.W.Placed.Get(new BlockPos(4, 64, 5), 0));
        var st = f.Memory.GetStructure("Fox's house")!;
        Assert.Equal("build", st.Kind);
        Assert.Equal("PoofImaFox", st.TaughtBy);
        Assert.Equal(new Agent.Memory.BlockPos(0, 63, 0), st.Min);
        Assert.Equal(new Agent.Memory.BlockPos(6, 67, 5), st.Max);
        Assert.Equal(rec.Id, DesignCapture.LinkedDesign(st));

        var adapter = new MemoryStoreAdapter(f.Memory);
        var protection = new Protection(new BotOptions(), adapter, adapter) { Placed = f.W.Placed, Dimension = () => 0 };
        var wall = protection.Check(new BlockPos(0, 64, 2), Blocks.Cobblestone);
        Assert.True(wall.Protected);
        // A saved design says how to work on it (saving is a record, not a lock: Fox 10-30).
        Assert.Equal("part of the Fox's house build (design #1; editing it needs a trusted player's ask)", wall.Why);
        Assert.True(protection.IsProtected(new BlockPos(4, 64, 5), Blocks.Slab));    // the bench too
        Assert.False(protection.Check(new BlockPos(-1, 64, 2), Blocks.Log).Protected); // the oak isn't the house

        // "my house" (Fox), "Fox's house", the exact name → design #1; someone else's "my house" isn't Fox's.
        Assert.Equal(rec.Id, DesignCapture.Resolve(f.Store, f.Memory, "my house", "PoofImaFox")?.Id);
        Assert.Equal(rec.Id, DesignCapture.Resolve(f.Store, f.Memory, "Fox's house", "kiwi")?.Id);
        Assert.Equal(rec.Id, DesignCapture.Resolve(f.Store, f.Memory, "the Fox's house", null)?.Id);
        Assert.Null(DesignCapture.Resolve(f.Store, f.Memory, "my house", "kiwi"));
        Assert.Null(DesignCapture.Resolve(f.Store, f.Memory, "1", "PoofImaFox"));
    }

    [Fact]
    public async Task The_design_tool_captures_where_the_speaker_stands_and_refuses_a_rebuild_on_top()
    {
        var (h, c) = TestBot.Create();
        h.Runtime.Options.Self.TrustedPlayers = ["PoofImaFox"];
        var w = c.World;
        w.ApplyChunkRegion(-48, 0, -32, 96, 128, 64, new byte[96 * 128 * 64], null, null, null);
        for (int x = -30; x <= 12; x++) for (int z = -10; z <= 12; z++) { w.SetBlock(x, 62, z, Blocks.Dirt, 0); w.SetBlock(x, 63, z, Blocks.Grass, 0); }
        House(w);
        w.Entities.SpawnPlayer(7, "PoofImaFox", new Vec3d(3.5, 64, 2.5), 0, 0);
        Agent.TurnContext.Current = new Agent.TurnInfo("PoofImaFox", ["PoofImaFox"], false);
        try
        {
            var r = await h.Run("design", new { op = "capture", name = "Fox's house" });
            Assert.True(r.Ok, r.Text);
            Assert.True(r.Text.StartsWith("captured 'Fox's house' as design #1: 7×6×5, 138 blocks", StringComparison.Ordinal), r.Text);
            Assert.Equal(1, DesignCapture.LinkedDesign(h.Runtime.Memory.GetStructure("Fox's house")!));
            var show = await h.Run("design", new { op = "show", id = "my house" });
            Assert.StartsWith("#1 \"Fox's house\" 7×6×5", show.Text);
            var again = await h.Run("design", new { op = "build", id = "my house" });
            Assert.False(again.Ok);
            Assert.Contains("on top of the original Fox's house", again.Text);
        }
        finally { Agent.TurnContext.Current = null; }
    }
}
