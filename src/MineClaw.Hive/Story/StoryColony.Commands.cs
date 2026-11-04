using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>What the browser asks of a colony: priorities, schedules, drafting, orders, zones and designations.</summary>
public sealed partial class StoryColony
{
    public static readonly IReadOnlyList<string> ZoneKinds = ["chop", "mine", "farm", "home", "forbidden"];
    public static readonly IReadOnlyList<string> OrderKinds = ["move", "attack", "chop", "mine", "haul", "build", "hold", "stop"];
    public static readonly IReadOnlyList<string> DesignationKinds = ["chop", "cut", "harvest", "mine", "haul", "hunt", "deconstruct", "forbid", "allow"];

    /// <summary>The work type each designation becomes (cut is plant cutting: the Chop column).</summary>
    public static string WorkOf(string designation) => designation switch
    {
        "chop" or "cut" => WorkTypes.Chop, "harvest" => WorkTypes.Farm, "mine" => WorkTypes.Mine, "haul" => WorkTypes.Haul,
        "hunt" => WorkTypes.Hunt, "deconstruct" => WorkTypes.Build, _ => "",
    };

    public string? SetPriorities(IReadOnlyList<PawnState> pawns, IReadOnlyDictionary<string, int> work)
    {
        foreach (var (t, v) in work)
        {
            if (!WorkTypes.Known(t)) return $"unknown work type '{t}' ({string.Join("|", WorkTypes.All)})";
            if (v is < 0 or > 4) return "priority is 0 (off) .. 4";
        }
        lock (_lock)
        {
            bool changed = false;
            foreach (var p in pawns)
            {
                foreach (var (t, v) in work)
                {
                    if (p.Priorities.GetValueOrDefault(t) == v) continue;
                    p.Priorities[t] = v;
                    changed = true;
                }
                p.Rev = Touch();
            }
            if (changed) WakeIdle(); // the same values again aren't new work
        }
        return null;
    }

    public string? SetSchedule(IReadOnlyList<PawnState> pawns, IReadOnlyDictionary<string, string> hours)
    {
        var parsed = new Dictionary<int, string>();
        foreach (var (h, b) in hours)
        {
            if (!int.TryParse(h, out var hour) || hour is < 0 or > 23) return $"hour '{h}' must be \"0\"..\"23\"";
            if (!ScheduleBlocks.Known(b)) return $"hour {h}: block must be work|sleep|anything|free";
            parsed[hour] = b;
        }
        lock (_lock)
            foreach (var p in pawns)
            {
                if (p.Schedule.Length != 24) p.Schedule = StoryCharacter.DefaultSchedule(false);
                foreach (var (h, b) in parsed) p.Schedule[h] = b;
                p.Rev = Touch();
            }
        return null;
    }

    public void Draft(IReadOnlyList<PawnState> pawns, bool drafted)
    {
        lock (_lock)
            foreach (var p in pawns)
            {
                if (p.Drafted == drafted) continue;
                p.Drafted = drafted;
                if (!drafted) p.Queue.Clear(); // undrafted pawns go back to the job picker
                p.Rev = Touch();
                Supersede(p, drafted ? "drafted" : "undrafted");
            }
    }

    /// <summary>An order for each pawn: replaces its queue (and cuts what it does now) unless <paramref name="queue"/>.</summary>
    public (JsonNode? Result, string? Error) GiveOrder(IReadOnlyList<PawnState> pawns, JsonObject order, bool queue)
    {
        var kind = order["kind"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (kind == null || !OrderKinds.Contains(kind)) return (null, $"order.kind must be {string.Join("|", OrderKinds)}");
        P3? at = null;
        int? entity = null;
        Area? area = null;
        if (order["target"] is JsonNode t)
        {
            if (t is JsonObject to && to["entity"] is { } en) entity = StoryJson.Int(en);
            else if (t is JsonObject ta && ta["area"] is { } an) area = Area.Parse(an) ?? (Area?)null;
            else at = P3.Parse(t);
            if (at == null && entity == null && area == null) return (null, "target must be {x,y,z} | {entity:id} | {area:[x1,y1,z1,x2,y2,z2]}");
        }
        long? design = StoryJson.Int(order["design"]);
        if (kind is "move" or "chop" or "mine" && at == null && area == null) return (null, $"{kind} needs a target position or area");
        if (kind is "attack" && entity == null && at == null) return (null, "attack needs a target entity");
        if (kind == "haul" && entity == null && at == null && area == null) return (null, "haul needs a target");
        if (kind == "build" && design == null && at == null) return (null, "build needs a design (or a target)");
        if (area is { Volume: > 32_768 }) return (null, "area too large for an order (max 32768 blocks)");
        // A chop order on a tree's canopy (a top-down click) or a branch means the tree: its trunk base.
        if (kind == "chop" && at is { } ca && World is { } cw && StoryBlocks.TreeBaseOf(cw, ca) is { } cb) at = cb;
        var ids = new JsonArray();
        lock (_lock)
            foreach (var p in pawns)
            {
                if (kind == "stop") { p.Queue.Clear(); p.Rev = Touch(); Supersede(p, "stop"); continue; }
                var o = new Order { Id = NextId("o"), Kind = kind, At = at, Entity = entity, Area = area, Design = design, Given = Now };
                if (area is { } a && World is { } w)
                    o.Targets = kind switch
                    {
                        "chop" => StoryBlocks.TreesIn(w, a, max: 64),
                        "mine" => a.Blocks().Where(b => StoryBlocks.MineMatch(w.Block(b.X, b.Y, b.Z).Id, "all")).OrderByDescending(b => b.Y).Take(512).ToList(),
                        _ => null,
                    };
                if (!queue) { p.Queue.Clear(); Supersede(p, "new order"); }
                if (p.Queue.Count >= 32) return (null, "queue full (32 orders)");
                p.Queue.Add(o);
                p.Rev = Touch();
                ids.Add(o.Id);
            }
        return (new JsonObject { ["ids"] = ids }, null);
    }

    /// <summary>The job for a pawn's first order (null = no runnable order: the order is dropped).</summary>
    private ActiveJob? OrderJob(PawnState p, PawnLive live)
    {
        while (p.Queue.Count > 0)
        {
            var o = p.Queue[0];
            var at = o.At;
            if (o.Targets is { } ts)
            {
                if (ts.Count == 0) { p.Queue.RemoveAt(0); p.Rev = Touch(); continue; }
                at = ts.OrderBy(t => t.Dist(live.Block)).First();
            }
            StoryEntity? e = null;
            if (o.Entity is { } eid)
            {
                e = World?.Entities().FirstOrDefault(x => x.Id == eid);
                if (e == null) { p.Queue.RemoveAt(0); Alert("info", $"{p.Display}: the {o.Kind} target is gone", p.Name); p.Rev = Touch(); continue; }
                at = e.Block;
            }
            string type = "order", src = "order";
            return o.Kind switch
            {
                "move" => Job(type, "go", $"moving to {at}", at ?? o.Area?.Center, null, src, o.Id, "moving", j => j["dist"] = 0, xp: 0),
                "attack" => Job(type, "attack", $"attacking {e?.Name ?? "target"}", at, e != null ? "e:" + e.Id : null, src, o.Id, "fighting", j => j["mob"] = e?.Name ?? "", xp: 0),
                "chop" => Job(WorkTypes.Chop, "chop", "chopping (order)", at, at is { } c ? "b:" + c : null, src, o.Id,
                    fill: j => ChopFill(j, at is { } ct && World is { } ow ? StoryBlocks.TreeFrom(ow, ct) : null), xp: 40),
                "mine" => Job(WorkTypes.Mine, "dig", "mining (order)", at, at is { } m ? "b:" + m : null, src, o.Id),
                "haul" => Job(WorkTypes.Haul, "collect", "hauling (order)", at ?? o.Area?.Center, e != null ? "e:" + e.Id : null, src, o.Id, "working", j => j["r"] = 4, xp: 5),
                "build" when o.Design is { } d => Job(WorkTypes.Build, "blueprint", $"building design #{d}", at, "design:" + d, src, o.Id, "working", j => j["design"] = d.ToString(), xp: 60),
                "hold" => Job(type, "hold", "holding position", at, null, src, o.Id, p.Drafted ? "drafted" : "idle", j => j["s"] = 5, xp: 0),
                _ => Job(type, "hold", "waiting", at, null, src, o.Id, "idle", j => j["s"] = 3, xp: 0),
            };
        }
        return null;
    }

    private void OrderDone(PawnState p, ActiveJob j, bool ok, string? note)
    {
        var o = p.Queue.FirstOrDefault(x => x.Id == j.SourceId);
        if (o == null) return;
        if (o.Kind == "hold" && ok) return; // holds until the next order (or undraft)
        if (o.Targets is { } ts && j.At is { } at)
        {
            ts.Remove(at);
            if (ts.Count > 0 && (ok || !string.IsNullOrEmpty(note))) { p.Rev = Touch(); return; }
        }
        p.Queue.Remove(o);
        if (!ok) Alert("warn", $"{p.Display} couldn't {o.Kind}: {note}", p.Name);
        p.Rev = Touch();
    }

    // ---------------------------------------------------------------- zones

    public (JsonNode? Result, string? Error) ZoneOp(string op, string? kind, Area? area, string? id = null, string? name = null, JsonObject? grow = null)
    {
        if (name is { Length: > 40 }) return (null, "name: at most 40 characters");
        if (op is "update" or "seek_seeds") return GrowZoneOp(op, id, grow); // growing zones (StoryColony.Farming.cs)
        if (kind == "grow" || kind == null && grow?["plant"] != null) kind = "farm";
        if (op == "rename")
        {
            lock (_lock)
            {
                if (Zones.FirstOrDefault(z => z.Id == id) is not { } rz) return (null, "id: no such zone");
                rz.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
                rz.Rev = Touch();
                return (new JsonObject { ["id"] = rz.Id, ["name"] = rz.Name }, null);
            }
        }
        if (op == "remove" && id != null)
        {
            lock (_lock)
            {
                if (Zones.FirstOrDefault(z => z.Id == id) is not { } gz) return (null, "id: no such zone");
                Zones.Remove(gz); Removed("zone", gz.Id); _zoneScan.Remove(gz.Id); _bedScan = null;
                return (new JsonObject { ["removed"] = new JsonArray(gz.Id) }, null);
            }
        }
        if (op is not ("add" or "remove")) return (null, "op must be add|remove|rename|update|seek_seeds");
        if (area is not { } a) return (null, "area [x1,y1,z1,x2,y2,z2] required");
        if (kind == "stockpile") return (null, "storage is chests: register them with /storage (no ground stockpiles)");
        if (op == "add" && (kind == null || !ZoneKinds.Contains(kind))) return (null, $"kind must be {string.Join("|", ZoneKinds)}");
        if ((long)(a.X2 - a.X1 + 1) * (a.Z2 - a.Z1 + 1) > 256 * 256) return (null, "zone too large (256 x 256 max)");
        lock (_lock)
        {
            if (op == "add")
            {
                var z = new ZoneState { Id = NextId("z"), Kind = kind!, Area = a, Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(), Rev = Touch() };
                List<string>? warnings = null;
                if (grow != null && (grow["plant"] != null || grow["sow"] != null || grow["harvest"] != null))
                {
                    if (kind != "farm") return (null, "only farm (growing) zones take plant, sow and harvest");
                    if (ApplyGrow(z, grow) is { } gerr) return (null, gerr);
                    var (bad, warn) = ValidateGrow(z);
                    if (bad != null) return (null, bad);
                    warnings = warn;
                }
                Zones.Add(z);
                _bedScan = null;
                WakeIdle();
                return (warnings is { Count: > 0 } ? new JsonObject { ["id"] = z.Id, ["warnings"] = new JsonArray(warnings.Select(x => (JsonNode)x!).ToArray()) }
                    : new JsonObject { ["id"] = z.Id }, null);
            }
            var gone = Zones.Where(z => (kind == null || z.Kind == kind) && z.Area.Overlaps(a)).ToList();
            foreach (var z in gone) { Zones.Remove(z); Removed("zone", z.Id); _zoneScan.Remove(z.Id); }
            _bedScan = null;
            return (new JsonObject { ["removed"] = new JsonArray(gone.Select(z => (JsonNode)z.Id!).ToArray()) }, null);
        }
    }

    // ---------------------------------------------------------------- designations

    /// <summary>
    /// add: the area (or blocks) expands now to matching targets from the world view, one designation per target (a tree is one
    /// job: its log column(s)); remove|cancel: designations in the area (or by id) go.
    /// </summary>
    public (JsonNode? Result, string? Error) Designate(string op, string? kind, Area? area, IReadOnlyList<P3>? blocks, string? filter,
        IReadOnlyList<string>? ids = null)
    {
        if (op is "remove" or "cancel")
        {
            lock (_lock)
            {
                var gone = Designations.Where(d => (ids != null && ids.Contains(d.Id))
                    || (kind == null || d.Kind == kind) && (area is { } a && (d.At is { } at && a.Contains(at) || d.Entity != null && d.Blocks?.FirstOrDefault() is { } eb && a.Contains(eb))
                        || blocks != null && d.At is { } bt && blocks.Contains(bt))).ToList();
                foreach (var d in gone) { Designations.Remove(d); Removed("designation", d.Id); }
                return (new JsonObject { ["count"] = gone.Count, ["removed"] = new JsonArray(gone.Select(d => (JsonNode)d.Id!).ToArray()) }, null);
            }
        }
        if (op == "retry")
        {
            // {op:"retry", ids} → {count, retried}: a failed designation is tried again now (its rest is over, its status open again).
            if (ids == null || ids.Count == 0) return (null, "ids required");
            lock (_lock)
            {
                var hit = Designations.Where(d => ids.Contains(d.Id)).ToList();
                foreach (var d in hit)
                {
                    var key = DesignationKey(d);
                    _skip.Remove(key);
                    _leaseFails.Remove(key);
                    _softFails.Remove(key);
                    d.Status = "open";
                    d.Rev = Touch();
                }
                return (new JsonObject { ["count"] = hit.Count, ["retried"] = new JsonArray(hit.Select(d => (JsonNode)d.Id!).ToArray()) }, null);
            }
        }
        if (op != "add") return (null, "op must be add|remove|cancel|retry");
        if (kind == null || !DesignationKinds.Contains(kind)) return (null, $"kind must be {string.Join("|", DesignationKinds)}");
        if (filter != null && filter is not ("all" or "ores" or "stone" or "soil")) return (null, "filter must be all|ores|stone|soil");
        if (area == null && (blocks == null || blocks.Count == 0)) return (null, "area or blocks required");
        if (area is { Volume: > 262_144 }) return (null, "area too large (max 262144 blocks)");
        if (World is not { } w) return (null, "no world view yet");
        lock (_lock)
        {
            var targets = new JsonArray();
            var made = new List<DesignationState>();
            var madeAt = new HashSet<P3>();
            var madeEnt = new HashSet<int>();
            // Also what this call made already: a tall plant's two halves (or any repeated cell) become one designation.
            // What this kind has already (sets: a 4096-cell box over a few hundred designations was O(n·m)).
            var hasAt = Designations.Where(d => d.Kind == kind && d.At != null).Select(d => d.At!.Value).ToHashSet();
            var hasEnt = Designations.Where(d => d.Kind == kind && d.Entity != null).Select(d => d.Entity!.Value).ToHashSet();
            bool Taken(string k, P3? at, int? ent) => at is { } ma && (!madeAt.Add(ma) || hasAt.Contains(ma)) || ent is { } me && (!madeEnt.Add(me) || hasEnt.Contains(me));
            void Block(P3 at, List<P3>? col = null, string? replant = null, string? label = null)
            {
                if (Taken(kind, at, null)) return;
                made.Add(new DesignationState { Id = NextId("d"), Kind = kind, At = at, Blocks = col, Replant = replant, Label = label, Created = Now });
                targets.Add(at.Json());
            }
            void Entity(StoryEntity e, string label)
            {
                if (Taken(kind, null, e.Id)) return;
                made.Add(new DesignationState { Id = NextId("d"), Kind = kind, Entity = e.Id, Blocks = [e.Block], Label = label, Created = Now });
                targets.Add(e.Id);
            }
            IEnumerable<P3> Cells() => blocks ?? area!.Value.Blocks();
            bool In(StoryEntity e) => area is { } a ? a.Contains(e.Block) || a.Contains(e.Block.Add(0, 1, 0)) : blocks!.Contains(e.Block);
            switch (kind)
            {
                case "chop":
                {
                    // Any log or leaf of a tree (the canopy top a top-down click lands on included) stands for that tree: its trunk base.
                    var bases = area is { } a ? StoryBlocks.TreesIn(w, a)
                        : blocks!.Select(b => StoryBlocks.TreeBaseOf(w, b)).Where(b => b != null).Select(b => b!.Value).Distinct().ToList();
                    foreach (var b in bases.Take(512)) Block(b, StoryBlocks.TreeFrom(w, b), null, "chopping a tree");
                    break;
                }
                case "cut":
                    foreach (var c in Cells())
                        if (w.Block(c.X, c.Y, c.Z).Id is var id && (StoryBlocks.Plants.Contains(id) || StoryBlocks.Leaves.Contains(id)))
                            Block(PlantBase(c), null, null, "cutting plants"); // a tall plant is one designation, on its lower half
                    break;
                case "harvest":
                    foreach (var c in Cells())
                        if (StoryBlocks.Crop(w, c.X, c.Y, c.Z) is { Ripe: true } crop) Block(c, null, crop.Replant, "harvesting");
                    break;
                case "mine":
                    foreach (var c in Cells())
                        if (StoryBlocks.MineMatch(w.Block(c.X, c.Y, c.Z).Id, filter)) Block(PlantBase(c), null, null, "mining"); // a tall plant: one designation, its lower half
                    break;
                case "deconstruct":
                    foreach (var c in Cells())
                        if (w.Block(c.X, c.Y, c.Z).Id is var id && !StoryBlocks.Natural.Contains(id) && !StoryBlocks.Undiggable.Contains(id)) Block(PlantBase(c), null, null, "deconstructing");
                    break;
                case "haul":
                    foreach (var e in w.Entities().Where(e => e.Kind == "item" && In(e))) Entity(e, "hauling " + e.Name);
                    break;
                case "hunt":
                    foreach (var e in w.Entities().Where(e => e.Kind == "mob" && !e.Hostile && In(e))) Entity(e, "hunting a " + e.Name);
                    break;
                case "forbid":
                    foreach (var e in w.Entities().Where(e => e.Kind == "item" && In(e))) Entity(e, "forbidden " + e.Name);
                    foreach (var c in Cells().Take(100_000)) if (StoryBlocks.IsDoor(w.Block(c.X, c.Y, c.Z).Id)) Block(c, null, null, "forbidden door");
                    break;
                case "allow":
                {
                    var gone = Designations.Where(d => d.Kind == "forbid" && (d.At is { } at && (area?.Contains(at) ?? blocks!.Contains(at))
                        || d.Blocks?.FirstOrDefault() is { } eb && (area?.Contains(eb) ?? blocks!.Contains(eb)))).ToList();
                    foreach (var d in gone) { Designations.Remove(d); Removed("designation", d.Id); }
                    return (new JsonObject { ["count"] = gone.Count, ["targets"] = new JsonArray() }, null);
                }
            }
            if (made.Count > 4096) return (null, $"{made.Count} targets: designate a smaller area (max 4096)");
            foreach (var d in made) { d.Rev = Touch(); Designations.Add(d); }
            if (made.Count > 0) WakeIdle();
            return (new JsonObject { ["count"] = made.Count, ["targets"] = targets }, null);
        }
    }
}
