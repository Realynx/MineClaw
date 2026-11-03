using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Designs;

namespace MineClaw.App.Tests;

/// <summary>
/// Below-ground building in the Design Studio: the canvas reaches 16 layers under the grass, digging is design data (dug cells →
/// forced air '.' + <c>ground N</c>) that survives save → revision → load / diff / revert, and the AI's tools dig and build down.
/// </summary>
public class DesignerUndergroundTests
{
    private static JsonElement E(object o) => JsonSerializer.SerializeToElement(o);

    private static (BlueprintService Service, AgentDb Db) Designs(Func<IBlockAccess?>? world = null)
    {
        var db = AgentDb.Open(null);
        var s = new BlueprintService(new BlueprintOptions(), new BlueprintStore(db), new BlueprintPreview())
        {
            Site = () => throw new InvalidOperationException("no site in tests"), Crew = () => throw new InvalidOperationException(),
            ResolveSite = (_, _) => (null, "n/a"), Trusted = () => ["poofimafox"],
        };
        s.History = new DesignHistory(s, new DesignRevisions(db)) { World = world ?? (() => null) };
        return (s, db);
    }

    /// <summary>A 4×4 house on the grass (floor y 0, walls y 1-2) over a 2×2 cellar: dug y -3..-1, a cobblestone floor at y -4.</summary>
    private static DesignerCanvas HouseWithCellar()
    {
        var c = new DesignerCanvas();
        DesignerTools.Run(c, "box", E(new { b = "planks", from = new[] { 30, 0, 30 }, to = new[] { 33, 0, 33 } }));
        DesignerTools.Run(c, "box", E(new { b = "planks", from = new[] { 30, 1, 30 }, to = new[] { 33, 2, 33 }, mode = "walls" }));
        var (text, dig, err) = DesignerTools.Run(c, "remove", E(new { from = new[] { 31, -3, 31 }, to = new[] { 32, -1, 32 } }));
        Assert.False(err, text);
        Assert.Equal(12, dig!.Cells.Count);
        DesignerTools.Run(c, "box", E(new { b = "cobblestone", from = new[] { 31, -4, 31 }, to = new[] { 32, -4, 32 } }));
        return c;
    }

    [Fact]
    public void The_canvas_reaches_16_layers_under_the_grass_and_digging_is_its_own_state()
    {
        Assert.True(DesignerCanvas.Inside(0, -16, 0));
        Assert.False(DesignerCanvas.Inside(0, -17, 0));
        Assert.True(DesignerCanvas.Inside(63, 47, 63));
        var c = new DesignerCanvas();
        Assert.True(c.IsEarth(5, -1, 5)); // undisturbed ground
        var dug = c.Apply([new CanvasCell(5, -1, 5, 0, 0), new CanvasCell(5, 3, 5, 0, 0)]);
        Assert.Single(dug); // air above ground changes nothing; below it digs
        Assert.Contains(new BlockPos(5, -1, 5), c.Dug);
        Assert.False(c.IsEarth(5, -1, 5));
        Assert.Equal(0, c.Count);
        // A block down there replaces the ground; removing it leaves the cell dug; earth fills it back.
        c.Apply([new CanvasCell(5, -2, 5, Blocks.Cobblestone, 0)]);
        Assert.DoesNotContain(new BlockPos(5, -2, 5), c.Dug);
        c.Apply([new CanvasCell(5, -2, 5, 0, 0)]);
        Assert.Contains(new BlockPos(5, -2, 5), c.Dug);
        var back = c.Apply([new CanvasCell(5, -2, 5, DesignerCanvas.Earth, 0), new CanvasCell(5, 9, 5, DesignerCanvas.Earth, 0)]);
        Assert.Equal([new CanvasCell(5, -2, 5, DesignerCanvas.Earth, 0)], back);
        Assert.True(c.IsEarth(5, -2, 5));
        Assert.Equal(1, c.Apply([new CanvasCell(5, -17, 5, 0, 0)], out var rejected).Count + rejected); // outside: rejected
        // Persistence keeps the dug cells; clear fills them back.
        var again = DesignerCanvas.Decode(c.Encode(), c.Rev);
        Assert.Equal(c.Dug.ToHashSet(), again.Dug.ToHashSet());
        Assert.Contains("dug 1 below ground", c.Describe());
        Assert.Contains("#=ground", c.Layer(-1));
        c.Clear();
        Assert.Empty(c.Dug);
    }

    [Fact]
    public void The_designer_knows_the_ground_and_its_tools_dig_and_build_down()
    {
        var system = DesignerTools.System("poofimafox");
        Assert.Contains("y -16..47", system);
        Assert.Contains("basements", system);
        var c = HouseWithCellar();
        Assert.Equal(4, c.Blocks.Count(kv => kv.Key.Y == -4));
        Assert.Equal(12, c.Dug.Count);
        // A stair down in the cellar: placed below y 0.
        var (text, stair, err) = DesignerTools.Run(c, "place", E(new { b = "cobblestone_stairs:north", at = new[] { new[] { 31, -3, 31 } } }));
        Assert.False(err, text);
        Assert.Single(stair!.Cells);
        Assert.DoesNotContain(new BlockPos(31, -3, 31), c.Dug);
        // "earth" fills a dug cell back.
        var (_, fill, _) = DesignerTools.Run(c, "place", E(new { b = "earth", at = new[] { new[] { 32, -1, 32 } } }));
        Assert.Equal(DesignerCanvas.Earth, fill!.Cells.Single().Id);
        Assert.Contains("filled 1 back with ground", DesignerCanvas.Summarize(fill.Cells));
        Assert.Contains("y-4", DesignerTools.Run(c, "describe", E(new { })).Text);
    }

    [Fact]
    public void A_cellar_survives_save_revisions_load_and_diff()
    {
        var (svc, _) = Designs();
        var session = new DesignerSessionRecord { Owner = "poofimafox", Canvas = HouseWithCellar() };
        var res = DesignerSave.Save(svc, session, "cellar house", "first", "poofimafox", false);
        Assert.True(res.Ok, res.Text);
        Assert.Contains("4 below ground", res.Text);
        var rec = svc.Store.Get(res.DesignId)!;
        Assert.Contains("\nground 4", rec.Dsl);
        // Placed on a site: the ground floor stands one above the ground block, the cellar is dug into the terrain under it.
        var plan = svc.PlanOf(rec, out var err)!;
        Assert.Null(err);
        int grade = plan.Origin.Y + plan.Blueprint.Ground;
        Assert.Equal(16 + 24 + 4, plan.Blocks.Count);
        Assert.Equal(12, plan.Air.Count(p => p.Y < grade));
        Assert.Equal(4, plan.Blocks.Values.Count(b => b.Pos.Y == grade - 4 && b.Id == Blocks.Cobblestone));
        var head = svc.History!.Revisions.Head(rec.Id)!;
        Assert.Equal(12, svc.History.Revisions.Snapshot(rec.Id, head.Rev)!.Air.Count(p => p.Y < grade));
        // Loading: the ground floor back on y 0, the cellar back as dug cells.
        var (canvas, off, _, why) = DesignerSave.Load(svc, rec.Id);
        Assert.Null(why);
        Assert.Equal(0, canvas!.Blocks.Where(kv => kv.Value.Id == Blocks.Planks).Min(kv => kv.Key.Y));
        Assert.Equal(-4, canvas.Blocks.Min(kv => kv.Key.Y));
        Assert.Equal(12, canvas.Dug.Count);
        Assert.All(canvas.Dug, p => Assert.InRange(p.Y, -3, -1));
        // Saving the loaded session again: same design, no new revision.
        var s2 = new DesignerSessionRecord { Owner = "kiwi", Canvas = canvas, Offset = off, DesignId = rec.Id };
        Assert.Equal(head.Rev, DesignerSave.Save(svc, s2, "cellar house", null, "kiwi", false).Rev);
        // Fill one cell back: r2; its diff says so even though no block changed.
        var p0 = canvas.Dug.First();
        canvas.Apply([new CanvasCell(p0.X, p0.Y, p0.Z, DesignerCanvas.Earth, 0)]);
        var r2 = DesignerSave.Save(svc, s2, "cellar house", "smaller cellar", "kiwi", false);
        Assert.Equal(head.Rev + 1, r2.Rev);
        Assert.Contains("filled 1 back", svc.History.Diff(svc.Store.Get(rec.Id)!, $"r{head.Rev} r{r2.Rev}"));
        Assert.Equal(11, DesignerSave.Load(svc, rec.Id).Canvas!.Dug.Count);
    }

    [Fact]
    public void A_design_that_starts_digging_keeps_its_house_where_it_stood_in_the_diff()
    {
        var (svc, _) = Designs();
        var c = new DesignerCanvas();
        DesignerTools.Run(c, "box", E(new { b = "planks", from = new[] { 30, 0, 30 }, to = new[] { 33, 0, 33 } }));
        var session = new DesignerSessionRecord { Owner = "poofimafox", Canvas = c };
        var r1 = DesignerSave.Save(svc, session, "pad", null, "poofimafox", false);
        var (loaded, off, _, _) = DesignerSave.Load(svc, r1.DesignId);
        DesignerTools.Run(loaded!, "remove", E(new { from = new[] { 31, -2, 31 }, to = new[] { 31, -1, 31 } }));
        var s2 = new DesignerSessionRecord { Owner = "poofimafox", Canvas = loaded!, Offset = off, DesignId = r1.DesignId };
        var r2 = DesignerSave.Save(svc, s2, "pad", "a pit", "poofimafox", false);
        Assert.True(r2.Ok, r2.Text);
        var text = svc.History!.Diff(svc.Store.Get(r1.DesignId)!, $"r{r1.Rev} r{r2.Rev}");
        Assert.Equal($"r{r1.Rev}→r{r2.Rev}: dug 2 below ground", text); // the planks did not "move"
    }

    [Fact]
    public void Reverting_a_placed_cellar_keeps_its_ground_and_digs_it_again()
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        for (int x = -8; x <= 8; x++) for (int z = -8; z <= 8; z++) for (int y = 55; y <= 63; y++) w.SetBlock(x, y, z, Blocks.Dirt, 0);
        var (svc, _) = Designs(() => w.PlanningView);
        var session = new DesignerSessionRecord { Owner = "poofimafox", Canvas = HouseWithCellar() };
        var res = DesignerSave.Save(svc, session, "cellar house", null, "poofimafox", false);
        var rec = svc.Store.Get(res.DesignId)!;
        // Put it in the world on a site at the ground block (0, 63, 0) and say it stands there (built).
        rec.Site = new BlockPos(0, 63, 0);
        var plan = svc.PlanOf(rec, out _)!;
        rec.Origin = plan.Origin;
        svc.Store.Update(rec);
        foreach (var b in plan.Blocks.Values) w.SetBlock(b.Pos.X, b.Pos.Y, b.Pos.Z, b.Id, b.Meta);
        foreach (var a in plan.Air) w.SetBlock(a.X, a.Y, a.Z, 0, 0);
        var r = svc.History!.Sync(rec, "poofimafox", RevisionAuthor.Player, "built")!;
        int grade = 63 + 1;
        Assert.Equal(grade, plan.Origin.Y + plan.Blueprint.Ground);
        // Someone fills the cellar in; "put it back" (revert to HEAD) digs it out again and keeps the ground.
        foreach (var a in plan.Air.Where(p => p.Y < grade)) w.SetBlock(a.X, a.Y, a.Z, Blocks.Dirt, 0);
        Assert.Contains("+12 dirt", svc.History.Status(rec));
        var (_, text) = svc.History.Revert(rec, $"r{r.Rev}", "poofimafox");
        Assert.Contains("reverting", text);
        var now = svc.Store.Get(rec.Id)!;
        Assert.Contains("\nground 4", now.Dsl);
        var head = svc.History.Revisions.Head(rec.Id)!;
        Assert.True(head.Rev > r.Rev);
        var after = svc.PlanOf(now, out _)!;
        Assert.Equal(12, after.Air.Count(p => p.Y < grade)); // the build digs them (forced air)
    }
}
