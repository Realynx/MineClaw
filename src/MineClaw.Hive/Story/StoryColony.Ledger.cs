using System.Text.RegularExpressions;

namespace MineClaw.Hive.Story;

/// <summary>
/// The storage ledger corrected from what a pawn saw (docs/web/story-mode.md "Tools are colony stock"). Live 11-04: "no wooden pickaxe
/// in the chest at 218,64,400" and "no stone hoe in the chest at 223,64,400 (it had: dirt x333, egg x24, …)" over and over: the
/// colony's record said the item was there, the pawn opened the chest and it wasn't (someone took it), and the next pick planned the
/// same fetch again. A fetch that finds its item missing now fixes the record at once (the item is gone from that chest; an empty
/// chest is empty; a full listing replaces the record), the job isn't counted as failed, and the next pick plans from real stock: another
/// chest, a pawn with one to spare, or a wait with why and an auto bill. The same fetch is never planned again until the record shows
/// the item there again (a deposit).
/// </summary>
public sealed partial class StoryColony
{
    // The client's take refusals (MinecraftClient.Windows EmptyTakeReason, MinecraftClient.Gear WithdrawMatchingAsync):
    // "no {item} in the chest at x,y,z: it is empty" | "no {item} in the chest at x,y,z (it had: a x3, b x2[, +N more])" | "no {item} in the chest at x,y,z".
    private static readonly Regex MissingTake = new(
        @"no (?<item>[^()]+?)(?: \([^)]*\))? in the (?:chest|trapped_chest|container) at (?<x>-?\d+),(?<y>-?\d+),(?<z>-?\d+)(?<empty>: it is empty)?(?: \(it had: (?<had>[^)]*)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A failed job whose note says a fetched item wasn't in its chest: the chest's record is corrected from what the pawn saw. True =
    /// it was that (callers then don't count it as a failure: the next pick plans from real stock).
    /// </summary>
    internal bool StaleFetch(PawnState p, ActiveJob j, string? note)
    {
        if (note == null || World is not { } w || MissingTake.Match(note) is not { Success: true } m) return false;
        var at = new P3(int.Parse(m.Groups["x"].Value), int.Parse(m.Groups["y"].Value), int.Parse(m.Groups["z"].Value));
        if (StorageAt(at) is not { } st) return false;
        var name = m.Groups["item"].Value.Trim();
        var item = w.Data.ItemByNameWithDamage(name);
        bool changed;
        if (m.Groups["empty"].Success) { changed = st.Contents.Count > 0; st.Contents.Clear(); st.Enchanted.Clear(); }
        else if (item is { } it) changed = ForgetTaken(w, st, it.Item.Id, m.Groups["had"]);
        else return false;
        if (changed) Recount(st); else st.Rev = Touch();
        _log_StaleFetch(p, name, at);
        return true;
    }

    private static readonly Regex Planning = new(
        @"no .+ (within \d+ blocks|in the (chest|container|trapped_chest)|in (its |the )?(inventory|bag|storage))|reserved for|not enough|missing \d|nothing to craft|can't craft|no recipe",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A failure that was the plan's (the stock it counted on wasn't there), not the pawn's: it never makes a pawn rest.</summary>
    internal static bool PlanningError(string? why) => why != null && Planning.IsMatch(why);

    private static readonly Regex ToolTrouble = new(
        @"needs (?:a|an) [a-z ]*?(?:pickaxe|axe|shovel|hoe|sword)\b|needs shears|lose the drop|couldn't hold|no [a-z ()_]+ carried|tool broke|pickaxe broke|axe broke|shovel broke|hoe broke",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A tool's trouble, not the target's (Fox, 11-04: "it shouldn't be touching it"): the strict dig refused for want of the tool, the
    /// tool couldn't be held, it broke. Never a target failure, never a back-off, never a pawn's rest: the next pick plans again.
    /// </summary>
    internal static bool ToolFailure(string? why) => why != null && ToolTrouble.IsMatch(why);

    // ---------------------------------------------------------------- eating (live 11-04: "eat failed: no rotten flesh in the chest … it
    // is empty" ×784 in 10 minutes: the need re-fetched the same missing food every 0.8 s, bypassing every back-off)

    /// <summary>Food that harms: rotten flesh, spider eye, poisonous potato. Eaten only when starving and nothing else exists.</summary>
    internal static bool BadFood(int id) => id is 367 or 375 or 394;

    /// <summary>Starving: hunger this low (the body's own reflex then eats even rotten flesh).</summary>
    public const int StarvingFood = 6;

    private readonly Dictionary<string, int> _needFails = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The item wasn't there: the chest's record loses it; a full listing (no "+N more") is what the chest holds, and the
    /// record becomes it. True when the record changed.</summary>
    private static bool ForgetTaken(IStoryWorld w, StorageState st, int itemId, Group had)
    {
        bool changed = st.Contents.RemoveAll(c => c.Id == itemId) > 0;
        st.Enchanted.RemoveAll(e => e.Id == itemId);
        if (had.Success && !had.Value.Contains("more", StringComparison.Ordinal) && Listing(w, had.Value) is { } seen)
        {
            changed |= !seen.SequenceEqual(st.Contents);
            st.Contents = seen;
        }
        return changed;
    }

    /// <summary>
    /// A hungry pawn's meal: food it carries (not rotten flesh, unless starving with nothing else), else the best stored food (most
    /// nourishing first; never a chest that just failed this pawn; never while its last fetch's back-off runs), else, starving, the bad
    /// food stored (the body's reflex eats it), else nothing: one alert, and it waits for stock (no eating attempts in a loop).
    /// </summary>
    internal ActiveJob? EatJob(PawnState p, PawnLive live)
    {
        if (World is not { } w) return null;
        var d = w.Data;
        bool Edible(Stack s) => d.Food(s.Id, s.Meta) != null;
        bool starving = live.Food <= StarvingFood;
        if (live.Inventory.Any(s => Edible(s) && !BadFood(s.Id)))
            return Job("need", "eat", "eating", null, null, "need", null, "eating", xp: 0);
        if (starving && live.Inventory.Any(s => Edible(s)))
            return Hold("starving: only rotten food left (eaten when it must)", "eating", s: 10, type: "need"); // the reflex eats it
        if (!Skipped("need:" + p.Name))
        {
            var got = BestFood(p, live, s => Edible(s) && !BadFood(s.Id) && !Raw(w, s.Id)) ?? BestFood(p, live, s => Edible(s) && !BadFood(s.Id))
                ?? (starving ? BestFood(p, live, Edible) : null);
            if (got is { } g) return FoodFetchJob(g);
        }
        else return null; // waiting out a failed fetch: no attempt
        NoFoodAlert(p);
        return null;
    }

    /// <summary>Fetching stored food (4): eaten at once, or (rotten flesh, starving) held for the body's reflex.</summary>
    private ActiveJob FoodFetchJob((StorageState Store, Stack Stack) g)
    {
        bool bad = BadFood(g.Stack.Id);
        var j = bad
            ? Job("need", "hold", "fetching rotten food (starving)", null, null, "need", null, "eating", f => { f["fetch"] = Fetch(g.Store.Chest, g.Stack, 4); f["s"] = 5; }, xp: 0)
            : Job("need", "eat", "fetching food", null, null, "need", null, "eating", f => f["fetch"] = Fetch(g.Store.Chest, g.Stack, 4), xp: 0);
        j.Takes.Add((g.Store.Chest, g.Stack with { Count = Math.Min(4, g.Stack.Count) }));
        return j;
    }

    /// <summary>Hungry with no food in storage: said at most every 5 minutes.</summary>
    private void NoFoodAlert(PawnState p)
    {
        if (_idleAlert.TryGetValue("food:" + p.Name, out var last) && Now - last <= TimeSpan.FromMinutes(5)) return;
        _idleAlert["food:" + p.Name] = Now;
        Alert("warn", $"{p.Display} is hungry and there is no food in storage", p.Name);
    }

    /// <summary>The most nourishing stored food matching <paramref name="pick"/> (then the nearest), skipping chests that failed this pawn.</summary>
    private (StorageState Store, Stack Stack)? BestFood(PawnState p, PawnLive live, Func<Stack, bool> pick)
    {
        var d = World!.Data;
        return Storages.Where(s => !Skipped($"needchest:{p.Name}@{s.Chest}") && ChestOpen(s.Chest))
            .SelectMany(s => s.Contents.Where(pick).Select(c => (Store: s, Stack: c)))
            .OrderByDescending(x => d.Food(x.Stack.Id, x.Stack.Meta)?.Hunger ?? 0).ThenBy(x => x.Store.Chest.Dist(live.Block))
            .Select(x => ((StorageState, Stack)?)x).FirstOrDefault();
    }

    /// <summary>A food fetch failed (the chest was empty, or out of reach): this pawn waits 15 s, then 60 s on the next failure (unless
    /// it gets food some other way), and that chest is skipped for it for 2 minutes.</summary>
    internal void NeedFailed(PawnState p, ActiveJob j)
    {
        if (j.Takes.Count == 0) return;
        int n = _needFails[p.Name] = _needFails.GetValueOrDefault(p.Name) + 1;
        _skip["need:" + p.Name] = Now + (n <= 1 ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(60));
        foreach (var (chest, _) in j.Takes) _skip[$"needchest:{p.Name}@{chest}"] = Now + TimeSpan.FromMinutes(2);
    }

    // ---------------------------------------------------------------- unreachable containers (live 11-04: 334 of 462 failed searches in 5
    // minutes were "can't reach chest at 218,64,400 … no path to use": the rest went to the designation, not the chest, so each pawn tried
    // that chest again for every fetch, each an A* run to its budget)

    private static readonly Regex CantReach = new(@"can't reach (?:the )?(?<what>[a-z_ ]+?) at (?<x>-?\d+),(?<y>-?\d+),(?<z>-?\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The client's "chest at 216,64,400 didn't open (blocked above, or protected?)": the pawn got there and the lid stayed shut
    /// (a block or an ocelot on either half). Live 11-06: ×241 in 7 hours, and the chest was never left out of the plans.</summary>
    private static readonly Regex DidntOpen = new(@"(?<what>chest|trapped chest|crafting table|furnace) at (?<x>-?\d+),(?<y>-?\d+),(?<z>-?\d+) didn't open",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The pawn whose job is being picked (its unreachable chests are left out of its plans), or null.</summary>
    private string? _pickPawn;
    /// <summary>Gear claims, read once per pick (StoredUnits asks for each block a pawn could dig).</summary>
    private Dictionary<(P3 Chest, int Id, int Meta), int>? _pickClaims;
    private readonly Dictionary<(string Pawn, P3 Chest), int> _chestFails = new();
    /// <summary>Chests no pawn gets to: their contents aren't stock until the time is up (or someone gets there).</summary>
    private readonly Dictionary<P3, DateTimeOffset> _unreachable = new();
    private readonly Dictionary<P3, DateTimeOffset> _unreachableSaid = new();
    /// <summary>A pawn's rest from a chest it couldn't reach, until when (by key, not a string: asked per chest per stack per pick).</summary>
    private readonly Dictionary<(string Pawn, P3 Chest), DateTimeOffset> _chestSkip = new();

    private bool ChestSkipped(string pawn, P3 chest) => _chestSkip.TryGetValue((pawn, chest), out var until) && until > Now;

    /// <summary>A chest's rest for one pawn after its n-th "can't reach": 2, then 5, then 10 minutes.</summary>
    public static TimeSpan ChestRest(int n) => TimeSpan.FromMinutes(n <= 1 ? 2 : n == 2 ? 5 : 10);

    /// <summary>No pawn gets to this chest (now).</summary>
    internal bool ChestUnreachable(P3 chest) => _unreachable.TryGetValue(chest, out var until) && until > Now;

    /// <summary>The chest is in this pick's plans: nothing shuts its lid (StoryColony.Storage.cs), someone gets there, and the pawn picking
    /// hasn't just failed to.</summary>
    internal bool ChestOpen(P3 chest) => !ChestUnreachable(chest) && LidBlocked(chest) == null
        && (_pickPawn == null || _chestSkip.Count == 0 || !ChestSkipped(_pickPawn, chest));

    /// <summary>
    /// A job failed because the pawn couldn't reach a chest or a bench (not its target): that pawn leaves the chest out of every plan for
    /// a while (2, 5, 10 minutes as it keeps failing), a bench likewise; when every pawn in game has failed it, the colony hears it once
    /// and its contents aren't stock. True = it was that (never the target's failure, never a rest).
    /// </summary>
    internal bool ContainerUnreachable(PawnState p, string? note)
    {
        if (note == null) return false;
        var m = CantReach.Match(note);
        bool shut = !m.Success && (m = DidntOpen.Match(note)).Success;
        if (!m.Success) return false;
        var at = new P3(int.Parse(m.Groups["x"].Value), int.Parse(m.Groups["y"].Value), int.Parse(m.Groups["z"].Value));
        if (Benches.FirstOrDefault(b => b.Pos == at) is { } bench)
        {
            int nb = _chestFails[(p.Name, at)] = _chestFails.GetValueOrDefault((p.Name, at)) + 1;
            _skip[PawnKey("bench:" + bench.Pos, p.Name)] = Now + ChestRest(nb);
            return true;
        }
        if (StorageAt(at) is not { } st) return false;
        var chest = st.Chest;
        int n = _chestFails[(p.Name, chest)] = _chestFails.GetValueOrDefault((p.Name, chest)) + 1;
        _chestSkip[(p.Name, chest)] = Now + ChestRest(n);
        var online = Pawns.Values.Where(o => Host.Live(o.Name).Online).ToList();
        if (online.Count > 0 && online.All(o => ChestSkipped(o.Name, chest))) NoPawnGetsIn(chest, shut);
        return true;
    }

    /// <summary>Every pawn in game failed this chest: its contents aren't stock for 10 minutes; the colony hears it once (per 30 minutes).</summary>
    private void NoPawnGetsIn(P3 chest, bool shut)
    {
        _unreachable[chest] = Now + TimeSpan.FromMinutes(10);
        MaterialsChanged(); // its contents aren't stock any more
        if (_unreachableSaid.TryGetValue(chest, out var said) && Now - said <= TimeSpan.FromMinutes(30)) return;
        _unreachableSaid[chest] = Now;
        Alert("warn", shut ? $"Chest at {chest} won't open for the pawns (a block or a cat on top of it?)" : $"Chest at {chest} is unreachable — pawns can't get to it");
    }

    /// <summary>A job that used chests finished: they are reachable (for that pawn, and so for the colony) again.</summary>
    internal void ChestsReached(PawnState p, ActiveJob j)
    {
        foreach (var chest in j.Takes.Select(t => t.Chest).Concat(j.Puts.Select(x => x.Chest)).Distinct())
        {
            var key = StorageAt(chest)?.Chest ?? chest;
            _chestFails.Remove((p.Name, key));
            _chestSkip.Remove((p.Name, key));
            if (_unreachable.Remove(key)) MaterialsChanged();
        }
    }

    private bool _purged;

    /// <summary>
    /// Once per process (after a load or a deploy): designations whose failures were the tool's or the record's (not the target's) start
    /// clean: no fail count, no rest, open again.
    /// </summary>
    internal void PurgeToolFailuresOnce()
    {
        if (_purged) return;
        _purged = true;
        foreach (var d in Designations)
        {
            bool tool = d.Reason is { } r && (ToolFailure(r) || PlanningError(r) || MissingTake.IsMatch(r));
            if (!tool && d.Status != "waiting") continue;
            var key = DesignationKey(d);
            _skip.Remove(key);
            _leaseFails.Remove(key);
            _softFails.Remove(key);
            if (tool) { d.Fails = 0; d.Reason = null; }
            d.Status = "open";
            d.Rev = Touch();
        }
    }

    // ---------------------------------------------------------------- bench throughput

    private DateTimeOffset _benchAlertAt = DateTimeOffset.MinValue, _benchCheckAt = DateTimeOffset.MinValue;

    /// <summary>
    /// One pawn works a bench at a time (the genre's rule). With several runnable bills on the only bench of its kind and a crafter
    /// idle, the colony hears it (at most every 10 minutes): "12 bills waiting on 1 crafting table — place another to craft in parallel".
    /// </summary>
    internal void BenchLoadAlert()
    {
        // Looked at every 30 s at most (it counts stock per bill: live 11-04, every state poll did it).
        if (World is not { } w || Now - _benchAlertAt < TimeSpan.FromMinutes(10) || Now - _benchCheckAt < TimeSpan.FromSeconds(30)) return;
        _benchCheckAt = Now;
        foreach (var kind in new[] { "crafting_table", "furnace" })
        {
            if (BenchBacklog(w, kind) is not { } waiting) continue;
            _benchAlertAt = Now;
            Alert("info", $"{waiting} bills waiting on 1 {(kind == "furnace" ? "furnace" : "crafting table")} — place another to {(kind == "furnace" ? "smelt" : "craft")} in parallel");
            return;
        }
    }

    /// <summary>The runnable bills (4 or more) waiting on the only bench of a kind while a crafter for it is idle; else null.</summary>
    private int? BenchBacklog(IStoryWorld w, string kind)
    {
        var benches = Benches.Where(b => b.Kind == kind).ToList();
        if (benches.Count != 1) return null;
        int waiting = BillsAndTasks(benches[0]).Count(b => !b.Paused && b.SuspendedReason == null && StoryRecipes.Get(w.Data, b.Recipe) is { } r && Wanted(b, r));
        if (waiting < 4) return null;
        var work = kind == "furnace" ? WorkTypes.Cook : WorkTypes.Craft;
        bool idleCrafter = Pawns.Values.Any(p => p.Priorities.GetValueOrDefault(work) is >= 1 and <= 4
            && Host.Live(p.Name).Online && (!_current.TryGetValue(p.Name, out var cur) || cur.Source == "none"));
        return idleCrafter ? waiting : null;
    }

    /// <summary>"dirt x333, egg x24" as stacks (null when a name doesn't resolve: the record then keeps the rest as it was).</summary>
    private static List<Stack>? Listing(IStoryWorld w, string had)
    {
        var list = new List<Stack>();
        foreach (var part in had.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int x = part.LastIndexOf(" x", StringComparison.Ordinal);
            if (x < 0 || !int.TryParse(part[(x + 2)..], out var n)) return null;
            if (w.Data.ItemByNameWithDamage(part[..x].Trim()) is not { } it) return null;
            list.Add(new Stack(it.Item.Id, Math.Max(0, it.Damage), n));
        }
        return list;
    }

    private readonly Dictionary<string, DateTimeOffset> _staleSaid = new(StringComparer.Ordinal);

    private void _log_StaleFetch(PawnState p, string item, P3 at)
    {
        var key = item + "@" + at;
        if (_staleSaid.TryGetValue(key, out var last) && Now - last < TimeSpan.FromMinutes(10)) return;
        _staleSaid[key] = Now;
        Alert("info", $"{p.Display} found no {item} in the chest at {at}: the colony's record is corrected", p.Name);
    }
}
