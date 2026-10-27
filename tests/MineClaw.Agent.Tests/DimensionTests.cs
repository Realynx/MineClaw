using System.Reflection;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Tools;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MineClaw.Agent.Tests;

/// <summary>Dimension-aware memory (schema v5): overworld and nether never mix.</summary>
public class DimensionTests(ITestOutputHelper output)
{
    private sealed class Kit : IDisposable
    {
        public int Dim;
        public AgentDb Db { get; } = AgentDb.Open(null);
        public RagIndex Rag { get; }
        public ObservationStore Obs { get; }
        public MemoryStore Mem { get; }
        public ToolRegistry Tools { get; } = new(600);
        public Kit()
        {
            Rag = new RagIndex(Db, backgroundEmbedding: false) { CurrentDimension = () => Dim };
            Obs = new ObservationStore(Rag);
            Mem = new MemoryStore(Db, Rag);
            MemoryTools.RegisterRecall(Tools, Mem, () => new BlockPos(0, 64, 0));
            WorldTools.Register(Tools, Obs, () => new BlockPos(0, 64, 0), memory: Mem);
        }
        public string Run(string tool, object input) =>
            Tools.ExecuteAsync(tool, System.Text.Json.JsonSerializer.SerializeToElement(input), CancellationToken.None).GetAwaiter().GetResult().Text;
        public void Dispose() { Rag.Dispose(); Db.Dispose(); }
    }

    private static Observation Furnace(int dim, string text = "furnace at 3,64,0") =>
        new("furnace", "3,64,0", text) { Bounds = Box.Point(3, 64, 0), Dimension = dim };

    [Fact]
    public void Same_coordinates_in_two_dimensions_are_distinct_rows_docs_and_tombstones()
    {
        using var k = new Kit();
        k.Obs.Upsert(Furnace(WorldDim.Overworld, "furnace at 3,64,0 (iron)"));
        k.Obs.Upsert(Furnace(WorldDim.Nether, "furnace at 3,64,0 (quartz)"));
        Assert.Equal(2, k.Obs.Count("furnace"));
        Assert.Contains("iron", k.Obs.Get("furnace", "3,64,0", dimension: 0)!.Text);
        Assert.Contains("quartz", k.Obs.Get("furnace", "3,64,0", dimension: -1)!.Text);
        Assert.Equal("obs:furnace:3,64,0", k.Obs.Get("furnace", "3,64,0", dimension: 0)!.DocRef); // v4 refs unchanged
        Assert.Equal("obs:d-1:furnace:3,64,0", k.Obs.Get("furnace", "3,64,0", dimension: -1)!.DocRef);
        Assert.NotNull(k.Rag.GetByRef("obs:d-1:furnace:3,64,0"));

        // Removing the nether one leaves the overworld one alone; contents are per row.
        k.Obs.Upsert(new Observation("chest", "1,64,1", "chest at 1,64,1") { Bounds = Box.Point(1, 64, 1), Dimension = 0, Contents = [new ItemCount(265, "iron_ingot", 5)] });
        k.Obs.Upsert(new Observation("chest", "1,64,1", "chest at 1,64,1") { Bounds = Box.Point(1, 64, 1), Dimension = -1, Contents = [new ItemCount(406, "quartz", 9)] });
        Assert.True(k.Obs.Remove("furnace", "3,64,0", dimension: -1));
        Assert.Null(k.Obs.Get("furnace", "3,64,0", dimension: -1));
        Assert.NotNull(k.Obs.Get("furnace", "3,64,0", dimension: 0));
        Assert.Single(k.Obs.FindItemInContainers(265, dimension: 0));
        Assert.Empty(k.Obs.FindItemInContainers(265, dimension: -1));
        Assert.Single(k.Obs.FindItemInContainers(406, dimension: -1));
        // Spatial: the R*Tree (or range fallback) only matches the asked dimension.
        Assert.Equal(0, k.Obs.Nearest("chest", 1, 64, 1).Single().Row.Dimension);
        Assert.Equal(-1, k.Obs.Nearest("chest", 1, 64, 1, dimension: -1).Single().Row.Dimension);
        Assert.Equal(2, k.Obs.Nearest("chest", 1, 64, 1, dimension: WorldDim.Any).Count);
        Assert.True(k.Db.HasRtree);
        Assert.Single(k.Rag.Nearby(1, 64, 1, 4, ["world"], dimension: -1), h => h.Tag == "chest");
    }

    [Fact]
    public void Where_is_defaults_to_the_current_dimension_tags_the_other_and_takes_dim_any()
    {
        using var k = new Kit();
        k.Obs.Upsert(Furnace(WorldDim.Overworld));
        k.Obs.Upsert(new Observation("chest", "5,64,5", "chest at 5,64,5") { Bounds = Box.Point(5, 64, 5), Dimension = 0, Contents = [new ItemCount(265, "iron_ingot", 5)] });
        k.Dim = WorldDim.Nether; // the bot went through a portal

        var none = k.Run("where_is", new { kind = "furnace" });
        output.WriteLine(none);
        Assert.StartsWith("no known furnace nearby", none);
        Assert.Contains("overworld", none); // hints where they are, without coordinates to walk to
        Assert.DoesNotContain("3,64,0", none);
        var item = k.Run("where_is", new { item = "iron_ingot" });
        output.WriteLine(item);
        Assert.StartsWith("no iron_ingot in known chests", item);
        Assert.Contains("5 in the overworld", item);

        var any = k.Run("where_is", new { kind = "furnace", dim = "any" });
        output.WriteLine(any);
        Assert.Contains("furnace@3,64,0 (overworld)", any);
        Assert.Contains("furnace@3,64,0 (overworld)", k.Run("where_is", new { kind = "furnace", dim = "overworld" }));
        Assert.Contains("chest@5,64,5 (overworld)", k.Run("where_is", new { item = "iron_ingot", dim = "any" }));
        Assert.Contains("(remembered ", any); // the other dimension is memory, not live: tagged with its age
        Assert.DoesNotContain("(seen ", any);
        Assert.Contains("dim", k.Run("where_is", new { kind = "furnace", dim = "end" }));

        k.Dim = WorldDim.Overworld; // back home: no tag
        var home = k.Run("where_is", new { kind = "furnace" });
        Assert.StartsWith("furnace@3,64,0 ", home);
        Assert.DoesNotContain("(overworld)", home);
    }

    [Fact]
    public void Recall_and_waypoints_are_filtered_by_dimension_and_dim_any_shows_both_tagged()
    {
        using var k = new Kit();
        k.Mem.SetWaypoint("quartz_mine", 10, 40, 10, "lots of quartz");     // overworld (current)
        Assert.Equal(0, k.Mem.GetWaypoint("quartz_mine")!.Dimension);
        k.Dim = WorldDim.Nether;
        k.Mem.SetWaypoint("fortress", 100, 70, -40, "blaze spawners");
        Assert.Equal(-1, k.Mem.GetWaypoint("fortress")!.Dimension);

        var here = k.Run("recall", new { query = "quartz fortress blaze" });
        output.WriteLine(here);
        Assert.Contains("fortress", here);
        Assert.DoesNotContain("quartz_mine", here);
        var any = k.Run("recall", new { query = "quartz fortress blaze", dim = "any" });
        output.WriteLine(any);
        Assert.Contains("(overworld) wp quartz_mine", any);
        Assert.DoesNotContain("(nether)", any);
        Assert.Contains("quartz_mine", k.Run("recall", new { query = "quartz", dim = "overworld" }));
        Assert.Contains("dim", k.Run("recall", new { query = "x", dim = "moon" }));
        // Stored dimension survives a reload (hive) and the list shows it.
        k.Mem.Reload();
        Assert.Equal(-1, k.Mem.GetWaypoint("fortress")!.Dimension);
        Assert.Contains("quartz_mine@10,40,10 (overworld)", k.Mem.List("waypoint"));
        // Near-search ("near=here") never returns the other dimension's spot at the same coordinates.
        k.Mem.SetWaypoint("nether_spot", 0, 64, 0);
        k.Dim = WorldDim.Overworld;
        k.Mem.SetWaypoint("home", 0, 64, 0);
        var near = k.Run("recall", new { near = "here", radius = 4 });
        Assert.Contains("home", near);
        Assert.DoesNotContain("nether_spot", near);
    }

    [Fact]
    public void One_to_eight_conversion_and_portal_pairing_plan_the_cheapest_route()
    {
        Assert.Equal((-3, 64, 2), WorldDim.ToNether(-17, 64, 16));       // floor(-17/8) = -3
        Assert.Equal((0, 70, -1), WorldDim.ToNether(7, 70, -1));
        Assert.Equal((-24, 64, 16), WorldDim.ToOverworld(-3, 64, 2));
        Assert.Equal((5, 1, 5), WorldDim.Convert(5, 1, 5, 0, 0));
        Assert.True(WorldDim.TryParse("nether", 0, out var n) && n == -1);
        Assert.True(WorldDim.TryParse(null, -1, out var c) && c == -1);
        Assert.True(WorldDim.TryParse("any", 0, out var a) && a == WorldDim.Any);
        Assert.False(WorldDim.TryParse("end", 0, out _));

        using var k = new Kit();
        var links = k.Obs.Portals;
        Assert.Null(links.Plan(0, (0, 64, 0), -1, (10, 70, 10))); // nothing known: no route
        var near = links.Link(new PortalEnd(-1, "1,70,1", 1, 70, 1), new PortalEnd(0, "8,64,8", 8, 64, 8)); // order-insensitive
        Assert.Equal(0, near.A.Dim);
        links.Link(new PortalEnd(0, "800,64,800", 800, 64, 800), new PortalEnd(-1, "100,70,100", 100, 70, 100));
        Assert.Equal(2, links.All().Count);
        var r = links.Plan(0, (0, 64, 0), -1, (10, 70, 10))!;
        Assert.Equal("8,64,8", r.Entry.Pos);
        Assert.Equal("1,70,1", r.Exit.Pos);
        var back = links.Plan(-1, (99, 70, 99), 0, (790, 64, 790))!;
        Assert.Equal("100,70,100", back.Entry.Pos);
        Assert.Equal(new PortalEnd(-1, "1,70,1", 1, 70, 1), links.LinkOf(0, "8,64,8"));
        // Re-linking an end to a new exit replaces the old pairing; using it again counts.
        links.Link(new PortalEnd(0, "8,64,8", 8, 64, 8), new PortalEnd(-1, "2,70,2", 2, 70, 2));
        Assert.Equal("2,70,2", links.LinkOf(0, "8,64,8")!.Pos);
        Assert.Equal(2, links.All().Count);
        Assert.Equal(2, links.Link(new PortalEnd(0, "8,64,8", 8, 64, 8), new PortalEnd(-1, "2,70,2", 2, 70, 2)).Uses);
    }

    [Fact]
    public void Claims_record_and_check_in_the_bots_dimension()
    {
        using var k = new Kit();
        var beliefs = new Rag.Beliefs.BeliefStore(k.Db, k.Rag);
        k.Dim = WorldDim.Nether;
        var r = beliefs.Ingest("fox", "the chest at 4,64,4 has quartz");
        Assert.True(r.Stored, r.Text);
        Assert.Equal(-1, r.Claim!.Dimension);
        Assert.Equal(-1, k.Rag.GetByRef(r.Claim.DocRef)!.Dimension);
        // Same words said in the overworld are another claim (another chest).
        k.Dim = WorldDim.Overworld;
        var o = beliefs.Ingest("fox", "the chest at 4,64,4 has quartz");
        Assert.NotEqual(r.Claim.Id, o.Claim!.Id);
        Assert.All(beliefs.Pending(), p => Assert.Equal(0, p.Dimension));
    }

    [Fact]
    public void V4_database_migrates_to_v5_backfilling_overworld_and_keeping_contents_and_hive_columns()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-dim-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "v4.db");
        try
        {
            // Build a genuine v4 file from the shipped v1..v4 migrations, with v4-era rows (and a hive clone_id column).
            var migrations = (string[])typeof(AgentDb).GetField("Migrations", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                raw.Open();
                void X(string sql) { using var cmd = raw.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
                for (int v = 0; v < 4; v++) X(migrations[v]);
                X("""
                    ALTER TABLE observations ADD COLUMN clone_id TEXT;
                    INSERT INTO observations(id, kind, key, min_x, min_y, min_z, max_x, max_y, max_z, fields, text, source, confidence, first_seen, last_seen, seen_count, clone_id)
                        VALUES (7, 'chest', '1,64,1', 1, 64, 1, 1, 64, 1, NULL, 'chest at 1,64,1', 'auto', 0.9, 1, 2, 3, 'maid_chan_67');
                    INSERT INTO container_items(obs_id, item_id, damage, name, count) VALUES (7, 265, 0, 'iron_ingot', 12);
                    INSERT INTO waypoints(name, x, y, z, note, updated) VALUES ('home', 1, 64, 1, NULL, 1);
                    INSERT INTO docs(ref, kind, tag, title, body, hash, created, updated, min_x, min_y, min_z, max_x, max_y, max_z)
                        VALUES ('obs:chest:1,64,1', 'world', 'chest', 'chest 1,64,1', 'chest at 1,64,1', 'h', 1, 1, 1, 64, 1, 1, 64, 1);
                    CREATE VIRTUAL TABLE docs_rtree USING rtree(id, min_x, max_x, min_y, max_y, min_z, max_z);
                    INSERT INTO docs_rtree SELECT id, min_x, max_x, min_y, max_y, min_z, max_z FROM docs;
                    INSERT INTO claims(claimant, text, norm, x, y, z, category, stakes, confidence, status, created, updated, clone_id)
                        VALUES ('fox', 'chest at 1,64,1 has iron', 'n', 1, 64, 1, 'container', 'low', 0.3, 'unverified', 1, 1, 'main');
                    PRAGMA user_version = 4;
                    """);
            }

            using var db = AgentDb.Open(path);
            Assert.Equal(AgentDb.LatestSchemaVersion, db.SchemaVersion);
            Assert.Equal(AgentDb.LatestSchemaVersion, (int)db.ScalarLong("PRAGMA user_version"));
            foreach (var t in new[] { "observations", "container_items", "waypoints", "structures", "docs", "claims" })
                Assert.Equal(0, db.ScalarLong($"SELECT count(*) FROM {t} WHERE dimension <> 0"));
            Assert.Equal("maid_chan_67", db.Scalar("SELECT clone_id FROM observations WHERE id = 7"));
            Assert.Equal(12, db.ScalarLong("SELECT count FROM container_items WHERE obs_id = 7"));
            Assert.Contains("min_d", (string)db.Scalar("SELECT sql FROM sqlite_master WHERE name = 'docs_rtree'")!);
            Assert.Equal(1, db.ScalarLong("SELECT count(*) FROM docs_rtree WHERE min_d = 0"));
            Assert.True(db.TableExists("portal_links"));

            using var rag = new RagIndex(db, backgroundEmbedding: false);
            var obs = new ObservationStore(rag);
            Assert.Equal("iron_ingot", obs.FindItemInContainers(265).Single().Name);
            // The (kind, key) idempotency key now includes the dimension.
            obs.Upsert(new Observation("chest", "1,64,1", "nether chest at 1,64,1") { Bounds = Box.Point(1, 64, 1), Dimension = -1 });
            Assert.Equal(2, obs.Count("chest"));
            Assert.Equal(0, new MemoryStore(db, rag).GetWaypoint("home")!.Dimension);
            // Re-opening is a no-op (already v5).
            db.Dispose();
            using var again = AgentDb.Open(path);
            Assert.Equal(2, again.ScalarLong("SELECT count(*) FROM observations"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
