using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    private DateTimeOffset _storageSignsAt = DateTimeOffset.MinValue;

    /// <summary>Only physically attached signs control a chest. Missing text or an unloaded column is not proof of removal.</summary>
    internal void ReconcileStorageSigns(bool force = false)
    {
        if (!force && Now - _storageSignsAt < TimeSpan.FromSeconds(1) || World is not { } w) return;
        _storageSignsAt = Now;
        var observed = w.Signs().GroupBy(s => s.At).ToDictionary(g => g.Key, g => g.Last());
        foreach (var sign in observed.Values)
            if (StoryStorageKeywords.AttachedChest(w, sign.At) is { } chest && StoryStorageKeywords.Parse(sign.Lines, w.Data) != null)
            {
                // A new half can join registered storage between stock refreshes. Canonicalize it before registration,
                // or the same inventory gets an unrestricted old record beside the newly labelled record.
                foreach (var storage in Storages.Where(s => s.Chest == chest || s.DoubleWith == chest || DoubleHalf(s.Chest, w) == chest).ToList())
                    ReconcileStorage(storage, w);
                if (StorageAt(chest) == null) Set(chest, null, null, null);
            }
        foreach (var storage in Storages.ToList())
        {
            var signs = new List<StorageSignState>();
            foreach (var old in storage.Signs)
                if (!w.Loaded(old.At.X, old.At.Z) || w.Block(old.At.X, old.At.Y, old.At.Z).Id is 63 or 68 && !observed.ContainsKey(old.At))
                    signs.Add(old);
            foreach (var sign in observed.Values)
                if (StoryStorageKeywords.AttachedChest(w, sign.At) is { } chest && (chest == storage.Chest || chest == storage.DoubleWith)
                    && StoryStorageKeywords.Parse(sign.Lines, w.Data) is { } filter)
                    signs.Add(new(sign.At, sign.Lines.ToArray(), filter.Allow, filter.Deny));
            signs = signs.OrderBy(s => s.At.X).ThenBy(s => s.At.Y).ThenBy(s => s.At.Z).ToList();
            bool changed = SignSignature(storage.Signs) != SignSignature(signs);
            if (changed) { storage.Signs = signs; StorageChanged(storage); }
            if (storage.PendingFilter == null) continue;
            // A new/edited label is a newer in-world instruction. Do not erase it for an older UI request.
            bool superseded = signs.Any(s => !storage.SignRemovals.Any(r => r.At == s.At && r.Lines.SequenceEqual(s.Lines)))
                || storage.SignRemovals.Any(r => observed.TryGetValue(r.At, out var seen)
                    && (!r.Lines.SequenceEqual(seen.Lines) || w.Loaded(r.Chest.X, r.Chest.Z) && StoryStorageKeywords.AttachedChest(w, r.At) != r.Chest));
            if (superseded)
            {
                CancelStorageChange(storage);
                Alert("info", $"Storage edit at {storage.Chest} cancelled because its sign changed");
                continue;
            }
            bool gone = storage.SignRemovals.All(r => w.Loaded(r.At.X, r.At.Z) && w.Block(r.At.X, r.At.Y, r.At.Z).Id is not (63 or 68));
            if (!gone || signs.Count != 0) continue;
            storage.Filter = storage.PendingFilter;
            storage.PendingFilter = null;
            if (storage.PendingPriority != null) storage.Priority = storage.PendingPriority;
            storage.PendingPriority = null;
            FinishSignMarks(storage);
            if (storage.PendingUnregister) { Storages.Remove(storage); Removed("storage", storage.Chest.ToString()); }
            storage.PendingUnregister = false;
            StorageChanged(storage);
        }
        PublishStorageSignRules(w, observed);
    }

    private static string SignSignature(IEnumerable<StorageSignState> signs) =>
        string.Join("|", signs.Select(s => s.At + ":" + string.Join("\n", s.Lines)));

    private void StorageChanged(StorageState storage)
    {
        storage.Rev = Touch();
        _resort = null;
        // A running delivery selected before the sign appeared must choose again.
        foreach (var (pawn, job) in _current.ToList())
            if (job.Puts.Any(p => (p.Chest == storage.Chest || p.Chest == storage.DoubleWith)
                && p.Stacks.Any(s => !Accepts(storage, s.Id, s.Meta))) && Pawns.TryGetValue(pawn, out var p))
                Supersede(p, "storage filter changed");
        WakeIdle();
    }

    private static StorageFilter CloneFilter(StorageFilter filter) => new() { Allow = [.. filter.Allow], Deny = [.. filter.Deny] };

    private void RequestStorageFilter(StorageState storage, StorageFilter filter)
    {
        storage.PendingUnregister = false;
        if (storage.Signs.Count == 0) { storage.Filter = CloneFilter(filter); StorageChanged(storage); return; }
        storage.PendingFilter = CloneFilter(filter);
        foreach (var sign in storage.Signs)
        {
            if (storage.SignRemovals.Any(r => r.At == sign.At)) continue;
            UnprotectCells([sign.At]); // an old design/preserve snapshot must not replace the label we were asked to remove
            var mark = Designations.FirstOrDefault(d => d.Kind == "deconstruct" && d.At == sign.At);
            if (mark == null)
            {
                mark = new DesignationState { Id = NextId("d"), Kind = "deconstruct", At = sign.At,
                    Label = "removing storage sign", Created = Now, Rev = Touch() };
                Designations.Add(mark);
            }
            var chest = World is { } w ? StoryStorageKeywords.AttachedChest(w, sign.At) : null;
            storage.SignRemovals.Add(new(sign.At, chest ?? storage.Chest, sign.Lines.ToArray(), mark.Id));
        }
        StorageChanged(storage);
    }

    private void FinishSignMarks(StorageState storage)
    {
        var ids = storage.SignRemovals.Select(r => r.Designation).ToHashSet();
        foreach (var (pawn, job) in _current.ToList())
            if (job.Source == "designation" && job.SourceId != null && ids.Contains(job.SourceId) && Pawns.TryGetValue(pawn, out var p))
                Supersede(p, "storage sign request ended");
        foreach (var d in Designations.Where(d => ids.Contains(d.Id)).ToList()) { Designations.Remove(d); Removed("designation", d.Id); }
        storage.SignRemovals.Clear();
    }

    private void CancelStorageChange(StorageState storage)
    {
        storage.PendingFilter = null;
        storage.PendingPriority = null;
        storage.PendingUnregister = false;
        FinishSignMarks(storage);
        StorageChanged(storage);
    }

    private void CancelStorageSignMark(DesignationState mark)
    {
        foreach (var storage in Storages.Where(s => s.SignRemovals.Any(r => r.Designation == mark.Id)).ToList()) CancelStorageChange(storage);
    }

    private void FillStorageSignJob(DesignationState mark, JsonObject job)
    {
        var removal = Storages.SelectMany(s => s.SignRemovals).FirstOrDefault(r => r.Designation == mark.Id);
        if (removal == null) return;
        job["storage_sign"] = new JsonObject { ["chest"] = removal.Chest.ToString(),
            ["lines"] = new JsonArray(removal.Lines.Select(s => (JsonNode)s!).ToArray()) };
    }

    private ActiveJob? StorageSignRemovalJob(PawnState pawn, PawnLive live)
    {
        var ids = Storages.SelectMany(s => s.SignRemovals).Select(r => r.Designation).ToHashSet();
        if (ids.Count == 0 || !Index().ByKind.TryGetValue("deconstruct", out var all)) return null;
        var list = all.Where(x => ids.Contains(x.D.Id)).ToList();
        var search = new MarkSearch("deconstruct", true, false, World, []);
        var best = NearestMark(pawn, live, live.Block, list, search);
        return best.D == null ? null : MarkJob(pawn, live, WorkTypes.Build, "dig", "removing storage sign", list, best, search);
    }

    private bool SignAllows(P3 chest, int id, int meta) => StorageAt(chest) is not { Signs.Count: > 0 } s || Accepts(s, id, meta);

    private static JsonObject FilterJson(StorageFilter f) => new()
    {
        ["allow"] = new JsonArray(f.Allow.Select(x => (JsonNode)x!).ToArray()), ["deny"] = new JsonArray(f.Deny.Select(x => (JsonNode)x!).ToArray()),
    };

    private static JsonObject StorageJson(StorageState st)
    {
        var row = new JsonObject
        {
            ["chest"] = st.Chest.Json(), ["double_with"] = st.DoubleWith?.Json(), ["label"] = st.Label, ["priority"] = st.Signs.Count > 0 ? "critical" : st.Priority,
            ["filter"] = FilterJson(st.EffectiveFilter), ["contents"] = new JsonArray(st.Contents.Select(c => (JsonNode)c.Json()).ToArray()),
            ["free_slots"] = st.FreeSlots, ["known_at"] = st.KnownAt, ["enchanted"] = EnchantedJson(st),
        };
        if (st.Signs.Count > 0) row["signs"] = new JsonArray(st.Signs.Select(s => (JsonNode)new JsonObject
        {
            ["at"] = s.At.Json(), ["lines"] = new JsonArray(s.Lines.Select(l => (JsonNode)l!).ToArray()),
            ["allow"] = new JsonArray(s.Allow.Select(l => (JsonNode)l!).ToArray()), ["deny"] = new JsonArray(s.Deny.Select(l => (JsonNode)l!).ToArray()),
        }).ToArray());
        if (st.PendingFilter != null) row["pending_filter"] = FilterJson(st.PendingFilter);
        if (st.PendingPriority != null) row["pending_priority"] = st.PendingPriority;
        return row;
    }
}
