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
        foreach (var st in list.ToList())
            if (RefreshOneSeen(st, force)) any = true;
        if (any) _stockChanged = true;
        return any;
    }

    /// <summary>One storage: a body's newer look replaces its record (unless the record changed since). True when it did.</summary>
    private bool RefreshOneSeen(StorageState st, bool force)
    {
        if (!force && _seenAsked.TryGetValue(st.Chest, out var asked) && Now - asked < TimeSpan.FromSeconds(5)) return false;
        _seenAsked[st.Chest] = Now;
        (IReadOnlyList<Stack> Contents, DateTimeOffset At)? seen;
        try { seen = Host.SeenChest(st.Chest); }
        catch (Exception) { return false; }
        if (seen is not { } s || st.SeenAt is { } s0 && s.At <= s0) return false;
        st.SeenAt = s.At;
        st.Rev = Touch();
        if (st.KnownAt is { } k && s.At <= k) return false; // the record's own last change is newer than that look
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
        var due = Storages.Where(s => s.Chest.Dist(bench.Pos) <= bill.Radius && (s.SeenAt is not { } seen || Now - seen > SeenStale)
                && (!_looked.TryGetValue(s.Chest, out var at) || Now - at > TimeSpan.FromMinutes(2)) && Open("look:" + s.Chest, p.Name, null))
            .OrderBy(s => s.Chest.Dist(bench.Pos)).FirstOrDefault();
        if (due == null) return null;
        _looked[due.Chest] = Now;
        return Job(work, "look", $"checking the chest at {due.Chest} for {item}", due.Chest, "look:" + due.Chest, "look", due.Chest.ToString(), "working",
            xp: 2);
    }

    /// <summary>A look done: the chest's record from what the pawn saw (its memory of the open window).</summary>
    internal void LookDone(ActiveJob j)
    {
        if (j.At is not { } at || StorageAt(at) is not { } st) return;
        if (RefreshSeen([st], force: true)) RetryOnStockChange();
        else if (st.SeenAt is not { } seen || Now - seen > SeenStale) { st.SeenAt = Now; st.Rev = Touch(); } // looked: not again right away
    }

    /// <summary>"checked 6 chests within 32 of the bench (2 not seen yet)" for a missing ingredient's reason.</summary>
    private string CheckedText(WorkbenchState bench, int radius)
    {
        var near = Storages.Where(s => s.Chest.Dist(bench.Pos) <= radius).ToList();
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
