using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// The job picker (per pawn, whenever it asks): orders (drafted pawns only those), urgent needs (hp/food, thresholds by trait),
/// deliveries of what it carries, the schedule hour, then the highest-priority work type with an available job; ties by column
/// order (or by skill first: <see cref="ColonyMeta.TieBreak"/>), then distance. Every target is leased, so two pawns never share one.
/// </summary>
public sealed partial class StoryColony
{
    private readonly Dictionary<string, DateTimeOffset> _skip = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _ghostFails = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _leaseFails = new(StringComparer.Ordinal);
    private readonly HashSet<string> _noStorageAlerted = new(StringComparer.Ordinal);

    /// <summary>The in-game hour now (12 when there is no world view yet).</summary>
    public int HourNow => World is { } w ? StoryCharacter.Hour(w.TimeOfDay) : 12;

    private static readonly HashSet<string> Gathering = new(StringComparer.Ordinal) { "designation", "zone", "crop", "drop", "furnace", "order", "deliver" };

    private string NewJobId() => $"{Id}.j{++_jobSeq}";

    private ActiveJob Job(string type, string kind, string label, P3? at, string? lease, string source, string? sourceId, string state = "working",
        Action<JsonObject>? fill = null, double xp = 20, int count = 0)
    {
        var json = new JsonObject { ["id"] = "", ["type"] = type, ["kind"] = kind, ["label"] = label };
        if (at is { } a) json["at"] = a.ToString();
        fill?.Invoke(json);
        var id = NewJobId();
        json["id"] = id;
        return new ActiveJob
        {
            Id = id, Type = type, Kind = kind, Label = label, At = at, Lease = lease, Json = json, Since = Now, State = state, Source = source,
            SourceId = sourceId, Xp = xp, Count = count,
        };
    }

    private ActiveJob Hold(string label, string state, P3? at = null, int s = 5, string type = "idle") =>
        Job(type, state == "sleeping" ? "sleep" : at != null && state == "idle" ? "idle" : "hold", label, at, null, "none", null, state, j => j["s"] = s, xp: 0);

    /// <summary>The job for <paramref name="p"/> now. Never null: with nothing to do it idles (and the colony hears about it).</summary>
    internal ActiveJob Pick(PawnState p, PawnLive live)
    {
        if (!live.Online) return Hold("offline", "offline");
        if (!Meta.Active) return Job("idle", "end", "colony released", null, null, "none", null, "idle", xp: 0); // the script ends cleanly
        if (Meta.Paused) return Hold("colony paused", "idle", s: 5);
        if (p.Drafted) return OrderJob(p, live) ?? Hold("drafted: waiting for orders", "drafted", s: 3, type: "order");
        if (p.Queue.Count > 0 && OrderJob(p, live) is { } order) return order;
        if (Urgent(p, live) is { } need) return need;
        if (Deliver(p, live) is { } deliver) return deliver;
        int hour = HourNow;
        var block = p.Schedule.Length == 24 && ScheduleBlocks.Known(p.Schedule[hour]) ? p.Schedule[hour] : ScheduleBlocks.Anything;
        var home = Home();
        switch (block)
        {
            case ScheduleBlocks.Sleep:
                return Hold($"sleeping ({hour:00}:00)", "sleeping", home, 30, "schedule");
            case ScheduleBlocks.Free:
                return Hold($"free time ({hour:00}:00)", "idle", home, 10, "schedule");
            case ScheduleBlocks.Anything when live.Food <= 14 && FoodIn(live) != null:
                return Job("need", "eat", "eating", null, null, "need", null, "eating", xp: 0);
        }
        if (Work(p, live) is { } work) return work;
        if (Deliver(p, live, force: true) is { } leftovers) return leftovers; // nothing else to do: store what it still carries
        if (!_idleAlert.TryGetValue(p.Name, out var last) || Now - last > TimeSpan.FromMinutes(5))
        {
            _idleAlert[p.Name] = Now;
            Alert("info", $"{p.Display} is idle: nothing to do", p.Name);
        }
        return Hold("idle: nothing to do", "idle", home, StoryCharacter.IdleSeconds(p));
    }

    // ---------------------------------------------------------------- needs

    private Stack? FoodIn(PawnLive live) =>
        World is { } w ? live.Inventory.FirstOrDefault(s => w.Data.Food(s.Id, s.Meta) != null && !Raw(w, s.Id)) : null;

    /// <summary>Raw meat is food but poor: pawns cook it first when they can (chicken can poison).</summary>
    private static bool Raw(IStoryWorld w, int id) => id is 319 or 363 or 365 or 411 or 423;

    private IReadOnlyList<StoryEntity> Hostiles(PawnLive live, double r) =>
        World?.Entities().Where(e => e.Kind == "mob" && e.Hostile && Math.Abs(e.X - live.X) <= r && Math.Abs(e.Z - live.Z) <= r && Math.Abs(e.Y - live.Y) <= 6).ToList() ?? [];

    private ActiveJob? Urgent(PawnState p, PawnLive live)
    {
        int flee = StoryCharacter.FleeHp(p), eat = StoryCharacter.EatFood(p);
        if (live.Hp <= flee && Hostiles(live, 12).Count > 0)
        {
            var home = Home();
            var mob = Hostiles(live, 12)[0];
            // Away from the monster: home if it is the other way, else a dozen blocks straight away from it.
            P3 to = home is { } h && h.Dist(mob.X, mob.Y, mob.Z) > h.Dist(live.X, live.Y, live.Z) ? h
                : new P3((int)(live.X + Math.Sign(live.X - mob.X) * 12), (int)live.Y, (int)(live.Z + Math.Sign(live.Z - mob.Z) * 12));
            return Job("need", "go", $"fleeing a {mob.Name}", to, null, "need", null, "fleeing", j => j["dist"] = 1, xp: 0);
        }
        if (live.Food <= eat || live.Hp <= flee && live.Food < 20)
        {
            if (FoodIn(live) != null || live.Inventory.Any(s => World?.Data.Food(s.Id, s.Meta) != null))
                return Job("need", "eat", "eating", null, null, "need", null, "eating", xp: 0);
            if (FindStored(s => World?.Data.Food(s.Id, s.Meta) != null && !Raw(World!, s.Id), live.Block) is { } got)
            {
                var j = Job("need", "eat", "fetching food", null, null, "need", null, "eating", f => f["fetch"] = Fetch(got.Store.Chest, got.Stack, 4), xp: 0);
                j.Takes.Add((got.Store.Chest, got.Stack with { Count = Math.Min(4, got.Stack.Count) }));
                return j;
            }
            if (!_idleAlert.TryGetValue("food:" + p.Name, out var last) || Now - last > TimeSpan.FromMinutes(5))
            {
                _idleAlert["food:" + p.Name] = Now;
                Alert("warn", $"{p.Display} is hungry and there is no food in storage", p.Name);
            }
        }
        return null;
    }

    private JsonArray Fetch(P3 from, Stack s, int n) =>
        new(new JsonObject { ["from"] = from.ToString(), ["item"] = ItemName(s.Id, s.Meta), ["n"] = Math.Min(n, s.Count) });

    internal string ItemName(int id, int meta) => World?.Data is { } d && d.ItemExists(id) ? d.Item(id).Name : id.ToString();

    // ---------------------------------------------------------------- deliveries

    /// <summary>Items a pawn keeps: tools, weapons, armour and one food stack.</summary>
    private List<Stack> Deliverables(PawnLive live)
    {
        if (World is not { } w) return [];
        var cats = StoryCategories.For(w.Data);
        bool foodKept = false;
        var list = new List<Stack>();
        foreach (var s in live.Inventory)
        {
            var leaf = cats.LeafOf(s.Id, s.Meta);
            if (leaf is "tools" or "weapons" or "armour") continue;
            if (!foodKept && w.Data.Food(s.Id, s.Meta) != null && !Raw(w, s.Id)) { foodKept = true; continue; }
            list.Add(s);
        }
        return list;
    }

    private ActiveJob? Deliver(PawnState p, PawnLive live, bool force = false)
    {
        bool after = _lastSource.TryGetValue(p.Name, out var src) && Gathering.Contains(src);
        if (!after && !force && live.FreeSlots > 4) return null;
        var items = Deliverables(live);
        if (items.Count == 0) { _lastSource.Remove(p.Name); return null; }
        var groups = new Dictionary<P3, List<Stack>>();
        foreach (var s in items)
        {
            if (BestChest(s.Id, s.Meta, live.Block) is not { } st)
            {
                var key = $"{s.Id}:{s.Meta}";
                if (_noStorageAlerted.Add(key)) Alert("warn", $"No storage accepts {World!.Data.DisplayName(s.Id, s.Meta)}: {p.Display} keeps it", p.Name);
                continue;
            }
            if (!groups.TryGetValue(st.Chest, out var g)) groups[st.Chest] = g = new List<Stack>();
            g.Add(s);
        }
        _lastSource.Remove(p.Name);
        if (groups.Count == 0) return null;
        var (chest, stacks) = groups.OrderBy(g => g.Key.Dist(live.X, live.Y, live.Z)).First();
        var names = stacks.Select(s => ItemName(s.Id, s.Meta)).Distinct().ToList();
        var j = Job(WorkTypes.Haul, "go", $"storing {string.Join(", ", names)}", chest, null, "deliver", chest.ToString(), "working",
            f =>
            {
                f["dist"] = 2;
                f["stash"] = new JsonArray(new JsonObject { ["to"] = chest.ToString(), ["items"] = new JsonArray(names.Select(n => (JsonNode)n!).ToArray()) });
            }, xp: 5);
        j.Puts.Add((chest, stacks));
        return j;
    }

    // ---------------------------------------------------------------- work

    /// <summary>The work types in the order this pawn tries them: priority 1 first; ties by column (or by skill first).</summary>
    internal IReadOnlyList<string> WorkOrder(PawnState p) =>
        WorkTypes.All.Where(t => p.Priorities.GetValueOrDefault(t) is >= 1 and <= 4)
            .OrderBy(t => p.Priorities[t])
            .ThenBy(t => Meta.TieBreak == "skill" ? -(p.Skills.GetValueOrDefault(t)?.Level ?? 0) : 0)
            .ThenBy(WorkTypes.Column).ToList();

    private ActiveJob? Work(PawnState p, PawnLive live)
    {
        foreach (var t in WorkOrder(p))
            if (Candidate(t, p, live) is { } j) return j;
        return null;
    }

    private bool Skipped(string key) => _skip.TryGetValue(key, out var until) && until > Now;

    private bool Forbidden(P3 at) => Zones.Any(z => z.Kind == "forbidden" && z.Area.Contains(at))
        || Designations.Any(d => d.Kind == "forbid" && d.At == at);

    private bool ForbiddenEntity(int id) => Designations.Any(d => d.Kind == "forbid" && d.Entity == id);

    private bool Open(string key, string pawn, P3? at) => Free(key, pawn) && !Skipped(key) && (at == null || !Forbidden(at.Value));

    /// <summary>The nearest available job of one work type for this pawn, or null.</summary>
    internal ActiveJob? Candidate(string type, PawnState p, PawnLive live)
    {
        var from = live.Block;
        switch (type)
        {
            case WorkTypes.Guard:
            {
                var home = Home();
                if (home == null) return null;
                var mob = World?.Entities().Where(e => e.Kind == "mob" && e.Hostile && InHome(e.Block, home.Value) && Open("e:" + e.Id, p.Name, null))
                    .OrderBy(e => e.Block.Dist(from)).FirstOrDefault();
                return mob == null ? null : Job(type, "attack", $"guarding: {mob.Name}", mob.Block, "e:" + mob.Id, "guard", mob.Id.ToString(), "fighting",
                    j => j["mob"] = mob.Name, xp: 25);
            }
            case WorkTypes.Doctor:
            {
                if (World is not { } w) return null;
                foreach (var other in Pawns.Values.Where(o => o != p))
                {
                    var ol = Host.Live(other.Name);
                    if (!ol.Online || ol.Food > StoryCharacter.EatFood(other) || ol.Inventory.Any(s => w.Data.Food(s.Id, s.Meta) != null)) continue;
                    var key = "feed:" + other.Name;
                    if (!Open(key, p.Name, null)) continue;
                    var mine = FoodIn(live);
                    var stored = mine == null ? FindStored(s => w.Data.Food(s.Id, s.Meta) != null && !Raw(w, s.Id), from) : null;
                    if (mine == null && stored == null) continue;
                    var item = mine ?? stored!.Value.Stack;
                    var j = Job(type, "give", $"feeding {other.Display}", ol.Block, key, "feed", other.Name, "working", f =>
                    {
                        f["to"] = other.Name; f["item"] = ItemName(item.Id, item.Meta); f["n"] = 3;
                        if (stored is { } st) f["fetch"] = Fetch(st.Store.Chest, st.Stack, 3);
                    }, xp: 30);
                    if (stored is { } st2) j.Takes.Add((st2.Store.Chest, st2.Stack with { Count = Math.Min(3, st2.Stack.Count) }));
                    return j;
                }
                return null;
            }
            case WorkTypes.Cook:
                return BillJob(p, live, WorkTypes.Cook);
            case WorkTypes.Craft:
                return BillJob(p, live, WorkTypes.Craft);
            case WorkTypes.Hunt:
            {
                var ents = World?.Entities().ToDictionary(e => e.Id) ?? new Dictionary<int, StoryEntity>();
                var d = Designations.Where(x => x.Kind == "hunt" && x.Entity is { } id && ents.ContainsKey(id) && Open("e:" + id, p.Name, null))
                    .OrderBy(x => ents[x.Entity!.Value].Block.Dist(from)).FirstOrDefault();
                if (d == null) return null;
                var e = ents[d.Entity!.Value];
                return Job(type, "attack", $"hunting a {e.Name}", e.Block, "e:" + e.Id, "designation", d.Id, "fighting", j => j["mob"] = e.Name, xp: 30);
            }
            case WorkTypes.Build:
                return GhostJob(p, live) ?? DesignationJob(p, from, type, "deconstruct", "dig", "deconstructing");
            case WorkTypes.Farm:
                return DesignationJob(p, from, type, "harvest", "harvest", "harvesting") ?? FarmZoneJob(p, live);
            case WorkTypes.Mine:
                return DesignationJob(p, from, type, "mine", "dig", "mining") ?? ZoneJob(p, from, type, "mine");
            case WorkTypes.Chop:
                return DesignationJob(p, from, type, "chop", "chop", "chopping a tree") ?? DesignationJob(p, from, type, "cut", "dig", "cutting plants")
                    ?? ZoneJob(p, from, type, "chop");
            case WorkTypes.Haul:
                return HaulJob(p, from) ?? RebalanceJob(p, from);
        }
        return null;
    }

    private bool InHome(P3 at, P3 home)
    {
        var hz = Zones.Where(z => z.Kind == "home").ToList();
        return hz.Count > 0 ? hz.Any(z => z.Area.ContainsXZ(at.X, at.Z)) : at.Dist(home) <= 24;
    }

    /// <summary>A block designation of <paramref name="kind"/> (nearest open first).</summary>
    private ActiveJob? DesignationJob(PawnState p, P3 from, string type, string kind, string scriptKind, string label)
    {
        var d = Designations.Where(x => x.Kind == kind && x.At is { } at && Open("b:" + at, p.Name, at)).OrderBy(x => x.At!.Value.Dist(from)).FirstOrDefault();
        if (d == null) return null;
        var at = d.At!.Value;
        return Job(type, scriptKind, d.Label ?? label, at, "b:" + at, "designation", d.Id, "working",
            j => { if (d.Replant != null) j["replant"] = d.Replant; }, xp: kind == "chop" ? 40 : 20);
    }

    // ---------------------------------------------------------------- zones (standing tasks)

    private readonly Dictionary<string, (DateTimeOffset At, List<(P3 Pos, string? Replant, string Kind)> Tasks)> _zoneScan = new();

    /// <summary>A zone's standing tasks (scanned at most every 10 s): chop = its trees, mine = its ores, farm = ripe crops and bare farmland.</summary>
    private List<(P3 Pos, string? Replant, string Kind)> ZoneTasks(ZoneState z)
    {
        if (_zoneScan.TryGetValue(z.Id, out var c) && Now - c.At < TimeSpan.FromSeconds(10)) return c.Tasks;
        var list = new List<(P3, string?, string)>();
        if (World is { } w)
        {
            var a = z.Area;
            long scanned = 0;
            if (z.Kind == "chop") list.AddRange(StoryBlocks.TreeBases(w, a).Take(64).Select(b => (b, (string?)null, "chop")));
            else
                for (int x = a.X1; x <= a.X2 && list.Count < 256; x++)
                    for (int zz = a.Z1; zz <= a.Z2 && list.Count < 256; zz++)
                        for (int y = Math.Max(0, a.Y1); y <= Math.Min(w.Height - 1, a.Y2); y++)
                        {
                            if (++scanned > 300_000) goto done;
                            var (id, _) = w.Block(x, y, zz);
                            if (z.Kind == "mine" && StoryBlocks.Ores.Contains(id)) list.Add((new P3(x, y, zz), null, "mine"));
                            else if (z.Kind == "farm")
                            {
                                if (StoryBlocks.Crop(w, x, y, zz) is { Ripe: true } crop) list.Add((new P3(x, y, zz), crop.Replant, "harvest"));
                                else if (id == 60 && w.Block(x, y + 1, zz).Id == 0) list.Add((new P3(x, y + 1, zz), null, "sow"));
                            }
                        }
            done:;
        }
        _zoneScan[z.Id] = (Now, list);
        return list;
    }

    private ActiveJob? ZoneJob(PawnState p, P3 from, string type, string zoneKind)
    {
        foreach (var z in Zones.Where(z => z.Kind == zoneKind).OrderBy(z => z.Area.Center.Dist(from)))
        {
            var t = ZoneTasks(z).Where(t => Open("b:" + t.Pos, p.Name, t.Pos)).OrderBy(t => t.Pos.Dist(from)).FirstOrDefault();
            if (t.Kind == null) continue;
            return zoneKind == "chop"
                ? Job(type, "chop", "chopping a tree (zone)", t.Pos, "b:" + t.Pos, "zone", z.Id, xp: 40)
                : Job(type, "dig", "mining ore (zone)", t.Pos, "b:" + t.Pos, "zone", z.Id);
        }
        return null;
    }

    private ActiveJob? FarmZoneJob(PawnState p, PawnLive live)
    {
        var from = live.Block;
        foreach (var z in Zones.Where(z => z.Kind == "farm").OrderBy(z => z.Area.Center.Dist(from)))
            foreach (var t in ZoneTasks(z).Where(t => Open("b:" + t.Pos, p.Name, t.Pos)).OrderBy(t => t.Pos.Dist(from)))
            {
                if (t.Kind == "harvest")
                    return Job(WorkTypes.Farm, "harvest", "harvesting (farm zone)", t.Pos, "b:" + t.Pos, "crop", z.Id, "working",
                        j => { if (t.Replant != null) j["replant"] = t.Replant; }, xp: 15);
                if (World is not { } w) continue;
                var seed = live.Inventory.FirstOrDefault(s => StoryCategories.For(w.Data).LeafOf(s.Id, s.Meta) == "plants_seeds" && s.Id != 372);
                (StorageState Store, Stack Stack)? stored = seed == null ? FindStored(s => StoryCategories.For(w.Data).LeafOf(s.Id, s.Meta) == "plants_seeds" && s.Id != 372, from) : null;
                if (seed == null && stored == null) continue;
                var item = seed ?? stored!.Value.Stack;
                var j2 = Job(WorkTypes.Farm, "place", "sowing", t.Pos, "b:" + t.Pos, "sow", z.Id, "working", f =>
                {
                    f["block"] = ItemName(item.Id, item.Meta);
                    if (stored is { } st) f["fetch"] = Fetch(st.Store.Chest, st.Stack, 16);
                }, xp: 10);
                if (stored is { } st2) j2.Takes.Add((st2.Store.Chest, st2.Stack with { Count = Math.Min(16, st2.Stack.Count) }));
                return j2;
            }
        return null;
    }

    // ---------------------------------------------------------------- hauling

    private ActiveJob? HaulJob(PawnState p, P3 from)
    {
        if (World is not { } w) return null;
        if (Storages.Count == 0) return null; // hauling delivers into chests only: no loot left on the ground in a pile
        var ents = w.Entities().Where(e => e.Kind == "item" && !ForbiddenEntity(e.Id)).ToDictionary(e => e.Id);
        var d = Designations.Where(x => x.Kind == "haul" && x.Entity is { } id && ents.ContainsKey(id) && Open("e:" + id, p.Name, null))
            .OrderBy(x => ents[x.Entity!.Value].Block.Dist(from)).FirstOrDefault();
        StoryEntity? e = d != null ? ents[d.Entity!.Value] : null;
        // Standing task: loose drops at home that some storage accepts.
        if (e == null && Home() is { } home)
            e = ents.Values.Where(x => InHome(x.Block, home) && !Forbidden(x.Block) && Open("e:" + x.Id, p.Name, null) && BestChest(x.ItemId, x.ItemMeta, x.Block) != null)
                .OrderBy(x => x.Block.Dist(from)).FirstOrDefault();
        if (e == null) return null;
        return Job(WorkTypes.Haul, "collect", $"hauling {w.Data.DisplayName(e.ItemId, e.ItemMeta)}", e.Block, "e:" + e.Id, d != null ? "designation" : "drop",
            d?.Id, "working", j => j["r"] = 3, xp: 5);
    }

    // ---------------------------------------------------------------- done / failed

    private void Done(PawnState p, ActiveJob j, string? note)
    {
        foreach (var (chest, s) in j.Takes) if (StorageAt(chest) is { } st) TakeContents(st, s.Id, s.Meta, s.Count);
        foreach (var (chest, stacks) in j.Puts) if (StorageAt(chest) is { } st) foreach (var s in stacks) AddContents(st, s);
        if (WorkTypes.Known(j.Type) && j.Xp > 0 && StoryCharacter.Learn(p, j.Type, j.Xp, HourNow) > 0)
            Alert("info", $"{p.Display} is now level {p.Skills[j.Type].Level} at {j.Type}", p.Name);
        switch (j.Source)
        {
            case "designation":
                if (Designations.FirstOrDefault(d => d.Id == j.SourceId) is { } des)
                {
                    Designations.Remove(des);
                    Removed("designation", des.Id);
                }
                break;
            case "ghost":
                GhostDone(j);
                break;
            case "bill":
            case "furnace":
                BillDone(p, j);
                break;
            case "order":
                OrderDone(p, j, ok: true, note);
                break;
            case "zone" or "crop":
                _zoneScan.Remove(j.SourceId ?? "");
                break;
        }
    }

    private void Failed(PawnState p, ActiveJob j, string? note)
    {
        var why = string.IsNullOrWhiteSpace(note) ? "failed" : note.Trim();
        if (why.Length > 120) why = why[..119] + "…";
        _log.LogInformation("story {Colony}: {Pawn} {Kind} {At} failed: {Why}", Id, p.Name, j.Kind, j.At?.ToString() ?? "", why);
        foreach (var (chest, _) in j.Puts)
            if (StorageAt(chest) is { } st && why.Contains("full", StringComparison.OrdinalIgnoreCase)) { st.FreeSlots = 0; st.Rev = Touch(); }
        if (j.Lease != null)
        {
            // Not this target again right away; one that keeps failing (an unreachable drop) rests for 10 minutes.
            int n = _leaseFails[j.Lease] = _leaseFails.GetValueOrDefault(j.Lease) + 1;
            _skip[j.Lease] = Now + (n >= 3 ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(30));
        }
        switch (j.Source)
        {
            case "designation":
                if (Designations.FirstOrDefault(d => d.Id == j.SourceId) is { } des && ++des.Fails >= 3)
                {
                    Designations.Remove(des);
                    Removed("designation", des.Id);
                    Alert("warn", $"Gave up {des.Kind} at {des.At?.ToString() ?? "#" + des.Entity}: {why}", p.Name);
                }
                break;
            case "ghost":
                GhostFailed(p, j, why);
                break;
            case "bill":
            case "furnace":
                BillFailed(p, j, why);
                break;
            case "order":
                OrderDone(p, j, ok: false, why);
                break;
        }
    }
}
