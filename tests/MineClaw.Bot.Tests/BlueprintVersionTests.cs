using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;
using BlockPos = MineClaw.World.BlockPos;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Fox, 11-03: "We'll need to get all the blocks in the designer, and we'll need a dropdown for which version we're designing
/// for, and then the designs will have version compatibility." These pin the design palette of each version (complete, with the
/// technical blocks left out), name resolution with "did you mean", the facing rules of the new families, two-cell pairing on the
/// canvas, the version recorded through save / revision / revert, conversion between the versions, and builds refusing what the
/// world can't hold (or leaving out what the builder can't place) instead of placing a wrong block.
/// </summary>
public class BlueprintVersionTests
{
    private const string Fox = "PoofImaFox";
    private static DesignPalette Modern => DesignPalette.For(DesignVersions.Release);
    private static DesignPalette Beta => DesignPalette.For(DesignVersions.Beta);

    // ------------------------------------------------------------------ the palettes

    [Fact]
    public void Each_version_offers_its_whole_palette_and_every_entry_resolves_back_to_itself()
    {
        Assert.InRange(Modern.Entries.Count, 390, 600);
        Assert.InRange(Beta.Entries.Count, 100, 180);
        foreach (var pal in new[] { Modern, Beta })
        {
            using var v = DesignVersions.Use(pal.Version);
            Assert.Equal(pal.Entries.Count, pal.Entries.Select(e => (e.Id, e.Meta)).Distinct().Count()); // one button per state
            foreach (var e in pal.Entries)
            {
                Assert.True(pal.Valid(e.Id, e.Meta), $"{pal.Version} {e.Spec} {e.Id}:{e.Meta}");
                Assert.True(pal.TryResolve(e.Spec, out var st, out var why), $"{pal.Version} {e.Spec}: {why}");
                Assert.Equal((e.Id, e.Meta), st);
                Assert.Contains(e.Category, DesignPalette.Categories);
            }
        }
        foreach (var cat in DesignPalette.Categories) Assert.Contains(Modern.Entries, e => e.Category == cat);
        // Spot checks: every colour, wood and stone kind; doors of every wood; beds; b1.8.1's wool by meta.
        Assert.Equal((251, 2), Modern.Resolve("concrete:magenta"));
        Assert.Equal((160, 5), Modern.Resolve("stained_glass_pane:lime"));
        Assert.Equal((193, 0), Modern.Resolve("spruce_door"));
        Assert.Equal((26, 0), Modern.Resolve("bed"));
        Assert.Equal((1, 3), Modern.Resolve("diorite"));
        Assert.Equal(16, Modern.Of(171).Count); // carpets
        Assert.Equal(16, Enumerable.Range(235, 16).Count(Modern.Has)); // glazed terracotta
        Assert.Equal(6, Modern.Of(5).Count);
        Assert.Equal((35, 7), Beta.Resolve("wool:7"));
        Assert.Equal((35, 7), Beta.Resolve("gray_wool"));
        Assert.Equal(16, Beta.Of(35).Count);
        Assert.Equal(3, Beta.Of(17).Count); // oak, spruce, birch
    }

    [Fact]
    public void Technical_and_unobtainable_blocks_are_left_out_with_the_reason()
    {
        int[] modernOut = [0, 7, 8, 10, 34, 36, 51, 52, 62, 74, 75, 90, 94, 97, 119, 120, 124, 125, 137, 150, 166, 181, 199, 204, 209, 210, 211, 212, 217, 255];
        foreach (var id in modernOut) Assert.False(Modern.Has(id), $"1.12.2 {id}");
        Assert.Contains("flowing", Modern.Refusal(8, 0));
        Assert.Contains("piston", Modern.Refusal(34, 0));
        Assert.Contains("operator-only", Modern.Refusal(137, 0));
        Assert.Contains("runtime state", Modern.Refusal(62, 2));
        Assert.Contains("double slab", Modern.Refusal(125, 0));
        Assert.NotNull(Modern.Refusal(44, 2)); // the petrified oak slab: technical (the oak slab is 126:0)
        Assert.Null(Modern.Refusal(43, 8));     // smooth stone: a double slab with no full block of its own
        Assert.Null(Modern.Refusal(9, 0));      // a water source is a design block
        int[] betaOut = [0, 2, 8, 10, 16, 18, 21, 30, 34, 36, 51, 52, 56, 62, 73, 74, 75, 79, 90, 94, 95, 97, 99, 100];
        foreach (var id in betaOut) Assert.False(Beta.Has(id), $"b1.8.1 {id}");
        Assert.Contains("silk touch", Beta.Refusal(2, 0));
        Assert.Contains("decay", Beta.Refusal(18, 0));
        Assert.Contains("doesn't exist in b1.8.1", Beta.Refusal(251, 0));
    }

    [Fact]
    public void Names_resolve_in_every_spelling_and_a_wrong_one_says_what_was_meant()
    {
        var m = Modern;
        Assert.Equal((53, 7), m.Resolve("minecraft:oak_stairs:north:top"));
        Assert.Equal((35, 14), m.Resolve("red_wool"));
        Assert.Equal((35, 14), m.Resolve("Red Wool"));
        Assert.Equal((35, 8), m.Resolve("wool:light_gray"));
        Assert.Equal((95, 3), m.Resolve("Light Blue Stained Glass"));
        Assert.Equal((17, 5), m.Resolve("log:spruce:x"));
        Assert.Equal((162, 0), m.Resolve("log:acacia"));
        Assert.Equal((126, 1), m.Resolve("spruce_slab"));
        Assert.Equal((126, 9), m.Resolve("wooden_slab:spruce:top"));
        Assert.Equal((50, 1), m.Resolve("torch:wall=west"));
        Assert.Equal((65, 3), m.Resolve("ladder:north"));
        Assert.Equal((54, 2), m.Resolve("chest:north"));
        Assert.Equal((61, 3), m.Resolve("furnace:south"));
        Assert.Equal((26, 2), m.Resolve("bed:north"));
        Assert.Equal((64, 3), m.Resolve("wooden_door:south")); // the side it opens to (BuildBlocks.DoorMeta)
        Assert.Equal((145, 4), m.Resolve("anvil:slightly_damaged"));
        Assert.Equal((175, 4), m.Resolve("rose_bush"));
        Assert.Equal((4, 0), Beta.Resolve("cobble"));
        Assert.Equal((44, 3), Beta.Resolve("slab:cobble"));
        Assert.Equal((44, 2), Beta.Resolve("wooden_slab"));
        Assert.Equal((98, 1), Beta.Resolve("stonebrick:mossy"));

        Assert.False(m.TryResolve("redwol", out _, out var why));
        Assert.Contains("did you mean", why);
        Assert.Contains("wool:red", why);
        Assert.False(m.TryResolve("concrete:mauve", out _, out why));
        Assert.Contains("isn't a variant of concrete", why);
        Assert.Contains("magenta", why);
        Assert.False(Beta.TryResolve("concrete", out _, out why));
        Assert.Contains("unknown b1.8.1 block 'concrete'", why);
        Assert.False(Beta.TryResolve("planks:spruce", out _, out why)); // b1.8.1 planks have no woods
        Assert.False(m.TryResolve("stone:north", out _, out why));
        Assert.Contains("has no facing", why);
        Assert.False(Beta.TryResolve("oak_stairs:top", out _, out why)); // no upside-down stairs in b1.8.1
        Assert.False(m.TryResolve("fire", out _, out why));
        Assert.Contains("fire", why);
    }

    [Fact]
    public void Find_blocks_searches_names_specs_and_categories()
    {
        Assert.Equal("concrete:magenta", Modern.Find("magenta concrete", 3)[0].Spec);
        Assert.All(Modern.Find("stairs", 40), e => Assert.Equal(DesignFamily.Stairs, e.Family));
        Assert.Contains(Modern.Find("redstone", 40), e => e.Spec == "unpowered_repeater");
        Assert.Contains(Modern.Find("rail", 10), e => e.Spec == "golden_rail");
        Assert.Contains(Modern.Find("251:5", 3), e => e.Spec == "concrete:lime");
        Assert.Empty(Beta.Find("concrete", 5));
        // The model's summary names the categories, never the whole list.
        var sum = Modern.Summary();
        Assert.Contains("Colored (", sum);
        Assert.Contains("concrete", sum);
        Assert.True(sum.Length < 1600, $"{sum.Length} chars");
    }

    // ------------------------------------------------------------------ facings

    [Fact]
    public void New_stateful_families_rotate_and_mirror()
    {
        Assert.Equal(1, DesignFacing.Rotate(26, 0, 1));      // a bed facing south turns to face west
        Assert.Equal(9, DesignFacing.Rotate(26, 8, 1));      // its head half keeps the head bit
        Assert.Equal(1, DesignFacing.Rotate(235, 0, 1));     // glazed terracotta
        Assert.Equal(5, DesignFacing.Rotate(66, 2, 1));      // a rail ascending east → ascending south
        Assert.Equal(7, DesignFacing.Rotate(66, 6, 1));      // curve SE → SW
        Assert.Equal(1, DesignFacing.Rotate(66, 0, 3));      // N-S → E-W
        Assert.Equal(8 | 3, DesignFacing.Rotate(27, 8 | 5, 1)); // a powered rail ascending south → west, still powered
        Assert.Equal(2, DesignFacing.Rotate(106, 1, 1));     // vines on the south → on the west
        Assert.Equal(4, DesignFacing.Rotate(63, 0, 1));      // a standing sign turns a quarter
        Assert.Equal(12, DesignFacing.Mirror(63, 4, 'x'));
        Assert.Equal(7, DesignFacing.Mirror(66, 6, 'x'));
        Assert.Equal(9, DesignFacing.Mirror(66, 6, 'z'));
        Assert.Equal(1, DesignFacing.Rotate(193, 0, 1));     // a spruce door
        Assert.Equal(5, DesignFacing.Rotate(93, 4, 1));      // a repeater keeps its delay
        Assert.Equal(4, DesignFacing.Rotate(155, 3, 1));     // a quartz pillar along x → along z
        Assert.Equal(3, DesignFacing.Rotate(54, 5, 1));      // a chest facing east → south
        Assert.Equal(3, DesignFacing.Rotate(69, 1, 1));      // a lever facing east → south
        Assert.Equal(6, DesignFacing.Rotate(69, 5, 1));      // a floor lever along z → along x
        Assert.Equal(3, DesignFacing.Rotate(23, 5, 1));      // a dispenser facing east → south
        Assert.Equal(1, DesignFacing.Rotate(33, 1, 1));      // a piston facing up stays up
        using (GameData.Override(Release1122GameData.Instance))
            Assert.Equal(9, DesignFacing.Rotate(17, 5, 1));  // a 1.12.2 spruce log along x → along z
        Assert.Equal(1, DesignFacing.Rotate(17, 1, 1));      // b1.8.1 logs stand upright
        // BuildBlocks keeps its own families and asks the design rules for the rest; upside-down stairs stay upside down.
        Assert.Equal(6, BuildBlocks.Rotate(53, 4, 1));
        Assert.Equal(1, BuildBlocks.Rotate(26, 0, 1));
        Assert.Equal(1, PlanTransform.Mirror(134, 0, 'z'));   // spruce stairs up east, mirrored east↔west: up west
    }

    // ------------------------------------------------------------------ the canvas

    [Fact]
    public void Two_cell_blocks_are_kept_whole_on_the_canvas_and_chests_pair()
    {
        var c = new DesignerCanvas { Version = DesignVersions.Release };
        c.Apply([new CanvasCell(10, 0, 10, 26, 0)]);                    // a bed facing south: its head one step south
        Assert.Equal((26, 8), c.Get(10, 0, 11));
        c.Apply([new CanvasCell(20, 0, 20, 26, 1)]);                    // facing west: the head to the west
        Assert.Equal((26, 9), c.Get(19, 0, 20));
        c.Apply([new CanvasCell(10, 0, 11, Blocks.Stone, 0)]);          // replacing the head takes the foot away
        Assert.Equal((0, 0), c.Get(10, 0, 10));
        c.Apply([new CanvasCell(30, 0, 30, 193, 1)]);                   // a spruce door: its upper half
        Assert.Equal((193, 9), c.Get(30, 1, 30));
        c.Apply([new CanvasCell(32, 0, 30, 175, 4)]);                   // a rose bush: two tall
        Assert.Equal((175, 12), c.Get(32, 1, 30));
        c.Apply([new CanvasCell(40, 0, 40, 54, 2)]);                    // a chest facing north…
        c.Apply([new CanvasCell(41, 0, 40, 54, 5)]);                    // …and one beside it facing east: they pair, facing north
        Assert.Equal((54, 2), c.Get(41, 0, 40));
        Assert.Equal(0, c.Apply([new CanvasCell(1, 0, 1, 251, 5)], out var rej).Count(x => x.Id == 0)); // concrete: a 1.12.2 block
        Assert.Equal(0, rej);
        var beta = new DesignerCanvas { Version = DesignVersions.Beta };
        beta.Apply([new CanvasCell(1, 0, 1, 251, 5), new CanvasCell(2, 0, 1, 95, 0)], out rej);
        Assert.Equal(2, rej);                                           // no concrete, no locked chest in a b1.8.1 design
    }

    // ------------------------------------------------------------------ save, revisions, revert

    private sealed class Air : IBlockAccess
    {
        public int GetBlock(int x, int y, int z) => 0;
        public int GetMeta(int x, int y, int z) => 0;
        public bool IsLoaded(int x, int z) => true;
        public BlockInfo Info(int id) => Blocks.Get(id);
    }

    private static BlueprintService Designs(out DesignHistory history)
    {
        var db = AgentDb.Open(null);
        var s = new BlueprintService(new BlueprintOptions(), new BlueprintStore(db), new BlueprintPreview())
        {
            Site = () => throw new InvalidOperationException("no site in tests"), Crew = () => throw new InvalidOperationException(),
            ResolveSite = (_, _) => (null, "n/a"), Trusted = () => [Fox],
        };
        s.History = history = new DesignHistory(s, new DesignRevisions(db));
        return s;
    }

    private static DesignerSessionRecord Session(string version, params CanvasCell[] cells)
    {
        var s = new DesignerSessionRecord { Owner = Fox, Model = "main", Name = "new design" };
        s.Canvas.Version = version;
        using (DesignVersions.Use(version)) s.Canvas.Apply(cells);
        return s;
    }

    [Fact]
    public void The_version_is_recorded_through_save_revision_load_and_revert()
    {
        // A 1.12.2 design on a b1.8.1 server: its own blocks, its own names.
        var svc = Designs(out var h);
        var s = Session(DesignVersions.Release, new CanvasCell(30, 0, 30, 251, 14), new CanvasCell(31, 0, 30, 95, 3), new CanvasCell(32, 0, 30, 193, 1),
            new CanvasCell(30, 1, 30, 53, 6), new CanvasCell(34, 0, 30, 26, 0));
        var res = DesignerSave.Save(svc, s, "red hut", null, Fox, asNew: false);
        Assert.True(res.Ok, res.Text);
        Assert.Contains("(1.12.2)", res.Text);
        var rec = svc.Store.Get(res.DesignId)!;
        Assert.Equal(DesignVersions.Release, DesignVersions.OfDsl(rec.Dsl));
        Assert.Equal("drawn exactly", rec.Eval);
        var r1 = h.Revisions.Head(rec.Id)!;
        Assert.Equal(DesignVersions.Release, DesignVersions.OfDsl(r1.Dsl));
        // Loaded back: the same version, every cell as it was (the door's and the bed's second halves too).
        var (canvas, _, _, why) = DesignerSave.Load(svc, rec.Id);
        Assert.Null(why);
        Assert.Equal(DesignVersions.Release, canvas!.Version);
        Assert.Equal(s.Canvas.Blocks.Values.OrderBy(x => x).ToList(), canvas.Blocks.Values.OrderBy(x => x).ToList());
        // A second revision, then a revert to r1: r3 is still 1.12.2.
        s.Canvas.Apply([new CanvasCell(33, 0, 30, 251, 1)]);
        Assert.True(DesignerSave.Save(svc, s, "red hut", null, Fox, asNew: false).Ok);
        rec = svc.Store.Get(rec.Id)!;
        Assert.Equal(2, h.Revisions.Head(rec.Id)!.Rev);
        h.World = () => new Air();
        var (_, text) = h.Revert(rec, "r1", Fox);
        Assert.Contains("reverting #1 red hut to r1 (now r3)", text);
        var r3 = h.Revisions.Head(rec.Id)!;
        Assert.Equal(3, r3.Rev);
        Assert.Equal(DesignVersions.Release, DesignVersions.OfDsl(r3.Dsl));
        Assert.Equal(h.Revisions.Snapshot(rec.Id, 1)!.Blocks.Count, h.Revisions.Snapshot(rec.Id, 3)!.Blocks.Count);
        // A legacy design (no version line) is the server's.
        Assert.Equal(DesignVersions.Beta, DesignVersions.Of(new BlueprintRecord { Dsl = "size 1x1x1\npalette C=cobblestone\nlayer 0\nC" }));
    }

    // ------------------------------------------------------------------ conversion and builds

    [Fact]
    public void Designs_convert_between_versions_with_a_report()
    {
        var cells = new Dictionary<BlockPos, (int, int)>
        {
            [new BlockPos(0, 0, 0)] = (1, 0), [new BlockPos(1, 0, 0)] = (5, 1), [new BlockPos(2, 0, 0)] = (251, 0), [new BlockPos(3, 0, 0)] = (95, 3),
            [new BlockPos(4, 0, 0)] = (193, 1), [new BlockPos(4, 1, 0)] = (193, 9), [new BlockPos(5, 0, 0)] = (53, 6), [new BlockPos(6, 0, 0)] = (26, 0),
        };
        var (down, report) = DesignConvert.Convert(cells, DesignVersions.Release, DesignVersions.Beta);
        Assert.False(report.Compatible);
        Assert.Equal(1, report.Missing["White Concrete"]);
        Assert.Equal(1, report.Substituted["Spruce Planks → Wooden Planks"]);
        Assert.Equal(1, report.Substituted["Light Blue Stained Glass → Glass"]);
        Assert.Equal(1, report.Substituted["Spruce Door → Wooden Door"]); // one door, not two halves
        Assert.Equal((64, 9), down[new BlockPos(4, 1, 0)]);
        Assert.Equal((53, 2), down[new BlockPos(5, 0, 0)]);            // upside-down stairs → b1.8.1's only kind
        Assert.Equal((26, 0), down[new BlockPos(6, 0, 0)]);
        Assert.False(down.ContainsKey(new BlockPos(2, 0, 0)));         // the concrete is left out
        Assert.Contains("not in b1.8.1: 1 White Concrete", report.Text());
        // b1.8.1 → 1.12.2: everything exists; the wooden slab is the oak slab.
        var (up, back) = DesignConvert.Convert(new Dictionary<BlockPos, (int, int)> { [default] = (44, 2), [new BlockPos(1, 0, 0)] = (35, 4) },
            DesignVersions.Beta, DesignVersions.Release);
        Assert.True(back.Exact);
        Assert.Equal((126, 0), up[default]);
        Assert.Equal(ConvertKind.Missing, DesignConvert.Map(DesignVersions.Beta, DesignVersions.Release, 95, 0).Kind); // a locked chest never becomes glass
    }

    private static BlueprintRecord Saved(BlueprintService svc, string version, params CanvasCell[] cells)
    {
        var res = DesignerSave.Save(svc, Session(version, cells), "test", null, Fox, asNew: true);
        Assert.True(res.Ok, res.Text);
        return svc.Store.Get(res.DesignId)!;
    }

    [Fact]
    public void A_build_refuses_blocks_its_world_lacks_and_leaves_out_what_the_builder_cant_place()
    {
        var svc = Designs(out _);
        // A 1.12.2 design with concrete on this b1.8.1 server: refused, the blocks named; never built wrong.
        var concrete = Saved(svc, DesignVersions.Release, new CanvasCell(30, 0, 30, 251, 0), new CanvasCell(31, 0, 30, 4, 0));
        var why = svc.WorldRefusal(concrete);
        Assert.NotNull(why);
        Assert.Contains("is drawn for 1.12.2; these blocks don't exist in b1.8.1: 1 White Concrete", why);
        Assert.Null(svc.PlanOf(concrete, out var err));
        Assert.Contains("White Concrete", err);
        // One whose blocks all map: converted (the substitutions said), the bed left out (the builder can't place beds yet).
        var hut = Saved(svc, DesignVersions.Release, new CanvasCell(30, 0, 30, 5, 1), new CanvasCell(31, 0, 30, 4, 0), new CanvasCell(33, 0, 30, 26, 0));
        Assert.Null(svc.WorldRefusal(hut));
        var plan = svc.PlanOf(hut, out err)!;
        Assert.Null(err);
        Assert.Contains("Spruce Planks → Wooden Planks", plan.Converted);
        Assert.Contains(plan.Blocks.Values, b => b.Id == Blocks.Planks && b.Meta == 0);
        Assert.Equal(2, plan.Unsupported.Count);                       // the bed's foot and head
        Assert.DoesNotContain(plan.Blocks.Values, b => b.Id == 26);
        Assert.Contains("2 cells the builder can't place yet, left out: 1 Bed", BlueprintService.CompatNote(plan));
        // As drawn (the history, the studio): nothing converted or left out.
        var drawn = svc.PlanOf(hut, out _, forWorld: false)!;
        Assert.Contains(drawn.Blocks.Values, b => b.Id == 5 && b.Meta == 1);
        Assert.Contains(drawn.Blocks.Values, b => b.Id == 26);
        // A legacy design builds as before.
        var legacy = svc.Store.Insert(new BlueprintRecord { Name = "old", Dsl = "blueprint old\nsize 2x1x1\npalette C=cobblestone\nlayer 0\nCC", Status = DesignStatus.Accepted });
        var lp = svc.PlanOf(legacy, out err)!;
        Assert.Null(err);
        Assert.Equal(2, lp.Blocks.Count);
        Assert.Empty(lp.Unsupported);
        Assert.Null(lp.Converted);
    }
}
