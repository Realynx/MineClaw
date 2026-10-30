using MineClaw.Agent.Rag;
using MineClaw.Bot.Excavation;
using MineClaw.Skills;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-30 02:01 "remember this build so we can do it again elsewhere" / 02:31 "The stair is recorded at y38-60 only": a dig site
/// (the stair's rule, anchor and repeats, the rooms off it) survives a restart in the shared db, is found by name or by the design
/// it was learned from, and its air comes back as must-be-air cells.
/// </summary>
public class DigSiteStoreTests
{
    [Fact]
    public void A_stair_site_survives_a_restart_and_its_air_is_kept_clear_again()
    {
        var path = Path.Combine(Path.GetTempPath(), $"digsites-{Guid.NewGuid():N}.db");
        try
        {
            const string rule = "column 3 step 0,-1,0 turn -90 pivot 0.5,0.5";
            using (var db = AgentDb.Open(path))
            {
                var store = new DigSiteStore(db);
                store.Save(new DigSite { Name = "Spiral Stair", Kind = "pattern", Rule = rule, Anchor = new BlockPos(153, 60, 275), Repeats = 30, Design = 11 });
                store.Save(new DigSite { Name = "spiral stair room y29", Kind = "room", Parent = "Spiral Stair", Cells = [new(155, 29, 275), new(155, 30, 275)], Note = "east" });
            }
            using (var db = AgentDb.Open(path))
            {
                var store = new DigSiteStore(db);
                var s = store.Find("spiral_stair");
                Assert.NotNull(s);
                Assert.Equal(rule, s!.Rule);
                Assert.Equal(30, s.Repeats);
                Assert.Equal(new BlockPos(153, 60, 275), s.Anchor);
                Assert.Equal("Spiral Stair", store.Find("#11")!.Name); // by the design it was learned from
                Assert.Equal(2, store.Find("spiral stair room y29")!.Cells.Count);
                var keep = new KeepAirLog();
                Assert.Equal(2, DigSites.Apply(store, keep));
                Assert.Equal(90 + 2, keep.Count);
                Assert.True(keep.Contains(new BlockPos(153, 60, 275), 0));
                Assert.NotNull(keep.Refusal(new BlockPos(155, 29, 275), 0, MineClaw.Data.Blocks.Cobblestone));
                Assert.Null(keep.Refusal(new BlockPos(155, 29, 275), 0, MineClaw.Data.Blocks.Torch)); // torches and signs are fine
                Assert.True(store.Delete("Spiral Stair"));
                Assert.Single(store.All());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) try { File.Delete(f); } catch (IOException) { }
        }
    }
}
