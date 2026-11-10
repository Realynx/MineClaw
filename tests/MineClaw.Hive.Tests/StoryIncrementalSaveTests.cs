using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Agent.Rag;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// A tick's save writes only the rows that changed (live 11-10: every save rewrote 13 mine builds of 550 KB each for one dug cell,
/// up to 1.5 s under the colony lock). The rows on disk and the order they load in stay what a full save writes.
/// </summary>
public sealed class StoryIncrementalSaveTests
{
    private sealed record Rig(AgentDb Db, StoryStore Store, StoryColony C, FakeStoryHost H, FakeStoryWorld W, StoryLiveShapeBenchmarkTests.SimClock T)
        : IDisposable
    {
        public void Dispose() => Db.Dispose();
    }

    /// <summary>A colony with a store and three mine builds of three cells each (stone at y 63, open sky above).</summary>
    private static Rig Make()
    {
        var db = AgentDb.Open(null);
        var store = new StoryStore(db);
        var time = new StoryLiveShapeBenchmarkTests.SimClock();
        var w = new FakeStoryWorld();
        var h = new FakeStoryHost(w);
        var c = new StoryColony(new ColonyMeta { Id = "story-1", Name = "Test", Home = new P3(0, 64, 0) }, h, time, store)
        {
            DoneGrace = TimeSpan.Zero,
        };
        var (accounts, _) = h.Spawn("story-1", 1, null);
        c.Joined(accounts);
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Mine ? 1 : 0; // the mine builds' digs are mining work
        for (int i = 0; i < 3; i++) Mine(c, 10 * i);
        return new Rig(db, store, c, h, w, time);
    }

    private static void Mine(StoryColony c, int x)
    {
        var cells = new JsonArray(new JsonArray(x, 63, 0), new JsonArray(x + 1, 63, 0), new JsonArray(x + 2, 63, 0));
        Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "mine", ["blocks"] = cells }), null).Error);
    }

    /// <summary>Every object row written from now on (inserted or updated), as kind/id.</summary>
    private static void Watch(AgentDb db)
    {
        db.Exec("CREATE TEMP TABLE written(what TEXT NOT NULL)");
        db.Exec("CREATE TEMP TRIGGER object_in AFTER INSERT ON story_objects BEGIN INSERT INTO written VALUES (new.kind || '/' || new.id); END");
        db.Exec("CREATE TEMP TRIGGER object_up AFTER UPDATE ON story_objects BEGIN INSERT INTO written VALUES (new.kind || '/' || new.id); END");
    }

    private static List<string> Written(AgentDb db)
    {
        var rows = db.Query("SELECT what FROM written ORDER BY rowid", r => r.GetString(0));
        db.Exec("DELETE FROM written");
        return rows;
    }

    /// <summary>The colony as the store loads it again.</summary>
    private static StoryColony Reload(Rig r)
    {
        var loaded = Assert.Single(new StoryStore(r.Db).Load(meta => new StoryColony(meta, new FakeStoryHost(r.W), r.T)));
        r.Db.Exec("DELETE FROM written"); // a load that migrates something saves the colony it loaded: not the save under test
        return loaded;
    }

    private static string Row(object o) => JsonSerializer.Serialize(o, StoryJson.Options);

    /// <summary>The loaded builds are the colony's, row for row and in its order.</summary>
    private static void SameBuilds(StoryColony c, StoryColony loaded)
    {
        Assert.Equal(c.Builds.Select(b => b.Id), loaded.Builds.Select(b => b.Id));
        Assert.Equal(c.Builds.Select(Row), loaded.Builds.Select(Row));
    }

    [Fact]
    public void A_save_after_one_dug_cell_writes_that_cells_build_and_no_other()
    {
        using var r = Make();
        var c = r.C;
        c.Save(force: true);
        Watch(r.Db);
        var job = c.Next(0);
        Assert.Equal("dig", job["kind"]!.GetValue<string>());
        var at = job["at"]!.GetValue<string>().Split(',').Select(int.Parse).ToArray();
        r.W.Set(at[0], at[1], at[2], 0);
        Assert.True(c.Report(c.P(0), job["id"]!.GetValue<string>(), "done", null, null));
        var dug = c.Builds.Single(b => b.Ghosts.Any(g => g.At == new P3(at[0], at[1], at[2]))).Id;
        r.T.Now += TimeSpan.FromSeconds(6);
        c.Save(); // as the tick saves
        // Before 11-10 every row went out again (all three builds).
        Assert.Equal(["build/" + dug], Written(r.Db));
        SameBuilds(c, Reload(r));
        // Nothing changed since: no object row is written.
        Assert.Equal(0, r.Store.Plan(c, full: false).ObjectRows);
    }

    [Fact]
    public void Removed_added_and_reordered_builds_load_in_the_order_the_colony_has()
    {
        using var r = Make();
        var c = r.C;
        c.Save(force: true);
        Watch(r.Db);
        Assert.Null(c.BuildsOp("remove", null, [c.Builds[1].Id]).Error);
        Mine(c, 40);
        r.T.Now += TimeSpan.FromSeconds(6);
        c.Save();
        Assert.Equal(["build/" + c.Builds[^1].Id], Written(r.Db)); // the new one; the removed one's row is deleted
        SameBuilds(c, Reload(r));
        // The list itself in another order: the kind is written again whole, so the rows load in the new order.
        c.Builds.Reverse();
        c.Pause(false);
        c.Pause(true); // something to save
        r.T.Now += TimeSpan.FromSeconds(6);
        c.Save();
        Assert.Equal(c.Builds.Count, Written(r.Db).Count(w => w.StartsWith("build/", StringComparison.Ordinal)));
        SameBuilds(c, Reload(r));
    }

    [Fact]
    public void A_change_that_stamped_no_revision_is_on_disk_after_the_next_full_save()
    {
        using var r = Make();
        var c = r.C;
        c.Save(force: true);
        Watch(r.Db);
        var b = c.Builds[0];
        b.Label = "renamed without a revision"; // no code path should do this; the periodic full save still catches it
        c.Pause(true);
        r.T.Now += TimeSpan.FromSeconds(6);
        c.Save();
        Assert.DoesNotContain("build/" + b.Id, Written(r.Db));
        c.Pause(false);
        r.T.Now += StoryColony.FullSaveEvery;
        c.Save();
        Assert.Contains("build/" + b.Id, Written(r.Db));
        SameBuilds(c, Reload(r));
    }

    /// <summary>The rows a full save makes now: build id → JSON.</summary>
    private static Dictionary<string, string> BuildRows(StoryStore store, StoryColony c) =>
        store.Plan(c, full: true).Kinds.Single(k => k.Kind == "build").Rows.ToDictionary(r => r.Id, r => r.Json);

    [Fact]
    public void A_build_row_is_the_serializers_own_text_after_its_cells_change()
    {
        using var r = Make();
        var c = r.C;
        var b = c.Builds[0];
        b.Ghosts[1].Item = "torch";
        b.Ghosts[1].Facing = "north";
        b.Ghosts[2].Reason = "a \"quoted\" reason, ünïcode";
        Assert.Equal(c.Builds.ToDictionary(x => x.Id, Row), BuildRows(r.Store, c));
        // Each saved field of a cell, changed in place: the row follows (none is served from the last write).
        var g = b.Ghosts[0];
        foreach (var change in new Action[]
                 {
                     () => g.At = g.At.Add(0, -1, 0), () => g.Id = 4, () => g.Meta = 2, () => g.Item = "cobblestone", () => g.Facing = "east",
                     () => g.Job = false, () => g.Remove = !g.Remove, () => g.State = "blocked", () => g.Reason = "why", () => g.Rev = 999,
                 })
        {
            change();
            Assert.Equal(Row(b), BuildRows(r.Store, c)[b.Id]);
        }
    }

    [Fact]
    public void The_cell_text_cache_compares_every_saved_field_of_a_cell()
    {
        var saved = typeof(GhostState).GetProperties()
            .Where(p => p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true).Length == 0)
            .Select(p => p.Name).Order();
        Assert.Equal(StoryStore.GhostRows.Fields.Order(), saved);
    }

    [Fact]
    public void A_forced_save_and_a_fresh_store_write_every_row()
    {
        using var r = Make();
        var c = r.C;
        c.Save(force: true);
        Watch(r.Db);
        Assert.Equal(0, r.Store.Plan(c, full: false).ObjectRows);
        c.Save(force: true);
        Assert.Equal(c.Builds.Count, Written(r.Db).Count(w => w.StartsWith("build/", StringComparison.Ordinal)));
        // Another store over the same db (a restart): its first save writes the whole colony, as the db may hold older rows.
        Assert.True(new StoryStore(r.Db).Plan(c, full: false).ObjectRows >= c.Builds.Count);
    }

    private static object Private(object o, string field) =>
        o.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(o)!;

    [Fact]
    public void A_preserved_zones_snapshot_filled_in_later_is_in_the_next_tick_save()
    {
        using var r = Make();
        var c = r.C;
        r.W.Unloaded.Add((33, 30));
        var (res, err) = c.ZoneOp("add", "preserve", Area.Of(30, 64, 30, 33, 64, 30), grow: new JsonObject { ["below"] = 0, ["above"] = 0 });
        Assert.Null(err);
        var id = res!["id"]!.GetValue<string>();
        Assert.Equal(PreserveSnap.Unknown, c.Zones.Single(z => z.Id == id).Preserve!.Data!.At(33, 64, 30));
        c.Save(force: true);
        Watch(r.Db);
        r.W.Set(33, 64, 30, 5);
        r.W.Unloaded.Clear();
        c.Reconcile(r.W); // the column loads: the scan takes its cell into the snapshot
        Assert.Equal(PreserveSnap.Pack(5, 0), c.Zones.Single(z => z.Id == id).Preserve!.Data!.At(33, 64, 30));
        r.T.Now += TimeSpan.FromSeconds(6);
        c.Save(); // as the tick saves
        // Before the fix the zone row waited for the 5-minute full save: a crash before it lost the cells taken meanwhile.
        Assert.Contains("zone/" + id, Written(r.Db));
        Assert.Equal(PreserveSnap.Pack(5, 0), Reload(r).Zones.Single(z => z.Id == id).Preserve!.Data!.At(33, 64, 30));
    }

    [Fact]
    public void A_save_plan_never_waits_for_a_db_write_in_progress()
    {
        using var r = Make();
        r.C.Save(force: true);
        var storeLock = Private(r.Store, "_lock");
        Monitor.Enter(storeLock); // another colony's write: its whole transaction holds the store's lock
        try
        {
            // The plan runs under this colony's lock in a tick: it must not wait out the other write.
            var plan = Task.Run(() => r.Store.Plan(r.C, full: false));
            Assert.True(plan.Wait(TimeSpan.FromSeconds(30)), "the plan waited for the store's write lock");
            Assert.Equal(0, plan.Result.ObjectRows);
        }
        finally { Monitor.Exit(storeLock); }
    }

    [Fact]
    public void A_storage_edit_saves_after_the_colony_lock_so_a_tick_save_waiting_for_that_lock_never_deadlocks()
    {
        using var r = Make();
        var c = r.C;
        r.W.Set(20, 64, 20, 54);
        c.Save(force: true);
        object gate = Private(c, "_saveGate"), colonyLock = Private(c, "_lock");
        Task<(JsonNode? Result, string? Error)>? edit = null;
        bool reached = false;
        Monitor.Enter(gate); // a tick's save: it holds the gate and goes for the colony lock next
        try
        {
            edit = Task.Run(() => c.StorageManage(JsonNode.Parse("""{"op":"set","area":[18,60,18,22,70,22],"allow":["*"]}""")!.AsObject()));
            // The tick gets the colony lock once the edit is made: the edit must not keep it while it waits for the gate.
            while (!reached)
            {
                if (!Monitor.TryEnter(colonyLock, TimeSpan.FromSeconds(30))) break; // held for good: the deadlock
                try { reached = c.Storages.Count > 0; }
                finally { Monitor.Exit(colonyLock); }
                if (!reached) Thread.Yield();
            }
        }
        finally { Monitor.Exit(gate); }
        Assert.True(reached, "the storage edit held the colony lock while it waited for the save gate");
        Assert.True(edit!.Wait(TimeSpan.FromSeconds(30)));
        Assert.Null(edit.Result.Error);
        // The edit's forced save wrote the new storage.
        Assert.Contains(new P3(20, 64, 20).ToString(), r.Db.Query("SELECT id FROM story_objects WHERE kind = 'storage'", x => x.GetString(0)));
    }
}
