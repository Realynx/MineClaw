using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>What the browser asks of a colony: priorities, schedules, drafting, orders, zones and designations.</summary>
public sealed partial class StoryColony
{
    public static readonly IReadOnlyList<string> ZoneKinds = ["chop", "mine", "farm", "fish", "home", "forbidden", "dump"];
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
                if (work.Count > 0) p.Role = null; // a cell edit detaches it from its role: Custom, the values kept (StoryColony.Roles.cs)
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
                OnDraftChanged(p, drafted); // weapon in hand (and armour, by its policy); undrafted: the old item back (StoryColony.Gear.cs)
                p.Rev = Touch();
                Supersede(p, drafted ? "drafted" : "undrafted");
            }
    }

    /// <summary>An order's target: a position, an entity (by id, or a player or bot by name), or an area.</summary>
    private readonly record struct OrderAim(P3? At, int? Entity, Area? Area)
    {
        /// <summary>A place to go: a position or an area.</summary>
        public bool HasPlace => At != null || Area != null;
        public bool HasAny => HasPlace || Entity != null;
    }

    /// <summary>An order for each pawn: replaces its queue (and cuts what it does now) unless <paramref name="queue"/>.</summary>
    public (JsonNode? Result, string? Error) GiveOrder(IReadOnlyList<PawnState> pawns, JsonObject order, bool queue)
    {
        var kind = order["kind"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (kind == null || !OrderKinds.Contains(kind)) return (null, $"order.kind must be {string.Join("|", OrderKinds)}");
        var (aim, err) = OrderAimOf(order["target"]);
        if (err != null) return (null, err);
        long? design = StoryJson.Int(order["design"]);
        if (OrderError(kind, aim, design) is { } bad) return (null, bad);
        if (kind == "attack" && AttackOrderRefused(aim.Entity, order["confirm"]) is { } refused) return refused; // a player or bot: the owner confirms (409)
        // A chop order on a tree's canopy (a top-down click) or a branch means the tree: its trunk base.
        if (kind == "chop") aim = aim with { At = TreeBaseOrCell(aim.At) };
        lock (_lock) return QueueOrders(pawns, kind, aim, design, queue);
    }

    /// <summary>The order's target (none when the order has none), or why it can't be read.</summary>
    private (OrderAim Aim, string? Error) OrderAimOf(JsonNode? t)
    {
        if (t == null) return (default, null);
        var (aim, err) = ReadOrderAim(t);
        if (err == null && !aim.HasAny) err = "target must be {x,y,z} | {entity:id} | {area:[x1,y1,z1,x2,y2,z2]}";
        return (aim, err);
    }

    private (OrderAim Aim, string? Error) ReadOrderAim(JsonNode t)
    {
        if (t is JsonObject o)
        {
            if (o["entity"] is { } en) return (new OrderAim(null, StoryJson.Int(en), null), null);
            // A player or bot by name (the map has no id for a bot).
            if (o["player"] is JsonValue pv && pv.TryGetValue<string>(out var pn))
                return PlayerIdInView(pn) is { } id ? (new OrderAim(null, id, null), null) : (default, $"no player named {pn} in view");
            if (o["area"] is { } an) return (new OrderAim(null, null, Area.Parse(an)), null);
        }
        return (new OrderAim(P3.Parse(t), null, null), null);
    }

    private int? PlayerIdInView(string name) =>
        World?.Entities().FirstOrDefault(e => e.Kind == "player" && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>What the order kind lacks, or an area too large (null = it can be given).</summary>
    private static string? OrderError(string kind, OrderAim aim, long? design) =>
        MissingTarget(kind, aim, design) ?? (aim.Area is { Volume: > 32_768 } ? "area too large for an order (max 32768 blocks)" : null);

    private static string? MissingTarget(string kind, OrderAim aim, long? design) => kind switch
    {
        "move" or "chop" or "mine" when !aim.HasPlace => $"{kind} needs a target position or area",
        "attack" when aim.Entity == null && aim.At == null => "attack needs a target entity",
        "haul" when !aim.HasAny => "haul needs a target",
        "build" when design == null && aim.At == null => "build needs a design (or a target)",
        _ => null,
    };

    /// <summary>An attack on a player or bot the owner hasn't confirmed: the answer (with what to confirm), else null.</summary>
    private (JsonNode? Result, string? Error)? AttackOrderRefused(int? entity, JsonNode? confirm)
    {
        lock (_lock)
            if (AttackCheck(entity, confirm) is { Error: { } aerr } check)
                return (check.Confirm is { } cf ? new JsonObject { ["confirm"] = cf } : null, aerr);
        return null;
    }

    private P3? TreeBaseOrCell(P3? at) => at is { } ca && World is { } cw && StoryBlocks.TreeBaseOf(cw, ca) is { } cb ? cb : at;

    private (JsonNode? Result, string? Error) QueueOrders(IReadOnlyList<PawnState> pawns, string kind, OrderAim aim, long? design, bool queue)
    {
        var ids = new JsonArray();
        foreach (var p in pawns)
        {
            if (kind == "stop") { p.Queue.Clear(); p.Rev = Touch(); Supersede(p, "stop"); NoteOrder(p, new Order { Kind = "stop" }); continue; }
            var o = NewOrder(kind, aim, design);
            if (!queue) { p.Queue.Clear(); Supersede(p, "new order"); }
            if (p.Queue.Count >= 32) return (null, "queue full (32 orders)");
            p.Queue.Add(o);
            NoteOrder(p, o); // a drafted pawn's move sets its post
            p.Rev = Touch();
            ids.Add(o.Id);
        }
        return (new JsonObject { ["ids"] = ids }, null);
    }

    private Order NewOrder(string kind, OrderAim aim, long? design)
    {
        var o = new Order { Id = NextId("o"), Kind = kind, At = aim.At, Entity = aim.Entity, Area = aim.Area, Design = design, Given = Now };
        if (aim.Area is { } a && World is { } w) o.Targets = AreaOrderTargets(kind, w, a);
        return o;
    }

    /// <summary>An area order's targets: its trees (chop), its blocks top down (mine); null for the other kinds.</summary>
    private static List<P3>? AreaOrderTargets(string kind, IStoryWorld w, Area a) => kind switch
    {
        "chop" => StoryBlocks.TreesIn(w, a, max: 64),
        "mine" => a.Blocks().Where(b => StoryBlocks.MineMatch(w.Block(b.X, b.Y, b.Z).Id, "all")).OrderByDescending(b => b.Y).Take(512).ToList(),
        _ => null,
    };

    /// <summary>The job for a pawn's first order (null = no runnable order: the order is dropped).</summary>
    private ActiveJob? OrderJob(PawnState p, PawnLive live)
    {
        while (p.Queue.Count > 0)
        {
            var o = p.Queue[0];
            if (OrderTarget(p, live, o, out var at, out var e)) return OrderKindJob(p, live, o, at, e);
        }
        return null;
    }

    /// <summary>Where the first order goes: its nearest target left, its entity, or its spot. False when it has nothing left (its last
    /// target done, its entity gone): the order is dropped.</summary>
    private bool OrderTarget(PawnState p, PawnLive live, Order o, out P3? at, out StoryEntity? e)
    {
        at = o.At;
        e = null;
        if (o.Targets is { } ts)
        {
            if (ts.Count == 0) { p.Queue.RemoveAt(0); p.Rev = Touch(); return false; }
            at = ts.OrderBy(t => t.Dist(live.Block)).First();
        }
        if (o.Entity is not { } eid) return true;
        e = World?.Entities().FirstOrDefault(x => x.Id == eid);
        if (e == null) { p.Queue.RemoveAt(0); Alert("info", $"{p.Display}: the {o.Kind} target is gone", p.Name); p.Rev = Touch(); return false; }
        at = e.Block;
        return true;
    }

    private ActiveJob? OrderKindJob(PawnState p, PawnLive live, Order o, P3? at, StoryEntity? e) => o.Kind switch
    {
        "move" => Job("order", "go", $"moving to {at}", at ?? o.Area?.Center, null, "order", o.Id, "moving", j => j["dist"] = 0, xp: 0),
        "attack" => AttackJob(p, o, e, at), // the exact entity, every ordered pawn at once (StoryColony.Combat.cs)
        "chop" => ChopOrderJob(o, at),
        "mine" => MineOrderJob(p, live, o, at),
        "haul" => Job(WorkTypes.Haul, "collect", "hauling (order)", at ?? o.Area?.Center, e != null ? "e:" + e.Id : null, "order", o.Id, "working", j => j["r"] = 4, xp: 5),
        "build" when o.Design is { } d => Job(WorkTypes.Build, "blueprint", $"building design #{d}", at, "design:" + d, "order", o.Id, "working", j => j["design"] = d.ToString(), xp: 60),
        "hold" => Job("order", "hold", "holding position", at, null, "order", o.Id, p.Drafted ? "drafted" : "idle", j => j["s"] = 5, xp: 0),
        _ => Job("order", "hold", "waiting", at, null, "order", o.Id, "idle", j => j["s"] = 3, xp: 0),
    };

    private ActiveJob ChopOrderJob(Order o, P3? at) =>
        Job(WorkTypes.Chop, "chop", "chopping (order)", at, at is { } c ? "b:" + c : null, "order", o.Id,
            fill: j => ChopFill(j, at is { } ct && World is { } ow ? StoryBlocks.TreeFrom(ow, ct) : null), xp: 40);

    private void OrderDone(PawnState p, ActiveJob j, bool ok, string? note)
    {
        var o = p.Queue.FirstOrDefault(x => x.Id == j.SourceId);
        if (o == null) return;
        if (o.Kind == "hold" && ok) return; // holds until the next order (or undraft)
        if (!ok && AttackRetry(o, note)) return; // "busy: fighting …": the attack order stays for another go
        if (o.Targets is { } ts && j.At is { } at && TargetsLeft(ts, at, ok, note)) { p.Rev = Touch(); return; }
        p.Queue.Remove(o);
        if (!ok) Alert("warn", $"{p.Display} couldn't {o.Kind}: {note}", p.Name);
        p.Rev = Touch();
    }

    /// <summary>One of an order's targets is finished with: the order goes on while others are left (a failure without a reason ends it).</summary>
    private static bool TargetsLeft(List<P3> ts, P3 at, bool ok, string? note)
    {
        ts.Remove(at);
        return ts.Count > 0 && (ok || !string.IsNullOrEmpty(note));
    }

    // ---------------------------------------------------------------- zones: StoryColony.Zones.cs

    // ---------------------------------------------------------------- designations

    /// <summary>
    /// add: the area (or blocks) expands now to matching targets from the world view, one designation per target (a tree is one
    /// job: its log column(s)); remove|cancel: designations in the area (or by id) go.
    /// </summary>
    public (JsonNode? Result, string? Error) Designate(string op, string? kind, Area? area, IReadOnlyList<P3>? blocks, string? filter,
        IReadOnlyList<string>? ids = null) => op switch
    {
        "remove" or "cancel" => RemoveDesignations(kind, area, blocks, ids),
        "retry" => RetryDesignations(ids),
        "add" => AddDesignations(kind, area, blocks, filter),
        _ => (null, "op must be add|remove|cancel|retry"),
    };

    private (JsonNode? Result, string? Error) RemoveDesignations(string? kind, Area? area, IReadOnlyList<P3>? blocks, IReadOnlyList<string>? ids)
    {
        lock (_lock)
        {
            var gone = Designations.Where(d => RemovalHits(d, kind, area, blocks, ids)).ToList();
            foreach (var d in gone) { Designations.Remove(d); Removed("designation", d.Id); }
            return (new JsonObject { ["count"] = gone.Count, ["removed"] = new JsonArray(gone.Select(d => (JsonNode)d.Id!).ToArray()) }, null);
        }
    }

    /// <summary>A remove|cancel hits a designation named by id, or one of the kind (any kind when null) in the area or on the blocks.</summary>
    private static bool RemovalHits(DesignationState d, string? kind, Area? area, IReadOnlyList<P3>? blocks, IReadOnlyList<string>? ids)
    {
        if (ids != null && ids.Contains(d.Id)) return true;
        if (kind != null && d.Kind != kind) return false;
        return InRemovalArea(d, area) || blocks != null && d.At is { } bt && blocks.Contains(bt);
    }

    /// <summary>Its cell, or (an entity's mark) the block its entity was on, lies in the area.</summary>
    private static bool InRemovalArea(DesignationState d, Area? area) =>
        area is { } a && (d.At is { } at && a.Contains(at) || d.Entity != null && d.Blocks?.FirstOrDefault() is { } eb && a.Contains(eb));

    /// <summary>{op:"retry", ids} → {count, retried}: a failed designation is tried again now (its rest is over, its status open again).</summary>
    private (JsonNode? Result, string? Error) RetryDesignations(IReadOnlyList<string>? ids)
    {
        if (ids == null || ids.Count == 0) return (null, "ids required");
        lock (_lock)
        {
            var hit = Designations.Where(d => ids.Contains(d.Id)).ToList();
            foreach (var d in hit) Reopen(d);
            return (new JsonObject { ["count"] = hit.Count, ["retried"] = new JsonArray(hit.Select(d => (JsonNode)d.Id!).ToArray()) }, null);
        }
    }

    /// <summary>A designation is open again now: its rests and fail counts (the target's and each pawn's) are forgotten.</summary>
    private void Reopen(DesignationState d)
    {
        var key = DesignationKey(d);
        _skip.Remove(key);
        _leaseFails.Remove(key);
        _softFails.Remove(key);
        ForgetPawnFails(key);
        d.Status = "open";
        d.Rev = Touch();
    }

    /// <param name="auto">Placed by the colony (seek_seeds), not the owner: see <see cref="DesignationState.Auto"/>.</param>
    private (JsonNode? Result, string? Error) AddDesignations(string? kind, Area? area, IReadOnlyList<P3>? blocks, string? filter, bool auto = false)
    {
        if (AddDesignationError(kind, area, blocks, filter) is { } err) return (null, err);
        if (World is not { } w) return (null, "no world view yet");
        lock (_lock)
        {
            if (kind == "allow") return AllowCells(area, blocks);
            var batch = new DesignationBatch(this, w, kind!, area, blocks);
            batch.Collect(filter);
            var made = batch.Made;
            if (made.Count > 4096) return (null, $"{made.Count} targets: designate a smaller area (max 4096)");
            foreach (var d in made) { d.Auto = auto; d.Rev = Touch(); Designations.Add(d); }
            if (made.Count > 0 || batch.Revived > 0) WakeIdle();
            return (new JsonObject { ["count"] = made.Count + batch.Revived, ["revived"] = batch.Revived, ["targets"] = batch.Targets }, null);
        }
    }

    private static string? AddDesignationError(string? kind, Area? area, IReadOnlyList<P3>? blocks, string? filter)
    {
        if (kind == null || !DesignationKinds.Contains(kind)) return $"kind must be {string.Join("|", DesignationKinds)}";
        if (!MineFilterKnown(filter)) return "filter must be all|ores|stone|soil";
        if (area == null && (blocks == null || blocks.Count == 0)) return "area or blocks required";
        return area is { Volume: > 262_144 } ? "area too large (max 262144 blocks)" : null;
    }

    private static bool MineFilterKnown(string? filter) => filter is null or "all" or "ores" or "stone" or "soil";

    /// <summary>allow: the forbid marks in the area (or on the blocks) go, a door's or an item's.</summary>
    private (JsonNode? Result, string? Error) AllowCells(Area? area, IReadOnlyList<P3>? blocks)
    {
        bool In(P3 c) => area?.Contains(c) ?? blocks!.Contains(c);
        var gone = Designations.Where(d => d.Kind == "forbid" && (d.At is { } at && In(at) || d.Blocks?.FirstOrDefault() is { } eb && In(eb))).ToList();
        foreach (var d in gone) { Designations.Remove(d); Removed("designation", d.Id); }
        return (new JsonObject { ["count"] = gone.Count, ["targets"] = new JsonArray() }, null);
    }

    /// <summary>
    /// One add: the targets it finds become new designations, one per cell or entity. A cell or entity this kind has already (or this
    /// call made already: a tall plant's two halves) is no new one; a failed/resting one there is tried again now (live 11-04:
    /// re-marking a grove whose cuts had failed answered "0" and nothing happened).
    /// </summary>
    private sealed class DesignationBatch
    {
        private readonly StoryColony _c;
        private readonly IStoryWorld _w;
        private readonly string _kind;
        private readonly Area? _area;
        private readonly IReadOnlyList<P3>? _blocks;
        private readonly HashSet<P3> _madeAt = new(), _hasAt;
        private readonly HashSet<int> _madeEnt = new(), _hasEnt;
        private readonly Dictionary<P3, DesignationState> _existing;

        public readonly JsonArray Targets = new();
        public readonly List<DesignationState> Made = new();
        public int Revived;

        public DesignationBatch(StoryColony c, IStoryWorld w, string kind, Area? area, IReadOnlyList<P3>? blocks)
        {
            _c = c; _w = w; _kind = kind; _area = area; _blocks = blocks;
            // What this kind has already (sets: a 4096-cell box over a few hundred designations was O(n·m)).
            _hasAt = c.Designations.Where(d => d.Kind == kind && d.At != null).Select(d => d.At!.Value).ToHashSet();
            _existing = c.Designations.Where(d => d.Kind == kind && d.At != null).GroupBy(d => d.At!.Value).ToDictionary(g => g.Key, g => g.First());
            _hasEnt = c.Designations.Where(d => d.Kind == kind && d.Entity != null).Select(d => d.Entity!.Value).ToHashSet();
        }

        public void Collect(string? filter)
        {
            switch (_kind)
            {
                case "chop": Trees(); break;
                case "cut": CellsWhere(id => StoryBlocks.Plants.Contains(id) || StoryBlocks.Leaves.Contains(id), "cutting plants"); break; // a tall plant: its lower half
                case "harvest": Crops(); break;
                case "mine": CellsWhere(id => StoryBlocks.MineMatch(id, filter), "mining"); break; // a tall plant: one designation, its lower half
                case "deconstruct": CellsWhere(id => !StoryBlocks.Natural.Contains(id) && !StoryBlocks.Undiggable.Contains(id), "deconstructing"); break;
                case "haul": EntitiesWhere(e => e.Kind == "item", "hauling "); break;
                case "hunt": EntitiesWhere(e => e.Kind == "mob" && !e.Hostile, "hunting a "); break;
                case "forbid": EntitiesWhere(e => e.Kind == "item", "forbidden "); Doors(); break;
            }
        }

        private IEnumerable<P3> Cells() => _blocks ?? _area!.Value.Blocks();

        private bool In(StoryEntity e) => _area is { } a ? a.Contains(e.Block) || a.Contains(e.Block.Add(0, 1, 0)) : _blocks!.Contains(e.Block);

        /// <summary>Any log or leaf of a tree (the canopy top a top-down click lands on included) stands for that tree: its trunk base.</summary>
        private void Trees()
        {
            var bases = _area is { } a ? StoryBlocks.TreesIn(_w, a)
                : _blocks!.Select(b => StoryBlocks.TreeBaseOf(_w, b)).Where(b => b != null).Select(b => b!.Value).Distinct().ToList();
            foreach (var b in bases.Take(512)) Block(b, StoryBlocks.TreeFrom(_w, b), null, "chopping a tree");
        }

        /// <summary>Each cell whose block matches, on its plant base (a tall plant is one designation, on its lower half).</summary>
        private void CellsWhere(Func<int, bool> match, string label)
        {
            foreach (var c in Cells())
                if (match(_w.Block(c.X, c.Y, c.Z).Id)) Block(_c.PlantBase(c), null, null, label);
        }

        private void Crops()
        {
            foreach (var c in Cells())
                if (StoryBlocks.Crop(_w, c.X, c.Y, c.Z) is { Ripe: true } crop) Block(c, null, crop.Replant, "harvesting");
        }

        private void EntitiesWhere(Func<StoryEntity, bool> match, string label)
        {
            foreach (var e in _w.Entities().Where(e => match(e) && In(e))) Entity(e, label + e.Name);
        }

        private void Doors()
        {
            foreach (var c in Cells().Take(100_000)) if (StoryBlocks.IsDoor(_w.Block(c.X, c.Y, c.Z).Id)) Block(c, null, null, "forbidden door");
        }

        private bool TakenAt(P3 at) => !_madeAt.Add(at) || _hasAt.Contains(at);

        private void Block(P3 at, List<P3>? col, string? replant, string label)
        {
            if (TakenAt(at)) { Revive(at); return; }
            Made.Add(new DesignationState { Id = _c.NextId("d"), Kind = _kind, At = at, Blocks = col, Replant = replant, Label = label, Created = _c.Now });
            Targets.Add(at.Json());
        }

        private void Entity(StoryEntity e, string label)
        {
            if (!_madeEnt.Add(e.Id) || _hasEnt.Contains(e.Id)) return;
            Made.Add(new DesignationState { Id = _c.NextId("d"), Kind = _kind, Entity = e.Id, Blocks = [e.Block], Label = label, Created = _c.Now });
            Targets.Add(e.Id);
        }

        /// <summary>Marking a cell that already holds a failed/resting designation of this kind tries it again now.</summary>
        private void Revive(P3 at)
        {
            if (!_existing.TryGetValue(at, out var d) || d.Status is null or "open") return;
            _c.Reopen(d);
            Revived++;
            Targets.Add(at.Json());
        }
    }
}
