using System.Diagnostics;
using MineClaw.Client.Trees;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Protocol.Packets;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Client;

/// <summary>Knobs for <see cref="MinecraftClient.ChopTreeAsync"/>.</summary>
public sealed record TreeChopOptions
{
    public double SearchRadius { get; init; } = 24;
    /// <summary>Leaves broken by hand for the 1-in-20 sapling chance (b1.8.1 BlockLeaves; no apples from leaves in b1.8.1).</summary>
    public int MaxLeafBreaks { get; init; } = 64;
    public TimeSpan LeafPhase { get; init; } = TimeSpan.FromSeconds(75);
    /// <summary>Wait for the rest of the crown to decay when no sapling came yet (bounded). Vanilla decay is random-tick
    /// driven (a given leaf ticks every ~20 s on average), so a crown needs most of a minute.</summary>
    public TimeSpan DecayWait { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Stop waiting for decay early when no leaf disappeared for this long.</summary>
    public TimeSpan DecayStall { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Stop waiting for decay once this many saplings were collected.</summary>
    public int WantSaplings { get; init; } = 2;
    /// <summary>Blocks the bot may pillar up next to the trunk for logs above reach (cobblestone/netherrack only, never dirt).</summary>
    public int MaxPillar { get; init; } = 2;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Stewardship: never leave fewer trees (planted saplings count) than this unless the replant is certain.
    /// Null = the client's <see cref="MinecraftClient.MinTrees"/>.</summary>
    public int? MinTrees { get; init; }
    /// <summary>Live 10-27 (L6): a worker chopped 10 trees and replanted none. After this many consecutive chops that felled a
    /// tree without replanting it, chop_tree refuses until a replant works again (0 = never refuse).</summary>
    public int MaxUnreplantedChops { get; init; } = 2;
    public static TreeChopOptions Default { get; } = new();
}

/// <summary>What one <see cref="MinecraftClient.ChopTreeAsync"/> did (raised as <see cref="MinecraftClient.TreeChopped"/>).</summary>
public sealed record TreeChopReport(string Wood, BlockPos Base, BlockPos Soil, int LogsTotal, int LogsFelled, int Logs, int Saplings, int Apples,
    int LostDrops, BlockPos? Replanted, string? ReplantProblem, bool Ok, string Message)
{
    public int LeavesBroken { get; init; }
    public int LeavesDecayed { get; init; }
    /// <summary>Drops still lying up in the leaves when the macro ended (not lost: break the leaf under them).</summary>
    public int StuckDrops { get; init; }
}

public sealed partial class MinecraftClient
{
    /// <summary>Raised (on the event dispatcher) after every chop_tree, success or not.</summary>
    public event Action<TreeChopReport>? TreeChopped;

    /// <summary>Tree stewardship (<see cref="TreeStewardship"/>): the island keeps at least this many trees + planted
    /// saplings. chop_tree refuses below it unless it can replant; plain digs/mines of those trees' logs are protected.
    /// 0 = off.</summary>
    public int MinTrees { get; set; } = TreeStewardship.DefaultMinTrees;

    /// <summary>
    /// The hive's shared lease board for work targets (one body per tree): chop_tree skips trees another body holds, leases the
    /// one it picks for the whole chop and gives it back when done, stopped, dead or timed out (the TTL covers a crash). Live
    /// 10-29: 20 bodies of one hive each picked "the nearest tree" and all walked to the same oak. Null = a single bot.
    /// </summary>
    public IClaimBoard? TargetClaims { get; set; }

    private readonly Dictionary<BlockPos, DateTime> _failedTrees = new();

    /// <summary>How long a tree a body failed on stays leased to it and skipped by its own next picks.</summary>
    public static readonly TimeSpan FailedTreeHold = TimeSpan.FromMinutes(3);

    /// <summary>A tree lease outlives the longest chop (<see cref="TreeChopOptions.Timeout"/> 5 min) so it never needs renewing.</summary>
    public static readonly TimeSpan TreeLeaseTtl = TimeSpan.FromMinutes(6);

    /// <summary>The lease key of one tree (its trunk base; dimension-qualified, a task key so it never blocks digs nearby).</summary>
    public static ClaimKey TreeKey(TreeInfo t, int dim) => ClaimKey.Task($"tree:{t.Base.X},{t.Base.Y},{t.Base.Z}@{dim}");

    /// <summary>Who else is chopping <paramref name="t"/> (a lease on it, or this call's own skip list), else null.</summary>
    private string? TreeTaken(TreeInfo t, IReadOnlySet<BlockPos>? skip)
    {
        if (skip?.Contains(t.Base) == true) return "another body";
        if (TargetClaims is not { } b) return null;
        var key = TreeKey(t, World.Dimension);
        return b.IsFree(key) ? null : b.HolderOf(key) ?? "another body";
    }

    /// <summary>Another player (a sibling body) within 3 blocks of the trunk: prefer a tree with room to work (no body-blocking).</summary>
    private bool TreeCrowded(TreeInfo t) =>
        World.Entities.Nearby(t.Base.Center, 3, e => e.Kind == EntityKind.Player && !string.Equals(e.Name, Options.Username, StringComparison.OrdinalIgnoreCase), 1).Any();

    /// <summary>The tree chop_tree is felling right now (its logs are exempt from the stewardship guard).</summary>
    private volatile TreeInfo? _chopping;
    private readonly object _treeGuardLock = new();
    private (long At, HashSet<BlockPos> Logs)? _treeGuard;

    /// <summary>
    /// Stewardship guard for every non-chop path (dig, mine, pathfinder dig moves, scripts): true for a log of a living
    /// tree while felling any tree would leave the island below <see cref="MinTrees"/>. Census cached for 3 s.
    /// </summary>
    internal bool TreeGuarded(BlockPos p)
    {
        if (MinTrees <= 0 || !WorldMode.Current.ScarceTerrain || _chopping is { } t && t.Logs.Contains(p)) return false;
        if (World.Placed.IsPlaced(p, World.Dimension, World.GetBlock(p))) return false; // my own log pillar is never a tree (rule 1/2)
        long now = Environment.TickCount64;
        HashSet<BlockPos> logs;
        lock (_treeGuardLock)
        {
            if (_treeGuard is not { } g || now - g.At > 3000)
            {
                try { _treeGuard = (now, TreeStewardship.GuardedLogs(TreeStewardship.Census(World, p.Center), MinTrees)); }
                catch (Exception ex) { Log($"tree guard: census failed: {ex.Message}"); _treeGuard = (now, []); }
            }
            logs = _treeGuard!.Value.Logs;
        }
        return logs.Contains(p);
    }

    /// <summary>Forget the cached census (trees felled/planted).</summary>
    private void InvalidateTreeGuard() { lock (_treeGuardLock) _treeGuard = null; }

    /// <summary>Cheap blocks a short tree pillar may use (dug back afterwards; never dirt: unrenewable on Skyblock). A log is
    /// the last resort: the bot stands on its OWN pillar, never on the tree's leaves, and a placed log is in the placed-block
    /// log, so it never reads as part of the tree (<see cref="TreeScan"/>) and is always minable afterwards (rule 1).</summary>
    private static readonly int[] PillarBlocks = [Blocks.Cobblestone, Blocks.Netherrack, Blocks.Stone, Blocks.Planks, Blocks.Sandstone, Blocks.StoneBricks, Blocks.Log];

    private sealed class ChopState(TreeInfo tree)
    {
        public TreeInfo Tree { get; } = tree;
        public int LogsBefore, SaplingsBefore, ApplesBefore, Felled, LeavesBroken, LeavesDecayed;
        public int TrunkEdge;
        public bool PlacedSapling;
        /// <summary>The spot under a hanging remnant already had a sapling (nothing to plant).</summary>
        public bool AlreadyPlanted;
        public readonly List<BlockPos> Pillar = new();
        /// <summary>Block id the bot itself placed at each pillar cell: dig-back only takes exactly those back.</summary>
        public readonly Dictionary<BlockPos, int> PillarIds = new();
        /// <summary>Drops lying on leaves (out of pickup reach until the leaf under them goes).</summary>
        public readonly HashSet<int> StuckIds = new();
        public readonly HashSet<BlockPos> Failed = new();
        /// <summary>Item entities left lying near the void (counted once however often we look).</summary>
        public readonly HashSet<int> LostIds = new();
        public int Lost => LostIds.Count;
    }

    /// <summary>
    /// Fells ONE tree and replants it: the nearest one, the one with a log near <paramref name="at"/>, or the nearest of
    /// <paramref name="wood"/> (0 oak, 1 spruce, 2 birch). Digs only that tree's logs (best axe, else hand) from safe
    /// standing spots (interior side first, never closer to the void than the trunk, never on the tree), clearing its own
    /// leaves when they hide a log, jump-digging or pillaring 1-2 cobblestone for logs above reach. Then breaks its
    /// reachable leaves (not over the void) for saplings, waits a bounded time for decay, collects safe drops near the
    /// tree and plants a sapling on the original dirt if it has light and room. Never digs dirt/grass.
    /// </summary>
    public Task<ActionResult> ChopTreeAsync(BlockPos? at = null, int? wood = null, TreeChopOptions? options = null, CancellationToken ct = default) =>
        RunActionAsync("chop", ct, async c =>
        {
            var o = options ?? TreeChopOptions.Default;
            if (await SurvivalGateAsync("chop a tree", c).ConfigureAwait(false) is { } refused) return refused;
            if (DateTime.UtcNow - _lastUnreplanted > UnreplantedMemory) _unreplantedChops = 0;
            if (o.MaxUnreplantedChops > 0 && WorldMode.Current.ScarceTerrain && _unreplantedChops >= o.MaxUnreplantedChops)
                return ActionResult.Fail($"stopping: the last {_unreplantedChops} trees I chopped weren't replanted (last: {_lastReplantProblem ?? "not replanted"}); " +
                                         "fix the replanting first (a sapling, free lit dirt) before felling more");
            var skip = new HashSet<BlockPos>();
            // Trees this body failed on lately aren't picked again first (an explicit "chop that one" still is).
            if (at is null) lock (_failedTrees)
                foreach (var (b, until) in _failedTrees.ToList())
                    if (DateTime.UtcNow < until) skip.Add(b); else _failedTrees.Remove(b);
            var tree = ResolveTree(at, wood, o, out var why, skip);
            if (tree is null) return ActionResult.Fail(why!);
            ClaimKey? lease = null;
            if (TargetClaims is { } board)
            {
                // Lease it before walking there; a sibling that won the race keeps it and this body takes the next one.
                for (int tries = 0; ; tries++)
                {
                    var key = TreeKey(tree, World.Dimension);
                    var r = await board.TryClaimAsync(key, TreeLeaseTtl, "chop", c).ConfigureAwait(false);
                    if (r.Ok) { lease = key; break; }
                    if (r.Holder == null)
                    {
                        // The board didn't answer: undo a late grant and chop unleased (leases coordinate, they aren't safety).
                        try { await board.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
                        break;
                    }
                    Log($"chop: {tree} taken by {r.Holder}; picking the next free tree");
                    skip.Add(tree.Base);
                    if (tries >= 8 || ResolveTree(at, wood, o, out why, skip) is not { } next)
                        return ActionResult.Fail(why ?? "exhausted: every tree near here is taken by other bodies");
                    tree = next;
                }
            }
            ActionResult? result = null;
            try { return result = await ChopResolvedAsync(tree, o, c).ConfigureAwait(false); }
            finally
            {
                // A tree it couldn't fell (logs out of reach, no stand) stays leased a while, so the next body doesn't walk over and
                // fail the same way, and this body tries others first (live 10-30: 119 and 1337 took turns failing on one spruce).
                bool failedTree = result is { Ok: false } && !c.IsCancellationRequested;
                if (failedTree) lock (_failedTrees) _failedTrees[tree.Base] = DateTime.UtcNow + FailedTreeHold;
                if (lease is { } k)
                    try
                    {
                        if (failedTree) await TargetClaims!.TryClaimAsync(k, FailedTreeHold, "chop failed", CancellationToken.None).ConfigureAwait(false);
                        else await TargetClaims!.ReleaseAsync(k, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception) { /* TTL */ }
            }
        });

    private async Task<ActionResult> ChopResolvedAsync(TreeInfo tree, TreeChopOptions o, CancellationToken c)
    {
        {
            _chopping = tree;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(c);
            limit.CancelAfter(o.Timeout);
            var st = new ChopState(tree)
            {
                LogsBefore = World.Inventory.CountItem(Wood.LogId(tree.Wood), Wood.LogDamage(tree.Wood)),
                SaplingsBefore = World.Inventory.CountItem(Blocks.Sapling),
                ApplesBefore = World.Inventory.CountItem(Items.Apple),
                TrunkEdge = Math.Max(1, Safety.EdgeDistance(World, tree.Base.X, tree.Base.Y, tree.Base.Z, 2)),
            };
            Log($"chop: {tree} soil {BlockName(tree.SoilId, 0)}");
            string? stop = null;
            BlockPos? planted = null;
            string? problem = null;
            try
            {
                stop = await FellAsync(st, o, limit.Token).ConfigureAwait(false);
                await CleanupPillarAsync(st, limit.Token).ConfigureAwait(false);
                await GatherSaplingsAsync(st, o, limit.Token).ConfigureAwait(false);
                (planted, problem) = await ReplantAsync(st, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested && !c.IsCancellationRequested)
            {
                stop = "timed out";
            }
            catch (OperationCanceledException) when (st.Felled > 0 || st.LeavesBroken > 0)
            {
                stop = _abortReason ?? "stopped";
            }
            finally
            {
                _chopping = null;
                InvalidateTreeGuard();
            }
            return Finish(st, stop, planted, problem);
        }
    }

    // ------------------------------------------------------------------ target

    private TreeInfo? ResolveTree(BlockPos? at, int? wood, TreeChopOptions o, out string? why, IReadOnlySet<BlockPos>? skip = null)
    {
        int minTrees = o.MinTrees ?? MinTrees;
        TreeCensus? census = null;
        TreeCensus Census(TreeInfo t) => census ??= TreeStewardship.Census(World, t.Base.Center);
        // Only the tree's own species replants it (a birch spot gets a birch sapling, never a silent oak).
        string? Refusal(TreeInfo t) => minTrees <= 0 ? null
            : TreeStewardship.Refusal(Census(t), t, World.Inventory.CountItem(Blocks.Sapling, t.Wood), ReplantPrecheck(t), minTrees);
        var tree = PickTree(at, wood, o, Refusal, out why, skip);
        if (tree is not null && Refusal(tree) is { } no)
        {
            Log($"chop: refused {tree}: {no} (island: {Census(tree)})");
            why = no;
            return null;
        }
        return tree;
    }

    /// <summary>Why the bot couldn't replant <paramref name="t"/>'s spot even holding a sapling (null = it can).</summary>
    private string? ReplantPrecheck(TreeInfo t)
    {
        if (!t.OnSoil) return $"it grows from {BlockName(t.SoilId, 0)}, not dirt";
        if (EditBlocked(t.Base)) return BlockedReason(t.Base);
        if (Wood.NeedsFourSaplings(t.Wood)) return DarkOakReplant;
        return null;
    }

    /// <summary>1.12.2 BlockSapling: a dark oak sapling only grows as a 2x2 of four; one alone never becomes a tree. chop_tree
    /// plants one sapling, so it reports this instead of planting a dud (and never counts it as a failed replant).</summary>
    private const string DarkOakReplant = "a dark oak only grows from 4 dark oak saplings planted as a 2x2; plant them together";

    private TreeInfo? PickTree(BlockPos? at, int? wood, TreeChopOptions o, Func<TreeInfo, string?> refusal, out string? why,
        IReadOnlySet<BlockPos>? skip = null)
    {
        why = null;
        TreeInfo? tree = null;
        // A named spot whose tree another body holds: the next free tree near that spot, never following it there.
        Vec3d? around = null;
        if (at is { } p)
        {
            var log = TreeScan.LogNear(World, p);
            tree = log is { } l ? TreeScan.Analyze(World, l) : null;
            if (tree is null)
            {
                why = $"no tree at {Fmt(p)}" + (Frontier(p.Center, 0, wood, skip) is { Length: > 0 } f ? $"; nearest trees: {f}" : "");
                return null;
            }
            if (tree.Logs.All(EditBlocked)) { why = $"tree at {Fmt(p)} is inside spawn protection (the server undid my digs there; needs op)"; return null; }
            if (TreeTaken(tree, skip) is { } holder)
            {
                Log($"chop: {tree} is {holder}'s; taking the next free tree near {Fmt(p)}");
                around = p.Center;
                tree = null;
            }
        }
        if (tree is null)
        {
            var center = around ?? World.Player.Position;
            var all = TreeScan.FindTrees(World, center, o.SearchRadius, wood)
                .Where(t => t.Logs.Any(l => !UserProtected(l, Wood.LogId(t.Wood))) && (t.OnSoil || t.Leaves.Count >= 3 || TreeScan.Unfinished(World, t) is not null)).ToList();
            var open = all.Where(t => TreeTaken(t, skip) == null).ToList();
            if (open.Count == 0 && all.Count > 0)
            {
                var holders = all.Select(t => TreeTaken(t, skip)).Where(h => h != null).Distinct().Take(3);
                why = $"exhausted: the {all.Count} tree(s) within {o.SearchRadius:0} are all taken by other bodies ({string.Join(", ", holders)})"
                      + (Frontier(center, o.SearchRadius, wood, skip) is { Length: > 0 } f ? $"; free trees beyond: {f}" : "; no free tree beyond in view");
                return null;
            }
            all = open;
            var trees = all.Where(t => !t.Logs.All(EditBlocked)).ToList();
            if (trees.Count == 0 && all.Count > 0)
            {
                why = $"the trees within {o.SearchRadius:0} are inside spawn protection (the server undid my digs within {_protectedRegion?.Radius ?? VanillaSpawnProtection} of spawn; needs op)";
                return null;
            }
            // Stewardship first (allowed trees), then the tree whose replant is most certain: on dirt, outside any protected
            // area, not on the rim (drops there fall into the void), then the nearest.
            // Our own unfinished tree (a chop that stopped part-way) goes before any new one.
            tree = trees.OrderBy(t => refusal(t) is null ? 0 : 1)
                .ThenBy(t => TreeScan.Unfinished(World, t) is not null ? 0 : 1)
                .ThenBy(t => TreeCrowded(t) ? 1 : 0)
                .ThenBy(t => t.OnSoil ? 0 : 1)
                .ThenBy(t => ReplantPrecheck(t) is null ? 0 : 1)
                .ThenBy(t => Safety.EdgeDistance(World, t.Base.X, t.Base.Y, t.Base.Z, 2) >= 2 ? 0 : 1)
                .ThenBy(t => t.Base.Center.DistanceSquaredTo(World.Player.Position)).FirstOrDefault();
            if (tree is null)
            {
                // Typed: the area is used up here, and where the work goes on (the brain widens it with one chop_tree at=).
                why = $"exhausted: no {(wood is { } w ? TreeScan.WoodName(w) + " " : "")}tree within {o.SearchRadius:0}"
                      + (Frontier(center, o.SearchRadius, wood, skip) is { Length: > 0 } f ? $"; nearest beyond: {f}" : "; none beyond in view either");
                return null;
            }
        }
        if (!tree.OnSoil && tree.Leaves.Count < 3 && TreeScan.Unfinished(World, tree) is null)
        {
            why = $"logs at {Fmt(tree.Base)} sit on {BlockName(tree.SoilId, 0)} with no leaves: a build, not a tree";
            return null;
        }
        if (tree.Logs.All(l => UserProtected(l, Wood.LogId(tree.Wood))))
        {
            why = $"the {tree.WoodName} tree at {Fmt(tree.Base)} is protected";
            return null;
        }
        return tree;
    }

    /// <summary>Widest look for trees past the chop radius: this body's own loaded view (view distance 4 = 64 blocks).</summary>
    public const double FrontierRadius = 64;

    /// <summary>
    /// The nearest free living trees beyond <paramref name="inner"/> blocks of <paramref name="center"/> (within
    /// <see cref="FrontierRadius"/>): "oak 200,64,300 (40 m E), spruce ... (+5 more)", or "" when none is in view.
    /// </summary>
    internal string Frontier(Vec3d center, double inner, int? wood = null, IReadOnlySet<BlockPos>? skip = null)
    {
        List<TreeInfo> beyond;
        try
        {
            beyond = TreeScan.FindTrees(World, center, FrontierRadius, wood)
                .Where(t => TreeStewardship.IsLiving(t) && t.Base.Center.DistanceTo(center) > inner && TreeTaken(t, skip) == null
                            && !t.Logs.All(l => UserProtected(l, Wood.LogId(t.Wood)))).ToList();
        }
        catch (Exception) { return ""; }
        if (beyond.Count == 0) return "";
        var me = World.Player.Position;
        string Dir(BlockPos b)
        {
            double dx = b.X + 0.5 - me.X, dz = b.Z + 0.5 - me.Z;
            return Math.Abs(dx) >= Math.Abs(dz) ? (dx >= 0 ? "E" : "W") : (dz < 0 ? "N" : "S");
        }
        return string.Join(", ", beyond.Take(3).Select(t => $"{t.WoodName} {Fmt(t.Base)} ({t.Base.Center.DistanceTo(me):0} m {Dir(t.Base)})"))
               + (beyond.Count > 3 ? $" (+{beyond.Count - 3} more)" : "");
    }

    /// <summary>Protection set by the owner (config, memory, claims) without the stewardship guard: chop_tree applies
    /// stewardship itself, with the precise refusal.</summary>
    private bool UserProtected(BlockPos p, int id) => IsProtected?.Invoke(p, id) ?? false;

    // ------------------------------------------------------------------ felling

    private List<BlockPos> RemainingLogs(ChopState st) =>
        st.Tree.Logs.Where(l => World.GetBlock(l) is var id && Wood.IsLog(id) && !st.Failed.Contains(l) && !IsBlockProtected(l, id)).ToList();

    /// <summary>Column under <paramref name="p"/> is void: whatever drops there is gone.</summary>
    private bool DropsIntoVoid(BlockPos p) => Safety.IsOverVoid(World, p.X, p.Y - 1, p.Z);

    /// <summary>A target in reach from where the bot stands now and safe to dig (not holding the bot up).</summary>
    private BlockPos? DiggableNow(IEnumerable<BlockPos> cands)
    {
        var eye = World.Player.EyePosition;
        return cands.Where(p => Aiming.FindDigAim(World, eye, p, Options.Reach) is not null
                                && DigSafetyVeto(p, "x", generator: false, positioned: true) is null)
            .OrderBy(p => DropsIntoVoid(p) ? 1 : 0).ThenBy(p => p.Y).ThenBy(p => p.Center.DistanceSquaredTo(eye))
            .Cast<BlockPos?>().FirstOrDefault();
    }

    /// <returns>Null when every log is down, else why felling stopped.</returns>
    private async Task<string?> FellAsync(ChopState st, TreeChopOptions o, CancellationToken ct)
    {
        int failures = 0, idle = 0;
        while (true)
        {
            var remaining = RemainingLogs(st);
            if (remaining.Count == 0) return st.Failed.Count > 0 ? $"{st.Failed.Count} log(s) wouldn't break" : null;
            await SurvivalWindowAsync(ct).ConfigureAwait(false);

            if (DiggableNow(remaining) is { } log)
            {
                var r = await DigCoreAsync(log, ct, allowPathing: false).ConfigureAwait(false);
                if (r.Ok) { st.Felled++; idle = 0; }
                else
                {
                    st.Failed.Add(log);
                    Log($"chop: {r.Message}");
                    if (++failures >= 6) return "too many failed digs, last: " + r.Message;
                }
                continue;
            }
            if (++idle > 24) return $"stuck with {remaining.Count} log(s) left";

            // 1. Walk to a safe spot (2+ from any edge) that sees a log, lowest first.
            var lowest = remaining.OrderBy(l => l.Y).Take(4).ToList();
            if ((await GotoSeeingAsync(st, lowest, ct, strictOnly: true).ConfigureAwait(false)).Ok && DiggableNow(remaining) is not null) continue;
            // 2. Its own leaves hide logs that are otherwise in reach: clear one next to them (also a sapling chance).
            if (lowest[0].Y - st.Tree.Base.Y <= 5 && await ClearOccludingLeafAsync(st, lowest, ct).ConfigureAwait(false)) continue;
            // 3. Just above reach: jump-dig from a safe spot.
            if ((await GotoSeeingAsync(st, lowest, ct, strictOnly: true, eyeBoost: JumpEyeBoost).ConfigureAwait(false)).Ok
                && await JumpDigAsync(st, remaining, ct).ConfigureAwait(false)) { idle = 0; continue; }
            // 4. A spot no closer to the void than the trunk (e.g. inside the trunk column of a rim tree).
            if ((await GotoSeeingAsync(st, lowest, ct, relaxedOnly: true).ConfigureAwait(false)).Ok && DiggableNow(remaining) is not null) continue;
            // 5. Short pillar (cheap block, never dirt), then jump from it if needed.
            if (st.Pillar.Count < o.MaxPillar && await PillarUpAsync(st, remaining, ct).ConfigureAwait(false))
            {
                if (DiggableNow(remaining) is null) await JumpDigAsync(st, remaining, ct).ConfigureAwait(false);
                continue;
            }
            int top = remaining.Max(l => l.Y);
            return $"{remaining.Count} log(s) out of reach (up to y {top})"
                   + (st.Pillar.Count == 0 && PillarBlock() is null ? "; no cobble/planks to pillar" : "");
        }
    }

    /// <summary>Standing cell rules: solid footing that isn't part of the tree, no lava, and away from the void
    /// (strict: 2+ from any edge; relaxed: never closer to the void than the trunk itself).</summary>
    private bool TreeStandOk(ChopState st, int x, int y, int z, bool strict)
    {
        if (!Safety.IsStandable(World, x, y, z) || Safety.LavaNear(World, x, y, z)) return false;
        var under = new BlockPos(x, y - 1, z);
        if (st.Tree.Logs.Contains(under) || st.Tree.Leaves.Contains(under) || Wood.IsLog(World.GetBlock(under)) || Wood.IsLeaves(World.GetBlock(under))) return false;
        int edge = Safety.EdgeDistance(World, x, y, z, 2);
        return strict ? edge >= 2 : edge >= Math.Min(2, st.TrunkEdge);
    }

    /// <summary>Eye gain at the top of a jump (vanilla jump 0.42 → apex ≈ 1.25 blocks).</summary>
    private const double JumpEyeBoost = 1.24;

    private bool CanAimFrom(int x, int y, int z, BlockPos target, double margin = 0.3, double eyeBoost = 0)
    {
        var eye = new Vec3d(x + 0.5, y + PlayerPhysics.EyeHeight + eyeBoost, z + 0.5);
        if (eye.DistanceTo(target.Center) > Options.Reach + 0.9) return false;
        return Aiming.FindDigAim(World, eye, target, Options.Reach - margin) is not null;
    }

    /// <summary>Goes to a spot near the tree from which one of <paramref name="targets"/> can be dug: strict (2+ from any
    /// edge) first, then relaxed (never closer to the void than the trunk). <paramref name="eyeBoost"/> &gt; 0 = reach at the
    /// top of a jump (strict spots with headroom only).</summary>
    private async Task<ActionResult> GotoSeeingAsync(ChopState st, IReadOnlyList<BlockPos> targets, CancellationToken ct, bool strictOnly = false,
        bool relaxedOnly = false, double eyeBoost = 0)
    {
        if (targets.Count == 0) return ActionResult.Fail("nothing to reach");
        var o = GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(30), MaxReplans = 2, AllowPartial = false };
        var center = targets[0];
        bool[] passes = strictOnly || eyeBoost > 0 ? [true] : relaxedOnly ? [false] : [true, false];
        foreach (bool strict in passes)
        {
            Goal goal = new GoalFiltered(new GoalNear(center, 6 + eyeBoost),
                (x, y, z) => TreeStandOk(st, x, y, z, strict) && (eyeBoost <= 0 || !BlockShapes.HasCollision(World, x, y + 2, z))
                             && targets.Any(t => CanAimFrom(x, y, z, t, margin: eyeBoost > 0 ? 0 : 0.3, eyeBoost: eyeBoost)),
                strict ? "safe spot by the tree" : "spot by the tree, not on the void side");
            var g = await GotoCoreAsync(goal, o, ct).ConfigureAwait(false);
            if (g.Ok) return g;
        }
        return ActionResult.Fail("no safe spot in reach");
    }

    private async Task<bool> ClearOccludingLeafAsync(ChopState st, IReadOnlyList<BlockPos> logs, CancellationToken ct)
    {
        var leaves = st.Tree.Leaves
            .Where(p => World.GetBlock(p) is var id && Wood.IsLeaves(id) && !st.Failed.Contains(p) && !IsBlockProtected(p, id)
                        && logs.Any(l => l.ManhattanTo(p) <= 2))
            .OrderBy(p => DropsIntoVoid(p) ? 1 : 0).ThenBy(p => logs.Min(l => l.ManhattanTo(p))).ThenBy(p => p.Y).ToList();
        if (leaves.Count == 0) return false;
        if (DiggableNow(leaves) is not { } leaf)
        {
            if (!(await GotoSeeingAsync(st, leaves.Take(12).ToList(), ct, strictOnly: true).ConfigureAwait(false)).Ok) return false;
            if (DiggableNow(leaves) is not { } l2) return false;
            leaf = l2;
        }
        var r = await DigCoreAsync(leaf, ct, allowPathing: false, noShears: true).ConfigureAwait(false);
        if (r.Ok) st.LeavesBroken++;
        else st.Failed.Add(leaf);
        return r.Ok;
    }

    /// <summary>
    /// Jump-reach: from a safe spot (2+ from the edge, clear headroom), jump, start digging at the top of the jump when the
    /// log comes into reach, land and finish (the b1.8.1 server times the dig from the start packet and only checks the
    /// distance; digging is 5x slower in the air, so the finish is sent on the ground).
    /// </summary>
    private async Task<bool> JumpDigAsync(ChopState st, List<BlockPos> remaining, CancellationToken ct)
    {
        var me = World.Player.Position;
        int fx = (int)Math.Floor(me.X), fy = (int)Math.Floor(me.Y + 1e-6), fz = (int)Math.Floor(me.Z);
        if (!World.Player.OnGround || Safety.EdgeDistance(World, fx, fy, fz, 2) < 2 || BlockShapes.HasCollision(World, fx, fy + 2, fz)) return false;
        var standOn = Safety.OtherPlayersSupport(World); // never dig out from under another player (hard safety)
        var target = remaining.Where(l => !standOn.Contains(l) && l.Center.DistanceTo(World.Player.EyePosition) <= Options.Reach + JumpEyeBoost + 0.9)
            .OrderBy(l => l.Y).Cast<BlockPos?>().FirstOrDefault();
        if (target is not { } t) return false;
        int id = World.GetBlock(t);
        int held = await SelectToolForAsync(id, ct).ConfigureAwait(false);
        int ticks = DigTicksNow(id, World.GetMeta(t), onGround: true, inWater: false);
        if (ticks == int.MaxValue) return false;

        DigAim? aim = null;
        int phase = 0;
        int eid = World.Player.EntityId;
        // Tick thread: only look for the moment the log comes into reach; packets go out from the action thread.
        await DriveAsync(phys =>
        {
            phase++;
            if (phase == 1) return new DriverStep(new MoveInput(Jump: true, Yaw: _lookYaw, Pitch: _lookPitch));
            if (Aiming.FindDigAim(World, phys.Position + new Vec3d(0, PlayerPhysics.EyeHeight, 0), t, Options.Reach) is { } a)
            {
                aim = a;
                return new DriverStep(new MoveInput(Yaw: a.Yaw, Pitch: a.Pitch), Done: true);
            }
            return new DriverStep(new MoveInput(Yaw: _lookYaw, Pitch: _lookPitch), Done: phase > 3 && phys.OnGround || phase > 40);
        }, ct).ConfigureAwait(false);
        if (aim is not { } hit) return false;
        _lookYaw = hit.Yaw;
        _lookPitch = hit.Pitch;
        byte face = (byte)hit.Face;
        Send(new AnimationPacket { EntityId = eid, Animation = 1 });
        Send(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusStarted, X = t.X, Y = t.Y, Z = t.Z, Face = face });
        long started = TickCount;
        await WaitUntilAsync(() => World.Player.OnGround, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

        int lastSwing = 0;
        while (TickCount < started + ticks + 2 && World.GetBlock(t) == id)
        {
            await WaitTicksAsync(2, ct).ConfigureAwait(false);
            int el = (int)(TickCount - started);
            if (el - lastSwing >= 4) { lastSwing = el; Send(new AnimationPacket { EntityId = eid, Animation = 1 }); }
        }
        Send(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusFinished, X = t.X, Y = t.Y, Z = t.Z, Face = face });
        bool broke = await WaitUntilAsync(() => World.GetBlock(t) != id, TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false);
        Log($"chop: jump-dig {Fmt(t)} {(broke ? "ok" : "didn't break")}");
        if (broke) NoteEditOk(t);
        else NoteEditReverted(t, "dig");
        if (broke) st.Felled++;
        else st.Failed.Add(t);
        return broke;
    }

    private int? PillarBlock()
    {
        foreach (int id in PillarBlocks)
            if (!ReservedItems.Contains(id) && World.Inventory.CountItem(id) > 0) return id;
        return null;
    }

    /// <summary>Places one cheap block under itself (cobblestone/netherrack, never dirt) in the felled trunk column or right
    /// beside it, only 3+ from any edge, so the next log comes into (jump) reach. The pillar is dug back afterwards.</summary>
    private async Task<bool> PillarUpAsync(ChopState st, List<BlockPos> remaining, CancellationToken ct)
    {
        if (PillarBlock() is not { } block) return false;
        var trunk = st.Tree.Base;
        var lowest = remaining.OrderBy(l => l.Y).Take(3).ToList();
        int feetY = Pathfinder.StartCellFor(World, World.Player.Position)?.Y ?? World.Player.Position.ToBlockPos().Y;
        bool Spot(int x, int y, int z, bool strictTrunk)
        {
            int ring = Math.Max(Math.Abs(x - trunk.X), Math.Abs(z - trunk.Z));
            if (strictTrunk ? ring != 0 : ring != 1) return false;
            var under = new BlockPos(x, y - 1, z);
            bool footing = st.Pillar.Contains(under) || TreeStandOk(st, x, y, z, strict: true);
            return footing && Safety.IsStandable(World, x, y, z) && Safety.EdgeDistance(World, x, y, z, 3) >= 3
                   && !BlockShapes.HasCollision(World, x, y + 2, z) && !BlockShapes.HasCollision(World, x, y + 3, z)
                   && lowest.Any(l => CanAimFrom(x, y + 1, z, l, margin: 0, eyeBoost: JumpEyeBoost));
        }
        var go = GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(30), MaxReplans = 2, AllowPartial = false };
        bool there = false;
        foreach (bool trunkColumn in new[] { true, false })
        {
            Goal g = new GoalFiltered(new GoalNear(trunk.X, feetY, trunk.Z, 3), (x, y, z) => Spot(x, y, z, trunkColumn),
                trunkColumn ? "pillar spot in the trunk" : "pillar spot by the trunk");
            if ((await GotoCoreAsync(g, go, ct).ConfigureAwait(false)).Ok) { there = true; break; }
        }
        if (!there) return false;
        var feet = Pathfinder.StartCellFor(World, World.Player.Position) ?? World.Player.Position.ToBlockPos();
        bool ok = await PillarOnceAsync(feet, block, ct).ConfigureAwait(false);
        // The feet cell was air (the bot stood in it): a block there now is the one just placed.
        if (World.GetBlock(feet) == block) { st.Pillar.Add(feet); st.PillarIds[feet] = block; }
        Log($"chop: pillar {BlockName(block, 0)} at {Fmt(feet)}: {(ok ? "ok" : "failed")}");
        return ok && st.Pillar.Contains(feet);
    }

    /// <summary>Centre on the column, jump, and at the top of the jump place <paramref name="block"/> into the feet cell
    /// against the top of the block below (predicted locally like vanilla, confirmed by the server). Only once the last
    /// position sent is already above the cell, so the server doesn't refuse it for overlapping the player.</summary>
    private async Task<bool> PillarOnceAsync(BlockPos feet, int block, CancellationToken ct)
    {
        if (await SelectItemAsync(block, null, ct).ConfigureAwait(false) < 0) return false;
        var against = feet.Down();
        int stage = 0, ticks = 0;
        double lastY = double.NaN;
        await DriveAsync(phys =>
        {
            ticks++;
            var pos = phys.Position;
            var c = new Vec3d(feet.X + 0.5, pos.Y, feet.Z + 0.5);
            double dx = c.X - pos.X, dz = c.Z - pos.Z, d = Math.Sqrt(dx * dx + dz * dz);
            float yaw = d > 0.05 ? PlayerPhysics.YawPitchTo(pos, c).Yaw : phys.Yaw;
            var step = new DriverStep(new MoveInput(Yaw: yaw, Pitch: 90), Done: ticks > 80);
            switch (stage)
            {
                case 0 when d > 0.15:
                    step = new DriverStep(new MoveInput(Forward: d > 0.3 ? 1 : 0.3f, Sneak: true, Yaw: yaw, Pitch: 90), Done: ticks > 80);
                    break;
                case 0 when phys.OnGround:
                    stage = 1;
                    step = new DriverStep(new MoveInput(Jump: true, Yaw: yaw, Pitch: 90));
                    break;
                case 1 when pos.Y >= feet.Y + 1.1 && lastY >= feet.Y + 1.0 && Aiming.IsReplaceable(World, World.GetBlock(feet)):
                    Send(new PlayerBlockPlacementPacket { X = against.X, Y = against.Y, Z = against.Z, Direction = (byte)BlockFace.Top, HeldItem = HeldStack() });
                    Send(new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 });
                    World.SetBlock(feet.X, feet.Y, feet.Z, block, 0); // client-side prediction (vanilla ItemBlock.onItemUse)
                    stage = 2;
                    break;
                case 1 when phys.OnGround && ticks > 4:
                    step = step with { Done = true }; // landed without placing
                    break;
                case 2 when phys.OnGround:
                    step = step with { Done = true };
                    break;
            }
            lastY = pos.Y;
            return step;
        }, ct).ConfigureAwait(false);
        if (stage < 2) return false;
        // The server confirms with a block change (or reverts it, and we fall back down).
        await WaitTicksAsync(4, ct).ConfigureAwait(false);
        return World.GetBlock(feet) == block && World.Player.Position.Y >= feet.Y + 1 - 1e-3;
    }

    /// <summary>Steps off the pillar and digs it back: only cells the bot itself placed this chop, only while they still
    /// hold exactly the block it placed (never dirt/grass, never someone else's block).</summary>
    private async Task CleanupPillarAsync(ChopState st, CancellationToken ct)
    {
        st.Pillar.RemoveAll(p => !IsOwnPillarBlock(st, p));
        if (st.Pillar.Count == 0) return;
        var bottom = st.Pillar.OrderBy(p => p.Y).First();
        Goal ground = new GoalFiltered(new GoalNear(bottom, 3),
            (x, y, z) => y == bottom.Y && TreeStandOk(st, x, y, z, strict: true) && !st.Pillar.Contains(new BlockPos(x, y - 1, z)), "ground by the pillar");
        await GotoCoreAsync(ground, GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(20), MaxReplans = 2, AllowPartial = false }, ct).ConfigureAwait(false);
        foreach (var p in st.Pillar.OrderByDescending(p => p.Y).ToList())
        {
            if (!IsOwnPillarBlock(st, p)) { st.Pillar.Remove(p); continue; }
            var r = await DigCoreAsync(p, ct).ConfigureAwait(false);
            if (r.Ok) st.Pillar.Remove(p);
            else Log($"chop: couldn't take the pillar block back: {r.Message}");
        }
    }

    private bool IsOwnPillarBlock(ChopState st, BlockPos p)
    {
        int id = World.GetBlock(p);
        bool own = st.PillarIds.TryGetValue(p, out int placed) && placed == id && id is not (Blocks.Dirt or Blocks.Grass or Blocks.Farmland);
        if (!own) Log($"chop: leaving {BlockName(id, World.GetMeta(p))} at {Fmt(p)}: not a pillar block I placed");
        return own;
    }

    // ------------------------------------------------------------------ saplings

    private int SaplingsGained(ChopState st) => World.Inventory.CountItem(Blocks.Sapling) - st.SaplingsBefore;

    private List<BlockPos> LeavesLeft(ChopState st) =>
        st.Tree.Leaves.Where(p => Wood.IsLeaves(World.GetBlock(p))).ToList();

    private Vec3d TreeCenter(ChopState st) => new(st.Tree.Base.X + 0.5, st.Tree.Base.Y + 0.5, st.Tree.Base.Z + 0.5);

    private async Task CollectAroundTreeAsync(ChopState st, CancellationToken ct, int max = 32)
    {
        await CollectDropsCoreAsync(7, max, ct, TreeCenter(st), st.LostIds, st.StuckIds).ConfigureAwait(false);
    }

    /// <summary>Drops near the tree lying on a leaf that can be broken without sending them into the void.</summary>
    private List<(int Id, int Item, BlockPos Leaf)> StuckDrops(ChopState st) =>
        World.Entities.Nearby(TreeCenter(st), 9, e => e.Kind == EntityKind.Item && World.Inventory.RoomFor(e.Item) > 0)
            .Select(e => (e.Id, Item: (int)e.Item.Id, Leaf: TreeScan.SupportingLeaf(World, e.Position)))
            .Where(t => t.Leaf is { } l && !st.Failed.Contains(l) && !DropsIntoVoid(l) && !IsBlockProtected(l, World.GetBlock(l)) && !EditBlocked(l))
            .Select(t => (t.Id, t.Item, t.Leaf!.Value))
            .ToList();

    /// <summary>
    /// Saplings (and logs) that landed up in the crown sit out of pickup range: the b1.8.1 pickup box only grows sideways,
    /// so from the ground nothing above head height is collected. Break the leaf each one rests on (saplings first) so it
    /// drops to the ground, then collect. Bounded; a leaf over the void is never broken for this.
    /// </summary>
    private async Task<int> FreeStuckDropsAsync(ChopState st, CancellationToken ct, int maxDigs = 12)
    {
        int freed = 0;
        for (int guard = 0; guard < maxDigs; guard++)
        {
            var stuck = StuckDrops(st);
            if (stuck.Count == 0) break;
            var leaves = stuck.OrderBy(t => t.Item == Blocks.Sapling ? 0 : 1).ThenBy(t => t.Leaf.Y)
                .Select(t => t.Leaf).Distinct().ToList();
            // A support leaf boxed in by other leaves can't be seen: open a way through a leaf beside it.
            var openers = leaves.SelectMany(l => BlockFaceExtensions.All.Select(f => l.Offset(f)))
                .Where(n => !leaves.Contains(n) && World.GetBlock(n) is var nid && Wood.IsLeaves(nid) && !st.Failed.Contains(n) && !DropsIntoVoid(n)
                            && !IsBlockProtected(n, nid) && !EditBlocked(n))
                .Distinct().OrderBy(n => n.Y).ToList();
            BlockPos? pick = DiggableNow(leaves) ?? DiggableNow(openers);
            if (pick is null)
            {
                var seen = await GotoSeeingAsync(st, leaves.Take(8).ToList(), ct).ConfigureAwait(false);
                if (!seen.Ok && openers.Count > 0) seen = await GotoSeeingAsync(st, openers.Take(12).ToList(), ct).ConfigureAwait(false);
                if (!seen.Ok) break;
                pick = DiggableNow(leaves) ?? DiggableNow(openers);
                if (pick is null) break;
            }
            var leaf = pick.Value;
            var r = await DigCoreAsync(leaf, ct, allowPathing: false, noShears: true).ConfigureAwait(false);
            if (r.Ok) { st.LeavesBroken++; freed++; }
            else { st.Failed.Add(leaf); continue; }
            await WaitTicksAsync(8, ct).ConfigureAwait(false); // b1.8 item gravity: a few ticks to land on the next block
        }
        if (freed > 0)
        {
            Log($"chop: broke {freed} leaf/leaves holding drops up in the crown");
            await CollectAroundTreeAsync(st, ct).ConfigureAwait(false);
        }
        return freed;
    }

    /// <summary>The tree's leaves that will decay by themselves (no log keeps them alive any more).</summary>
    private List<BlockPos> DecayingLeaves(ChopState st)
    {
        var left = LeavesLeft(st);
        if (left.Count == 0) return left;
        var alive = TreeScan.SupportedLeaves(World, left);
        return left.Where(l => !alive.Contains(l)).ToList();
    }

    /// <summary>
    /// Breaks the tree's reachable leaves (none over the void), frees drops stuck in the crown, then waits a bounded time
    /// for the rest to decay (only while some leaf can still decay and decay is making progress); collects drops.
    /// </summary>
    private async Task GatherSaplingsAsync(ChopState st, TreeChopOptions o, CancellationToken ct)
    {
        await CollectAroundTreeAsync(st, ct).ConfigureAwait(false);
        // A normal world has saplings to spare: leave the crown to decay by itself (no leaf clearing, no decay wait); only
        // the drops already on the ground are picked up. Skyblock clears the crown for every sapling chance.
        if (!WorldMode.Current.ScarceTerrain) return;
        var sw = Stopwatch.StartNew();
        var skip = new HashSet<BlockPos>();
        int sinceCollect = 0, misses = 0;
        while (sw.Elapsed < o.LeafPhase && st.LeavesBroken < o.MaxLeafBreaks)
        {
            var cands = LeavesLeft(st).Where(p => !skip.Contains(p) && !DropsIntoVoid(p) && !IsBlockProtected(p, World.GetBlock(p)) && !EditBlocked(p)).ToList();
            if (cands.Count == 0) break;
            if (DiggableNow(cands) is not { } leaf)
            {
                var near = cands.OrderBy(p => p.Y).ThenBy(p => p.Center.DistanceSquaredTo(World.Player.Position)).Take(16).ToList();
                if (++misses > 4 || !(await GotoSeeingAsync(st, near, ct, strictOnly: true).ConfigureAwait(false)).Ok) break;
                continue;
            }
            misses = 0;
            var r = await DigCoreAsync(leaf, ct, allowPathing: false, noShears: true).ConfigureAwait(false);
            if (r.Ok) st.LeavesBroken++;
            else skip.Add(leaf);
            if (++sinceCollect >= 12)
            {
                sinceCollect = 0;
                await CollectAroundTreeAsync(st, ct, 16).ConfigureAwait(false);
            }
        }
        await CollectAroundTreeAsync(st, ct).ConfigureAwait(false);
        await FreeStuckDropsAsync(st, ct).ConfigureAwait(false);

        // The rest decays by itself (random ticks, ~20 s per leaf on average): wait while that is still happening.
        int leftBefore = LeavesLeft(st).Count, prev = leftBefore, brokenBefore = st.LeavesBroken;
        var wait = Stopwatch.StartNew();
        var progress = Stopwatch.StartNew();
        while (wait.Elapsed < o.DecayWait && SaplingsGained(st) < o.WantSaplings)
        {
            var decaying = DecayingLeaves(st);
            if (decaying.Count == 0 && StuckDrops(st).Count == 0) break;
            await WaitUntilAsync(() => false, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            await FreeStuckDropsAsync(st, ct, maxDigs: 4).ConfigureAwait(false);
            await CollectAroundTreeAsync(st, ct, 16).ConfigureAwait(false);
            int now = LeavesLeft(st).Count;
            if (now < prev) { prev = now; progress.Restart(); }
            else if (progress.Elapsed > o.DecayStall) break;
        }
        st.LeavesDecayed = Math.Max(0, leftBefore - LeavesLeft(st).Count - (st.LeavesBroken - brokenBefore));
        if (wait.Elapsed > TimeSpan.Zero)
        {
            await FreeStuckDropsAsync(st, ct, maxDigs: 4).ConfigureAwait(false);
            await CollectAroundTreeAsync(st, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ replanting

    /// <summary>Consecutive chops that felled a tree but left its spot unplanted (see <see cref="TreeChopOptions.MaxUnreplantedChops"/>).</summary>
    private int _unreplantedChops;
    private DateTime _lastUnreplanted;
    private string? _lastReplantProblem;
    /// <summary>The unreplanted-chop streak is forgotten after this long without another failed replant.</summary>
    private static readonly TimeSpan UnreplantedMemory = TimeSpan.FromMinutes(15);

    /// <summary>A sapling was planted some other way (plant tool, a player): the unreplanted-chop streak starts over.</summary>
    public void NoteSaplingPlanted() => _unreplantedChops = 0;

    /// <summary>b1.8.1 BlockSapling.canBlockStay: dirt/grass below and (full light ≥ 8 or sees the sky). Plus room for
    /// the tree to grow (4 cells of air/leaves above), otherwise the sapling never becomes a tree.</summary>
    private string? ReplantProblem(TreeInfo tree) => ReplantProblem(tree.Base, tree.Soil);

    private string? ReplantProblem(BlockPos cell, BlockPos soilPos)
    {
        int soil = World.GetBlock(soilPos);
        // Only a tree that grew from dirt gets here (hanging remnants are handled before): the dirt really was there.
        if (soil is not (Blocks.Dirt or Blocks.Grass)) return $"the dirt under the tree at {Fmt(soilPos)} is gone (now {BlockName(soil, 0)})";
        int here = World.GetBlock(cell);
        if (!Aiming.IsReplaceable(World, here)) return $"{BlockName(here, World.GetMeta(cell))} on the dirt at {Fmt(cell)}";
        for (int dy = 1; dy <= 4; dy++)
        {
            int id = World.GetBlock(cell.Up(dy));
            if (id != 0 && !Wood.IsLeaves(id) && BlockShapes.HasCollision(World, cell.X, cell.Y + dy, cell.Z))
                return $"no room to grow ({BlockName(id, 0)} at {Fmt(cell.Up(dy))})";
        }
        if (!SeesSky(cell) && TreeScan.SaplingLight(World, cell) < 8)
            return $"too dark at {Fmt(cell)} (needs light 8+)";
        return null;
    }


    /// <summary>Vanilla canBlockSeeTheSky: nothing with light opacity above (leaves and water count, glass/torches don't).</summary>
    private bool SeesSky(BlockPos p)
    {
        for (int y = p.Y + 1, h = World.WorldHeight; y < h; y++)
        {
            int id = World.GetBlock(p.X, y, p.Z);
            if (id == 0 || id == Blocks.Glass) continue;
            var b = Blocks.Get(id);
            if (Wood.IsLeaves(id) || b.Liquid || b.Solid) return false;
        }
        return true;
    }

    private async Task<(BlockPos? Planted, string? Problem)> ReplantAsync(ChopState st, CancellationToken ct)
    {
        var tree = st.Tree;
        if (World.GetBlock(tree.Base) == Blocks.Sapling) return (tree.Base, null); // someone (or decay timing) beat us to it
        if (RemainingLogs(st).Any(l => l == tree.Base)) return (null, "the trunk is still standing");
        var cell = tree.Base;
        var soilPos = tree.Soil;
        if (!tree.OnSoil)
        {
            // Logs left hanging after their lower trunk was cut: the tree's real spot is the dirt under the gap. Never
            // report that dirt as "gone": it was never under these logs.
            var (sapling, dirt, other) = TreeScan.ColumnUnder(World, tree.Soil);
            if (sapling is { } sp)
            {
                st.AlreadyPlanted = true;
                return (sp, null);
            }
            if (dirt is not { } d)
                return (null, other is { } o
                    ? $"these logs hung above {BlockName(World.GetBlock(o), World.GetMeta(o))} at {Fmt(o)}, not dirt; nothing to replant"
                    : "these logs hung over the void; nothing to replant");
            soilPos = d;
            cell = d.Up();
        }
        if (World.GetBlock(cell) == Blocks.Sapling) { st.AlreadyPlanted = cell != tree.Base; return (cell, null); }
        if (ReplantProblem(cell, soilPos) is { } p) return (null, p);
        if (Wood.NeedsFourSaplings(tree.Wood)) return (null, DarkOakReplant);
        // The tree's own species only (live 10-28, task #55: a birch stump would have become an oak); another kind is the
        // player's call: "no birch sapling; plant oak?".
        if (World.Inventory.CountItem(Blocks.Sapling, tree.Wood) <= 0)
            return (null, World.Inventory.FindSlots(Blocks.Sapling) is { Count: > 0 } any
                ? $"no {tree.WoodName} sapling; plant {TreeScan.WoodName(World.Inventory.GetPlayerSlot(any[0]).Damage)}?"
                : "no sapling");
        var r = await PlaceCoreAsync(cell, Blocks.Sapling, ct, allowPathing: true, damage: tree.Wood).ConfigureAwait(false);
        if (!r.Ok) return (null, "placing the sapling failed: " + r.Message);
        st.PlacedSapling = true;
        return World.GetBlock(cell) == Blocks.Sapling ? (cell, null) : (null, "the sapling popped off");
    }

    // ------------------------------------------------------------------ result

    /// <summary>Remembers what this chop felled and left standing: a remnant it leaves is our own unfinished tree.</summary>
    private void NoteChop(ChopState st)
    {
        var t = st.Tree;
        var felled = t.Logs.Where(l => !Wood.IsLog(World.GetBlock(l))).ToHashSet();
        if (felled.Count == 0) return;
        var left = t.Logs.Where(l => Wood.IsLog(World.GetBlock(l))).ToHashSet();
        var soil = t.OnSoil ? t.Soil : TreeScan.ColumnUnder(World, t.Soil).Dirt ?? t.Soil;
        World.Chops.Note(new ChopRecord(t.Base, soil, World.Dimension, t.Wood, felled, left, Options.Username, DateTimeOffset.UtcNow));
    }

    private ActionResult Finish(ChopState st, string? stop, BlockPos? planted, string? problem)
    {
        var t = st.Tree;
        int logs = World.Inventory.CountItem(Wood.LogId(t.Wood), Wood.LogDamage(t.Wood)) - st.LogsBefore;
        int saplings = Math.Max(0, World.Inventory.CountItem(Blocks.Sapling) - st.SaplingsBefore + (st.PlacedSapling ? 1 : 0));
        int apples = World.Inventory.CountItem(Items.Apple) - st.ApplesBefore;
        int left = RemainingLogs(st).Count + st.Failed.Count(f => Wood.IsLog(World.GetBlock(f)));
        string counts = $"{logs} log{(logs == 1 ? "" : "s")}, {saplings} sapling{(saplings == 1 ? "" : "s")}"
                        + (apples > 0 ? $", {apples} apple{(apples == 1 ? "" : "s")}" : "");
        // Lost = fell (or will fall) into the void. Drops still lying in the crown aren't lost: say where they are.
        int stuck = StuckDrops(st).Count;
        int stuckSaplings = StuckDrops(st).Count(d => d.Item == Blocks.Sapling);
        string lost = (st.Lost > 0 ? $"; {st.Lost} drop(s) lost near the void" : "")
                      + (stuck > 0 ? $"; {stuck} drop(s) ({stuckSaplings} sapling(s)) still lying on the leaves" : "");
        string leafStats = $"broke {st.LeavesBroken} leaves, {st.LeavesDecayed} decayed";
        bool allDown = left == 0;
        // A normal world doesn't hinge on one replant: felling the tree is the job, the replant a bonus.
        bool replantOptional = !WorldMode.Current.ScarceTerrain;
        bool ok = allDown && stop is null && (planted is not null || replantOptional);
        string msg;
        if (ok && planted is null) msg = $"chopped {t.WoodName} tree ({counts}); not replanted ({problem ?? "no sapling"}){lost}";
        else if (ok && st.AlreadyPlanted) msg = $"chopped {t.WoodName} tree ({counts}); its spot {Fmt(planted!.Value)} already has a sapling (dirt intact){lost}";
        else if (ok) msg = $"chopped {t.WoodName} tree ({counts}), replanted at {Fmt(planted!.Value)}{lost}";
        else if (!allDown || stop is not null)
            msg = $"{stop ?? $"{left} log(s) left"}; chopped {st.Felled}/{t.Logs.Count} {t.WoodName} logs ({counts})"
                  + (planted is { } pl ? $", replanted at {Fmt(pl)}" : ", not replanted") + lost;
        else if (problem == "no sapling")
        {
            var soil = t.OnSoil ? t.Soil : TreeScan.ColumnUnder(World, t.Soil).Dirt ?? t.Soil;
            msg = $"no sapling dropped, NOT replanted (chopped {t.WoodName} tree: {counts}; dirt at {Fmt(soil)} is free; {leafStats}){lost}";
        }
        else
        {
            // Live 10-27 (L6): ", sapling kept" was appended with 0 saplings. Say "kept" only when one is really held.
            int held = World.Inventory.CountItem(Blocks.Sapling);
            string keep = held > 0 ? ", sapling kept" : ", no sapling to plant either";
            msg = $"replant blocked: {problem} (chopped {t.WoodName} tree: {counts}{keep}){lost}";
        }
        // Consecutive chops that felled a tree without replanting it: the next chop_tree refuses after MaxUnreplantedChops.
        if (planted is not null) _unreplantedChops = 0;
        else if (st.Felled > 0 && problem != DarkOakReplant && !replantOptional)
        {
            _unreplantedChops++;
            _lastUnreplanted = DateTime.UtcNow;
            _lastReplantProblem = stop ?? problem;
        }
        NoteChop(st);
        var report = new TreeChopReport(t.WoodName, t.Base, t.Soil, t.Logs.Count, st.Felled, logs, saplings, apples, st.Lost, planted, problem, ok, msg)
        {
            LeavesBroken = st.LeavesBroken, LeavesDecayed = st.LeavesDecayed, StuckDrops = stuck,
        };
        if (!msg.Contains(leafStats)) Log($"chop: {leafStats}, {stuck} drop(s) still on the leaves");
        Log("chop: " + msg);
        Raise(() => TreeChopped?.Invoke(report));
        return ok ? ActionResult.Success(msg, logs) : ActionResult.Fail(msg, logs);
    }
}
