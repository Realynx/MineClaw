using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MineClaw.Hive.Story;

/// <summary>
/// Hauling the genre's way (docs/web/story-mode.md "Hauling"). Fox 11-04: "Dropped items should be auto-hauled by pawns that have
/// hauling with high priority", and "if I limit the types of items allowed in a chest, pawns … should rearrange … Otherwise they end
/// up dropped on the ground, where they despawn."
/// <list type="bullet">
/// <item><b>Which drops</b>: every drop the colony sees that isn't forbidden, at home or within <see cref="HaulRadius"/> of home or of
/// colony work (designations, zones, builds); not under ground, not next to a monster, not one a player (not a pawn) just dropped at
/// their feet (<see cref="PlayerDropGrace"/>), not one already lying at the drop spot with nowhere better to go.</item>
/// <item><b>When</b>: by the Haul work type's priority like any work; within one priority, drops about to despawn first
/// (<see cref="DespawnRisk"/> of the 5 minutes), labelled "despawns in Ns". A working pawn is cut for a haul only when a drop despawns
/// within <see cref="DespawnCut"/> and its Haul priority is at least its current work's.</item>
/// <item><b>Where</b>: the chest whose filter accepts it (priority, then distance); else, with
/// <see cref="ColonyMeta.UnfilteredTakesAnything"/>, any chest with room that doesn't deny it; with no storage at all, the drop spot
/// (a "dump" zone, else by home). Nowhere: one alert "No storage for: …" and <c>unstored_kinds</c> in the state.</item>
/// <item><b>Batching</b>: one trip collects the drops near the first one that go to the same place, up to its free slots.</item>
/// <item><b>Re-sorting</b>: what a chest's filter no longer allows goes to a chest that takes it (batched per chest); a better-priority
/// chest pulls what it accepts; with nowhere to go it is dropped beside the chest (unless <see cref="ColonyMeta.KeepDisallowed"/>),
/// where it despawns unless it is hauled once a chest takes it. What the fallback put in a chest stays there.</item>
/// </list>
/// </summary>
public sealed partial class StoryColony
{
    public const double HaulRadius = 48;
    /// <summary>Items despawn this long after they were dropped (6000 ticks).</summary>
    public static readonly TimeSpan DespawnAfter = TimeSpan.FromMinutes(5);
    /// <summary>A drop this old is at risk: hauled before same-priority work.</summary>
    public static readonly TimeSpan DespawnRisk = TimeSpan.FromMinutes(3);
    /// <summary>A drop this close to despawning may take a hauler off lesser work.</summary>
    public static readonly TimeSpan DespawnCut = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PlayerDropGrace = TimeSpan.FromSeconds(10);
    public const int BatchRadius = 6;

    private readonly Dictionary<int, DateTimeOffset> _itemSeen = new();
    private readonly Dictionary<(int Id, int Meta), string> _unstored = new();
    private readonly HashSet<(P3 Chest, int Id, int Meta)> _fallbackStored = new();
    private readonly HashSet<int> _despawnCut = new();
    private DateTimeOffset _haulTick = DateTimeOffset.MinValue;

    // ---------------------------------------------------------------- options

    /// <summary>The hauling and rescue options (<c>POST …/options</c>). Null leaves one as it is.</summary>
    public JsonObject SetHaulOptions(bool? rescueTeleport, bool? unfiltered, bool? keepDisallowed, int? pillarStack = null, int? stuckTeleportAfterS = null)
    {
        lock (_lock)
        {
            if (stuckTeleportAfterS is { } sts) { Meta.StuckTeleportAfterS = Math.Clamp(sts, 5, 3600); Touch(); }
            if (pillarStack is { } ps) { Meta.PillarStack = Math.Clamp(ps, 0, 64); Touch(); }
            if (rescueTeleport is { } r) Meta.RescueTeleport = r;
            if (unfiltered is { } u) Meta.UnfilteredTakesAnything = u;
            if (keepDisallowed is { } k) Meta.KeepDisallowed = k;
            if (rescueTeleport != null || unfiltered != null || keepDisallowed != null) { Touch(); WakeIdle(); }
        }
        Save(force: true);
        lock (_lock) return OptionsJson();
    }

    private void HaulOptionsJson(JsonObject o)
    {
        o["rescue_teleport"] = Meta.RescueTeleport;
        o["unfiltered_storage_takes_anything"] = Meta.UnfilteredTakesAnything;
        o["keep_disallowed"] = Meta.KeepDisallowed;
        o["pillar_stack"] = Meta.PillarStack;
        o["stuck_teleport_after_s"] = Meta.StuckTeleportAfterS;
    }

    // ---------------------------------------------------------------- destinations

    /// <summary>Where a stack goes: a chest (by filter, else the fallback), the drop spot (no storage at all), or nowhere (null).</summary>
    private (StorageState? Chest, P3? Dump, bool Fallback)? HaulDest(int id, int meta, P3 from, StorageState? except = null)
    {
        if (BestChest(id, meta, from, except) is { } best) return (best, null, false);
        if (Meta.UnfilteredTakesAnything && Fallback(id, meta, from, except) is { } fb) return (fb, null, true);
        if (Storages.Count == 0 && DumpSpot() is { } dump) return (null, dump, false);
        return null;
    }

    private bool Denied(StorageState s, int id, int meta) => Cats is { } c && !c.Accepts(new StorageFilter { Allow = ["*"], Deny = s.Filter.Deny }, id, meta);

    /// <summary>"Unfiltered storage takes anything": a chest with room that doesn't deny it, open ("*") chests first, then the lowest
    /// priority (general storage), then the nearest.</summary>
    private StorageState? Fallback(int id, int meta, P3 from, StorageState? except) =>
        Storages.Where(s => s != except && HasRoom(s, id, meta) && !Denied(s, id, meta))
            .OrderBy(s => s.Filter.Allow.Contains("*") ? 0 : 1).ThenBy(s => StoragePriority.Rank(s.Priority)).ThenBy(s => s.Chest.Dist(from))
            .FirstOrDefault();

    /// <summary>The drop spot: the nearest "dump" zone's ground, else a standable spot by home. Null without a world or home.</summary>
    internal P3? DumpSpot()
    {
        if (World is not { } w) return null;
        var home = Home();
        var dump = Zones.Where(z => z.Kind == "dump").OrderBy(z => home is { } h ? z.Area.Center.Dist(h.X, h.Y, h.Z) : 0).FirstOrDefault();
        if (dump != null) return SafeSpot(w, dump.Area.Center, 3) ?? dump.Area.Center;
        return home is { } hh ? SafeSpot(w, hh) ?? hh : null;
    }

    private bool AtDump(P3 at) =>
        Zones.Any(z => z.Kind == "dump" && z.Contains(at.X, at.Z) && at.Y >= z.Area.Y1 - 1 && at.Y <= z.Area.Y2 + 2) || DumpSpot() is { } d && Math.Abs(d.X - at.X) <= 3 && Math.Abs(d.Z - at.Z) <= 3 && Math.Abs(d.Y - at.Y) <= 3;

    /// <summary>No storage takes this: one condition alert while any kind has nowhere to go (<c>unstored_kinds</c> lists them).</summary>
    private void Unstored(int id, int meta, string? pawn = null)
    {
        bool first = _unstored.Count == 0;
        if (!_unstored.TryAdd((id, meta), World?.Data.DisplayName(id, meta) ?? ItemName(id, meta))) return;
        if (first) Alert("warn", $"No storage for: {string.Join(", ", _unstored.Values)}", pawn);
    }

    /// <summary>The kinds with nowhere to go now (those a chest takes again drop off the list).</summary>
    internal JsonArray UnstoredKinds()
    {
        foreach (var k in _unstored.Keys.ToList())
            if (HaulDest(k.Id, k.Meta, Home() ?? default) is { Chest: not null }) _unstored.Remove(k);
        return new JsonArray(_unstored.Values.Select(v => (JsonNode)v!).ToArray());
    }

    // ---------------------------------------------------------------- deliveries (what a pawn carries)

    /// <summary>What a pawn carries goes where it belongs: all its chests in one trip (filter-accepted first); drops it hauled go to
    /// the drop spot when the colony has no storage at all (<paramref name="dumpOk"/>); what has nowhere to go stays with it (and the
    /// condition alert says so).</summary>
    private ActiveJob? DeliverJob(PawnState p, PawnLive live, List<Stack> items, bool dumpOk)
    {
        var groups = new Dictionary<P3, (StorageState S, bool Fallback, List<Stack> Stacks)>();
        var drops = new List<Stack>();
        P3? dump = null;
        foreach (var s in items)
        {
            switch (HaulDest(s.Id, s.Meta, live.Block))
            {
                case { Chest: { } st, Fallback: var fb }:
                    if (!groups.TryGetValue(st.Chest, out var g)) groups[st.Chest] = g = (st, fb, new List<Stack>());
                    g.Stacks.Add(s);
                    if (fb) _fallbackStored.Add((st.Chest, s.Id, s.Meta));
                    break;
                case { Dump: { } d } when dumpOk: // only drops it hauled: a pawn keeps its own loot (seeds to sow, blocks to build)
                    dump = d;
                    drops.Add(s);
                    Unstored(s.Id, s.Meta, p.Name);
                    break;
                default:
                    Unstored(s.Id, s.Meta, p.Name);
                    break;
            }
        }
        if (groups.Count == 0 && drops.Count == 0) return null;
        var order = groups.Values.OrderBy(g => g.Fallback ? 1 : 0).ThenBy(g => g.S.Chest.Dist(live.X, live.Y, live.Z)).ToList();
        var stash = new JsonArray(order.Select(g => (JsonNode)new JsonObject
        {
            ["to"] = g.S.Chest.ToString(),
            ["items"] = new JsonArray(g.Stacks.Select(s => ItemName(s.Id, s.Meta)).Distinct().Select(n => (JsonNode)n!).ToArray()),
        }).ToArray());
        var names = order.SelectMany(g => g.Stacks).Concat(drops).Select(s => ItemName(s.Id, s.Meta)).Distinct().ToList();
        ActiveJob j;
        if (drops.Count > 0)
            j = Job(WorkTypes.Haul, "dump", $"dropping {string.Join(", ", drops.Select(s => ItemName(s.Id, s.Meta)).Distinct())} at the drop spot (no storage)",
                dump, null, "deliver", dump.ToString(), "working", f =>
                {
                    f["dist"] = 1;
                    f["drop"] = new JsonArray(drops.Select(s => (JsonNode)new JsonObject { ["item"] = ItemName(s.Id, s.Meta), ["n"] = s.Count }).ToArray());
                    if (stash.Count > 0) f["stash"] = stash;
                }, xp: 5);
        else
            j = Job(WorkTypes.Haul, "go", $"storing {string.Join(", ", names)}", order[0].S.Chest, null, "deliver", order[0].S.Chest.ToString(), "working", f =>
            {
                f["dist"] = 2;
                f["stash"] = stash;
            }, xp: 5);
        foreach (var g in order) j.Puts.Add((g.S.Chest, g.Stacks));
        return j;
    }

    // ---------------------------------------------------------------- keeping loot while gathering

    /// <summary>A pawn this close to full unloads before its next target.</summary>
    public const int NearlyFull = 2;
    private readonly Dictionary<string, string> _lastWork = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The old way (benchmarks only): every gathering job is followed by a trip to storage.</summary>
    internal bool DepositEachJob { get; set; }

    /// <summary>
    /// After a gathering job the pawn keeps its loot and goes on when: it has more than <see cref="NearlyFull"/> free slots, its hours
    /// are still work (or anything), and there is another target of the same work for it. Otherwise one trip takes everything to
    /// storage. A drafted pawn never gets here (it keeps what it has); a build that needs what it carries takes it from its pockets.
    /// </summary>
    private bool KeepGathering(PawnState p, PawnLive live)
    {
        if (DepositEachJob || live.FreeSlots <= NearlyFull) return false;
        if (!_lastWork.TryGetValue(p.Name, out var type) || type == WorkTypes.Haul) return false; // a haul's drops go now
        if (p.Priorities.GetValueOrDefault(type) is < 1 or > 4) return false;
        int hour = HourNow;
        var block = p.Schedule.Length == 24 && ScheduleBlocks.Known(p.Schedule[hour]) ? p.Schedule[hour] : ScheduleBlocks.Anything;
        if (block is not (ScheduleBlocks.Work or ScheduleBlocks.Anything)) return false; // bed or free time: unload first
        return Candidate(type, p, live) != null;
    }

    // ---------------------------------------------------------------- drops on the ground

    /// <summary>A drop's age: from the first sighting by any body (or by the colony).</summary>
    private TimeSpan Age(StoryEntity e)
    {
        if (!_itemSeen.TryGetValue(e.Id, out var seen)) _itemSeen[e.Id] = seen = Now;
        var first = e.FirstSeen is { } f && f < seen ? f : seen;
        return Now - first;
    }

    private string Despawns(StoryEntity e) => $"despawns in {Math.Max(0, (int)(DespawnAfter - Age(e)).TotalSeconds)}s";

    private (long Stamp, List<P3> Points)? _anchors;

    /// <summary>Where the colony works: home, its zones, its designations (one point per 8×8 cell), its builds.</summary>
    private List<P3> HaulAnchors()
    {
        long stamp = Index().Stamp * 31 + Builds.Count;
        if (_anchors is { } a && a.Stamp == stamp) return a.Points;
        var pts = new List<P3>();
        var cells = new HashSet<(int, int, int)>();
        void Add(P3 p) { if (cells.Add((p.X >> 3, p.Y >> 3, p.Z >> 3))) pts.Add(p); }
        if (Home() is { } h) Add(h);
        foreach (var z in Zones) Add(z.Area.Center);
        foreach (var d in Designations) if (d.At is { } at) Add(at);
        foreach (var b in Builds.Where(b => b.Status != "done")) if (b.Ghosts.FirstOrDefault() is { } g) Add(g.At);
        _anchors = (stamp, pts);
        return pts;
    }

    private bool InHaulArea(P3 at) =>
        InHome(at) || HaulAnchors().Any(a => Math.Abs(a.X - at.X) <= HaulRadius && Math.Abs(a.Z - at.Z) <= HaulRadius
            && a.Dist(at.X + 0.5, at.Y, at.Z + 0.5) <= HaulRadius + 8);

    /// <summary>A drop a pawn may go for now (see the class summary), with where it goes.</summary>
    private (StorageState? Chest, P3? Dump, bool Fallback)? Haulable(StoryEntity e, PawnState p, P3 from, IReadOnlyList<StoryEntity> ents)
    {
        if (ForbiddenEntity(e.Id) || Forbidden(e.Block) || !Open("e:" + e.Id, p.Name, null)) return null;
        if (!InHaulArea(e.Block)) return null;
        if (Math.Abs(e.Block.Y - from.Y) > 12 && Math.Abs(e.Block.Y - (Home()?.Y ?? from.Y)) > 12) return null;
        if (World is { } w && StoryWander.Underground(w, e.Block)) return null;
        foreach (var o in ents)
        {
            if (o.Kind == "mob" && o.Hostile && Fresh(o) && Math.Abs(o.X - e.X) <= 4 && Math.Abs(o.Z - e.Z) <= 4 && Math.Abs(o.Y - e.Y) <= 3) return null;
            if (o.Kind == "player" && !Pawns.ContainsKey(o.Name) && Math.Abs(o.X - e.X) <= 2 && Math.Abs(o.Z - e.Z) <= 2 && Math.Abs(o.Y - e.Y) <= 3
                && Age(e) < PlayerDropGrace) return null; // a player just dropped it at their feet
        }
        if (HaulDest(e.ItemId, e.ItemMeta, e.Block) is not { } dest)
        {
            Unstored(e.ItemId, e.ItemMeta);
            return null;
        }
        if (dest.Dump != null && AtDump(e.Block)) return null; // already where it can go
        return dest;
    }

    /// <summary>
    /// A haul of drops for this pawn: a designated drop first, then the standing task (despawn risk first, then nearest), batched
    /// with the drops near it that go to the same place. <paramref name="riskOnly"/>: only drops at despawn risk (the same-priority
    /// check before other work).
    /// </summary>
    internal ActiveJob? HaulJob(PawnState p, PawnLive live, bool riskOnly = false)
    {
        if (World is not { } w) return null;
        var from = live.Block;
        var ents = Ents();
        var items = ents.Where(e => e.Kind == "item").ToList();
        if (items.Count == 0) return null;
        if (!riskOnly)
        {
            var byId = items.Where(e => !ForbiddenEntity(e.Id)).ToDictionary(e => e.Id);
            var d = Designations.Where(x => x.Kind == "haul" && x.Entity is { } id && byId.ContainsKey(id) && Open("e:" + id, p.Name, null))
                .OrderBy(x => byId[x.Entity!.Value].Block.Dist(from)).FirstOrDefault();
            if (d != null)
            {
                var de = byId[d.Entity!.Value];
                // The designated drops around it go in the same trip (each leased; their designations are done with it).
                int slots = Math.Max(1, live.FreeSlots - 1);
                var more = Designations.Where(x => x != d && x.Kind == "haul" && x.Entity is { } mid && byId.ContainsKey(mid) && Open("e:" + mid, p.Name, null))
                    .Select(x => byId[x.Entity!.Value])
                    .Where(m => Math.Abs(m.X - de.X) <= BatchRadius && Math.Abs(m.Z - de.Z) <= BatchRadius && Math.Abs(m.Y - de.Y) <= 3)
                    .OrderBy(m => m.Block.Dist(de.X, de.Y, de.Z)).Take(slots - 1).ToList();
                double far = more.Count == 0 ? 0 : more.Max(m => Math.Sqrt(Math.Pow(m.X - de.X, 2) + Math.Pow(m.Z - de.Z, 2)));
                var dj = Job(WorkTypes.Haul, "collect", $"hauling {w.Data.DisplayName(de.ItemId, de.ItemMeta)}" + (more.Count > 0 ? $" (+{more.Count} more)" : ""),
                    de.Block, "e:" + de.Id, "designation", d.Id, "working", j => j["r"] = Math.Clamp((int)Math.Ceiling(far) + 2, 3, 8), xp: 5);
                foreach (var m in more) dj.MoreLeases.Add("e:" + m.Id);
                return dj;
            }
        }
        var cands = new List<(StoryEntity E, (StorageState? Chest, P3? Dump, bool Fallback) Dest, bool Risk)>();
        foreach (var e in items)
        {
            if (Haulable(e, p, from, ents) is not { } dest) continue;
            bool risk = Age(e) >= DespawnRisk;
            if (riskOnly && !risk) continue;
            cands.Add((e, dest, risk));
        }
        if (cands.Count == 0) return null;
        var first = cands.OrderBy(c => c.Risk ? 0 : 1).ThenBy(c => c.E.Block.Dist(from)).First();
        object DestKey((StorageState? Chest, P3? Dump, bool Fallback) d) => d.Chest?.Chest ?? d.Dump ?? default;
        var key0 = DestKey(first.Dest);
        int room = Math.Max(1, live.FreeSlots - 1);
        var batch = cands.Where(c => c.E != first.E && Equals(DestKey(c.Dest), key0)
                && Math.Abs(c.E.X - first.E.X) <= BatchRadius && Math.Abs(c.E.Z - first.E.Z) <= BatchRadius && Math.Abs(c.E.Y - first.E.Y) <= 3)
            .OrderBy(c => c.E.Block.Dist(first.E.X, first.E.Y, first.E.Z)).Take(room - 1).ToList();
        double reach = batch.Count == 0 ? 0 : batch.Max(c => Math.Sqrt(Math.Pow(c.E.X - first.E.X, 2) + Math.Pow(c.E.Z - first.E.Z, 2)));
        var e0 = first.E;
        var label = $"hauling {w.Data.DisplayName(e0.ItemId, e0.ItemMeta)}" + (batch.Count > 0 ? $" (+{batch.Count} more)" : "")
            + (first.Risk ? $" ({Despawns(e0)})" : "");
        var job = Job(WorkTypes.Haul, "collect", label, e0.Block, "e:" + e0.Id, "drop", null, "working",
            j => j["r"] = Math.Clamp((int)Math.Ceiling(reach) + 2, 3, 8), xp: 5);
        foreach (var c in batch) job.MoreLeases.Add("e:" + c.E.Id);
        return job;
    }

    // ---------------------------------------------------------------- re-sorting chests

    private (long Stamp, List<(StorageState Src, Stack St, StorageState? Dst, bool Fallback)> Moves)? _resort;

    /// <summary>What should leave which chest (cached per storage revision): misfiled stacks to a chest that takes them (or the
    /// fallback, or nowhere: dropped, or kept with <see cref="ColonyMeta.KeepDisallowed"/>), and stacks a better chest pulls.</summary>
    private List<(StorageState Src, Stack St, StorageState? Dst, bool Fallback)> Resorts()
    {
        long stamp = Storages.Count + (Meta.UnfilteredTakesAnything ? 7 : 0) + (Meta.KeepDisallowed ? 13 : 0);
        foreach (var s in Storages) stamp = stamp * 31 + s.Rev;
        if (_resort is { } r && r.Stamp == stamp) return r.Moves;
        var moves = new List<(StorageState, Stack, StorageState?, bool)>();
        foreach (var src in Storages)
            foreach (var st in src.Contents)
            {
                bool allowed = Accepts(src, st.Id, st.Meta);
                var dst = BestChest(st.Id, st.Meta, src.Chest, except: src);
                if (dst != null && (!allowed || StoragePriority.Rank(dst.Priority) > StoragePriority.Rank(src.Priority))) { moves.Add((src, st, dst, false)); continue; }
                if (allowed) continue;
                // No chest's filter takes it. What the fallback put here stays; anything else goes to a fallback chest, else out.
                if (Meta.UnfilteredTakesAnything && !Denied(src, st.Id, st.Meta) && _fallbackStored.Contains((src.Chest, st.Id, st.Meta))) continue;
                if (Meta.UnfilteredTakesAnything && Fallback(st.Id, st.Meta, src.Chest, src) is { } fb) { moves.Add((src, st, fb, true)); continue; }
                Unstored(st.Id, st.Meta);
                if (!Meta.KeepDisallowed) moves.Add((src, st, null, false));
            }
        _resort = (stamp, moves);
        return moves;
    }

    /// <summary>One re-sort trip: from the chest with the most to move (lowest priority, then nearest), as many stacks as the pawn has
    /// room for, each to its chest; what has nowhere to go is dropped beside the chest.</summary>
    internal ActiveJob? RebalanceJob(PawnState p, PawnLive live)
    {
        var from = live.Block;
        var all = Resorts();
        if (all.Count == 0) return null;
        foreach (var g in all.GroupBy(m => m.Src).OrderBy(g => StoragePriority.Rank(g.Key.Priority)).ThenBy(g => g.Key.Chest.Dist(from)))
        {
            var src = g.Key;
            var key = "resort:" + src.Chest;
            if (!Open(key, p.Name, null)) continue;
            int room = Math.Max(1, live.FreeSlots - 1), used = 0;
            var take = new List<(Stack St, int N, StorageState? Dst, bool Fallback)>();
            foreach (var m in g)
            {
                int stacks = (m.St.Count + MaxStack(m.St.Id) - 1) / MaxStack(m.St.Id);
                if (used >= room) break;
                int n = Math.Min(m.St.Count, (room - used) * MaxStack(m.St.Id));
                used += Math.Min(stacks, room - used);
                take.Add((m.St, n, m.Dst, m.Fallback));
            }
            if (take.Count == 0) continue;
            var drops = take.Where(t => t.Dst == null).ToList();
            var puts = take.Where(t => t.Dst != null).GroupBy(t => t.Dst!).ToList();
            foreach (var fb in take.Where(x => x.Fallback)) _fallbackStored.Add((fb.Dst!.Chest, fb.St.Id, fb.St.Meta));
            var fetch = new JsonArray(take.Select(t => (JsonNode)new JsonObject { ["from"] = src.Chest.ToString(), ["item"] = ItemName(t.St.Id, t.St.Meta), ["n"] = t.N }).ToArray());
            var stash = new JsonArray(puts.Select(pg => (JsonNode)new JsonObject
            {
                ["to"] = pg.Key.Chest.ToString(),
                ["items"] = new JsonArray(pg.Select(t => ItemName(t.St.Id, t.St.Meta)).Distinct().Select(n => (JsonNode)n!).ToArray()),
            }).ToArray());
            string where = src.Label ?? src.Chest.ToString();
            ActiveJob j;
            if (drops.Count > 0)
            {
                var beside = World is { } w ? SafeSpot(w, src.Chest, 2) ?? src.Chest : src.Chest;
                j = Job(WorkTypes.Haul, "dump", $"re-sorting {where}: dropping {string.Join(", ", drops.Select(t => ItemName(t.St.Id, t.St.Meta)).Distinct())} (no chest takes it)",
                    beside, key, "move", src.Chest.ToString(), "working", f =>
                    {
                        f["dist"] = 1;
                        f["fetch"] = fetch;
                        f["drop"] = new JsonArray(drops.Select(t => (JsonNode)new JsonObject { ["item"] = ItemName(t.St.Id, t.St.Meta), ["n"] = t.N }).ToArray());
                        if (stash.Count > 0) f["stash"] = stash;
                    }, xp: 5);
                _log.LogInformation("story {Colony}: re-sorting {Chest}: dropping {Items} beside it (no chest takes them)", Id, src.Chest,
                    string.Join(", ", drops.Select(t => ItemName(t.St.Id, t.St.Meta))));
            }
            else
                j = Job(WorkTypes.Haul, "go", $"re-sorting {where}: moving {string.Join(", ", take.Select(t => ItemName(t.St.Id, t.St.Meta)).Distinct())}",
                    puts[0].Key.Chest, key, "move", src.Chest.ToString(), "working", f =>
                    {
                        f["dist"] = 2;
                        f["fetch"] = fetch;
                        f["stash"] = stash;
                    }, xp: 5);
            foreach (var t in take) j.Takes.Add((src.Chest, t.St with { Count = t.N }));
            foreach (var pg in puts) j.Puts.Add((pg.Key.Chest, pg.Select(t => t.St with { Count = t.N }).ToList()));
            return j;
        }
        return null;
    }

    // ---------------------------------------------------------------- the tick: a drop about to despawn

    /// <summary>
    /// Every 5 s: a drop that despawns within <see cref="DespawnCut"/> and that nobody is hauling may take one pawn off its work, the
    /// nearest whose Haul priority is at least its current work's (never an order, a need, an escape or a haul). Each drop once.
    /// </summary>
    private void HaulTick()
    {
        if (Now - _haulTick < TimeSpan.FromSeconds(5) || World is not { } w || Meta.Paused || !Meta.Active) return;
        _haulTick = Now;
        var ents = w.Entities();
        var ids = ents.Select(e => e.Id).ToHashSet();
        foreach (var k in _itemSeen.Keys.Where(k => !ids.Contains(k)).ToList()) _itemSeen.Remove(k);
        _despawnCut.RemoveWhere(k => !ids.Contains(k));
        foreach (var e in ents)
        {
            if (e.Kind != "item" || _despawnCut.Contains(e.Id) || DespawnAfter - Age(e) > DespawnCut || _leases.ContainsKey("e:" + e.Id)) continue;
            PawnState? best = null;
            double bestD = double.MaxValue;
            foreach (var p in Pawns.Values)
            {
                if (p.Drafted || !_current.TryGetValue(p.Name, out var cur) || !WorkTypes.Known(cur.Type) || cur.Type == WorkTypes.Haul) continue;
                if (cur.Source is "order" or "need" or "escape" or "deliver") continue;
                int haul = p.Priorities.GetValueOrDefault(WorkTypes.Haul);
                if (haul is < 1 or > 4 || haul > p.Priorities.GetValueOrDefault(cur.Type, 4)) continue;
                var live = Host.Live(p.Name);
                if (!live.Online || Haulable(e, p, live.Block, ents) == null) continue;
                double d = e.Block.Dist(live.X, live.Y, live.Z);
                if (d < bestD) { bestD = d; best = p; }
            }
            if (best == null) continue;
            _despawnCut.Add(e.Id);
            _log.LogInformation("story {Colony}: {Pawn} leaves its work for a {Item} that {Despawns}", Id, best.Name, e.Name, Despawns(e));
            Supersede(best, "a drop is about to despawn");
            return; // one per tick
        }
    }
}
