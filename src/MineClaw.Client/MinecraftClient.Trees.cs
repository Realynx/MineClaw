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
    /// <summary>A named tree (<c>at</c>) only: when another body holds it, fail instead of taking the next free tree near it.</summary>
    public bool OnlyThisTree { get; init; }
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
    /// <summary>The same in a normal world, where dirt is everywhere: any placeable junk, dirt first (the classic nerd-pole block), then
    /// stone kinds, gravel and sand (they stand on the pillar block under them and are dug back from the top), planks, and the felled
    /// logs of this very tree last. Live 11-04 story-1: "no cobble/planks to pillar" with dirt all around.</summary>
    private static readonly int[] PillarBlocksNormal = [Blocks.Dirt, Blocks.Cobblestone, Blocks.Netherrack, Blocks.Stone, Blocks.Sandstone,
        Blocks.Gravel, Blocks.Sand, Blocks.Planks, Blocks.StoneBricks, Blocks.Log, Wood.Log2];
    /// <summary>How high a tree pillar may go in a normal world (Story Mode, Fox 11-04: "it should nerd-pole up to reach those blocks").</summary>
    public const int TallTreePillar = 12;
    private static int[] TreePillarBlocks => MineClaw.Data.Versioning.WorldMode.Current.ScarceTerrain ? PillarBlocks : PillarBlocksNormal;

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
        /// <summary>Dirt it dug for a pillar (normal world): filled again once the pillar is back down.</summary>
        public readonly List<BlockPos> Holes = new();
        public int DirtDug;
        /// <summary>The dig-and-climb approach (normal world) was tried this chop.</summary>
        public bool DugApproach;
        /// <summary>Felling: failed log digs, and passes in a row that dug nothing.</summary>
        public int FellFailures, FellIdle;
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
            if (UnreplantedRefusal(o) is { } stopping) return stopping;
            // Trees this body failed on lately aren't picked again first (an explicit "chop that one" still is).
            var skip = at is null ? RecentlyFailedTrees() : new HashSet<BlockPos>();
            var tree = ResolveTree(at, wood, o, out var why, skip);
            if (tree is null) return ActionResult.Fail(why!);
            var (leased, lease, taken) = await LeaseTreeAsync(tree, at, wood, o, skip, c).ConfigureAwait(false);
            if (taken is not null) return taken;
            ActionResult? result = null;
            try { return result = await ChopResolvedAsync(leased, o, c).ConfigureAwait(false); }
            finally { await SettleTreeAsync(leased, lease, result, c).ConfigureAwait(false); }
        });

    /// <summary>Too many chops in a row went unreplanted (Skyblock): refuses felling more until the replanting is fixed.</summary>
    private ActionResult? UnreplantedRefusal(TreeChopOptions o)
    {
        if (DateTime.UtcNow - _lastUnreplanted > UnreplantedMemory) _unreplantedChops = 0;
        if (o.MaxUnreplantedChops <= 0 || !WorldMode.Current.ScarceTerrain || _unreplantedChops < o.MaxUnreplantedChops) return null;
        return ActionResult.Fail($"stopping: the last {_unreplantedChops} trees I chopped weren't replanted (last: {_lastReplantProblem ?? "not replanted"}); " +
                                 "fix the replanting first (a sapling, free lit dirt) before felling more");
    }

    /// <summary>The trees this body failed on that are still held back (expired holds are dropped).</summary>
    private HashSet<BlockPos> RecentlyFailedTrees()
    {
        var skip = new HashSet<BlockPos>();
        lock (_failedTrees)
            foreach (var (b, until) in _failedTrees.ToList())
                if (DateTime.UtcNow < until) skip.Add(b); else _failedTrees.Remove(b);
        return skip;
    }

    /// <summary>Leases the tree before walking there; a sibling that won the race keeps it and this body takes the next one. No
    /// board, or a board that doesn't answer: chops unleased (leases coordinate, they aren't safety). Taken = every tree is held.</summary>
    private async Task<(TreeInfo Tree, ClaimKey? Lease, ActionResult? Taken)> LeaseTreeAsync(TreeInfo tree, BlockPos? at, int? wood,
        TreeChopOptions o, HashSet<BlockPos> skip, CancellationToken c)
    {
        if (TargetClaims is not { } board) return (tree, null, null);
        string? why = null; // every pick so far found a tree
        for (int tries = 0; ; tries++)
        {
            var key = TreeKey(tree, World.Dimension);
            var r = await board.TryClaimAsync(key, TreeLeaseTtl, "chop", c).ConfigureAwait(false);
            if (r.Ok) return (tree, key, null);
            if (r.Holder == null)
            {
                // The board didn't answer: undo a late grant and chop unleased.
                try { await board.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
                return (tree, null, null);
            }
            Log($"chop: {tree} taken by {r.Holder}; picking the next free tree");
            skip.Add(tree.Base);
            if (tries >= 8 || ResolveTree(at, wood, o, out why, skip) is not { } next)
                return (tree, null, ActionResult.Fail(why ?? "exhausted: every tree near here is taken by other bodies"));
            tree = next;
        }
    }

    /// <summary>A tree it couldn't fell (logs out of reach, no stand) stays leased a while, so the next body doesn't walk over and
    /// fail the same way, and this body tries others first (live 10-30: 119 and 1337 took turns failing on one spruce).</summary>
    private async Task SettleTreeAsync(TreeInfo tree, ClaimKey? lease, ActionResult? result, CancellationToken c)
    {
        bool failedTree = result is { Ok: false } && !c.IsCancellationRequested;
        if (failedTree) lock (_failedTrees) _failedTrees[tree.Base] = DateTime.UtcNow + FailedTreeHold;
        if (lease is not { } k) return;
        try
        {
            if (failedTree) await TargetClaims!.TryClaimAsync(k, FailedTreeHold, "chop failed", CancellationToken.None).ConfigureAwait(false);
            else await TargetClaims!.ReleaseAsync(k, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { /* TTL */ }
    }

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
                await LeavesFromPillarAsync(st, limit.Token).ConfigureAwait(false);
                await CleanupPillarAsync(st, limit.Token).ConfigureAwait(false);
                await RefillHolesAsync(st, limit.Token).ConfigureAwait(false);
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
        TreeInfo? tree = null;
        // A named spot whose tree another body holds: the next free tree near that spot, never following it there.
        Vec3d? around = null;
        if (at is { } p && !PickNamedTree(p, wood, o, skip, out tree, out around, out why)) return null;
        if (tree is null && !PickNearestTree(around ?? World.Player.Position, wood, o, refusal, skip, out tree, out why)) return null;
        why = NotATreeReason(tree!);
        return why is null ? tree : null;
    }

    /// <summary>The tree at a named spot. False = refused (<paramref name="why"/>); true with no tree = it is another body's, look
    /// for the next free one <paramref name="around"/> that spot.</summary>
    private bool PickNamedTree(BlockPos p, int? wood, TreeChopOptions o, IReadOnlySet<BlockPos>? skip, out TreeInfo? tree, out Vec3d? around,
        out string? why)
    {
        around = null;
        why = null;
        tree = TreeScan.LogNear(World, p) is { } l ? TreeScan.Analyze(World, l) : null;
        if (tree is null)
        {
            why = $"no tree at {Fmt(p)}" + FrontierText(p.Center, 0, wood, skip, "nearest trees", "");
            return false;
        }
        if (tree.Logs.All(EditBlocked)) { why = $"tree at {Fmt(p)} is inside spawn protection (the server undid my digs there; needs op)"; return false; }
        if (TreeTaken(tree, skip) is not { } holder) return true;
        if (o.OnlyThisTree)
        {
            why = $"the {tree.WoodName} tree at {Fmt(tree.Base)} is {holder}'s right now (only that tree was asked for)";
            return false;
        }
        Log($"chop: {tree} is {holder}'s; taking the next free tree near {Fmt(p)}");
        around = p.Center;
        tree = null;
        return true;
    }

    /// <summary>The best free tree near <paramref name="center"/>. False = none (<paramref name="why"/>: all taken, all in spawn
    /// protection, or none at all, with the trees beyond).</summary>
    private bool PickNearestTree(Vec3d center, int? wood, TreeChopOptions o, Func<TreeInfo, string?> refusal, IReadOnlySet<BlockPos>? skip,
        out TreeInfo? tree, out string? why)
    {
        tree = null;
        why = null;
        var all = TreeScan.FindTrees(World, center, o.SearchRadius, wood)
            .Where(t => t.Logs.Any(l => !UserProtected(l, Wood.LogId(t.Wood))) && (t.OnSoil || t.Leaves.Count >= 3 || TreeScan.Unfinished(World, t) is not null)).ToList();
        var open = all.Where(t => TreeTaken(t, skip) == null).ToList();
        if (open.Count == 0 && all.Count > 0)
        {
            var holders = all.Select(t => TreeTaken(t, skip)).Where(h => h != null).Distinct().Take(3);
            why = $"exhausted: the {all.Count} tree(s) within {o.SearchRadius:0} are all taken by other bodies ({string.Join(", ", holders)})"
                  + FrontierText(center, o.SearchRadius, wood, skip, "free trees beyond", "; no free tree beyond in view");
            return false;
        }
        var trees = open.Where(t => !t.Logs.All(EditBlocked)).ToList();
        if (trees.Count == 0 && open.Count > 0)
        {
            why = $"the trees within {o.SearchRadius:0} are inside spawn protection (the server undid my digs within {_protectedRegion?.Radius ?? VanillaSpawnProtection} of spawn; needs op)";
            return false;
        }
        tree = BestTree(trees, refusal);
        if (tree is not null) return true;
        // Typed: the area is used up here, and where the work goes on (the brain widens it with one chop_tree at=).
        why = $"exhausted: no {(wood is { } w ? TreeScan.WoodName(w) + " " : "")}tree within {o.SearchRadius:0}"
              + FrontierText(center, o.SearchRadius, wood, skip, "nearest beyond", "; none beyond in view either");
        return false;
    }

    /// <summary>Stewardship first (allowed trees), then the tree whose replant is most certain: on dirt, outside any protected
    /// area, not on the rim (drops there fall into the void), then the nearest. Our own unfinished tree (a chop that stopped
    /// part-way) goes before any new one.</summary>
    private TreeInfo? BestTree(List<TreeInfo> trees, Func<TreeInfo, string?> refusal) =>
        trees.OrderBy(t => refusal(t) is null ? 0 : 1)
            .ThenBy(t => TreeScan.Unfinished(World, t) is not null ? 0 : 1)
            .ThenBy(t => TreeCrowded(t) ? 1 : 0)
            .ThenBy(t => t.OnSoil ? 0 : 1)
            .ThenBy(t => ReplantPrecheck(t) is null ? 0 : 1)
            .ThenBy(t => Safety.EdgeDistance(World, t.Base.X, t.Base.Y, t.Base.Z, 2) >= 2 ? 0 : 1)
            .ThenBy(t => t.Base.Center.DistanceSquaredTo(World.Player.Position)).FirstOrDefault();

    /// <summary>"; label: trees" listing the free trees beyond <paramref name="inner"/>, else <paramref name="none"/>.</summary>
    private string FrontierText(Vec3d center, double inner, int? wood, IReadOnlySet<BlockPos>? skip, string label, string none) =>
        Frontier(center, inner, wood, skip) is { Length: > 0 } f ? $"; {label}: {f}" : none;

    /// <summary>Why the picked logs are no tree to chop: a build (off soil, no leaves, not our unfinished one), or protected.</summary>
    private string? NotATreeReason(TreeInfo tree)
    {
        if (!tree.OnSoil && tree.Leaves.Count < 3 && TreeScan.Unfinished(World, tree) is null)
            return $"logs at {Fmt(tree.Base)} sit on {BlockName(tree.SoilId, 0)} with no leaves: a build, not a tree";
        if (tree.Logs.All(l => UserProtected(l, Wood.LogId(tree.Wood))))
            return $"the {tree.WoodName} tree at {Fmt(tree.Base)} is protected";
        return null;
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
        // Never its own pillar: a log it placed in the felled trunk column to climb on is no log of the tree (live 11-04 tests: it dug
        // its own log pillar out from under its feet as "the next log" and was stranded).
        st.Tree.Logs.Where(l => World.GetBlock(l) is var id && Wood.IsLog(id) && !st.Failed.Contains(l) && !st.PillarIds.ContainsKey(l) && !IsBlockProtected(l, id)).ToList();

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
        st.FellFailures = 0;
        st.FellIdle = 0;
        while (true)
        {
            var remaining = RemainingLogs(st);
            if (remaining.Count == 0) return st.Failed.Count > 0 ? $"{st.Failed.Count} log(s) wouldn't break" : null;
            await SurvivalWindowAsync(ct).ConfigureAwait(false);

            if (DiggableNow(remaining) is { } log)
            {
                if (await FellLogAsync(st, log, ct).ConfigureAwait(false) is { } failed) return failed;
                continue;
            }
            if (++st.FellIdle > 24) return $"stuck with {remaining.Count} log(s) left";
            if (await ReachLogsAsync(st, o, remaining, ct).ConfigureAwait(false)) continue;
            return OutOfReachReason(st, remaining);
        }
    }

    /// <summary>Digs one log in reach. Null = go on; else why felling stops (too many failed digs).</summary>
    private async Task<string?> FellLogAsync(ChopState st, BlockPos log, CancellationToken ct)
    {
        var r = await DigCoreAsync(log, ct, allowPathing: false).ConfigureAwait(false);
        if (r.Ok)
        {
            st.Felled++;
            st.FellIdle = 0;
            return null;
        }
        st.Failed.Add(log);
        Log($"chop: {r.Message}");
        return ++st.FellFailures >= 6 ? "too many failed digs, last: " + r.Message : null;
    }

    /// <summary>No log in reach from here: tries each way of getting one into reach in turn (walk, dig a way up, clear a leaf, jump,
    /// a relaxed spot, a pillar). True = one worked (look again).</summary>
    private async Task<bool> ReachLogsAsync(ChopState st, TreeChopOptions o, List<BlockPos> remaining, CancellationToken ct)
    {
        var lowest = remaining.OrderBy(l => l.Y).Take(4).ToList();
        bool normal = !WorldMode.Current.ScarceTerrain;
        // 0. Below the tree (a cave or trench under it, a quarry beside it; live 11-04 story-1 "chopped 0/39", "it's 5 blocks up"):
        // in a normal world it digs its own short way up beside the trunk (a stair through the dirt, a pillar step) instead of a
        // long walk round under ground. Once per chop; elsewhere it is the fallback when no walk reaches a spot (1b).
        if (DigUpFirst(st, normal) && await DigApproachAsync(st, lowest, remaining, ct).ConfigureAwait(false)) return true;
        // 1. Walk to a safe spot (2+ from any edge) that sees a log, lowest first.
        if (await GotoSeesLogAsync(st, lowest, remaining, ct, strictOnly: true).ConfigureAwait(false)) return true;
        // 1b. No walk gets there: dig and climb the way (normal world, once).
        if (DigUpFallback(st, normal) && await DigApproachAsync(st, lowest, remaining, ct).ConfigureAwait(false)) return true;
        // 2. Its own leaves hide logs that are otherwise in reach: clear one next to them (also a sapling chance).
        if (lowest[0].Y - st.Tree.Base.Y <= 5 && await ClearOccludingLeafAsync(st, lowest, ct).ConfigureAwait(false)) return true;
        return await ClimbToLogsAsync(st, o, remaining, lowest, normal, ct).ConfigureAwait(false);
    }

    private bool DigUpFirst(ChopState st, bool normal) => normal && !st.DugApproach && FeetY() < GroundY(st) - 1;

    private static bool DigUpFallback(ChopState st, bool normal) => normal && !st.DugApproach && st.Felled == 0;

    /// <summary>The dig-and-climb approach (normal world), tried once per chop.</summary>
    private Task<bool> DigApproachAsync(ChopState st, List<BlockPos> lowest, List<BlockPos> remaining, CancellationToken ct)
    {
        st.DugApproach = true;
        return GotoSeesLogAsync(st, lowest, remaining, ct, strictOnly: true, dig: true);
    }

    /// <summary>Went to a spot by the tree and a log is diggable from there.</summary>
    private async Task<bool> GotoSeesLogAsync(ChopState st, List<BlockPos> lowest, List<BlockPos> remaining, CancellationToken ct,
        bool strictOnly = false, bool relaxedOnly = false, bool dig = false) =>
        (await GotoSeeingAsync(st, lowest, ct, strictOnly: strictOnly, relaxedOnly: relaxedOnly, dig: dig).ConfigureAwait(false)).Ok
        && DiggableNow(remaining) is not null;

    /// <summary>Steps 3-5 of <see cref="ReachLogsAsync"/>: jump-dig, a relaxed spot, a pillar (dirt dug for it in a normal world).</summary>
    private async Task<bool> ClimbToLogsAsync(ChopState st, TreeChopOptions o, List<BlockPos> remaining, List<BlockPos> lowest, bool normal,
        CancellationToken ct)
    {
        // 3. Just above reach: jump-dig from a safe spot.
        if ((await GotoSeeingAsync(st, lowest, ct, strictOnly: true, eyeBoost: JumpEyeBoost).ConfigureAwait(false)).Ok
            && await JumpDigAsync(st, remaining, ct).ConfigureAwait(false))
        {
            st.FellIdle = 0;
            return true;
        }
        // 4. A spot no closer to the void than the trunk (e.g. inside the trunk column of a rim tree).
        if (await GotoSeesLogAsync(st, lowest, remaining, ct, relaxedOnly: true).ConfigureAwait(false)) return true;
        // 5. Short pillar (cheap block, never dirt), then jump from it if needed.
        // A normal world has dirt to spare: a tall tree is climbed on a pillar (dug back down afterwards); skyblock keeps it short.
        int maxPillar = MineClaw.Data.Versioning.WorldMode.Current.ScarceTerrain ? o.MaxPillar : Math.Max(o.MaxPillar, TallTreePillar);
        await DirtForPillarAsync(st, remaining, maxPillar, normal, ct).ConfigureAwait(false);
        if (st.Pillar.Count >= maxPillar || !await PillarUpAsync(st, remaining, ct).ConfigureAwait(false)) return false;
        if (DiggableNow(remaining) is null) await JumpDigAsync(st, remaining, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Nothing to pillar with in a normal world: a few dirt blocks dug near the tree (filled again afterwards).</summary>
    private async Task DirtForPillarAsync(ChopState st, List<BlockPos> remaining, int maxPillar, bool normal, CancellationToken ct)
    {
        if (normal && st.Pillar.Count == 0 && PillarBlock() is null && st.DirtDug < MaxPillarDirt)
            await GatherPillarDirtAsync(st, Math.Clamp(remaining.Max(l => l.Y) - (GroundY(st) + 4) + 1, 1, maxPillar), ct).ConfigureAwait(false);
    }

    /// <summary>Why the logs left are out of reach once every way up failed.</summary>
    private string OutOfReachReason(ChopState st, List<BlockPos> remaining)
    {
        bool normal = !WorldMode.Current.ScarceTerrain;
        int top = remaining.Max(l => l.Y);
        string noBlock = st.Pillar.Count == 0 && PillarBlock() is null
            ? normal ? "; no block to pillar with (and no dirt to dig near the tree)" : "; no cobble/planks to pillar"
            : "";
        string below = st.Felled == 0 && FeetY() < GroundY(st) - 1 ? $"; I'm {GroundY(st) - FeetY()} blocks below the tree and found no way up" : "";
        return $"{remaining.Count} log(s) out of reach (up to y {top})" + noBlock + below;
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
    /// <param name="dig">Normal world: the path may dig natural ground (a stair up out of a trench or a cave) and place pillar
    /// blocks it carries; never placed or protected blocks.</param>
    private async Task<ActionResult> GotoSeeingAsync(ChopState st, IReadOnlyList<BlockPos> targets, CancellationToken ct, bool strictOnly = false,
        bool relaxedOnly = false, double eyeBoost = 0, bool dig = false)
    {
        if (targets.Count == 0) return ActionResult.Fail("nothing to reach");
        var o = GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(30), MaxReplans = 2, AllowPartial = false, Diagnose = "chop" };
        if (dig)
            o = o with
            {
                AllowBreak = true, BreakOnly = (id, pos) => NaturalGround(id) && pos != st.Tree.Soil && !st.Tree.Logs.Contains(pos.Up()), AllowPlace = ScaffoldItem() is not null, ScaffoldBudget = 8, PreferSurface = true,
                Diagnose = "chop dig", PlanTimeout = TimeSpan.FromSeconds(3),
            };
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
            if (dig) Log($"chop: no dug way up to the tree: {g.Message}");
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
        if (JumpDigTarget(remaining) is not { } t) return false;
        int id = World.GetBlock(t);
        int held = await SelectToolForAsync(id, ct).ConfigureAwait(false);
        int ticks = DigTicksNow(id, World.GetMeta(t), onGround: true, inWater: false);
        if (ticks == int.MaxValue) return false;

        int eid = World.Player.EntityId;
        if (await JumpForDigAimAsync(t, ct).ConfigureAwait(false) is not { } hit) return false;
        _lookYaw = hit.Yaw;
        _lookPitch = hit.Pitch;
        byte face = (byte)hit.Face;
        Send(new AnimationPacket { EntityId = eid, Animation = 1 });
        Send(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusStarted, X = t.X, Y = t.Y, Z = t.Z, Face = face });
        long started = TickCount;
        await WaitUntilAsync(() => World.Player.OnGround, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        await SwingWhileDiggingAsync(t, id, started, started + ticks + 2, eid, ct).ConfigureAwait(false);
        Send(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusFinished, X = t.X, Y = t.Y, Z = t.Z, Face = face });
        bool broke = await WaitUntilAsync(() => World.GetBlock(t) != id, TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false);
        Log($"chop: jump-dig {Fmt(t)} {(broke ? "ok" : "didn't break")}");
        if (broke) { NoteEditOk(t); st.Felled++; }
        else { NoteEditReverted(t, "dig"); st.Failed.Add(t); }
        return broke;
    }

    /// <summary>The lowest log in reach at the top of a jump from here, when here is a safe spot to jump (on the ground, 2+ from the
    /// edge, clear headroom); never one another player stands on (hard safety).</summary>
    private BlockPos? JumpDigTarget(List<BlockPos> remaining)
    {
        var me = World.Player.Position;
        int fx = (int)Math.Floor(me.X), fy = (int)Math.Floor(me.Y + 1e-6), fz = (int)Math.Floor(me.Z);
        if (!World.Player.OnGround || Safety.EdgeDistance(World, fx, fy, fz, 2) < 2 || BlockShapes.HasCollision(World, fx, fy + 2, fz)) return null;
        var standOn = Safety.OtherPlayersSupport(World);
        return remaining.Where(l => !standOn.Contains(l) && l.Center.DistanceTo(World.Player.EyePosition) <= Options.Reach + JumpEyeBoost + 0.9)
            .OrderBy(l => l.Y).Cast<BlockPos?>().FirstOrDefault();
    }

    /// <summary>Jumps and returns the aim at the moment <paramref name="t"/> comes into reach (null: it never did before landing). Tick
    /// thread: only looks for that moment; packets go out from the action thread.</summary>
    private async Task<DigAim?> JumpForDigAimAsync(BlockPos t, CancellationToken ct)
    {
        DigAim? aim = null;
        int phase = 0;
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
        return aim;
    }

    /// <summary>Swings every 4 ticks while the block is still there, until the dig's tick budget runs out.</summary>
    private async Task SwingWhileDiggingAsync(BlockPos t, int id, long started, long until, int eid, CancellationToken ct)
    {
        int lastSwing = 0;
        while (TickCount < until && World.GetBlock(t) == id)
        {
            await WaitTicksAsync(2, ct).ConfigureAwait(false);
            int el = (int)(TickCount - started);
            if (el - lastSwing >= 4) { lastSwing = el; Send(new AnimationPacket { EntityId = eid, Animation = 1 }); }
        }
    }

    private int? PillarBlock()
    {
        foreach (int id in TreePillarBlocks)
            if (!ReservedItems.Contains(id) && World.Inventory.CountItem(id) > 0) return id;
        return null;
    }

    /// <summary>Places one cheap block under itself (<see cref="TreePillarBlocks"/>: on Skyblock cobblestone and the like, never dirt; in a
    /// normal world any junk, dirt first) in the felled trunk column or right beside it, only 3+ from any edge, so the next log comes into
    /// (jump) reach, or a few steps on up a free column. The pillar is dug back afterwards.</summary>
    private async Task<bool> PillarUpAsync(ChopState st, List<BlockPos> remaining, CancellationToken ct)
    {
        if (PillarBlock() is not { } block) return false;
        // Already on top of my own pillar with room above: the next step goes straight on (no walk; its top is an edge on all sides).
        var at = Pathfinder.StartCellFor(World, World.Player.Position) ?? World.Player.Position.ToBlockPos();
        if (OnMyPillarTop(st, at)) return await PillarStepAsync(st, at, block, " (on my pillar)", ct).ConfigureAwait(false);
        if (!await GotoPillarSpotAsync(st, remaining, ct).ConfigureAwait(false)) return false;
        var feet = Pathfinder.StartCellFor(World, World.Player.Position) ?? World.Player.Position.ToBlockPos();
        return await PillarStepAsync(st, feet, block, "", ct).ConfigureAwait(false);
    }

    private bool OnMyPillarTop(ChopState st, BlockPos at) =>
        st.Pillar.Contains(at.Down()) && !BlockShapes.HasCollision(World, at.X, at.Y + 1, at.Z) && !BlockShapes.HasCollision(World, at.X, at.Y + 2, at.Z)
        && Safety.VoidNeighbourCount(World, at.X, at.Y, at.Z) == 0;

    /// <summary>One pillar block into the feet cell (air while the bot stood in it: a block there now is the one just placed).</summary>
    private async Task<bool> PillarStepAsync(ChopState st, BlockPos feet, int block, string where, CancellationToken ct)
    {
        bool ok = await PillarOnceAsync(feet, block, ct).ConfigureAwait(false);
        if (World.GetBlock(feet) == block) { st.Pillar.Add(feet); st.PillarIds[feet] = block; }
        Log($"chop: pillar {BlockName(block, 0)} at {Fmt(feet)}{where}: {(ok ? "ok" : "failed")}");
        return ok && st.Pillar.Contains(feet);
    }

    /// <summary>Walks to a pillar spot in the felled trunk column, else right beside it (3+ from any edge, room to climb) from which a
    /// pillar brings a log into reach.</summary>
    private async Task<bool> GotoPillarSpotAsync(ChopState st, List<BlockPos> remaining, CancellationToken ct)
    {
        var trunk = st.Tree.Base;
        var lowest = remaining.OrderBy(l => l.Y).Take(3).ToList();
        // The spot is by the trunk at its base, wherever the body stands now (live 11-04: from a trench 5 below the tree, "within 3 of
        // my feet" held no cell by the trunk at all and the pillar never started).
        int feetY = GroundY(st);
        bool Spot(int x, int y, int z, bool strictTrunk)
        {
            int ring = Math.Max(Math.Abs(x - trunk.X), Math.Abs(z - trunk.Z));
            if (strictTrunk ? ring != 0 : ring != 1) return false;
            var under = new BlockPos(x, y - 1, z);
            bool footing = st.Pillar.Contains(under) || TreeStandOk(st, x, y, z, strict: true);
            return footing && Safety.IsStandable(World, x, y, z) && Safety.EdgeDistance(World, x, y, z, 3) >= 3
                   && !BlockShapes.HasCollision(World, x, y + 2, z) && !BlockShapes.HasCollision(World, x, y + 3, z)
                   && PillarReaches(lowest, x, y, z);
        }
        var go = GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(30), MaxReplans = 2, AllowPartial = false };
        foreach (bool trunkColumn in new[] { true, false })
        {
            Goal g = new GoalFiltered(new GoalNear(trunk.X, feetY, trunk.Z, 3), (x, y, z) => Spot(x, y, z, trunkColumn),
                trunkColumn ? "pillar spot in the trunk" : "pillar spot by the trunk");
            if ((await GotoCoreAsync(g, go, ct).ConfigureAwait(false)).Ok) return true;
        }
        return false;
    }

    /// <summary>One step brings a log into (jump) reach; in a normal world a taller pillar does too, when its column is free all the way
    /// up (live 11-04 tests: with the lowest log left 9 above the ground no first step "reached" it, so the climb never started).</summary>
    private bool PillarReaches(List<BlockPos> lowest, int x, int y, int z)
    {
        if (lowest.Any(l => CanAimFrom(x, y + 1, z, l, margin: 0, eyeBoost: JumpEyeBoost))) return true;
        if (WorldMode.Current.ScarceTerrain) return false;
        for (int k = 2; k <= TallTreePillar; k++)
        {
            if (lowest.Any(l => CanAimFrom(x, y + k, z, l, margin: 0))) return true;
            if (BlockShapes.HasCollision(World, x, y + k + 2, z)) return false; // room to jump from the k-th block
            if (lowest.Any(l => CanAimFrom(x, y + k, z, l, margin: 0, eyeBoost: JumpEyeBoost))) return true;
        }
        return false;
    }

    /// <summary>Rising through the jump with the body (and the last position sent) already above the feet cell, still free.</summary>
    private bool PillarPlaceMoment(BlockPos feet, double y, double lastY) =>
        y >= feet.Y + 1.1 && lastY >= feet.Y + 1.0 && Aiming.IsReplaceable(World, World.GetBlock(feet));

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
                case 1 when PillarPlaceMoment(feet, pos.Y, lastY):
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
        bool held = World.GetBlock(feet) == block && World.Player.Position.Y >= feet.Y + 1 - 1e-3;
        // Mine (rule 1: self-placed is always minable): the pillar is dug back down standing on it, one short step at a time, even
        // when the server's echo of the placement never came (the prediction already shows it).
        if (held) World.Placed.Note(feet, World.Dimension, block, Options.Username, DateTimeOffset.UtcNow);
        return held;
    }

    /// <summary>Steps off the pillar and digs it back: only cells the bot itself placed this chop, only while they still
    /// hold exactly the block it placed (never dirt/grass, never someone else's block).</summary>
    private async Task CleanupPillarAsync(ChopState st, CancellationToken ct)
    {
        st.Pillar.RemoveAll(p => !IsOwnPillarBlock(st, p));
        if (st.Pillar.Count == 0) return;
        await DigPillarDownAsync(st, ct).ConfigureAwait(false);
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

    /// <summary>On top of a tall pillar: down the way it came up, digging the block under my feet (my own: a 1-block step each time).</summary>
    private async Task DigPillarDownAsync(ChopState st, CancellationToken ct)
    {
        for (int guard = 0; guard < 32; guard++)
        {
            var feet = Pathfinder.StartCellFor(World, World.Player.Position) ?? World.Player.Position.ToBlockPos();
            var under = feet.Down();
            if (!st.Pillar.Contains(under) || !IsOwnPillarBlock(st, under)) break;
            var r = await DigCoreAsync(under, ct).ConfigureAwait(false);
            if (!r.Ok) { Log($"chop: couldn't dig my pillar down at {Fmt(under)}: {r.Message}"); break; }
            st.Pillar.Remove(under);
            await WaitUntilAsync(() => World.Player.OnGround && World.Player.Position.Y < feet.Y - 0.5, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }

    private bool IsOwnPillarBlock(ChopState st, BlockPos p)
    {
        int id = World.GetBlock(p);
        // Dirt only in a normal world, where the bot pillars with it (the cell is recorded as one it placed: never ground it found).
        bool dirtOk = !MineClaw.Data.Versioning.WorldMode.Current.ScarceTerrain && id == Blocks.Dirt;
        bool own = st.PillarIds.TryGetValue(p, out int placed) && placed == id && (dirtOk || id is not (Blocks.Dirt or Blocks.Grass or Blocks.Farmland));
        if (!own) Log($"chop: leaving {BlockName(id, World.GetMeta(p))} at {Fmt(p)}: not a pillar block I placed");
        return own;
    }

    /// <summary>
    /// Normal world, still up on its pillar with no sapling of the tree's kind: breaks the crown's leaves in reach from up there (no
    /// walking) for one before the pillar comes down. A tall tree's crown is out of reach from the ground, so its spot was left bare
    /// ("chopped oak tree (12 logs, 0 saplings); not replanted").
    /// </summary>
    private async Task LeavesFromPillarAsync(ChopState st, CancellationToken ct)
    {
        if (WorldMode.Current.ScarceTerrain || st.Pillar.Count == 0 || HasOwnSapling(st)) return;
        var skip = new HashSet<BlockPos>();
        bool SaplingDropped() => World.Entities.Nearby(TreeCenter(st), 12,
            e => e.Kind == EntityKind.Item && e.Item.Id == Blocks.Sapling && e.Item.Damage == st.Tree.Wood, 1).Any();
        for (int i = 0; i < NormalLeafBreaks && !HasOwnSapling(st) && !SaplingDropped(); i++)
        {
            if (i > 0) await WaitTicksAsync(2, ct).ConfigureAwait(false); // a drop shows up a tick or two after the break
            var cands = LeavesLeft(st).Where(p => !skip.Contains(p) && !IsBlockProtected(p, World.GetBlock(p)) && !EditBlocked(p)).ToList();
            if (DiggableNow(cands) is not { } leaf) break;
            await BreakLeafAsync(st, leaf, skip, ct).ConfigureAwait(false);
        }
        if (st.LeavesBroken > 0) Log($"chop: broke {st.LeavesBroken} leaves from my pillar for a sapling");
    }

    /// <summary>The foot level of the ground at the trunk: the base itself for a standing tree, the cell over whatever is under a hanging
    /// remnant (its lower trunk already cut), else where the body stands.</summary>
    private int GroundY(ChopState st)
    {
        if (st.Tree.OnSoil) return st.Tree.Base.Y;
        var (sapling, dirt, other) = TreeScan.ColumnUnder(World, st.Tree.Soil, 24);
        return (sapling?.Y ?? (dirt ?? other)?.Y + 1) ?? FeetY();
    }

    /// <summary>The foot cell's y (the cell the body stands in).</summary>
    private int FeetY() => Pathfinder.StartCellFor(World, World.Player.Position)?.Y ?? World.Player.Position.ToBlockPos().Y;

    /// <summary>Most dirt one chop digs for a pillar (normal world).</summary>
    private const int MaxPillarDirt = 16;

    /// <summary>Natural ground a chop's dug approach (or a detour's dig-through) may break: soil, sand, gravel, clay, snow, stone kinds and
    /// leaves. Never ores (a lost drop), never anything a player placed (the placement log) or protects (the path's own veto).</summary>
    internal static bool NaturalGround(int id) =>
        id is Blocks.Grass or Blocks.Dirt or Blocks.Sand or Blocks.Gravel or Blocks.Clay or Blocks.SnowBlock or Blocks.SnowLayer or Blocks.Stone
            or Blocks.Sandstone or Blocks.Netherrack or Blocks.Leaves or Wood.Leaves2 or 110 /* mycelium */;

    /// <summary>A liquid touches the cell on any side (a dug hole there would flood or drain something).</summary>
    private bool NextToLiquid(BlockPos p) => BlockFaceExtensions.All.Any(f => BlockShapes.IsLiquid(World.GetBlock(p.Offset(f))));

    /// <summary>
    /// Normal world, nothing to pillar with (live 11-04 story-1: "no clutch blocks on me (no cobble/planks to be had)"; "4 log(s) out of
    /// reach", chopped 0/4): digs up to <paramref name="want"/> dirt or grass blocks near the tree, 2 to 5 out from the trunk (never its
    /// soil, never the ring a pillar stands on, never under anyone, beside water or under a plant other than grass), and picks them up.
    /// The holes are filled again once the pillar is dug back (<see cref="RefillHolesAsync"/>).
    /// </summary>
    private async Task<int> GatherPillarDirtAsync(ChopState st, int want, CancellationToken ct)
    {
        var cands = PillarDirtCandidates(st);
        if (cands.Count == 0) { Log("chop: no dirt to dig near the tree for a pillar"); return 0; }
        var eye = World.Player.EyePosition;
        int before = World.Inventory.CountItem(Blocks.Dirt), tries = 0;
        foreach (var p in cands.OrderBy(p => p.Center.DistanceSquaredTo(eye)).ThenByDescending(p => p.Y))
        {
            if (World.Inventory.CountItem(Blocks.Dirt) - before >= want || ++tries > want * 3 || st.DirtDug >= MaxPillarDirt) break;
            await DigPillarDirtAsync(st, p, ct).ConfigureAwait(false);
        }
        int got = World.Inventory.CountItem(Blocks.Dirt) - before;
        Log($"chop: dug {st.Holes.Count} dirt for a pillar ({got} picked up, wanted {want})");
        return got;
    }

    /// <summary>Digs one dirt block for the pillar, books its hole for the refill and picks the drop up.</summary>
    private async Task DigPillarDirtAsync(ChopState st, BlockPos p, CancellationToken ct)
    {
        var r = await DigCoreAsync(p, ct).ConfigureAwait(false);
        if (!r.Ok) { Log($"chop: couldn't dig dirt for a pillar: {r.Message}"); return; }
        st.Holes.Add(p);
        st.DirtDug++;
        await CollectDropsCoreAsync(4, 6, ct, p.Center, st.LostIds, st.StuckIds).ConfigureAwait(false);
    }

    /// <summary>Dirt or grass 2 to 5 out from the trunk, from 4 below its ground to 1 above, that may be dug for a pillar.</summary>
    private List<BlockPos> PillarDirtCandidates(ChopState st)
    {
        var t = st.Tree.Base;
        var support = Safety.OtherPlayersSupport(World);
        var me = Pathfinder.StartCellFor(World, World.Player.Position) ?? World.Player.Position.ToBlockPos();
        var cands = new List<BlockPos>();
        for (int dx = -5; dx <= 5; dx++)
        for (int dz = -5; dz <= 5; dz++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) < 2) continue;
            for (int y = GroundY(st) - 4; y <= GroundY(st) + 1; y++)
            {
                var p = new BlockPos(t.X + dx, y, t.Z + dz);
                if (PillarDirtOk(st, p, me, support)) cands.Add(p);
            }
        }
        return cands;
    }

    /// <summary>Dirt or grass with an open top (air, tall grass, snow), not a liquid over it.</summary>
    private bool PillarDirtOk(ChopState st, BlockPos p, BlockPos me, IReadOnlySet<BlockPos> support)
    {
        int id = World.GetBlock(p);
        if (id is not (Blocks.Dirt or Blocks.Grass)) return false;
        int above = World.GetBlock(p.Up());
        if (!Aiming.IsReplaceable(World, above) || BlockShapes.IsLiquid(above)) return false;
        return PillarDirtFree(st, p, id, me, support);
    }

    /// <summary>Never under me or anyone, under a log of the tree, its soil, protected, or beside water or lava.</summary>
    private bool PillarDirtFree(ChopState st, BlockPos p, int id, BlockPos me, IReadOnlySet<BlockPos> support) =>
        !(p == me.Down() || support.Contains(p) || st.Tree.Logs.Contains(p.Up()) || p == st.Tree.Soil)
        && !(IsBlockProtected(p, id) || EditBlocked(p) || NextToLiquid(p) || NextToLava(p));

    /// <summary>Fills the holes <see cref="GatherPillarDirtAsync"/> dug, with the dirt the pillar gave back (best effort).</summary>
    private async Task RefillHolesAsync(ChopState st, CancellationToken ct)
    {
        foreach (var h in st.Holes.OrderBy(h => h.Center.DistanceSquaredTo(World.Player.Position)).ToList())
        {
            if (World.Inventory.CountItem(Blocks.Dirt) <= 0 || ReservedItems.Contains(Blocks.Dirt)) break;
            if (!Aiming.IsReplaceable(World, World.GetBlock(h)) || BlockShapes.IsLiquid(World.GetBlock(h))) continue;
            if (World.Entities.Nearby(h.Center, 1.2, e => e.Kind == EntityKind.Player, 1).Any()) continue; // someone stands in it
            var r = await PlaceCoreAsync(h, Blocks.Dirt, ct).ConfigureAwait(false);
            if (!r.Ok) Log($"chop: couldn't fill the hole at {Fmt(h)}: {r.Message}");
        }
        st.Holes.Clear();
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
            if (await PickStuckLeafAsync(st, leaves, LeafOpeners(st, leaves), ct).ConfigureAwait(false) is not { } leaf) break;
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

    /// <summary>A support leaf boxed in by other leaves can't be seen: the leaves beside them that open a way through.</summary>
    private List<BlockPos> LeafOpeners(ChopState st, List<BlockPos> leaves) =>
        leaves.SelectMany(l => BlockFaceExtensions.All.Select(f => l.Offset(f)))
            .Where(n => !leaves.Contains(n) && World.GetBlock(n) is var nid && Wood.IsLeaves(nid) && !st.Failed.Contains(n) && !DropsIntoVoid(n)
                        && !IsBlockProtected(n, nid) && !EditBlocked(n))
            .Distinct().OrderBy(n => n.Y).ToList();

    /// <summary>A holding leaf (else an opener) in reach, walking to a spot that sees one when none is. Null = none to be had.</summary>
    private async Task<BlockPos?> PickStuckLeafAsync(ChopState st, List<BlockPos> leaves, List<BlockPos> openers, CancellationToken ct)
    {
        if ((DiggableNow(leaves) ?? DiggableNow(openers)) is { } inReach) return inReach;
        var seen = await GotoSeeingAsync(st, leaves.Take(8).ToList(), ct).ConfigureAwait(false);
        if (!seen.Ok && openers.Count > 0) seen = await GotoSeeingAsync(st, openers.Take(12).ToList(), ct).ConfigureAwait(false);
        return seen.Ok ? DiggableNow(leaves) ?? DiggableNow(openers) : null;
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
        // A normal world has saplings to spare, but the tree farm wants its spot planted again (Fox 11-04: "make it farm trees"; live
        // 11-04 "0 saplings, not replanted"): with no sapling of the tree's own species on it, it breaks a few of the crown's leaves it
        // can reach (no long walks) and waits a little for the rest to drop, until one comes. Skyblock clears the crown for every chance.
        bool normal = !WorldMode.Current.ScarceTerrain;
        if (normal && (HasOwnSapling(st) || RemainingLogs(st).Contains(st.Tree.Base))) return;
        await BreakLeavesForSaplingsAsync(st, o, normal, ct).ConfigureAwait(false);
        await CollectAroundTreeAsync(st, ct).ConfigureAwait(false);
        await FreeStuckDropsAsync(st, ct).ConfigureAwait(false);
        await WaitForDecayAsync(st, o, normal, ct).ConfigureAwait(false);
    }

    /// <summary>Breaks the crown's reachable leaves (none over the void) within the leaf phase and the break budget, walking to a
    /// spot that sees some when none is in reach (a few times), collecting every 12 breaks.</summary>
    private async Task BreakLeavesForSaplingsAsync(ChopState st, TreeChopOptions o, bool normal, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var skip = new HashSet<BlockPos>();
        int sinceCollect = 0, misses = 0;
        int maxBreaks = normal ? Math.Min(o.MaxLeafBreaks, NormalLeafBreaks) : o.MaxLeafBreaks;
        while (sw.Elapsed < o.LeafPhase && st.LeavesBroken < maxBreaks && !(normal && HasOwnSapling(st)))
        {
            var cands = LeavesLeft(st).Where(p => !skip.Contains(p) && !DropsIntoVoid(p) && !IsBlockProtected(p, World.GetBlock(p)) && !EditBlocked(p)).ToList();
            if (cands.Count == 0) break;
            if (DiggableNow(cands) is not { } leaf)
            {
                if (!await SeekLeavesAsync(st, cands, ++misses, normal, ct).ConfigureAwait(false)) break;
                continue;
            }
            misses = 0;
            await BreakLeafAsync(st, leaf, skip, ct).ConfigureAwait(false);
            if (++sinceCollect < 12) continue;
            sinceCollect = 0;
            await CollectAroundTreeAsync(st, ct, 16).ConfigureAwait(false);
        }
    }

    /// <summary>No leaf in reach: walks to a spot that sees one of the lowest, nearest ones, a few times (fewer in a normal world).</summary>
    private async Task<bool> SeekLeavesAsync(ChopState st, List<BlockPos> cands, int misses, bool normal, CancellationToken ct)
    {
        if (misses > (normal ? 2 : 4)) return false;
        var near = cands.OrderBy(p => p.Y).ThenBy(p => p.Center.DistanceSquaredTo(World.Player.Position)).Take(16).ToList();
        return (await GotoSeeingAsync(st, near, ct, strictOnly: true).ConfigureAwait(false)).Ok;
    }

    /// <summary>Breaks one of the tree's leaves by hand (never shears: a sapling chance); one that won't break is skipped after.</summary>
    private async Task BreakLeafAsync(ChopState st, BlockPos leaf, HashSet<BlockPos> skip, CancellationToken ct)
    {
        var r = await DigCoreAsync(leaf, ct, allowPathing: false, noShears: true).ConfigureAwait(false);
        if (r.Ok) st.LeavesBroken++;
        else skip.Add(leaf);
    }

    /// <summary>The rest decays by itself (random ticks, ~20 s per leaf on average): waits while that is still happening (bounded,
    /// stops on a stall), freeing stuck drops and collecting as they come; counts the leaves that decayed.</summary>
    private async Task WaitForDecayAsync(ChopState st, TreeChopOptions o, bool normal, CancellationToken ct)
    {
        int leftBefore = LeavesLeft(st).Count, prev = leftBefore, brokenBefore = st.LeavesBroken;
        var wait = Stopwatch.StartNew();
        var progress = Stopwatch.StartNew();
        var decayWait = normal ? TimeSpan.FromTicks(Math.Min(o.DecayWait.Ticks, NormalDecayWait.Ticks)) : o.DecayWait;
        while (wait.Elapsed < decayWait && (normal ? !HasOwnSapling(st) : SaplingsGained(st) < o.WantSaplings))
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

    /// <summary>Normal world: leaves broken for a sapling when none of the tree's species is held, and how long to wait for decay.</summary>
    private const int NormalLeafBreaks = 24;
    private static readonly TimeSpan NormalDecayWait = TimeSpan.FromSeconds(12);

    /// <summary>A sapling of the tree's own species is held (what the replant plants).</summary>
    private bool HasOwnSapling(ChopState st) => World.Inventory.CountItem(Blocks.Sapling, st.Tree.Wood) > 0;

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
        var (cell, soilPos, settled) = ReplantSpot(st);
        if (settled is { } done) return done;
        if (World.GetBlock(cell) == Blocks.Sapling) { st.AlreadyPlanted = cell != tree.Base; return (cell, null); }
        if (ReplantRefusal(tree, cell, soilPos) is { } no) return (null, no);
        var r = await PlaceCoreAsync(cell, Blocks.Sapling, ct, allowPathing: true, damage: tree.Wood).ConfigureAwait(false);
        if (!r.Ok) return (null, "placing the sapling failed: " + r.Message);
        st.PlacedSapling = true;
        return World.GetBlock(cell) == Blocks.Sapling ? (cell, null) : (null, "the sapling popped off");
    }

    /// <summary>Where the sapling goes: the tree's base on its soil, or, for logs left hanging after their lower trunk was cut, the dirt
    /// under the gap (never reported as "gone": it was never under these logs). Settled = nothing to plant (already planted there, or
    /// no dirt under the remnant).</summary>
    private (BlockPos Cell, BlockPos Soil, (BlockPos? Planted, string? Problem)? Settled) ReplantSpot(ChopState st)
    {
        var tree = st.Tree;
        if (tree.OnSoil) return (tree.Base, tree.Soil, null);
        var (sapling, dirt, other) = TreeScan.ColumnUnder(World, tree.Soil);
        if (sapling is { } sp)
        {
            st.AlreadyPlanted = true;
            return (tree.Base, tree.Soil, (sp, null));
        }
        if (dirt is { } d) return (d.Up(), d, null);
        return (tree.Base, tree.Soil, (null, other is { } o
            ? $"these logs hung above {BlockName(World.GetBlock(o), World.GetMeta(o))} at {Fmt(o)}, not dirt; nothing to replant"
            : "these logs hung over the void; nothing to replant"));
    }

    /// <summary>Why the spot can't be replanted now: the spot itself, a dark oak, or no sapling of the tree's own species (live 10-28,
    /// task #55: a birch stump would have become an oak; another kind is the player's call: "no birch sapling; plant oak?").</summary>
    private string? ReplantRefusal(TreeInfo tree, BlockPos cell, BlockPos soilPos)
    {
        if (ReplantProblem(cell, soilPos) is { } p) return p;
        if (Wood.NeedsFourSaplings(tree.Wood)) return DarkOakReplant;
        if (World.Inventory.CountItem(Blocks.Sapling, tree.Wood) > 0) return null;
        return World.Inventory.FindSlots(Blocks.Sapling) is { Count: > 0 } any
            ? $"no {tree.WoodName} sapling; plant {TreeScan.WoodName(World.Inventory.GetPlayerSlot(any[0]).Damage)}?"
            : "no sapling";
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

    /// <summary>What a chop brought in (counted from the inventory against the start) and what it left standing.</summary>
    private readonly record struct ChopTally(int Logs, int Saplings, int Apples, int Left, string Counts);

    private ChopTally TallyChop(ChopState st)
    {
        var t = st.Tree;
        int logs = World.Inventory.CountItem(Wood.LogId(t.Wood), Wood.LogDamage(t.Wood)) - st.LogsBefore;
        int saplings = Math.Max(0, World.Inventory.CountItem(Blocks.Sapling) - st.SaplingsBefore + (st.PlacedSapling ? 1 : 0));
        int apples = World.Inventory.CountItem(Items.Apple) - st.ApplesBefore;
        int left = RemainingLogs(st).Count + st.Failed.Count(f => Wood.IsLog(World.GetBlock(f)));
        string counts = $"{logs} log{Plural(logs)}, {saplings} sapling{Plural(saplings)}" + (apples > 0 ? $", {apples} apple{Plural(apples)}" : "");
        return new ChopTally(logs, saplings, apples, left, counts);
    }

    private static string Plural(int n) => n == 1 ? "" : "s";

    /// <summary>Lost = fell (or will fall) into the void. Drops still lying in the crown aren't lost: says where they are.</summary>
    private static string ChopLostText(ChopState st, List<(int Id, int Item, BlockPos Leaf)> stuck) =>
        (st.Lost > 0 ? $"; {st.Lost} drop(s) lost near the void" : "")
        + (stuck.Count > 0 ? $"; {stuck.Count} drop(s) ({stuck.Count(d => d.Item == Blocks.Sapling)} sapling(s)) still lying on the leaves" : "");

    private ActionResult Finish(ChopState st, string? stop, BlockPos? planted, string? problem)
    {
        var t = st.Tree;
        var n = TallyChop(st);
        var stuck = StuckDrops(st);
        string lost = ChopLostText(st, stuck);
        string leafStats = $"broke {st.LeavesBroken} leaves, {st.LeavesDecayed} decayed";
        // A normal world doesn't hinge on one replant: felling the tree is the job, the replant a bonus.
        bool replantOptional = !WorldMode.Current.ScarceTerrain;
        bool ok = n.Left == 0 && stop is null && (planted is not null || replantOptional);
        string msg = ok ? ChoppedMessage(st, n, planted, problem) + lost
            : n.Left != 0 || stop is not null ? UnfinishedMessage(st, n, stop, planted) + lost
            : NotReplantedMessage(st, n, problem, leafStats) + lost;
        CountUnreplanted(st, stop, planted, problem, replantOptional);
        NoteChop(st);
        var report = new TreeChopReport(t.WoodName, t.Base, t.Soil, t.Logs.Count, st.Felled, n.Logs, n.Saplings, n.Apples, st.Lost, planted, problem, ok, msg)
        {
            LeavesBroken = st.LeavesBroken, LeavesDecayed = st.LeavesDecayed, StuckDrops = stuck.Count,
        };
        if (!msg.Contains(leafStats)) Log($"chop: {leafStats}, {stuck.Count} drop(s) still on the leaves");
        Log("chop: " + msg);
        Raise(() => TreeChopped?.Invoke(report));
        return ok ? ActionResult.Success(msg, n.Logs) : ActionResult.Fail(msg, n.Logs);
    }

    /// <summary>The tree is down and the chop counts as done: replanted, its spot already planted, or (normal world) not replanted.</summary>
    private string ChoppedMessage(ChopState st, ChopTally n, BlockPos? planted, string? problem)
    {
        var t = st.Tree;
        if (planted is not { } p) return $"chopped {t.WoodName} tree ({n.Counts}); not replanted ({problem ?? "no sapling"})";
        return st.AlreadyPlanted
            ? $"chopped {t.WoodName} tree ({n.Counts}); its spot {Fmt(p)} already has a sapling (dirt intact)"
            : $"chopped {t.WoodName} tree ({n.Counts}), replanted at {Fmt(p)}";
    }

    private static string UnfinishedMessage(ChopState st, ChopTally n, string? stop, BlockPos? planted) =>
        $"{stop ?? $"{n.Left} log(s) left"}; chopped {st.Felled}/{st.Tree.Logs.Count} {st.Tree.WoodName} logs ({n.Counts})"
        + (planted is { } pl ? $", replanted at {Fmt(pl)}" : ", not replanted");

    /// <summary>The tree is down but the replant (required on Skyblock) didn't happen: no sapling dropped, or the spot refused it.</summary>
    private string NotReplantedMessage(ChopState st, ChopTally n, string? problem, string leafStats)
    {
        var t = st.Tree;
        if (problem == "no sapling")
        {
            var soil = t.OnSoil ? t.Soil : TreeScan.ColumnUnder(World, t.Soil).Dirt ?? t.Soil;
            return $"no sapling dropped, NOT replanted (chopped {t.WoodName} tree: {n.Counts}; dirt at {Fmt(soil)} is free; {leafStats})";
        }
        // Live 10-27 (L6): ", sapling kept" was appended with 0 saplings. Say "kept" only when one is really held.
        string keep = World.Inventory.CountItem(Blocks.Sapling) > 0 ? ", sapling kept" : ", no sapling to plant either";
        return $"replant blocked: {problem} (chopped {t.WoodName} tree: {n.Counts}{keep})";
    }

    /// <summary>Consecutive chops that felled a tree without replanting it: the next chop_tree refuses after MaxUnreplantedChops.</summary>
    private void CountUnreplanted(ChopState st, string? stop, BlockPos? planted, string? problem, bool replantOptional)
    {
        if (planted is not null) _unreplantedChops = 0;
        else if (st.Felled > 0 && problem != DarkOakReplant && !replantOptional)
        {
            _unreplantedChops++;
            _lastUnreplanted = DateTime.UtcNow;
            _lastReplantProblem = stop ?? problem;
        }
    }
}
