namespace MineClaw.Hive.Story;

/// <summary>
/// The storage record kept honest (docs/web/story-mode.md "Bills see the chests"). Live 11-04: "Make Stick (from any planks) suspended:
/// no planks within 32 blocks" with planks in the chest beside the table: the colony's record of a chest only changed with its own
/// pawns' takes and puts, so what a player put in was invisible. Now
/// <list type="bullet">
/// <item>a body's look inside a chest (the bot's memory, any body, any reason) newer than the record replaces it;</item>
/// <item>before a bill waits for a missing ingredient, a pawn looks into the chests within its radius that nobody has looked into (or
/// not for 10 minutes): a "look" job;</item>
/// <item>every change of the stock retries the waiting bills at once (not on a timer);</item>
/// <item>the reason says what was looked at: "no planks: checked 6 chests within 32 of the bench (2 not seen yet)".</item>
/// </list>
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>A look older than this doesn't vouch for a chest's record (a player may have used it since).</summary>
    public static readonly TimeSpan SeenStale = TimeSpan.FromMinutes(10);

    private bool _stockChanged;
    private readonly Dictionary<P3, DateTimeOffset> _looked = new();
    private readonly Dictionary<P3, DateTimeOffset> _seenAsked = new();

    /// <summary>
    /// Each storage's record is replaced by a body's look newer than the record's last change (checked at most every 5 s a chest).
    /// True when a record changed (the waiting bills are then retried).
    /// </summary>
    internal bool RefreshSeen(IEnumerable<StorageState> list, bool force = false)
    {
        bool any = false;
        IReadOnlyDictionary<P3, DateTimeOffset>? looks = null;
        bool asked = force; // a forced look reads its chest itself
        foreach (var st in list.ToList())
        {
            if (!force && SeenAskedLately(st)) continue;
            if (!asked)
            {
                asked = true;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { looks = Host.ChestLooks(); } catch (Exception) { looks = null; } // once a sweep, and only when a chest is due
                finally { StoryPerf.ChestLooks.Note(t0); } // every chest look in the bot's db, under the colony lock: on the perf line
            }
            if (RefreshOneSeen(st, force, looks)) any = true;
        }
        if (any) _stockChanged = true;
        return any;
    }

    private bool SeenAskedLately(StorageState st) => _seenAsked.TryGetValue(st.Chest, out var asked) && Now - asked < TimeSpan.FromSeconds(5);

    /// <summary>
    /// No body looked into the chest (or the other half of a double chest: any chest beside it counts) after its record: reading the
    /// chest memory would change nothing. Live 11-10: every haul search read all 374 chests' memories every 5 s, 3,782 reads a minute
    /// under the colony lock.
    /// </summary>
    private static bool NoNewerLook(StorageState st, IReadOnlyDictionary<P3, DateTimeOffset> looks)
    {
        var record = st.SeenAt is { } s0 && st.KnownAt is { } k0 ? (s0 > k0 ? s0 : k0) : st.SeenAt ?? st.KnownAt;
        foreach (var (dx, dz) in ChestCells)
            if (looks.TryGetValue(st.Chest.Add(dx, 0, dz), out var at) && (record is not { } r || at > r)) return false;
        return true;
    }

    private static readonly (int Dx, int Dz)[] ChestCells = [(0, 0), (1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>One storage: a body's newer look replaces its record (unless the record changed since). True when it did.</summary>
    private bool RefreshOneSeen(StorageState st, bool force, IReadOnlyDictionary<P3, DateTimeOffset>? looks = null)
    {
        if (!force && SeenAskedLately(st)) return false;
        bool changed = World is { } w && ReconcileStorage(st, w);
        _seenAsked[st.Chest] = Now;
        if (st.Missing) return changed;
        if (looks != null && NoNewerLook(st, looks)) return changed;
        (IReadOnlyList<Stack> Contents, DateTimeOffset At)? seen;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { seen = Host.SeenChest(st.Chest); }
        catch (Exception) { return changed; }
        finally { StoryPerf.SeenChest.Note(t0); } // the bot's chest memory (its db), read under the colony lock: on the perf line
        if (seen is not { } s || st.SeenAt is { } s0 && s.At <= s0) return changed;
        if (st.KnownAt is { } k && s.At <= k) return changed; // the record's own last change is newer than that look
        st.SeenAt = s.At;
        st.Rev = Touch();
        st.Contents = s.Contents.GroupBy(x => (x.Id, x.Meta)).Select(g => new Stack(g.Key.Id, g.Key.Meta, g.Sum(x => x.Count))).ToList();
        Recount(st);
        return true;
    }

    /// <summary>Waiting bills are retried once the stock changed (a deposit, a take, a look that corrected a record).</summary>
    private void RetryOnStockChange()
    {
        if (!_stockChanged) return;
        _stockChanged = false;
        RetrySuspended();
    }

    /// <summary>
    /// Before a bill waits for <paramref name="item"/>: a pawn looks into the nearest chest within the bill's radius of the bench that
    /// nobody has looked into for <see cref="SeenStale"/> (each chest at most every 2 minutes). Null when every one is known.
    /// </summary>
    private ActiveJob? LookJob(PawnState p, string work, WorkbenchState bench, BillState bill, string item)
    {
        if (!Host.KnowsChests) return null;
        var due = Storages.Where(s => ChestOpen(s.Chest) && !LookRested(s.Chest) && s.Chest.Dist(bench.Pos) <= bill.Radius && (s.SeenAt is not { } seen || Now - seen > SeenStale)
                && (!_looked.TryGetValue(s.Chest, out var at) || Now - at > TimeSpan.FromMinutes(2)) && Open("look:" + s.Chest, p.Name, null))
            .OrderBy(s => s.Chest.Dist(bench.Pos)).FirstOrDefault();
        if (due == null) return null;
        _looked[due.Chest] = Now;
        return Job(work, "look", $"checking the chest at {due.Chest} for {item}", due.Chest, "look:" + due.Chest, "look", due.Chest.ToString(), "working",
            xp: 2);
    }

    /// <summary>Looks at a chest that failed because the pawn couldn't get to it, in a row (any pawn), and until when nobody looks again.</summary>
    private readonly Dictionary<P3, (int Misses, DateTimeOffset Until)> _lookRest = new();

    /// <summary>Looks into this chest (the haul upkeep's and the bills') rest: one failed lately for want of a way there.</summary>
    private bool LookRested(P3 chest) => _lookRest.TryGetValue(chest, out var r) && r.Until > Now;

    /// <summary>
    /// A look that failed because the pawn couldn't get to the chest ("can't reach chest at …: no walkable spot that sees it"): nobody
    /// looks into that chest again for 2, then 5, then 10 minutes (<see cref="ChestRest"/>), whoever tries. Live 11-10: "look … failed:
    /// can't reach chest" over and over, the same chests, each pawn in turn (each pawn's own rest only kept that one pawn away, and a
    /// look is upkeep: nothing waits on it). Deliveries and fetches keep the per-pawn rest alone (another pawn may well get there).
    /// </summary>
    private void LookMissed(ActiveJob j, string? note)
    {
        if (j is not { Source: "look", Kind: "look", At: { } at } || note == null || CantReach.Match(note) is not { Success: true } m) return;
        var chest = StorageAt(at)?.Chest ?? at;
        var said = new P3(int.Parse(m.Groups["x"].Value), int.Parse(m.Groups["y"].Value), int.Parse(m.Groups["z"].Value));
        if ((StorageAt(said)?.Chest ?? said) != chest) return; // the way to it, not the chest
        int n = _lookRest.TryGetValue(chest, out var r) ? r.Misses + 1 : 1;
        _lookRest[chest] = (n, Now + ChestRest(n));
    }

    /// <summary>A look done: the chest's record from what the pawn saw (its memory of the open window).</summary>
    internal void LookDone(ActiveJob j)
    {
        if (j.At is not { } at || StorageAt(at) is not { } st) return;
        _lookRest.Remove(st.Chest);
        if (RefreshSeen([st], force: true)) RetryOnStockChange();
        else if (st.SeenAt is not { } seen || Now - seen > SeenStale) { st.SeenAt = Now; st.Rev = Touch(); } // looked: not again right away
    }

    /// <summary>"checked 6 chests within 32 of the bench (2 not seen yet)" for a missing ingredient's reason.</summary>
    private string CheckedText(WorkbenchState bench, int radius)
    {
        var near = Storages.Where(s => !s.Missing && s.Chest.Dist(bench.Pos) <= radius).ToList();
        int unseen = Host.KnowsChests ? near.Count(s => s.SeenAt is not { } seen || Now - seen > SeenStale) : 0;
        return near.Count == 0
            ? $"no storage chest within {radius} of the bench"
            : $"checked {near.Count} chest{(near.Count == 1 ? "" : "s")} within {radius} of the bench" + (unseen > 0 ? $" ({unseen} not looked into lately)" : "");
    }

    /// <summary>Other pawns' pockets holding the ingredient (they store it soon): "Ann carries 4".</summary>
    private string? PocketText(RecipeInput input, PawnState? except)
    {
        var who = Pawns.Values.Where(q => q != except).Select(q => (q, n: Host.Live(q.Name).Inventory.Where(s => input.Options.Any(o => o.Matches(s.Id, s.Meta))).Sum(s => s.Count)))
            .Where(t => t.n > 0).Select(t => $"{t.q.Display} carries {t.n}").ToList();
        return who.Count == 0 ? null : string.Join(", ", who);
    }
}
