using MineClaw.Scripting.Story;

namespace MineClaw.Hive.Story;

/// <summary>
/// Removing a pawn (docs/web/story-mode.md "Adding and removing pawns"): its job and claims go back at once, its bed is free, then it
/// takes what it carries to the colony's storage (by the filters; what no chest accepts stays on it and the logout's own deposit takes
/// it, with an alert) and its script ends. Then the host logs it out and releases the account (whitelist removal through main), and it
/// leaves the colony. An offline pawn leaves at once.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>Pawns on their way out: since when, and whether their last job (the end) is done.</summary>
    private readonly Dictionary<string, (DateTimeOffset Since, bool Done)> _leaving = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A leaving pawn is released after this even if it never finished storing its things.</summary>
    public static readonly TimeSpan LeaveTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Is this pawn on its way out?</summary>
    public bool IsLeaving(string pawn) { lock (_lock) return _leaving.ContainsKey(pawn); }

    /// <summary>The pawns that stay (not leaving).</summary>
    public int Staying { get { lock (_lock) return Pawns.Keys.Count(k => !_leaving.ContainsKey(k)); } }

    /// <summary>Starts removing a pawn: its job, claims and bed go back now. False when it is unknown or already leaving.</summary>
    internal bool Leave(string pawn)
    {
        lock (_lock)
        {
            if (!Pawns.TryGetValue(pawn, out var p) || _leaving.ContainsKey(p.Name)) return false;
            _leaving[p.Name] = (Now, false);
            p.Queue.Clear();
            p.Drafted = false;
            Supersede(p, "leaving the colony");
            foreach (var k in _leases.Where(l => l.Value.Owner.Equals(p.Name, StringComparison.OrdinalIgnoreCase)).Select(l => l.Key).ToList()) _leases.Remove(k);
            _spots.Remove(p.Name);
            _restSpot.Remove(p.Name);
            foreach (var bed in Meta.Beds.Where(kv => kv.Value.Equals(p.Name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList()) Meta.Beds.Remove(bed);
            _bedScan = null;
            p.Rev = Touch();
            return true;
        }
    }

    /// <summary>A leaving pawn's job: store what it carries, then end its script.</summary>
    private ActiveJob? LeavingJob(PawnState p, PawnLive live)
    {
        if (!_leaving.TryGetValue(p.Name, out var l)) return null;
        if (!l.Done && Deliver(p, live, force: true) is { } store) return store;
        if (!l.Done && Deliverables(live).Count > 0)
            Alert("warn", $"{p.Display} leaves with things no storage accepts: they go with its logout's deposit", p.Name);
        _leaving[p.Name] = (l.Since, true);
        return Job("idle", "end", "leaving the colony", null, null, "none", null, "idle", xp: 0);
    }

    /// <summary>The leaving pawns that are ready to go now (their things stored, or offline, or out of time).</summary>
    private List<string> ReadyToGo()
    {
        var list = new List<string>();
        foreach (var (name, l) in _leaving)
            if (l.Done || Now - l.Since > LeaveTimeout || !Host.Live(name).Online) list.Add(name);
        return list;
    }

    /// <summary>Releases the account (logout, whitelist removal) and drops the pawn from the colony.</summary>
    private void Depart(string pawn)
    {
        ResetSpeed([pawn]); // back to normal speed (StoryColony.Speed.cs)
        string? err;
        try { err = Host.ReleasePawn(pawn, $"story {Id}: removed from the colony"); } catch (Exception ex) { err = ex.Message; }
        lock (_lock)
        {
            _leaving.Remove(pawn);
            if (!Pawns.Remove(pawn, out var p)) return;
            if (_current.Remove(pawn, out var cur)) Release(cur);
            StoryPorts.Unbind(pawn, this);
            _online.Remove(pawn);
            Removed("pawn", p.Name); // the delta's removed.pawns
            Alert("info", $"{p.Display} left the colony" + (err != null ? $" (its account: {err})" : ""), null);
        }
        Save(force: true);
    }
}
