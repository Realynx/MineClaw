using MineClaw.Agent.Llm;
using MineClaw.Agent.Perception;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Bot.Perception.Vision.Eval;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-27 21:12, the roofed dual cobble gen: Sonnet perception got 5 of 6 claims wrong. Root cause: a cobble roof at y66
/// over the gen hid every gen cell (and its water) in the TOP and ISO panels, the "$ protected" letter couldn't be drawn and
/// its gold fill covered the whole "gen housing" region. Fixes: roofs above the feet layer are cut away, '$' is drawable and
/// protected cells are outlined only; and the questions the voxels answer exactly (gen cells/floor/walls, gen fluids, faces,
/// void, focus block, portal, trees/chests/ores) are routed to <see cref="VisionRouter"/> before any model call. Guardrails:
/// a capped terse model answer, a rolling hallucination pause for auto-vision, refuted claims never reach the brain.
/// </summary>
public class VisionRouterTests
{
    private static string RepoFile(params string[] parts)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MineClaw.sln"))) d = d.Parent;
        return Path.Combine([d!.FullName, .. parts]);
    }

    private static VisionEvalCase DualGen() =>
        VisionEval.DualGenLive(File.ReadAllText(RepoFile("tests", "fixtures", "vision-eval", "dual-gen.txt")))!;

    private static readonly (int X, int Y, int Z)[] GenTruth = [(4, 64, 6), (5, 64, 7), (6, 64, 8)];

    private static VisionScene Top(VisionEvalCase c, bool cut) => new()
    {
        View = VisionView.Top, Radius = 5, Cx = 4, Cy = 64, Cz = 6, Blocks = c.World.PlanningView, Roles = VisionEval.Roles(c).Map,
        Focus = (4, 64, 6), CutAway = cut, CellPx = 18, Up = 2, Down = 8, SideUp = 4, SideDown = 5, IsoUp = 4, IsoDown = 5,
    };

    /// <summary>Pixels of the gen-cell role colour (outline + G letter) inside world column (x, z) of a top view.</summary>
    private static int GenPixels(VisionImage img, VisionScene s, int x, int z)
    {
        var (px, py, cs) = VisionRenderer.TopCellPixel(s, x, z);
        int n = 0;
        for (int j = 0; j < cs; j++)
            for (int i = 0; i < cs; i++)
                if ((img.Pixels![(py + j) * img.Width + px + i] & 0xffffff) == BlockRoles.Colour(BlockRole.GenCell)) n++;
        return n;
    }

    [Fact]
    public void Roofed_dual_gen_shows_its_gen_cells_in_the_top_view()
    {
        var c = DualGen();
        var roles = VisionEval.Roles(c);
        foreach (var (x, y, z) in GenTruth) Assert.Equal(BlockRole.GenCell, roles.RoleOf(x, y, z));
        Assert.Equal(BlockRole.SoftProtected, roles.RoleOf(4, 63, 6));               // the floor under a cell: housing ($), not G
        Assert.Equal(BlockRole.None, roles.RoleOf(4, 65, 6));                        // nor the air over it (lava + water there too)
        Assert.Equal(Blocks.Cobblestone, c.World.GetBlock(4, 66, 6));                // the roof that hid them
        Assert.True(Blocks.IsWater(c.World.GetBlock(5, 64, 6)) && Blocks.IsLava(c.World.GetBlock(4, 64, 7)));

        var r = new VisionRenderer();
        // The live bug: without the cut-away the roof hid every gen cell in TOP.
        var before = Top(c, cut: false);
        var b = r.Render(before);
        foreach (var (x, _, z) in GenTruth) Assert.Equal(0, GenPixels(b, before, x, z));
        // Fixed: each cell shows its G exactly in its own column; its neighbours (gen wall, water, lava) don't.
        var after = Top(c, cut: true);
        var a = r.Render(after);
        foreach (var (x, _, z) in GenTruth) Assert.True(GenPixels(a, after, x, z) > 20, $"{x},{z}: {GenPixels(a, after, x, z)}");
        foreach (var (x, z) in new[] { (3, 6), (5, 6), (4, 7), (6, 7), (5, 8), (4, 5), (7, 8), (6, 9) })
            Assert.Equal(0, GenPixels(a, after, x, z));
        Assert.Contains("Roof above y64 cut away", a.Legend);
        // The ruler: the focus x tick is centred over the focus column (same layout helper as the renderer).
        var (fx, _, cs) = VisionRenderer.TopCellPixel(after, 4, 6);
        var (gx, _, _) = VisionRenderer.TopCellPixel(after, 5, 7);
        Assert.Equal(cs, gx - fx);
        // The contact sheet the bot renders cuts roofs in TOP and ISO, and says so.
        var sheet = VisionEval.Sheet(c);
        Assert.Contains("roofs above it cut away", sheet.Legend);
        Assert.Contains("ISO (corner letters = compass, roofs cut away)", sheet.Legend);
    }

    [Fact]
    public void Every_role_letter_in_the_legend_is_drawable()
    {
        foreach (var role in Enum.GetValues<BlockRole>())
            if (BlockRoles.Glyph(role) is { } g) Assert.True(PixelFont.Has(g), $"{role} '{g}' has no glyph: the legend would name a letter the picture never shows");
    }

    [Fact]
    public void Router_answers_the_dual_gen_exactly_with_no_model()
    {
        var c = DualGen();
        var roles = VisionEval.Roles(c).Map;
        Assert.Equal(VisionIntent.GenCells, VisionRouter.Classify(VisionEval.GenQuestion));
        var a = VisionRouter.Answer(VisionEval.GenQuestion, c.World.PlanningView, roles, c.Focus, c.Radius)!;
        Assert.True(c.Grade(a), a);
        Assert.StartsWith("[vision] exact: gen cells (G, mine only these): 4,64,6 water E lava S; 5,64,7 water ", a);
        Assert.Contains("6,64,8 water N lava W", a);
        Assert.Contains("not gen cells: floor y63 under them; walls 3,64,6", a);
        Assert.DoesNotContain("NO water", a);
        Assert.True(a.Length <= PerceptionVerifier.MaxChars);

        // 21:14 "is water touching the lava, why did it stop?": the gen is fine (Fox checked); the router says exactly that.
        const string health = "Is there lava and water flowing next to cell 4,64,6? Why would it stop making cobble?";
        Assert.Equal(VisionIntent.GenFluids, VisionRouter.Classify(health));
        var h = VisionRouter.Answer(health, c.World.PlanningView, roles, (4, 64, 6), 5)!;
        Assert.Contains("4,64,6 water E lava S", h);
        // ... and when the water really is gone, it says that instead.
        c.World.SetBlock(5, 64, 6, 0, 0);
        var broken = VisionRouter.Answer(health, c.World.PlanningView, roles, (4, 64, 6), 5)!;
        Assert.Contains("4,64,6 NO water next to it lava S", broken);
    }

    [Theory]
    [InlineData("Which blocks here are generator cells (G, MINE HERE) and which are island floor or gen walls? Coords of the gen cells.", VisionIntent.GenCells)]
    [InlineData("I need cobblestone from the generator. Which exact block do I mine (x,y,z)? Not the island floor.", VisionIntent.GenCells)]
    [InlineData("Is there lava and water flowing next to cell 4,64,6? Why would it stop making cobble?", VisionIntent.GenFluids)]
    [InlineData("Is 7,62,9 free, which solid blocks next to it can a block be placed against, and is there void under it?", VisionIntent.Faces)]
    [InlineData("I'm standing in my own hole at 7,62,9 and want to place a block to climb out. Which adjacent solid blocks can I place against? coords.", VisionIntent.Faces)]
    [InlineData("What is the block at the focus: its role (gen cell, floor, wall, protected) and what is under it (void?)", VisionIntent.FocusBlock)]
    [InlineData("How many more floor blocks are east of me before the void edge? answer a number", VisionIntent.Void)]
    [InlineData("Where exactly is the nether portal (x range, y range, z range), and if I walk east out of it, what is there?", VisionIntent.Portal)]
    [InlineData("Are there fully grown oak trees (tall trunk with leaves canopy) near here? Describe which spots have logs and where saplings are.", VisionIntent.Things)]
    [InlineData("What items are in the chest at this spot? List them.", VisionIntent.Things)]
    [InlineData("any iron or coal ore around?", VisionIntent.Things)]
    [InlineData("Which one is the middle one of the three generator cells? coords x,y,z", VisionIntent.Model)]
    [InlineData("Check the fill floor at the focus: is it complete? List gaps or missing blocks with coords, and any void edge.", VisionIntent.Model)]
    [InlineData("what does this build look like?", VisionIntent.Model)]
    [InlineData("PoofimaFox pointed (pink box) and said \"this one\": which block is it, its role, and where is it relative to its neighbours?", VisionIntent.Model)]
    public void Router_classifies_rules_first(string question, VisionIntent expected) => Assert.Equal(expected, VisionRouter.Classify(question));

    [Fact]
    public void Router_finds_trees_saplings_and_chests_from_the_voxels()
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-32, 0, -32, 64, 128, 64, new byte[64 * 128 * 64], null, null, null);
        for (int x = -2; x <= 10; x++) for (int z = -2; z <= 10; z++) w.SetBlock(x, 63, z, Blocks.Grass, 0);
        for (int y = 64; y <= 68; y++) w.SetBlock(5, y, 5, Blocks.Log, 0);                    // an oak: 5 logs
        for (int x = 3; x <= 7; x++) for (int z = 3; z <= 7; z++) for (int y = 67; y <= 69; y++)
            if (w.GetBlock(x, y, z) == 0) w.SetBlock(x, y, z, Blocks.Leaves, 0);
        w.SetBlock(1, 64, 1, Blocks.Sapling, 0);
        w.SetBlock(8, 64, 2, Blocks.Chest, 0);
        RoleMap roles = (_, _, _) => BlockRole.None;
        const string q = "Are there fully grown oak trees (tall trunk with leaves canopy) near here? Describe which spots have logs and where saplings are.";
        var a = VisionRouter.Answer(q, w.PlanningView, roles, (0, 64, 0), 6)!;
        Assert.Contains("1 grown tree: oak 5,64,5 5 logs", a);
        Assert.Contains("1 sapling: 1,64,1", a);
        Assert.DoesNotContain("chest", a);
        var chest = VisionRouter.Answer("What items are in the chest at this spot? List them.", w.PlanningView, roles, (8, 64, 2), 6)!;
        Assert.Contains("chest 8,64,2", chest);
        Assert.Contains("contents aren't visible", chest);
    }

    [Fact]
    public void Refuted_claims_never_reach_the_brain_and_unsure_ones_are_marked()
    {
        // Live 21:14 replayed on the real gen: one true coordinate, a false "water 8 blocks west", a false "no water touching".
        var c = DualGen();
        var r = PerceptionPrompt.Result("""
            {"answer":"Lava is right next to 4,64,6, but the water is about 8 blocks west. With no water touching the lava, no cobble forms.","facts":[
              {"claim":"focus cell is cobblestone next to lava","coords":[[4,64,6]],"role":null,"confidence":0.8},
              {"claim":"the gen's water is far west","coords":[[-4,64,6]],"role":"water","confidence":0.7},
              {"claim":"gen cells in rows z7-z8","coords":[[6,64,7],[6,64,8]],"role":"gen cell","confidence":0.7},
              {"claim":"no water touches the lava","coords":[],"confidence":0.8}]}
            """, new LlmUsage(1100, 120, 0, 0), "claude-sonnet-5-5", "anthropic", 900);
        var v = PerceptionVerifier.Verify("q", r, c.World.PlanningView, VisionEval.Roles(c).Map, c.Focus, c.Radius);
        Assert.Null(v.Answer);
        Assert.DoesNotContain("8 blocks west", v.Text);
        Assert.DoesNotContain("-4,64,6", v.Text);
        Assert.DoesNotContain("6,64,7", v.Text);
        Assert.DoesNotContain("rows z7-z8", v.Text);                    // a partly refuted claim keeps only what the voxels confirm
        Assert.Contains("gen cell @6,64,8", v.Text);
        Assert.Contains("no water touches the lava (uncertain)", v.Text);
        Assert.Equal(2, v.Mismatched);
    }

    /// <summary>
    /// Perception output is advisory text for the brain/worker turn only: the vision code never writes memory, dig/gen-cell
    /// lists or beliefs (only verified voxel facts may be stored, by the code that owns them).
    /// </summary>
    [Fact]
    public void Perception_code_never_writes_memory_gen_cells_or_beliefs()
    {
        var dir = RepoFile("src", "MineClaw.Bot", "Perception", "Vision");
        var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).Append(RepoFile("src", "MineClaw.Bot", "GameTools", "VisionHub.cs"));
        // Writers of memory, dig cells, beliefs, observations and the shared world memory (reads are fine: landmarks, chunks).
        string[] writers = ["BeliefStore", "DigCells", "DigCellCorrections", ".Teach", "Retract", "Memory.Update", "UpsertStructure",
            "SetWaypoint", "AddNote", ".Upsert(", "UpsertMany", "Observations.Remove", "PutChunks", "TouchChunks", "SaveStructure"];
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            foreach (var wr in writers) Assert.False(text.Contains(wr, StringComparison.Ordinal), $"{Path.GetFileName(f)} references {wr}");
        }
    }

    [Fact]
    public void Rolling_hallucination_rate_pauses_auto_vision_with_a_warning()
    {
        var t = new PerceptionTelemetry();
        VerifiedAnswer A(int chk, int bad) => new("q", null, [], chk, bad, 0, 0, 0, "[vision] x");
        t.Record(A(5, 1), "see", 0);
        Assert.False(t.DeterministicOnly);                              // window not full yet
        t.Record(A(6, 5), "auto:gen", 0);                               // the live 21:12 call
        Assert.True(t.RollingRate > PerceptionTelemetry.PauseRate);
        Assert.True(t.DeterministicOnly);
        t.Record(A(10, 1), "see", 0);                                   // explicit see calls recover it
        Assert.False(t.DeterministicOnly);
        t.Record(A(0, 0), "see", 0);
        Assert.Equal(1, t.NoClaims);
    }
}
