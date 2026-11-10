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

    /// <summary>The in-game hour now (12 when there is no world view yet).</summary>
    public int HourNow => World is { } w ? StoryCharacter.Hour(w.TimeOfDay) : 12;

    private static readonly HashSet<string> Gathering = new(StringComparer.Ordinal) { "designation", "zone", "crop", "drop", "furnace", "order", "deliver", "fish" };

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
        if (_humanControlled.Contains(p.Name)) return Hold("controlled by player", "controlled", s: 1);
        ReconcileStorageSigns();
        _pickEnts = null; // the world's entities are read once per pick
        _toolPlans.Clear(); // and each block's tool once per pick
        _pickPawn = p.Name; _pickClaims = null; // chests this pawn can't reach are left out of its plans (StoryColony.Ledger.cs)
        PurgeToolFailuresOnce(); // after a load: tool/record failures of old never hold a target back
        try
        {
            var job = QuarryTravel(PillarHint(FollowOre(PickCore(p, live)), live), live);
            // Idle after an empty search: the wanderers' wake check need not search again until something changes (StoryColony.IdleAudit.cs).
            if (job.Source is "none" or "wander" && _looks.TryGetValue(p.Name, out var look) && look.At == Now && !look.Found) _probed[p.Name] = (Now, WorkStamp());
            return job;
        }
        finally { _pickEnts = null; _pickPawn = null; }
    }

    private ActiveJob PickCore(PawnState p, PawnLive live)
    {
        if (GateJob(p, live) is { } gated) return gated;
        if (ErrandJob(p, live) is { } errand) return errand;
        int hour = HourNow;
        var block = ScheduleBlockAt(p, hour);
        var home = Home();
        return ScheduledJob(p, live, block, hour, home) ?? WorkOrIdle(p, live, hour);
    }

    /// <summary>The pawn's schedule block for this hour (anything when its schedule is missing or the block unknown).</summary>
    private static string ScheduleBlockAt(PawnState p, int hour) =>
        p.Schedule.Length == 24 && ScheduleBlocks.Known(p.Schedule[hour]) ? p.Schedule[hour] : ScheduleBlocks.Anything;

    /// <summary>What comes before anything else: offline, released, leaving, paused, an escape, gear work, drafted.</summary>
    private ActiveJob? GateJob(PawnState p, PawnLive live)
    {
        if (!live.Online) return Hold("offline", "offline");
        if (!Meta.Active) return Job("idle", "end", "colony released", null, null, "none", null, "idle", xp: 0); // the script ends cleanly
        if (LeavingJob(p, live) is { } leave) return leave; // removed from the colony: store its things, then go (StoryColony.Roster.cs)
        if (Meta.Paused) return Hold("colony paused", "idle", s: 5);
        if (!p.Drafted && EscapeJob(p, live) is { } escape) return escape; // stuck in a pit, a cave, water (StoryColony.Rescue.cs)
        if (GearJob(p, live) is { } gear) return gear; // gear work first: arming, armour, fetches the owner asked for (StoryColony.Gear.cs)
        return p.Drafted ? DraftedJob(p, live) : null;
    }

    private ActiveJob DraftedJob(PawnState p, PawnLive live) =>
        DraftedEngage(p, live) ?? OrderJob(p, live) ?? DraftedReturn(p, live) ?? Hold("drafted: waiting for orders", "drafted", s: 3, type: "order");

    /// <summary>Before its schedule: orders, urgent needs, a back-off rest (only with no work there), deliveries, its gear policy.</summary>
    private ActiveJob? ErrandJob(PawnState p, PawnLive live)
    {
        if (p.Queue.Count > 0 && OrderJob(p, live) is { } order) return order;
        if (Urgent(p, live) is { } need) return need;
        if (QuarryLavaDelivery(p, live) is { } lava) return lava;
        // A back-off rests the failed targets; the pawn itself rests only when nothing else is there (Fox 11-04: never idle with work).
        if (RestLeft(p) != null && Work(p, live) == null && Resting(p, live) is { } rest) return rest;
        if (Deliver(p, live) is { } deliver) return deliver;
        return PolicyJob(p, live); // its gear policy: tools for its work, weapon, armour, food
    }

    /// <summary>The schedule hour's own job: sleep, free time, a meal in "anything" hours when hungry (null = work hours).</summary>
    private ActiveJob? ScheduledJob(PawnState p, PawnLive live, string block, int hour, P3? home)
    {
        switch (block)
        {
            case ScheduleBlocks.Sleep:
                return SleepJob(p, live, hour, home);
            case ScheduleBlocks.Free: // idle behaviour (StoryColony.Idle.cs): wander the home area, else stay put (never all to one cell)
                return IdleJob(p, live, "free", hour) ?? Hold($"free time ({hour:00}:00)", "idle", null, 10, "schedule");
            case ScheduleBlocks.Anything when live.Food <= 14 && FoodIn(live) != null:
                return Job("need", "eat", "eating", null, null, "need", null, "eating", xp: 0);
        }
        return null;
    }

    private ActiveJob WorkOrIdle(PawnState p, PawnLive live, int hour)
    {
        if (RescueJob(p, live) is { } rescue) return rescue; // a pawn stuck in a pit gets blocks brought to it first
        if (p.Role == "guard" && GuardZoneJob(p, live) is { } guard) return guard;
        if (Work(p, live) is { } work) return work;
        if (Deliver(p, live, force: true) is { } leftovers) return leftovers; // nothing else to do: store what it still carries
        var idleLabel = WorkIdleLabel(p);
        if (!_idleAlert.TryGetValue(p.Name, out var last) || Now - last > TimeSpan.FromMinutes(5))
        {
            _idleAlert[p.Name] = Now;
            Alert("info", $"{p.Display} is {idleLabel}", p.Name);
        }
        // ---- nothing to do: idle behaviour (StoryColony.Idle.cs). It wanders the home area, one leg at a time, and real work
        // takes the leg back at once; with nowhere to go it holds where it is (never all pawns to the home cell).
        return IdleJob(p, live, "work", hour) ?? Hold(idleLabel, "idle", null, StoryCharacter.IdleSeconds(p));
    }

    // ---------------------------------------------------------------- back-off

    private readonly Dictionary<string, List<DateTimeOffset>> _fails = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Failures in a row this close together make a pawn rest before it picks again (no tight fail → pick → fail loop).</summary>
    public static readonly TimeSpan FailWindow = TimeSpan.FromSeconds(20), FailRest = TimeSpan.FromSeconds(15);
    public const int FailBurst = 3;

    /// <summary>A pawn whose last <see cref="FailBurst"/> jobs all failed within <see cref="FailWindow"/> holds a while (orders and needs still win).</summary>
    private ActiveJob? Resting(PawnState p, PawnLive live)
    {
        if (RestLeft(p) is not { } left) return null;
        // It rests on its feet: idle legs about the home area (StoryColony.Recreation.cs) for as long as the back-off lasts.
        return IdleJob(p, live, "rest", HourNow) ?? Hold("resting: the last jobs failed", "idle", null, (int)Math.Ceiling(left.TotalSeconds));
    }

    /// <summary>How long this pawn's back-off still lasts (null = not resting).</summary>
    private TimeSpan? RestLeft(PawnState p)
    {
        if (!_fails.TryGetValue(p.Name, out var f) || f.Count < FailBurst) return null;
        var last = f[^1];
        if (last - f[^FailBurst] > FailWindow || Now - last >= FailRest) return null;
        return FailRest - (Now - last);
    }

    private void NoteFailure(PawnState p)
    {
        if (!_fails.TryGetValue(p.Name, out var f)) _fails[p.Name] = f = new List<DateTimeOffset>();
        f.Add(Now);
        if (f.Count > FailBurst) f.RemoveRange(0, f.Count - FailBurst);
    }

    /// <summary>The rest after the n-th failure of one target: 30 s, 1 min, then 10 min.</summary>
    private static TimeSpan Backoff(int n) => n >= 3 ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(30 * Math.Max(1, n));

    // ---------------------------------------------------------------- needs

    private Stack? FoodIn(PawnLive live) =>
        World is { } w ? live.Inventory.FirstOrDefault(s => w.Data.Food(s.Id, s.Meta) != null && !Raw(w, s.Id)) : null;

    /// <summary>Raw meat is food but poor: pawns cook it first when they can (chicken can poison).</summary>
    private static bool Raw(IStoryWorld w, int id) => id is 319 or 349 or 363 or 365 or 411 or 423; // 349: raw fish (a catch is stored, cooked by bills)

    private IReadOnlyList<StoryEntity> Hostiles(PawnLive live, double r) =>
        Ents().Where(e => e.Kind == "mob" && e.Hostile && Math.Abs(e.X - live.X) <= r && Math.Abs(e.Z - live.Z) <= r && Math.Abs(e.Y - live.Y) <= 6).ToList();

    private ActiveJob? Urgent(PawnState p, PawnLive live)
    {
        int flee = StoryCharacter.FleeHp(p), eat = StoryCharacter.EatFood(p);
        if (live.Hp <= flee && Hostiles(live, 12).Count > 0)
        {
            var home = Home();
            var mob = Hostiles(live, 12)[0];
            // Away from the monster: home if it is the other way, else a dozen blocks straight away from it.
            P3 to = home is { } h && h.Dist(mob.X, mob.Y, mob.Z) > h.Dist(live.X, live.Y, live.Z) ? SpreadHome(p, live, h) ?? h
                : new P3((int)(live.X + Math.Sign(live.X - mob.X) * 12), (int)live.Y, (int)(live.Z + Math.Sign(live.Z - mob.Z) * 12));
            return Job("need", "go", $"fleeing a {mob.Name}", to, null, "need", null, "fleeing", j => j["dist"] = 1, xp: 0);
        }
        // Good food first, rotten flesh only when starving, a failed fetch backs off (StoryColony.Ledger.cs EatJob).
        if (live.Food <= eat || live.Hp <= flee && live.Food < 20) return EatJob(p, live);
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
        int pillar = PillarKeepKind(live); // its small stack of blocks to nerd-pole with stays (StoryColony.Reach.cs)
        var list = new List<Stack>();
        foreach (var s in live.Inventory)
        {
            if (s.Id == pillar) continue;
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
        if (Skipped("deliver:" + p.Name)) return null; // its last delivery failed: not again right away
        // The genre's way: it keeps what it gathers and goes on to the next target of that work; one trip to storage later
        // (inventory nearly full, that work done, its hours over). Live 11-04 (Fox): miners walked every block to the chest.
        if (!force && StaysGathering(p, live, after, src)) return null;
        var items = Deliverables(live);
        if (items.Count == 0) { _lastSource.Remove(p.Name); return null; }
        _lastSource.Remove(p.Name);
        // To the chests or the fallback chest; drops it hauled with no storage at all go to the drop spot (StoryColony.Hauling.cs).
        return DeliverJob(p, live, items, dumpOk: after && src == "drop");
    }

    /// <summary>After gathering work (a designation, a zone, a crop, fishing) the pawn keeps what it gathered while it goes on.</summary>
    private bool StaysGathering(PawnState p, PawnLive live, bool after, string? src) =>
        ContinuesMining(p, live) || after && src is "designation" or "zone" or "crop" or "fish" && KeepGathering(p, live);

    // ---------------------------------------------------------------- work

    /// <summary>The work types in the order this pawn tries them: priority 1 first; ties by column (or by skill first).</summary>
    internal IReadOnlyList<string> WorkOrder(PawnState p) =>
        WorkTypes.All.Where(t => p.Priorities.GetValueOrDefault(t) is >= 1 and <= 4)
            .OrderBy(t => p.Priorities[t])
            .ThenBy(t => Meta.TieBreak == "skill" ? -(p.Skills.GetValueOrDefault(t)?.Level ?? 0) : 0)
            .ThenBy(WorkTypes.Column).ToList();

    /// <summary>The pawn's work now, or null; what it saw is kept for the idle audit (StoryColony.IdleAudit.cs: it never searches itself).</summary>
    private ActiveJob? Work(PawnState p, PawnLive live)
    {
        Searches++;
        var outer = _blockedNotes;
        var notes = new List<string>(0);
        _blockedNotes = notes;
        Array.Clear(_passed);
        try
        {
            var job = WorkSearch(p, live);
            _looks[p.Name] = new Look(Now, job != null, notes, job == null ? NoReachNote() : null, _passed[(int)MarkPass.LeftToSurface] > 0);
            return job;
        }
        finally { _blockedNotes = outer; }
    }

    private ActiveJob? WorkSearch(PawnState p, PawnLive live)
    {
        // A craft work waits on right now (a missing tool, a farm's hoe, a water build's bucket) comes before its other work (Fox 11-05:
        // "Immediate requirements like that should always be at the top of the list for crafting"): StoryColony.CraftTasks.cs.
        if (ImmediateCraftJob(p, live) is { } now) return now;
        int riskChecked = 0;
        bool quarryWait = false;
        foreach (var t in WorkOrder(p))
        {
            // Within one priority, a drop about to despawn is hauled first (StoryColony.Hauling.cs).
            if (t != WorkTypes.Haul && p.Priorities.GetValueOrDefault(t) is var pr && pr != riskChecked && pr == p.Priorities.GetValueOrDefault(WorkTypes.Haul))
            {
                riskChecked = pr;
                if (HaulJob(p, live, riskOnly: true) is { } risky) return risky;
            }
            if (Candidate(t, p, live) is { } j) return j;
            if (t == WorkTypes.Mine) quarryWait = WaitingForQuarry(p, live);
        }
        return quarryWait ? Hold("waiting for the quarry's next layer", "working", s: 2, type: WorkTypes.Mine) : null;
    }

    private bool Skipped(string key) => _skip.TryGetValue(key, out var until) && until > Now;

    private bool Forbidden(P3 at)
    {
        var ix = Index();
        if (ix.ForbidCells.Contains(at)) return true;
        if (ix.ForbidCols.TryGetValue(ZoneCells.Key(at.X, at.Z), out var fy) && at.Y >= fy.Y1 && at.Y <= fy.Y2) return true;
        return false;
    }

    private bool ForbiddenEntity(int id) => Index().ForbidEntities.Contains(id);

    private bool Open(string key, string pawn, P3? at) =>
        Free(key, pawn) && !Skipped(key) && !Skipped(PawnKey(key, pawn)) && (at == null || !Forbidden(at.Value) && !NearHazard(at.Value, pawn));

    /// <summary>The nearest available job of one work type for this pawn, or null.</summary>
    internal ActiveJob? Candidate(string type, PawnState p, PawnLive live) =>
        CandidateSearch.TryGetValue(type, out var search) ? search(this, p, live) : null;

    /// <summary>Each work type's job search (static: nothing is allocated per pick).</summary>
    private static readonly Dictionary<string, Func<StoryColony, PawnState, PawnLive, ActiveJob?>> CandidateSearch = new(StringComparer.Ordinal)
    {
        [WorkTypes.Guard] = static (c, p, live) => c.GuardJob(p, live), // painted guard zones, then visible monsters at home
        [WorkTypes.Doctor] = static (c, p, live) => c.FeedJob(p, live),
        [WorkTypes.Cook] = static (c, p, live) => c.BillJob(p, live, WorkTypes.Cook),
        [WorkTypes.Craft] = static (c, p, live) => c.BillJob(p, live, WorkTypes.Craft) ?? c.SupplyJob(p, live), // the builds' own crafts (a bench to place, a 2×2 craft)
        [WorkTypes.Hunt] = static (c, p, live) => c.HuntJob(p, live),
        [WorkTypes.Fish] = static (c, p, live) => c.FishJob(p, live), // StoryColony.Fishing.cs
        [WorkTypes.Build] = static (c, p, live) => c.StorageSignRemovalJob(p, live) ?? c.VeinJob(p, live, WorkTypes.Build) ?? c.GhostJob(p, live) ?? c.DesignationJob(p, live, live.Block, WorkTypes.Build, "deconstruct", "dig", "deconstructing"),
        [WorkTypes.Farm] = static (c, p, live) => c.DesignationJob(p, live, live.Block, WorkTypes.Farm, "harvest", "harvest", "harvesting") ?? c.FarmZoneJob(p, live),
        [WorkTypes.Mine] = static (c, p, live) => c.VeinJob(p, live, WorkTypes.Mine) ?? c.DesignationJob(p, live, live.Block, WorkTypes.Mine, "mine", "dig", "mining") ?? c.ZoneJob(p, live.Block, WorkTypes.Mine, "mine", live),
        [WorkTypes.Chop] = static (c, p, live) => c.ChopJob(p, live),
        [WorkTypes.Haul] = static (c, p, live) => c.HaulingWork(p, live), // pickups, organization, and fresh chest contents
    };

    private ActiveJob? ChopJob(PawnState p, PawnLive live)
    {
        if (LeaveToSurfacePawn(p, live, WorkTypes.Chop)) { PassedMark(MarkPass.LeftToSurface, "chop", null); return null; } // a pawn up top takes the trees
        var from = live.Block;
        return DesignationJob(p, live, from, WorkTypes.Chop, "chop", "chop", "chopping a tree") ?? DesignationJob(p, live, from, WorkTypes.Chop, "cut", "dig", "cutting plants")
            ?? ZoneJob(p, from, WorkTypes.Chop, "chop", live);
    }

    /// <summary>A pawn that is hungry and has no food: food brought to it (carried, or fetched from storage).</summary>
    private ActiveJob? FeedJob(PawnState p, PawnLive live)
    {
        if (World is not { } w) return null;
        foreach (var other in Pawns.Values.Where(o => o != p))
        {
            var ol = Host.Live(other.Name);
            if (!Starving(w, other, ol)) continue;
            var key = "feed:" + other.Name;
            if (!Open(key, p.Name, null)) continue;
            var mine = FoodIn(live);
            var stored = mine == null ? FindStored(s => w.Data.Food(s.Id, s.Meta) != null && !Raw(w, s.Id), live.Block) : null;
            if (mine == null && stored == null) continue;
            var item = mine ?? stored!.Value.Stack;
            var j = Job(WorkTypes.Doctor, "give", $"feeding {other.Display}", ol.Block, key, "feed", other.Name, "working", f =>
            {
                f["to"] = other.Name; f["item"] = ItemName(item.Id, item.Meta); f["n"] = 3;
                if (stored is { } st) f["fetch"] = Fetch(st.Store.Chest, st.Stack, 3);
            }, xp: 30);
            if (stored is { } st2) j.Takes.Add((st2.Store.Chest, st2.Stack with { Count = Math.Min(3, st2.Stack.Count) }));
            return j;
        }
        return null;
    }

    /// <summary>Online, at or under its eating threshold, and carrying no food at all.</summary>
    private static bool Starving(IStoryWorld w, PawnState p, PawnLive live) =>
        live.Online && live.Food <= StoryCharacter.EatFood(p) && !live.Inventory.Any(s => w.Data.Food(s.Id, s.Meta) != null);

    /// <summary>The nearest hunt designation whose animal is in view.</summary>
    private ActiveJob? HuntJob(PawnState p, PawnLive live)
    {
        var from = live.Block;
        var ents = Ents().ToDictionary(e => e.Id);
        var d = Designations.Where(x => x.Kind == "hunt" && x.Entity is { } id && ents.ContainsKey(id) && Open("e:" + id, p.Name, null))
            .OrderBy(x => ents[x.Entity!.Value].Block.Dist(from)).FirstOrDefault();
        if (d == null) { PassedEntityMarks("hunt", ents); return null; }
        var e = ents[d.Entity!.Value];
        return Job(WorkTypes.Hunt, "attack", $"hunting a {e.Name}", e.Block, "e:" + e.Id, "designation", d.Id, "fighting", j => j["mob"] = e.Name, xp: 30);
    }

    /// <summary>
    /// Surface work (trees) for a pawn under ground (a quarry, a cave, a trench: natural ground over it) waits while another pawn that
    /// does that work stands up top: that one goes, and this one keeps to work down there or heads up by itself (live 11-04 story-1:
    /// pawns 5 to 8 blocks under the trees failed "no path … it's 5 blocks up" and walked far round under ground). With no pawn up top
    /// it still takes it (its route out digs short through the ground, the chop climbs out beside the trunk). Only a pawn up top that
    /// will take it counts: one idle now or at that work now, never one busy at other work (it may not come back to the trees for
    /// hours, while this one stood idle under ground with the trees open: "no reachable target").
    /// </summary>
    private bool LeaveToSurfacePawn(PawnState p, PawnLive live, string type)
    {
        if (World is not { } w || !live.Online || !w.Loaded(live.Block.X, live.Block.Z) || !StoryWander.Underground(w, live.Block)) return false;
        foreach (var o in Pawns.Values)
            if (o != p && SurfacePawnFor(w, o, type)) return true;
        return false;
    }

    /// <summary>Does that work, is up top (online, not stuck), and idle now or at that work now.</summary>
    private bool SurfacePawnFor(IStoryWorld w, PawnState o, string type)
    {
        if (o.Drafted || o.Priorities.GetValueOrDefault(type) is not (>= 1 and <= 4) || _stuck.ContainsKey(o.Name)) return false;
        if (!IdleNow(o) && !(_current.TryGetValue(o.Name, out var cur) && cur.Type == type)) return false;
        var ol = Host.Live(o.Name);
        return ol.Online && !StoryWander.Underground(w, ol.Block);
    }

    /// <summary>A block designation of <paramref name="kind"/> (nearest open first).</summary>
    private ActiveJob? DesignationJob(PawnState p, PawnLive live, P3 from, string type, string kind, string scriptKind, string label)
    {
        // A tall plant's two halves are one target (its lower half: breaking either drops both), leased once, so two pawns never split it
        // (live 11-03: "double_plant ... is busy: another hive bot is working on that part" when a mine and a cut box overlapped it).
        if (!Index().ByKind.TryGetValue(kind, out var list) || list.Count == 0) return null;
        bool digs = kind is "mine" or "deconstruct";
        // The dig takes its material's tool (StoryColony.Gear.cs): never a lost drop.
        var s = new MarkSearch(kind, digs, digs || kind is "cut" or "chop", World, live.Online ? Support(live.X, live.Y, live.Z).ToHashSet() : []);
        var best = NearestMark(p, live, from, list, s);
        if (best.D == null) return null;
        return MarkJob(p, live, type, scriptKind, label, list, best, s);
    }

    /// <summary>One designation search: its kind, whether it digs (and takes a tool), the world, the cells the pawn stands on.</summary>
    private readonly record struct MarkSearch(string Kind, bool Digs, bool Tooled, IStoryWorld? W, HashSet<P3> Mine);

    /// <summary>The best open mark: nearest first, higher cells a little earlier (top-down), its own floor last (it would have to step
    /// off first). None (default) when no mark is open and in reach.</summary>
    private (DesignationState D, string Key, P3 Target) NearestMark(PawnState p, PawnLive live, P3 from, List<(DesignationState D, string Key, P3 Target)> list,
        MarkSearch s)
    {
        HashSet<P3>? stoodOn = null;
        (DesignationState D, string Key, P3 Target) best = default;
        double bestScore = double.MaxValue;
        foreach (var x in list)
        {
            var at = x.Target;
            double score = at.Dist(from) - 0.25 * at.Y + (s.Mine.Contains(at) ? 1000 : 0);
            if (score >= bestScore) continue;
            if (!Open(x.Key, p.Name, null) || !MarkWorkable(p, live, x.D, at, s, ref stoodOn)) continue; // leased or resting, else why not
            best = x;
            bestScore = score;
        }
        return best;
    }

    /// <summary>Can the pawn work this mark now: not forbidden nor by a stuck spot, surface work out of caves, a dig only exposed and
    /// not under someone's feet (each passed over is noted for the idle audit), a tool for it (else the mark waits, said on the map, no
    /// failure).</summary>
    private bool MarkWorkable(PawnState p, PawnLive live, DesignationState d, P3 at, MarkSearch s, ref HashSet<P3>? stoodOn)
    {
        if (d.OreOrigin != null) return false; // vein work retains its parent's work type and access rules
        if (d.Auto && Meta.TempBlocks?.Any(t => t.Designation == d.Id && (t.RetryAfter > Now || t.DeferredReason != null)) == true) return false;
        if (MarkPassedOver(p.Name, d, at, s, ref stoodOn) is { } pass) { PassedMark(pass, s.Kind, at); return false; }
        // No tool for its material (carried or stored): the target waits, said on the map, no failure.
        if (s.Tooled && s.W is { } w && PlanTool(p, live, s.Kind == "chop" ? ChopBlockAt(w, at, d.Blocks) : w.Block(at.X, at.Y, at.Z).Id)
            is { Wait: { } why }) { NoteWait(d, why); return false; }
        return true;
    }

    /// <summary>Why the pawn can't take this mark now (null = it can).</summary>
    private MarkPass? MarkPassedOver(string pawn, DesignationState d, P3 at, MarkSearch s, ref HashSet<P3>? stoodOn)
    {
        if (Forbidden(at)) return MarkPass.Forbidden;
        if (NearHazard(at, pawn)) return MarkPass.StuckSpot;
        if (s.W is not { } w) return null;
        if (CaveLeftAlone(w, d, at)) return MarkPass.Cave;
        return s.Digs ? DigPassedOver(w, pawn, at, ref stoodOn) : null;
    }

    /// <summary>
    /// Cutting and harvesting the colony chose (<see cref="DesignationState.Auto"/>) is surface work: a plant in a cave by it waits by
    /// design (live 11-04: pawns went into caves). An owner's mark is ordinary work wherever it is (live 11-04 story-1: five pawns idle a
    /// whole evening on "cut 91,55,463: in a cave"): the route, reach and stuck rules decide, and the idle audit says why if they can't.
    /// </summary>
    internal static bool CaveLeftAlone(IStoryWorld w, DesignationState d, P3 at) =>
        d is { Auto: true, Kind: "cut" or "harvest" } && StoryWander.Underground(w, at);

    /// <summary>Only what a pawn can get at now: a buried cell waits (no failure) until the one over or beside it is gone. Never the
    /// block under someone else's feet (the client refuses it: "won't dig: someone is standing on it").</summary>
    private MarkPass? DigPassedOver(IStoryWorld w, string pawn, P3 at, ref HashSet<P3>? stoodOn)
    {
        if (!Exposed(w, at)) return MarkPass.Buried;
        return (stoodOn ??= StoodOnByOthers(pawn)).Contains(at) ? MarkPass.Underfoot : null;
    }

    private ActiveJob? MarkJob(PawnState p, PawnLive live, string type, string scriptKind, string label, List<(DesignationState D, string Key, P3 Target)> list,
        (DesignationState D, string Key, P3 Target) best, MarkSearch s)
    {
        var (d, key, target) = best;
        bool chest = s.W is { } world && ManualChestRemoval(d, world, target);
        if (chest)
        {
            var preparation = PrepareChestRemoval(p, live, d, target);
            if (!preparation.Ready)
            {
                if (preparation.Job == null) _skip[key] = Now + TimeSpan.FromSeconds(2);
                return preparation.Job;
            }
        }
        // A temporary block's cleanup always digs from beside it (live 11-06: "won't dig the dirt I'm standing on … the drop below is too far").
        P3? stand = s.Digs && s.W != null && (s.Mine.Contains(target) || IsTempMark(d))
            ? StandBeside(s.W, target, c => c == target || list.Any(o => o.Target == c)) : null;
        NoteWait(d, null);
        var tool = s.Tooled && s.W != null ? PlanTool(p, live,
            s.Kind == "chop" ? ChopBlockAt(s.W, target, d.Blocks) : s.W.Block(target.X, target.Y, target.Z).Id) : null;
        string kind = s.Kind;
        return WithTool(tool, Job(type, scriptKind, d.Label ?? label, target, key, "designation", d.Id, "working",
            j =>
            {
                if (chest) j["deconstruct"] = true;
                FillStorageSignJob(d, j);
                if (d.Replant != null) j["replant"] = d.Replant;
                if (kind == "chop") ChopFill(j, d.Blocks);
                if (stand is { } st) j["stand"] = st.ToString(); // it stands on its target: step off to the side first
            }, xp: kind == "chop" ? 40 : 20));
    }

    /// <summary>A designation's lease (and rest) key: its entity, or its block (a tall plant's lower half).</summary>
    internal string DesignationKey(DesignationState d) =>
        d.Entity is { } e ? "e:" + e : d.At is { } at ? "b:" + BlockTarget(d.Kind, at) : "d:" + d.Id;

    /// <summary>When a resting (failed) designation is tried again, or null when it is open now.</summary>
    internal DateTimeOffset? RetryAt(DesignationState d) => _skip.TryGetValue(DesignationKey(d), out var until) && until > Now ? until : null;

    /// <summary>The lower half of a tall plant (1.12.2 double_plant: sunflower, lilac, tall grass, large fern, rose bush, peony) at or
    /// under <paramref name="at"/>; any other block is its own base.</summary>
    internal P3 PlantBase(P3 at)
    {
        if (World is not { } w || w.Block(at.X, at.Y, at.Z).Id != StoryBlocks.DoublePlant) return at;
        return w.Block(at.X, at.Y - 1, at.Z).Id == StoryBlocks.DoublePlant ? at.Add(0, -1, 0) : at;
    }

    /// <summary>
    /// What the pawn script needs to finish a tree from an empty inventory: the version's log item names (to tell whether any came
    /// down), its planks name (logs → planks in the 2x2 grid: clutch and pillar blocks for the logs up high) and the tree's logs (where
    /// the rest of it is).
    /// </summary>
    private void ChopFill(JsonObject j, IReadOnlyList<P3>? logs)
    {
        if (World?.Data is { } d)
        {
            j["wood"] = new JsonArray(StoryBlocks.Logs.Where(d.ItemExists).Select(id => (JsonNode)d.Item(id).Name).ToArray());
            j["wood_blocks"] = new JsonArray(StoryBlocks.Logs.Where(d.BlockExists).Select(id => (JsonNode)d.Block(id).Name).ToArray());
            if (d.ItemExists(5)) j["planks"] = d.Item(5).Name;
        }
        if (logs is { Count: > 0 }) j["logs"] = new JsonArray(logs.OrderBy(l => l.Y).Take(48).Select(l => (JsonNode)l.ToString()).ToArray());
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
            var cells = z.Cells;
            if (z.Kind == "chop")
                list.AddRange(StoryBlocks.TreesIn(w, z.Area, max: 256).Where(b => cells.Contains(b.X, b.Z)).Take(64).Select(b => (b, (string?)null, "chop")));
            else ScanZoneCells(w, z, list);
        }
        _zoneScan[z.Id] = (Now, list);
        return list;
    }

    /// <summary>A mine or farm zone's columns, up to 256 tasks (300k blocks scanned at most).</summary>
    private static void ScanZoneCells(IStoryWorld w, ZoneState z, List<(P3, string?, string)> list)
    {
        long scanned = 0;
        int y1 = Math.Max(0, z.Area.Y1);
        foreach (var (x, zz) in z.Cells.Sorted())
        {
            if (list.Count >= 256) continue;
            for (int y = y1; y <= Math.Min(w.Height - 1, z.Area.Y2); y++)
            {
                if (++scanned > 300_000) return;
                ZoneCellTask(w, z.Kind, x, y, zz, list);
            }
        }
    }

    /// <summary>One block's task: an ore in a mine zone; a ripe crop (harvest) or bare farmland (sow above it) in a farm zone.</summary>
    private static void ZoneCellTask(IStoryWorld w, string kind, int x, int y, int zz, List<(P3, string?, string)> list)
    {
        var (id, _) = w.Block(x, y, zz);
        if (kind == "mine" && StoryBlocks.Ores.Contains(id)) list.Add((new P3(x, y, zz), null, "mine"));
        else if (kind == "farm")
        {
            if (StoryBlocks.Crop(w, x, y, zz) is { Ripe: true } crop) list.Add((new P3(x, y, zz), crop.Replant, "harvest"));
            else if (id == 60 && w.Block(x, y + 1, zz).Id == 0) list.Add((new P3(x, y + 1, zz), null, "sow"));
        }
    }

    private ActiveJob? ZoneJob(PawnState p, P3 from, string type, string zoneKind, PawnLive? live = null)
    {
        foreach (var z in Zones.Where(z => z.Kind == zoneKind).OrderBy(z => z.Area.Center.Dist(from)))
        {
            if (IsQuarry(z)) { if (live != null && QuarryJob(p, live, z) is { } dig) return dig; continue; } // StoryColony.Quarry.cs
            // Ores only with the pickaxe they need (carried or stored): never one dug for nothing.
            ToolPlan? Tool((P3 Pos, string? Replant, string Kind) t) => live != null ? PlanTool(p, live, DigBlockAt(t.Pos)) : null;
            var t = ZoneTasks(z).Where(t => Open("b:" + t.Pos, p.Name, t.Pos) && Tool(t) is not { Wait: not null } && HarvestAgrees(p.Name, t.Kind, t.Pos)).OrderBy(t => t.Pos.Dist(from)).FirstOrDefault();
            if (t.Kind == null) continue;
            return zoneKind == "chop"
                ? WithTool(Tool(t), Job(type, "chop", "chopping a tree (zone)", t.Pos, "b:" + t.Pos, "zone", z.Id,
                    fill: j => ChopFill(j, World is { } tw ? StoryBlocks.TreeFrom(tw, t.Pos) : null), xp: 40))
                : WithTool(Tool(t), Job(type, "dig", "mining ore (zone)", t.Pos, "b:" + t.Pos, "zone", z.Id));
        }
        return null;
    }

    private ActiveJob? FarmZoneJob(PawnState p, PawnLive live)
    {
        var from = live.Block;
        foreach (var z in Zones.Where(z => z.Kind == "farm").OrderBy(z => z.Area.Center.Dist(from)))
        {
            if (z.Bills != null || z.Plant != null) { if (GrowJob(p, live, z) is { } grow) return grow; continue; } // a growing zone (StoryColony.Farming.cs)
            foreach (var t in ZoneTasks(z).Where(t => Open("b:" + t.Pos, p.Name, t.Pos) && HarvestAgrees(p.Name, t.Kind, t.Pos)).OrderBy(t => t.Pos.Dist(from)))
                if (FarmZoneTask(live, z, t, from) is { } j) return j;
        }
        return null;
    }

    /// <summary>A plain farm zone's task: harvest a ripe crop (replanted), or sow bare farmland with seeds carried or fetched.</summary>
    private ActiveJob? FarmZoneTask(PawnLive live, ZoneState z, (P3 Pos, string? Replant, string Kind) t, P3 from)
    {
        if (t.Kind == "harvest")
            return Job(WorkTypes.Farm, "harvest", "harvesting (farm zone)", t.Pos, "b:" + t.Pos, "crop", z.Id, "working",
                j => { if (t.Replant != null) j["replant"] = t.Replant; }, xp: 15);
        if (World is not { } w) return null;
        var seed = live.Inventory.FirstOrDefault(s => StoryCategories.For(w.Data).LeafOf(s.Id, s.Meta) == "plants_seeds" && s.Id != 372);
        (StorageState Store, Stack Stack)? stored = seed == null ? FindStored(s => StoryCategories.For(w.Data).LeafOf(s.Id, s.Meta) == "plants_seeds" && s.Id != 372, from) : null;
        if (seed == null && stored == null) return null;
        var item = seed ?? stored!.Value.Stack;
        var j2 = Job(WorkTypes.Farm, "place", "sowing", t.Pos, "b:" + t.Pos, "sow", z.Id, "working", f =>
        {
            f["block"] = ItemName(item.Id, item.Meta);
            if (stored is { } st) f["fetch"] = Fetch(st.Store.Chest, st.Stack, 16);
        }, xp: 10);
        if (stored is { } st2) j2.Takes.Add((st2.Store.Chest, st2.Stack with { Count = Math.Min(16, st2.Stack.Count) }));
        return j2;
    }

    // ---------------------------------------------------------------- done / failed

    private void Done(PawnState p, ActiveJob j, string? note)
    {
        if (j.DigCells != null) { DigBatchDone(p, j); return; }
        DeferredCraftStorage(p, j, note);
        SettleStock(j);
        GearTakesEnded(j, done: true);
        ChestsReached(p, j);
        if (j.Source == "need") _needFails.Remove(p.Name);
        LearnFrom(p, j);
        _fails.Remove(p.Name);
        // A build batch always ends done, also when its first cell was "not now": that cell's soft count ends when it is built (CellDone).
        if (j.Lease != null) { _leaseFails.Remove(j.Lease); if (j.Source != "ghost") _softFails.Remove(j.Lease); }
        Reached(p, j); // it isn't stuck; a rescue's blocks arrived (StoryColony.Rescue.cs)
        if (WorkTypes.Known(j.Type)) _lastWork[p.Name] = j.Type; // what it gathers next keeps its loot (KeepGathering)
        if (j.Type == WorkTypes.Mine && j.Source == "zone" && j.Kind == "dig" && j.SourceId is { } zone)
            _lastQuarryWork[p.Name] = (zone, Now);
        HauledDone(j); // every drop a collect trip leased: its haul designation is done (StoryColony.EntityMarks.cs)
        if (j.Source == "deliver") _leaseFails.Remove("deliver:" + p.Name);
        if (DoneBySource.TryGetValue(j.Source, out var done)) done(this, p, j, note);
    }

    /// <summary>The chests' records follow a finished job: what it took out, what it put in.</summary>
    private void SettleStock(ActiveJob j)
    {
        foreach (var (chest, s) in j.Takes) if (StorageAt(chest) is { } st) TakeContents(st, s.Id, s.Meta, s.Count);
        foreach (var (chest, stacks) in j.Puts) if (StorageAt(chest) is { } st) foreach (var s in stacks) AddContents(st, s);
    }

    /// <summary>The job's experience; a new level is said.</summary>
    private void LearnFrom(PawnState p, ActiveJob j)
    {
        if (WorkTypes.Known(j.Type) && j.Xp > 0 && StoryCharacter.Learn(p, j.Type, j.Xp, HourNow) > 0)
            Alert("info", $"{p.Display} is now level {p.Skills[j.Type].Level} at {j.Type}", p.Name);
    }

    /// <summary>What a finished job does to where it came from, by its source.</summary>
    private static readonly Dictionary<string, Action<StoryColony, PawnState, ActiveJob, string?>> DoneBySource = new(StringComparer.Ordinal)
    {
        ["designation"] = static (c, p, j, note) => c.DesignationDone(p, j, note),
        ["ghost"] = static (c, p, j, note) => c.BatchDone(p, j, note),
        ["bill"] = static (c, p, j, note) => c.BillDone(p, j, note),
        ["furnace"] = static (c, p, j, note) => c.BillDone(p, j, note),
        ["look"] = static (c, _, j, _) => c.LookDone(j), // the chest's record from what the pawn saw (StoryColony.StockCheck.cs)
        ["order"] = static (c, p, j, note) => c.OrderDone(p, j, ok: true, note),
        ["station"] = static (c, p, j, _) => c.SupplyDone(p, j),
        ["hand"] = static (c, p, j, _) => c.SupplyDone(p, j),
        ["gear"] = static (c, p, j, note) => c.GearEnded(p, j, ok: true, note),
        ["policy"] = static (c, p, j, note) => c.GearEnded(p, j, ok: true, note),
        ["fish"] = static (c, _, j, note) => c.FishDone(j, note), // the catch counts toward the zone (StoryColony.Fishing.cs)
        ["zone"] = static (c, _, j, _) => c.ZoneWorkDone(j),
        ["crop"] = static (c, _, j, _) => c.ZoneWorkDone(j),
        ["sow"] = static (c, _, j, _) => c.ZoneWorkDone(j),
        ["site"] = static (c, _, j, _) => c.SiteVisited(j), // a build out of view: a pawn went to load it (StoryColony.Builds.cs)
    };

    /// <summary>A zone's job done: its scans start over (a quarry rescans at once); a harvest counts toward the zone's planting bills.</summary>
    private void ZoneWorkDone(ActiveJob j)
    {
        if (j.Source == "crop") GrowHarvested(j);
        _zoneScan.Remove(j.SourceId ?? "");
        _quarryScan.Remove(j.SourceId ?? ""); // StoryColony.Quarry.cs
        QuarryPlaceDone(j); // a seal it placed is the quarry's own (StoryColony.QuarrySweep.cs)
        _growScan.Remove(j.SourceId ?? "");
    }

    private void DesignationDone(PawnState p, ActiveJob j, string? note)
    {
        if (Designations.FirstOrDefault(d => d.Id == j.SourceId) is not { } des) return;
        bool partial = note != null && note.StartsWith("partial", StringComparison.OrdinalIgnoreCase);
        // Part of the tree came down and the rest still stands: the designation stays, now on its lowest log left, rested a moment for
        // this pawn (Fox 11-04 tree farming: the rest is chopped too, never dropped with "0/N" or half a tree).
        if (partial && ChopRest(des) is { } rest)
        {
            Alert("info", $"{p.Display} took what it could reach of the tree at {des.At}: {Short(note!)}; the rest ({rest.Count} log(s)) stays designated", p.Name);
            des.At = rest[0];
            des.Rev = Touch();
            _skip[PawnKey("b:" + rest[0], p.Name)] = Now + PartialRest;
            return;
        }
        _chopPartials.Remove(des.Id);
        Designations.Remove(des);
        Removed("designation", des.Id);
        // Part of the target was out of reach: what could be reached came down; the rest is said, not silently dropped.
        if (partial) Alert("info", $"{p.Display} took what it could reach of the {des.Kind} at {des.At?.ToString() ?? "#" + des.Entity}: {Short(note!)}", p.Name);
    }

    /// <summary>
    /// What of a designation's target the colony's view still sees after the pawn said done (null = it's done, or the view can't tell:
    /// not loaded). chop: the trunk's base log; cut: the plant; harvest: the ripe crop; mine, deconstruct: the block; haul, hunt: the entity.
    /// </summary>
    private string? StillThere(ActiveJob j, IStoryWorld? view = null)
    {
        if ((view ?? World) is not { } w || Designations.FirstOrDefault(d => d.Id == j.SourceId) is not { } d) return null;
        if (d.Entity is { } eid)
            return d.Kind is "haul" or "hunt" && w.Entities().FirstOrDefault(e => e.Id == eid) is { } e ? $"the {e.Name} is still there" : null;
        if (d.At is not { } at || !w.Loaded(at.X, at.Z)) return null;
        return BlockStillThere(w, d.Kind, at, w.Block(at.X, at.Y, at.Z).Id);
    }

    private static string? BlockStillThere(IStoryWorld w, string kind, P3 at, int id) => kind switch
    {
        "chop" when StoryBlocks.Logs.Contains(id) => $"the tree's base log at {at} is still standing",
        "cut" when StoryBlocks.Plants.Contains(id) || StoryBlocks.Leaves.Contains(id) => $"the {BlockIdName(w, id)} at {at} is still there",
        "harvest" when StoryBlocks.Crop(w, at.X, at.Y, at.Z) is { Ripe: true } => $"the ripe {BlockIdName(w, id)} at {at} is still there",
        "mine" or "deconstruct" when id != 0 && !StoryBlocks.Liquids.Contains(id) => $"the {BlockIdName(w, id)} at {at} is still there",
        _ => null,
    };

    /// <summary>A block's id name in the world's version, else its number.</summary>
    private static string BlockIdName(IStoryWorld w, int id) => w.Data.BlockExists(id) ? w.Data.Block(id).Name : id.ToString();

    /// <summary>
    /// What is left of a designation's target after <paramref name="pawn"/> said done, as that pawn's own body sees it when its view holds
    /// the cell (its connection carried the server's answer to its dig), else as the colony (main) sees it. Live 11-03: "reported done,
    /// but the tallgrass is still there" ×54: the check read main's view, whose copy of the block change can come a moment after the
    /// pawn's; main's view lagging behind is no failure.
    /// </summary>
    private string? Left(ActiveJob j, string pawn)
    {
        IStoryWorld? own = null;
        try { own = Host.ViewOf(pawn); } catch (Exception) { /* no view: main's */ }
        if (own != null && j.At is { } at && own.Loaded(at.X, at.Z))
        {
            var left = StillThere(j, own);
            return left == null ? null : left + " (in its own view)";
        }
        return StillThere(j);
    }

    /// <summary>Failures after which a designation shows as failed (it stays, with its reason).</summary>
    public const int DesignationFailLimit = 3;

    private static string Short(string s) => s.Length > 160 ? s[..159] + "…" : s;

    private readonly Dictionary<string, int> _softFails = new(StringComparer.Ordinal);

    /// <summary>Transient refusals of a target in a row before they count as a failure.</summary>
    public const int SoftLimit = 5;
    /// <summary>How long a target rests after a transient refusal (someone stands on it, another bot is at it, it can't be reached yet).</summary>
    public static readonly TimeSpan SoftRest = TimeSpan.FromSeconds(15);

    /// <summary>A refusal that says "not now" rather than "can't be done": unreachable from here yet, someone standing on it, a hive
    /// claim (another body is at it), not loaded.</summary>
    internal static bool Transient(string why) =>
        why.Contains("can't reach", StringComparison.OrdinalIgnoreCase) || why.Contains("can't see/reach", StringComparison.OrdinalIgnoreCase)
        || why.Contains("someone is standing on it", StringComparison.OrdinalIgnoreCase) || why.Contains("is busy", StringComparison.OrdinalIgnoreCase)
        || why.StartsWith("busy", StringComparison.OrdinalIgnoreCase) || why.Contains("a hive claim", StringComparison.OrdinalIgnoreCase)
        || why.Contains("is not loaded", StringComparison.OrdinalIgnoreCase)
        // A placement out of reach from where the pawn got to, or with someone in the cell: it walks again next time.
        || why.Contains("couldn't get closer", StringComparison.OrdinalIgnoreCase) || why.Contains("standing in it", StringComparison.OrdinalIgnoreCase)
        || why.Contains("I'm in the way of", StringComparison.OrdinalIgnoreCase); // the client's own words for a pawn in the cell

    private void Failed(PawnState p, ActiveJob j, string? note)
    {
        if (ChestRemovalWait(j, note)) return;
        if (HarvestGone(p, j, note)) return;
        if (ForeverBillShortage(j, note)) return;
        // A batch has already accounted for every attempted cell. Its untouched tail is simply released.
        if (j.DigCells != null && (j.DigReported || note == "dig batch interrupted")) { GearTakesEnded(j, done: false); return; }
        // Older/single-cell callers can report the whole placement as failed when only its support is pending.
        if (j.Source == "ghost" && j.Kind == "place" && NoFaceYet(note ?? ""))
        {
            BatchFailed(p, j, note!);
            return;
        }
        var why = Reason(note);
        // Someone else picked its drops up on the way: nothing failed (StoryColony.Hauling.cs), and the log says so, not "failed".
        bool gone = DropsGone(j, why);
        if (gone) DropsTakenLog(p, j, why); else LogFailure(p, j, why);
        NoteQuarryMiss(j, why);
        GearOrNeedFailed(p, j, why);
        if (gone) return;
        if (TempCleanupFailed(p, j, why)) return; // a temporary block's cleanup: not ours any more, or out of reach (StoryColony.TempBlocks.cs)
        MarkFullChests(j, why); // first: a delivery's "A full; chest at B didn't open" is both
        // The chest's record was wrong (corrected), or the tool's trouble (none, broke, couldn't hold): re-planned at once, never a
        // target failure, back-off or rest (StoryColony.Ledger.cs).
        // A furnace with nothing to collect yet, or a load left without fuel (a refuel follows): no failure (StoryColony.Refuel.cs).
        if (MissingContainer(p, note) || StaleFetch(p, j, note) || ToolFailure(note) || ContainerUnreachable(p, note) || FurnaceNotReady(j, why)) return;
        NoteDropMiss(p, j, why); // the spot it missed them at: several misses there and its drops rest, whatever their ids (StoryColony.DropSpots.cs)
        // A build job's failure is its first open cell's, sorted like a cell's report: only one about the cell counts (StoryColony.Builds.cs).
        if (j.Source == "ghost" && BatchMissed(p, j, why)) return;
        // Unreachable from where this pawn stands, a stuck pawn, a monster it couldn't find: its own rest, not the target's failure
        // (live 11-04: one pawn in a hole failed grass after grass for everyone; StoryColony.Rescue.cs).
        if (PawnOnly(p, j, why)) return;
        if (SoftFailure(p, j, why)) return;
        if (!PlanningError(why)) NoteFailure(p); // stock and ledger errors are the plan's, not the pawn's: no resting (StoryColony.Ledger.cs)
        BackOffTarget(p, j);
        FailedBySource(p, j, why);
    }

    /// <summary>What a failed job does to where it came from, by its source.</summary>
    private void FailedBySource(PawnState p, ActiveJob j, string why)
    {
        switch (j.Source)
        {
            case "designation":
                DesignationFailed(p, j, why);
                break;
            case "ghost":
                BatchFailed(p, j, why);
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

    /// <summary>The failure's reason as kept and shown (trimmed, at most 120 characters).</summary>
    private static string Reason(string? note)
    {
        var why = string.IsNullOrWhiteSpace(note) ? "failed" : note.Trim();
        return why.Length > 120 ? why[..119] + "…" : why;
    }

    private void LogFailure(PawnState p, ActiveJob j, string why) =>
        _log.LogInformation("story {Colony}: {Pawn} {Kind} {At} failed: {Why}", Id, p.Name, j.Kind, j.At?.ToString() ?? "", why);

    /// <summary>"collect 203,64,391: its drops were gone when it got there (picked up by someone else): no failure (…)".</summary>
    private void DropsTakenLog(PawnState p, ActiveJob j, string why) =>
        _log.LogInformation("story {Colony}: {Pawn} {Kind} {At}: its drops were gone when it got there (picked up by someone else): no failure ({Why})",
            Id, p.Name, j.Kind, j.At?.ToString() ?? "", why);

    private void GearOrNeedFailed(PawnState p, ActiveJob j, string why)
    {
        GearTakesEnded(j, done: false);
        if (j.Source is "gear" or "policy") GearEnded(p, j, ok: false, why);
        if (j.Source == "need") NeedFailed(p, j); // a food fetch that failed: that pawn waits a little, that chest is skipped
    }

    /// <summary>A put that failed for a full chest: the chest is full now (no plan sends more there until it is seen again).</summary>
    private void MarkFullChests(ActiveJob j, string why)
    {
        foreach (var (chest, _) in j.Puts)
            if (StorageAt(chest) is { } st && why.Contains("full", StringComparison.OrdinalIgnoreCase)) { st.FreeSlots = 0; st.Rev = Touch(); }
    }

    /// <summary>
    /// Not now, not a failure (live 11-03: "can't reach dirt" ×46, "someone is standing on it" ×6, a tall plant "busy" ×11 all counted,
    /// alerted and backed off as failures): a designation's or a build cell's target rests a little, no fail count, no alert; only when
    /// it keeps happening (SoftLimit times in a row) does it count as a real failure. "can't reach" rests it for this pawn only: from
    /// where another pawn stands it may well be in reach. True = handled (no failure).
    /// </summary>
    private bool SoftFailure(PawnState p, ActiveJob j, string why)
    {
        // A build job's "not now" was sorted by its cell already (BatchMissed): one that gets here counts. A quarry dig is
        // softened too (StoryColony.Quarry.cs): its layer waits for each cell, so a 10-minute back-off held the whole quarry.
        bool notNow = j.Source == "designation" ? Transient(why) : j.Source != "ghost" && QuarryDigTransient(j, why);
        if (!notNow || j.Lease == null || Unreachable(why)) return false;
        int soft = _softFails[j.Lease] = _softFails.GetValueOrDefault(j.Lease) + 1;
        if (soft < SoftLimit)
        {
            _skip[ReachOnly(why) ? PawnKey(j.Lease, p.Name) : j.Lease] = Now + SoftRest;
            return true;
        }
        _softFails.Remove(j.Lease);
        return false;
    }

    /// <summary>Not this target again right away; one that keeps failing (an unreachable drop) rests longer each time (up to 10 minutes).</summary>
    private void BackOffTarget(PawnState p, ActiveJob j)
    {
        if (j.Lease != null)
        {
            int n = _leaseFails[j.Lease] = _leaseFails.GetValueOrDefault(j.Lease) + 1;
            _skip[j.Lease] = Now + Backoff(n);
        }
        if (j.Source == "deliver")
        {
            int n = _leaseFails["deliver:" + p.Name] = _leaseFails.GetValueOrDefault("deliver:" + p.Name) + 1;
            _skip["deliver:" + p.Name] = Now + Backoff(n);
        }
    }

    /// <summary>Kept on the map with why (the browser shows it); retried after a rest, until it is done or cancelled.</summary>
    private void DesignationFailed(PawnState p, ActiveJob j, string why)
    {
        if (Designations.FirstOrDefault(d => d.Id == j.SourceId) is not { } des) return;
        des.Fails++;
        des.Reason = why;
        if (des.Fails >= DesignationFailLimit) des.Status = "failed";
        des.Rev = Touch();
        if (des.Fails == 1 || des.Fails % DesignationFailLimit == 0)
            Alert("warn", $"{des.Kind} at {des.At?.ToString() ?? "#" + des.Entity} failed {des.Fails} times: {why} (kept on the map, "
                + $"retried in {Backoff(des.Fails).TotalMinutes:0} min; cancel it to drop it)", p.Name);
    }
}
