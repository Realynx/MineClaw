using System.Text.Json;
using MineClaw.Agent.Rag;

namespace MineClaw.Hive.Story;

/// <summary>
/// Story Mode in the agent db (tables created on first use, idempotent): <c>story_colonies</c> (one row per colony: its settings),
/// <c>story_pawns</c> (characteristics, priorities, schedule, drafted, queue) and <c>story_objects</c> (zones, designations,
/// storages, builds with their ghosts, workbenches with their bills). A colony survives restarts and resumes.
/// </summary>
public sealed class StoryStore(AgentDb db)
{
    private bool _ready;
    private readonly object _lock = new();

    private void Ensure()
    {
        if (_ready) return;
        db.Exec("CREATE TABLE IF NOT EXISTS story_colonies(id TEXT PRIMARY KEY, json TEXT NOT NULL, rev INTEGER NOT NULL, updated INTEGER NOT NULL)");
        db.Exec("CREATE TABLE IF NOT EXISTS story_pawns(colony TEXT NOT NULL, name TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(colony, name))");
        db.Exec("CREATE TABLE IF NOT EXISTS story_objects(colony TEXT NOT NULL, kind TEXT NOT NULL, id TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(colony, kind, id))");
        _ready = true;
    }

    private static readonly JsonSerializerOptions J = StoryJson.Options;

    /// <summary>The whole colony written now (a load's migration, a caller that holds the colony still): as <see cref="Plan"/> and
    /// <see cref="Write"/> with every row.</summary>
    public void Save(StoryColony c) => Write(Plan(c, full: true));

    // ---------------------------------------------------------------- saves that write only what changed

    /// <summary>What this store last wrote of one colony: each object row's object and revision stamp, and each kind's row order.</summary>
    internal sealed class Written
    {
        public readonly Dictionary<(string Kind, string Id), (object Obj, long Rev)> Rows = new();
        public readonly Dictionary<string, List<string>> Order = new(StringComparer.Ordinal);
    }

    /// <summary>Per colony id. None = the next save writes the whole colony (the db may hold rows of an earlier run).</summary>
    private readonly Dictionary<string, Written> _written = new(StringComparer.Ordinal);

    /// <summary>Guards <see cref="_written"/> only, never held around the db: <see cref="Plan"/> runs under a colony's lock and must
    /// not wait for another colony's write (which holds <see cref="_lock"/> for its whole transaction).</summary>
    private readonly object _writtenLock = new();

    /// <summary>One kind's rows in a save: all of them rewritten (<see cref="All"/>), or the changed ones and the ids that went.</summary>
    internal sealed class KindRows(string kind)
    {
        public readonly string Kind = kind;
        public bool All;
        public readonly List<(string Id, string Json)> Rows = new();
        public readonly List<string> Gone = new();
    }

    /// <summary>One save, made under the colony lock (<see cref="Plan"/>) and written after it (<see cref="Write"/>): the slow part,
    /// the db, no longer holds the colony.</summary>
    public sealed class SavePlan
    {
        internal string Colony = "", Meta = "";
        internal long Rev;
        internal bool Full;
        internal readonly List<(string Name, string Json)> Pawns = new();
        internal readonly List<KindRows> Kinds = new();
        internal Written Next = new();

        /// <summary>Object rows this save writes (pawns and the colony row apart).</summary>
        public int ObjectRows => Kinds.Sum(k => k.Rows.Count);
        /// <summary>The JSON characters of those rows.</summary>
        public long ObjectChars => Kinds.Sum(k => k.Rows.Sum(r => (long)r.Json.Length));
    }

    /// <summary>
    /// The rows a save writes, serialized now (call it while the colony can't change: under its lock). A full save writes every row as
    /// saves always did. Otherwise only the object rows whose revision moved since this store last wrote them (a build's revision is
    /// its newest cell's), the ones that went, and the pawns and the colony row (small). Before, every save rewrote every row: 13
    /// mine builds of 550 KB each, every 5 s, for one dug cell (live 11-10). A kind whose rows changed order is rewritten whole, so
    /// the rows load in the order they have now. Same tables and JSON as ever.
    /// </summary>
    public SavePlan Plan(StoryColony c, bool full)
    {
        Written? last;
        lock (_writtenLock) last = full ? null : _written.GetValueOrDefault(c.Id);
        var plan = new SavePlan { Colony = c.Id, Meta = JsonSerializer.Serialize(c.Meta, J), Rev = c.Rev, Full = last == null };
        foreach (var p in c.Pawns.Values) plan.Pawns.Add((p.Name, JsonSerializer.Serialize(p, J)));
        PlanKind(plan, last, "zone", c.Zones, z => z.Id, z => z.Rev, J);
        PlanKind(plan, last, "designation", c.Designations, d => d.Id, d => d.Rev, J);
        PlanKind(plan, last, "storage", c.Storages, s => s.Chest.ToString(), s => s.Rev, J);
        PlanKind(plan, last, "build", c.Builds, b => b.Id, BuildRev, _buildJson);
        PlanKind(plan, last, "bench", c.Benches, w => w.Pos.ToString(), w => w.Rev, J);
        return plan;
    }

    /// <summary>Builds are written with <see cref="GhostRows"/>: a changed 3,900-cell mine build serializes its changed cells, not all.</summary>
    private readonly JsonSerializerOptions _buildJson = new(J) { Converters = { new GhostRows() } };

    /// <summary>
    /// Each cell's JSON as the serializer wrote it last, written again as it was while the cell's saved fields are unchanged. The text
    /// is the serializer's own (StoryJson.Options), so a build row is the same text it always was. Kept per cell object while it lives.
    /// </summary>
    internal sealed class GhostRows : System.Text.Json.Serialization.JsonConverter<GhostState>
    {
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<GhostState, Row> _rows = new();

        /// <summary>The saved fields a cell's JSON was made from, and the JSON.</summary>
        private sealed class Row
        {
            private P3 _at;
            private int _id, _meta;
            private string? _item, _facing, _state, _reason;
            private bool _job, _remove;
            private long _rev;
            public byte[]? Json;

            public bool Same(GhostState g) => Json != null && g.At == _at && g.Id == _id && g.Meta == _meta && g.Job == _job && g.Remove == _remove
                && g.Rev == _rev && string.Equals(g.State, _state, StringComparison.Ordinal) && string.Equals(g.Reason, _reason, StringComparison.Ordinal)
                && string.Equals(g.Item, _item, StringComparison.Ordinal) && string.Equals(g.Facing, _facing, StringComparison.Ordinal);

            public void Take(GhostState g)
            {
                (_at, _id, _meta, _item, _facing, _job, _remove, _state, _reason, _rev) = (g.At, g.Id, g.Meta, g.Item, g.Facing, g.Job, g.Remove, g.State, g.Reason, g.Rev);
                Json = JsonSerializer.SerializeToUtf8Bytes(g, J);
            }
        }

        /// <summary>The saved fields <see cref="Row"/> compares: a test holds GhostState's serialized properties to this list.</summary>
        internal static readonly string[] Fields = ["At", "Id", "Meta", "Item", "Facing", "Job", "Remove", "State", "Reason", "Rev"];

        public override GhostState? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<GhostState>(ref reader, J);

        public override void Write(Utf8JsonWriter writer, GhostState g, JsonSerializerOptions options)
        {
            var row = _rows.GetOrCreateValue(g);
            if (!row.Same(g)) row.Take(g);
            writer.WriteRawValue(row.Json!, skipInputValidation: true);
        }
    }

    /// <summary>A build's row changes with any of its cells: its newest revision, its own or a cell's.</summary>
    internal static long BuildRev(BuildState b)
    {
        long rev = b.Rev;
        var gs = b.Ghosts;
        for (int i = 0; i < gs.Count; i++) if (gs[i].Rev > rev) rev = gs[i].Rev;
        return rev;
    }

    private static void PlanKind<T>(SavePlan plan, Written? last, string kind, List<T> list, Func<T, string> idOf, Func<T, long> revOf,
        JsonSerializerOptions json) where T : class
    {
        var rows = new KindRows(kind);
        var order = new List<string>(list.Count);
        var stamps = new (string Id, long Rev)[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            stamps[i] = (idOf(list[i]), revOf(list[i]));
            order.Add(stamps[i].Id);
        }
        rows.All = last == null || !InOrder(last, kind, order);
        for (int i = 0; i < list.Count; i++)
        {
            var (id, rev) = stamps[i];
            if (rows.All || !last!.Rows.TryGetValue((kind, id), out var was) || !ReferenceEquals(was.Obj, list[i]) || was.Rev != rev)
                rows.Rows.Add((id, JsonSerializer.Serialize(list[i], json)));
            plan.Next.Rows[(kind, id)] = (list[i], rev);
        }
        if (!rows.All && last!.Order.TryGetValue(kind, out var before))
        {
            var now = new HashSet<string>(order, StringComparer.Ordinal);
            foreach (var id in before) if (!now.Contains(id)) rows.Gone.Add(id);
        }
        plan.Next.Order[kind] = order;
        plan.Kinds.Add(rows);
    }

    /// <summary>
    /// The kind's ids now keep the order of its rows (rowid, the load order): the ids written before in their order, some gone, the
    /// new ones after them, no id twice. Else the kind is rewritten whole.
    /// </summary>
    private static bool InOrder(Written last, string kind, List<string> order)
    {
        if (!last.Order.TryGetValue(kind, out var before)) return false;
        var now = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in order) if (!now.Add(id)) return false; // the same id twice: the last one wins, as a full write has it
        int k = 0;
        bool appending = false;
        var known = new HashSet<string>(before, StringComparer.Ordinal);
        foreach (var id in order)
        {
            if (!known.Contains(id)) { appending = true; continue; }
            if (appending) return false;
            while (k < before.Count && !now.Contains(before[k])) k++;
            if (k >= before.Count || before[k] != id) return false;
            k++;
        }
        return true;
    }

    /// <summary>Writes a plan in one transaction; then it is what this store last wrote of the colony. After a write that failed the
    /// next save writes the whole colony.</summary>
    public void Write(SavePlan plan)
    {
        lock (_lock)
        {
            Ensure();
            lock (_writtenLock) _written.Remove(plan.Colony); // until this write is in, nothing of the colony counts as written
            db.Tx(() =>
            {
                db.Exec("INSERT INTO story_colonies(id, json, rev, updated) VALUES ($i, $j, $r, $u) ON CONFLICT(id) DO UPDATE SET json = excluded.json, rev = excluded.rev, updated = excluded.updated",
                    ("$i", plan.Colony), ("$j", plan.Meta), ("$r", plan.Rev), ("$u", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                db.Exec("DELETE FROM story_pawns WHERE colony = $c", ("$c", plan.Colony));
                foreach (var (name, json) in plan.Pawns)
                    db.Exec("INSERT INTO story_pawns(colony, name, json) VALUES ($c, $n, $j)", ("$c", plan.Colony), ("$n", name), ("$j", json));
                if (plan.Full) db.Exec("DELETE FROM story_objects WHERE colony = $c", ("$c", plan.Colony));
                foreach (var k in plan.Kinds) WriteKind(plan.Colony, plan.Full, k);
            });
            lock (_writtenLock) _written[plan.Colony] = plan.Next;
        }
    }

    private void WriteKind(string colony, bool full, KindRows k)
    {
        if (k.All && !full) db.Exec("DELETE FROM story_objects WHERE colony = $c AND kind = $k", ("$c", colony), ("$k", k.Kind));
        foreach (var id in k.Gone) db.Exec("DELETE FROM story_objects WHERE colony = $c AND kind = $k AND id = $i", ("$c", colony), ("$k", k.Kind), ("$i", id));
        // A changed row keeps its rowid (its place in the load order); a whole kind goes in, in order, as before.
        var sql = k.All
            ? "INSERT OR REPLACE INTO story_objects(colony, kind, id, json) VALUES ($c, $k, $i, $j)"
            : "INSERT INTO story_objects(colony, kind, id, json) VALUES ($c, $k, $i, $j) ON CONFLICT(colony, kind, id) DO UPDATE SET json = excluded.json";
        foreach (var (id, json) in k.Rows) db.Exec(sql, ("$c", colony), ("$k", k.Kind), ("$i", id), ("$j", json));
    }

    /// <summary>Every saved colony (released ones too: their state is kept).</summary>
    public List<StoryColony> Load(Func<ColonyMeta, StoryColony> make)
    {
        lock (_lock)
        {
            Ensure();
            var list = new List<StoryColony>();
            foreach (var (id, json, rev) in db.Query("SELECT id, json, rev FROM story_colonies ORDER BY id", r => (r.GetString(0), r.GetString(1), r.GetInt64(2))))
                if (LoadColony(make, id, json, rev) is { } c) list.Add(c);
            return list;
        }
    }

    /// <summary>One saved colony: its meta, pawns and objects (zones, designations, storages, builds, benches), migrated when older.</summary>
    private StoryColony? LoadColony(Func<ColonyMeta, StoryColony> make, string id, string json, long rev)
    {
        var meta = JsonSerializer.Deserialize<ColonyMeta>(json, J);
        if (meta == null) return null;
        var c = make(meta);
        foreach (var pj in db.Query("SELECT json FROM story_pawns WHERE colony = $c", r => r.GetString(0), ("$c", id)))
            if (JsonSerializer.Deserialize<PawnState>(pj, J) is { } p) c.Pawns[p.Name] = p;
        foreach (var (kind, oj) in db.Query("SELECT kind, json FROM story_objects WHERE colony = $c ORDER BY rowid", r => (r.GetString(0), r.GetString(1)), ("$c", id)))
            LoadObject(c, kind, oj);
        c.SetRev(rev);
        // Older saves: rectangle zones become cell sets (the same cells), a growing zone's plant a one-bill list, auto bills craft tasks.
        // Saved at once.
        if (c.MigrateZones() | c.MigrateRoles() | c.MigrateWaterFailures() | c.MigrateCraftTasks()) Save(c);
        return c;
    }

    private static void LoadObject(StoryColony c, string kind, string oj)
    {
        switch (kind)
        {
            case "zone" when JsonSerializer.Deserialize<ZoneState>(oj, J) is { } z: c.Zones.Add(z); break;
            case "designation" when JsonSerializer.Deserialize<DesignationState>(oj, J) is { } d: c.Designations.Add(d); break;
            case "storage" when JsonSerializer.Deserialize<StorageState>(oj, J) is { } s: c.Storages.Add(s); break;
            case "build" when JsonSerializer.Deserialize<BuildState>(oj, J) is { } b:
                foreach (var g in b.Ghosts.Where(g => g.State == "leased")) g.State = "todo"; // its lease went with the old process
                c.Builds.Add(b);
                break;
            case "bench" when JsonSerializer.Deserialize<WorkbenchState>(oj, J) is { } w: c.Benches.Add(w); break;
        }
    }

    public void Delete(string colony)
    {
        lock (_lock)
        {
            Ensure();
            lock (_writtenLock) _written.Remove(colony);
            db.Tx(() =>
            {
                db.Exec("DELETE FROM story_colonies WHERE id = $c", ("$c", colony));
                db.Exec("DELETE FROM story_pawns WHERE colony = $c", ("$c", colony));
                db.Exec("DELETE FROM story_objects WHERE colony = $c", ("$c", colony));
            });
        }
    }
}
