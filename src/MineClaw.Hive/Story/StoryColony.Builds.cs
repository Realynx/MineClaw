using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// The build queue (docs/web/story-mode.md "Construction"): items expand to ghost blocks; each ghost is one placement (or dig) job
/// leased to a pawn whose Build priority is on. Order: queue order, then bottom-up (support first), then blocks touching something
/// solid, then nearest. A pawn fetches the material from storage (or uses its own); none anywhere → the build is blocked with
/// "need N item" and an alert until some arrives. Never a block where a player (pawn or not) stands.
/// </summary>
public sealed partial class StoryColony
{
    private GhostState? FindGhost(string? key)
    {
        if (key == null) return null;
        var parts = key.Split('|');
        if (parts.Length != 2) return null;
        var b = Builds.FirstOrDefault(x => x.Id == parts[0]);
        return b?.Ghosts.FirstOrDefault(g => g.At.ToString() == parts[1]);
    }

    /// <summary>add {items} | remove {ids} | reorder {ids} | pause {ids} | resume {ids}.</summary>
    public (JsonNode? Result, string? Error) BuildsOp(string op, JsonArray? items, IReadOnlyList<string>? ids)
    {
        if (World is not { } w) return (null, "no world view yet");
        lock (_lock)
        {
            switch (op)
            {
                case "add":
                {
                    if (items is not { Count: > 0 }) return (null, "items required");
                    if (items.Count > 64) return (null, "at most 64 items per request");
                    var made = new List<BuildState>();
                    foreach (var n in items)
                    {
                        if (n is not JsonObject item) return (null, "each item is an object {kind, area|blocks, material, ...}");
                        var (ghosts, mat, err) = StoryConstruction.Expand(w.Data, item, w);
                        if (err != null) return (null, err);
                        if (ghosts.Count > 4096) return (null, "an item expands to at most 4096 blocks");
                        made.Add(new BuildState
                        {
                            Kind = item["kind"]!.GetValue<string>().ToLowerInvariant(), Spec = (JsonObject)item.DeepClone(), Ghosts = ghosts,
                            Material = mat?.Name ?? "", Created = Now,
                        });
                    }
                    int order = Builds.Count == 0 ? 0 : Builds.Max(b => b.Order) + 1;
                    foreach (var b in made)
                    {
                        b.Id = NextId("b");
                        b.Order = order++;
                        b.Label = $"{b.Kind} ({b.BlocksTotal} block{(b.BlocksTotal == 1 ? "" : "s")}{(b.Material.Length > 0 && b.Kind != "remove" ? ", " + b.Material : "")})";
                        var r = Touch();
                        b.Rev = r;
                        foreach (var g in b.Ghosts)
                        {
                            g.Rev = r;
                            // Already there (a wall over a finished one): done at once.
                            var (id, meta) = w.Block(g.At.X, g.At.Y, g.At.Z);
                            if (!g.Remove && id == g.Id && (g.Meta == meta || !g.Job)) g.State = "done";
                            if (g.Remove && id == 0) g.State = "done";
                        }
                        Builds.Add(b);
                    }
                    return (new JsonObject { ["ids"] = new JsonArray(made.Select(b => (JsonNode)b.Id!).ToArray()) }, null);
                }
                case "remove":
                case "pause":
                case "resume":
                {
                    if (ids is not { Count: > 0 }) return (null, "ids required");
                    var hit = Builds.Where(b => ids.Contains(b.Id)).ToList();
                    if (hit.Count == 0) return (null, "no such build");
                    foreach (var b in hit)
                    {
                        if (op == "remove")
                        {
                            Builds.Remove(b);
                            Removed("build", b.Id);
                            foreach (var k in _leases.Keys.Where(k => k.StartsWith("g:" + b.Id + "|", StringComparison.Ordinal)).ToList()) _leases.Remove(k);
                            continue;
                        }
                        if (b.Status != "done") b.Status = op == "pause" ? "paused" : "queued";
                        b.Rev = Touch();
                    }
                    return (new JsonObject { ["ids"] = new JsonArray(hit.Select(b => (JsonNode)b.Id!).ToArray()) }, null);
                }
                case "reorder":
                {
                    if (ids is not { Count: > 0 }) return (null, "ids required");
                    if (ids.Any(i => Builds.All(b => b.Id != i))) return (null, "unknown build id");
                    // The named ones first, in that order; the rest keep their relative order after them.
                    var rest = Builds.Where(b => !ids.Contains(b.Id)).OrderBy(b => b.Order).ToList();
                    int o = 0;
                    foreach (var id in ids.Distinct()) { var b = Builds.First(x => x.Id == id); b.Order = o++; b.Rev = Touch(); }
                    foreach (var b in rest) { b.Order = o++; b.Rev = Touch(); }
                    return (new JsonObject { ["ids"] = new JsonArray(Builds.OrderBy(b => b.Order).Select(b => (JsonNode)b.Id!).ToArray()) }, null);
                }
                default:
                    return (null, "op must be add|remove|reorder|pause|resume");
            }
        }
    }

    private bool Solid(P3 p) => World is { } w && w.Block(p.X, p.Y, p.Z).Id is var id && !StoryBlocks.Replaceable.Contains(id);

    private bool Occupied(P3 at)
    {
        foreach (var e in World?.Entities() ?? [])
            if (e.Kind == "player" && (int)Math.Floor(e.X) == at.X && (int)Math.Floor(e.Z) == at.Z && (at.Y == (int)Math.Floor(e.Y) || at.Y == (int)Math.Floor(e.Y) + 1))
                return true;
        return false;
    }

    /// <summary>The next placement for this pawn over the queue, or null; marks builds blocked when their material is nowhere.</summary>
    private ActiveJob? GhostJob(PawnState p, PawnLive live)
    {
        if (World is not { } w) return null;
        var from = live.Block;
        foreach (var b in Builds.Where(b => b.Status is not ("paused" or "done")).OrderBy(b => b.Order))
        {
            var todo = b.Ghosts.Where(g => g.Job && g.State is "todo" or "leased").ToList();
            if (todo.Count == 0)
            {
                if (b.Ghosts.Any(g => g.Job && g.State == "blocked")) continue;
                b.Status = "done"; b.Rev = Touch();
                continue;
            }
            int lowest = todo.Min(g => g.At.Y);
            var open = todo.Where(g => g.State == "todo" && g.At.Y == lowest).ToList();
            foreach (var g in StoryConstruction.Ordered(open, Solid, from))
            {
                var key = $"g:{b.Id}|{g.At}";
                if (!Open(key, p.Name, g.At) || Occupied(g.At)) continue;
                if (g.Remove)
                {
                    if (w.Block(g.At.X, g.At.Y, g.At.Z).Id == 0) { g.State = "done"; g.Rev = Touch(); continue; }
                    return Claim(b, g, Job(WorkTypes.Build, "dig", $"removing a block ({b.Label})", g.At, key, "ghost", $"{b.Id}|{g.At}", xp: 10));
                }
                var (cur, curMeta) = w.Block(g.At.X, g.At.Y, g.At.Z);
                if (cur == g.Id && cur != 0) { g.State = "done"; g.Rev = Touch(); continue; }
                var item = g.Item ?? w.Data.Item(g.Id).Name;
                var itemId = w.Data.ItemByNameWithDamage(item)?.Item.Id ?? g.Id;
                bool have = live.Inventory.Any(s => s.Id == itemId);
                (StorageState Store, Stack Stack)? stored = have ? null : FindStored(s => s.Id == itemId, from);
                if (!have && stored == null)
                {
                    int need = b.Ghosts.Count(x => x.Job && x.State is "todo" or "leased" && (x.Item ?? "") == item);
                    var reason = $"need {need} {w.Data.DisplayName(itemId, 0).ToLowerInvariant()}";
                    if (b.Status != "blocked" || b.Reason != reason)
                    {
                        b.Status = "blocked"; b.Reason = reason; b.Rev = Touch();
                        Alert("warn", $"Build {b.Id} ({b.Kind}) is blocked: {reason}");
                    }
                    goto nextBuild;
                }
                if (b.Status != "active") { b.Status = "active"; b.Reason = null; b.Rev = Touch(); }
                bool clear = !StoryBlocks.Replaceable.Contains(cur);
                int n = Math.Min(64, todo.Count(x => (x.Item ?? "") == item));
                var job = Job(WorkTypes.Build, "place", $"building {b.Label}", g.At, key, "ghost", $"{b.Id}|{g.At}", "working", f =>
                {
                    f["block"] = item;
                    if (clear) f["clear"] = true;
                    if (g.Facing is { } fc) f["facing"] = fc;
                    if (stored is { } st) f["fetch"] = Fetch(st.Store.Chest, st.Stack, n);
                }, xp: 12);
                if (stored is { } st2) job.Takes.Add((st2.Store.Chest, st2.Stack with { Count = Math.Min(n, st2.Stack.Count) }));
                return Claim(b, g, job);
            }
            nextBuild:;
        }
        return null;
    }

    private ActiveJob Claim(BuildState b, GhostState g, ActiveJob j)
    {
        g.State = "leased";
        g.Rev = Touch();
        return j;
    }

    private void GhostDone(ActiveJob j)
    {
        if (FindGhost(j.SourceId) is not { } g) return;
        g.State = "done";
        g.Rev = Touch();
        var b = Builds.First(x => x.Ghosts.Contains(g));
        // A door's / bed's other half came with it.
        foreach (var other in b.Ghosts.Where(o => !o.Job && o.State != "done" && Math.Abs(o.At.X - g.At.X) + Math.Abs(o.At.Y - g.At.Y) + Math.Abs(o.At.Z - g.At.Z) == 1))
        { other.State = "done"; other.Rev = g.Rev; }
        if (b.Ghosts.Where(x => x.Job).All(x => x.State == "done"))
        {
            b.Status = "done"; b.Reason = null; b.Rev = Touch();
            Alert("info", $"Build {b.Id} ({b.Label}) is done");
        }
    }

    private void GhostFailed(PawnState p, ActiveJob j, string why)
    {
        if (FindGhost(j.SourceId) is not { } g) return;
        var key = j.SourceId!;
        int n = _ghostFails[key] = _ghostFails.GetValueOrDefault(key) + 1;
        g.State = n >= 3 ? "blocked" : "todo";
        g.Rev = Touch();
        if (n >= 3 && Builds.FirstOrDefault(x => x.Ghosts.Contains(g)) is { } b)
        {
            b.Reason = $"can't place at {g.At}: {why}";
            b.Rev = Touch();
            Alert("warn", $"Build {b.Id}: {b.Reason}", p.Name);
        }
    }
}
