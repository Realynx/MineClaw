using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    // ------------------------------------------------------------------ drops

    /// <summary>Walks over dropped items within <paramref name="radius"/>. Never chases items over or near the void:
    /// an item is collected only from a standing spot at least 2 blocks from any edge (vanilla picks items up within
    /// ~1.3 blocks), otherwise it is reported as lost.</summary>
    public Task<ActionResult> CollectDropsAsync(double radius = 8, CancellationToken ct = default) =>
        RunActionAsync("collect", ct, async c =>
        {
            var r = await CollectDropsCoreAsync(radius, 32, c).ConfigureAwait(false);
            string lost = LostText(r)
                          + (r.Stuck > 0 ? $"; {r.Stuck} drop(s) lying up on leaves (break the leaf under them; not lost)" : "");
            return r.Entities > 0
                ? ActionResult.Success($"picked up {r.Items} item(s){lost}", r.Items)
                : ActionResult.Fail("no reachable drops nearby" + lost);
        });

    /// <param name="Hazard">Of <paramref name="Lost"/>: drops left in/next to lava or water (a generator's channel), not
    /// over the void.</param>
    private readonly record struct CollectResult(int Entities, int Items, int Lost, int LostItems, int Stuck = 0, int Hazard = 0);

    /// <summary>"; 2 drop(s) left near the void (2 item(s) lost); 3 drop(s) left in lava/water (hazard, not chased)".</summary>
    private static string LostText(CollectResult r)
    {
        int voidLost = r.Lost - r.Hazard;
        return (voidLost > 0 ? $"; {voidLost} drop(s) left near the void" + (r.Hazard == 0 ? $" ({r.LostItems} item(s) lost)" : "") : "")
               + (r.Hazard > 0 ? $"; {r.Hazard} drop(s) left in lava/water (hazard, not chased)" : "");
    }

    /// <summary>Drop entity ids already reported as not chased: logged once per drop, not on every collect pass.</summary>
    private readonly HashSet<int> _lostDropsLogged = new();

    private bool FirstLostReport(int id)
    {
        lock (_lostDropsLogged)
        {
            if (_lostDropsLogged.Count > 256) _lostDropsLogged.RemoveWhere(x => World.Entities.Get(x) is null);
            return _lostDropsLogged.Add(id);
        }
    }

    /// <summary>Standing cells from which a drop at <paramref name="item"/> is picked up without going near an edge.</summary>
    private Goal SafePickupGoal(Vec3d item)
    {
        var cell = new BlockPos((int)Math.Floor(item.X), (int)Math.Floor(item.Y + 0.1), (int)Math.Floor(item.Z));
        return new GoalFiltered(new GoalNear(cell, 2), (x, y, z) =>
                Math.Abs(x + 0.5 - item.X) <= 1.1 && Math.Abs(z + 0.5 - item.Z) <= 1.1 && Math.Abs(y - item.Y) <= 1.2
                && Safety.EdgeDistance(World, x, y, z, 2) >= 2 && !Safety.LavaNear(World, x, y, z)
                && !BlockShapes.IsWater(World.GetBlock(x, y, z)) // never wade into a generator's water channel for a drop
                && World.GetBlock(x, y - 1, z) != Blocks.Leaves, // never walk out onto a crown for a drop
            "safe pickup spot");
    }

    /// <summary>Whether some standable cell can pick up the drop at <paramref name="item"/> safely.</summary>
    private bool HasSafePickupSpot(Vec3d item) => LostDropReason(item) is null;

    /// <summary>
    /// Why a drop can't be picked up safely (null = it can): "over the void", "in lava/water (hazard)" (a cobble generator's
    /// channel: lava or water at/next to it), or "next to the void" (only edge cells could reach it).
    /// </summary>
    private string? LostDropReason(Vec3d item)
    {
        int cx = (int)Math.Floor(item.X), cy = (int)Math.Floor(item.Y + 0.1), cz = (int)Math.Floor(item.Z);
        if (World.IsOverVoid(cx, cy, cz)) return "over the void";
        var goal = SafePickupGoal(item);
        for (int x = cx - 2; x <= cx + 2; x++)
        for (int y = cy - 1; y <= cy + 1; y++)
        for (int z = cz - 2; z <= cz + 2; z++)
            if (Safety.IsStandable(World, x, y, z) && goal.IsReached(x, y, z)) return null;
        return HazardAround(cx, cy, cz) ? "in lava/water (hazard)" : "next to the void";
    }

    /// <summary>Lava or water in the drop's cell or right next to it (a generator's channel).</summary>
    private bool HazardAround(int x, int y, int z)
    {
        if (Safety.LavaNear(World, x, y, z)) return true;
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
            if (BlockShapes.IsWater(World.GetBlock(x + dx, y + dy, z + dz))) return true;
        return false;
    }

    /// <param name="lostIds">Collects (and logs once) drops written off over/next to the void.</param>
    /// <param name="stuckIds">Collects (and logs once) drops lying on a leaf block up in a crown: out of pickup reach from
    /// the ground but not lost (breaking that leaf drops them; chop_tree does).</param>
    private async Task<CollectResult> CollectDropsCoreAsync(double radius, int maxEntities, CancellationToken ct, Vec3d? around = null,
        ISet<int>? lostIds = null, ISet<int>? stuckIds = null)
    {
        var skip = new HashSet<int>();
        int picked = 0, lost = 0, lostItems = 0, stuck = 0, hazard = 0;
        int startCount = TotalItems();
        var origin = around ?? World.Player.Position;
        for (int guard = 0; guard < maxEntities * 2 && picked < maxEntities; guard++)
        {
            var me = World.Player.Position;
            var drop = World.Entities.Nearby(origin, radius, e => e.Kind == EntityKind.Item && !skip.Contains(e.Id)
                    && World.Inventory.RoomFor(e.Item) > 0)
                .OrderBy(e => e.Position.DistanceSquaredTo(me)).FirstOrDefault();
            if (drop is null) break;
            int id = drop.Id;
            // mc-dev pickup box (ItemEntityRules): an item one block below the feet is not in range.
            bool inRange = ItemEntityRules.InPickupRange(me, drop.Position);
            if (!inRange)
            {
                // Lying on a leaf up in a crown: out of reach from the ground but not lost (the leaf under it is what to break).
                if (MineClaw.Client.Trees.TreeScan.SupportingLeaf(World, drop.Position) is { } leaf
                    && !World.IsOverVoid(leaf.X, leaf.Y - 1, leaf.Z))
                {
                    skip.Add(id);
                    stuck++;
                    if (stuckIds?.Add(id) ?? true)
                        Log($"collect: {drop.DisplayName} at {Fmt(drop.Position)} is lying on the leaves at {Fmt(leaf)} (out of reach, not lost)");
                    continue;
                }
                if (LostDropReason(drop.Position) is { } why)
                {
                    skip.Add(id);
                    lost++;
                    lostItems += drop.Item.Count;
                    if (why.Contains("hazard", StringComparison.Ordinal)) hazard++;
                    // Logged once per drop: callers without their own set (mine/harvest passes) share the client's.
                    if (lostIds?.Add(id) ?? FirstLostReport(id))
                        Log($"collect: not chasing {drop.DisplayName} at {Fmt(drop.Position)}: {why}");
                    continue;
                }
                var g = await GotoCoreAsync(SafePickupGoal(drop.Position), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(15), MaxReplans = 1 }, ct)
                    .ConfigureAwait(false);
                if (!g.Ok && World.Entities.Get(id) is not null) { skip.Add(id); continue; }
            }
            // A freshly thrown item (player hand-off) has a 40-tick pickup delay: wait it out on top of the usual 1.5 s.
            var wait = TimeSpan.FromSeconds(1.5) + ItemEntityRules.RemainingPickupDelay(DateTime.UtcNow - drop.SpawnedAt);
            if (await WaitUntilAsync(() => World.Entities.Get(id) is null, wait, ct).ConfigureAwait(false)) picked++;
            else skip.Add(id);
        }
        return new CollectResult(picked, Math.Max(0, TotalItems() - startCount), lost, lostItems, stuck, hazard);
    }

    private int TotalItems()
    {
        int n = 0;
        foreach (var s in World.Inventory.PlayerSlots().Skip(9)) if (!s.IsEmpty) n += s.Count;
        return n;
    }

    // ------------------------------------------------------------------ mining

    /// <summary>Mines up to <paramref name="count"/> blocks of a type (name or id) within <paramref name="radius"/>,
    /// nearest/reachable first, collecting drops. Never digs protected blocks, blocks holding the bot up, or blocks
    /// next to lava. <paramref name="protect"/> adds to <see cref="IsProtected"/> for this call.</summary>
    public Task<ActionResult> MineAsync(string blockName, int count, double radius = 16, Func<BlockPos, int, bool>? protect = null,
        CancellationToken ct = default)
    {
        var b = Blocks.ByName(blockName);
        if (b is null) return Task.FromResult(ActionResult.Fail($"unknown block '{blockName}'"));
        var ids = new HashSet<int> { b.Id };
        if (b.Id == Blocks.RedstoneOre) ids.Add(Blocks.LitRedstoneOre);
        if (b.Id == Blocks.Furnace) ids.Add(Blocks.LitFurnace);
        return MineAsync(ids, count, radius, protect, ct);
    }

    public Task<ActionResult> MineAsync(IReadOnlySet<int> blockIds, int count, double radius = 16, Func<BlockPos, int, bool>? protect = null,
        CancellationToken ct = default) =>
        RunActionAsync("mine", ct, async c =>
        {
            if (count >= Options.Survival.LongMiningCount && await SurvivalGateAsync($"mine {count} blocks", c).ConfigureAwait(false) is { } refused)
                return refused;
            int firstId = blockIds.First();
            string name = BlockName(firstId, 0);
            var dropItem = Drops.Primary(firstId, 0, BestToolId(firstId))?.ItemId ?? firstId;
            int haveBefore = World.Inventory.CountItem(dropItem);
            var failed = new HashSet<BlockPos>();
            int mined = 0, failures = 0, lostDrops = 0, hazardDrops = 0;
            string stop = "";
            try
            {
                while (mined < count)
                {
                    if (World.Inventory.RoomFor(new ItemStack((short)dropItem, 1, 0)) == 0) { stop = "inventory full"; break; }
                    await SurvivalWindowAsync(c).ConfigureAwait(false);
                    var target = PickMineTarget(blockIds, radius, failed, (p, id) => EditBlocked(p) || (protect?.Invoke(p, id) ?? false));
                    if (target is null)
                    {
                        stop = $"no more reachable {name} within {radius:0}";
                        if (PickMineTarget(blockIds, radius, failed, protect) is { } inside && EditBlocked(inside))
                            stop += $" outside spawn protection (the server undid my digs within {_protectedRegion?.Radius ?? VanillaSpawnProtection} of spawn; needs op)";
                        // Never refuse silently (rule 5): protected candidates were skipped, say which and how to unlock them.
                        else if (_lastMineSkip is { } skip)
                            stop += $"; skipped {_lastMineSkipCount} protected: " + ProtectedRefusal(skip, World.GetBlock(skip));
                        if (_lastMineNonSource > 0)
                            stop += $"; left {_lastMineNonSource} placed {name} alone (floors/walls/builds): mine only takes generator cells, "
                                    + "ores, sand/gravel, tree logs and my own blocks"
                                    + (firstId is Blocks.Cobblestone or Blocks.Stone ? " (for cobble use harvest_generator)" : "")
                                    + "; a trusted player can point at one and say 'you can break it'";
                        break;
                    }
                    var r = await DigCoreAsync(target.Value, c, floorGuard: true).ConfigureAwait(false);
                    if (!r.Ok)
                    {
                        failed.Add(target.Value);
                        if (++failures >= 8) { stop = "too many failures, last: " + r.Message; break; }
                        continue;
                    }
                    mined++;
                    var cr = await CollectDropsCoreAsync(5, 4, c).ConfigureAwait(false);
                    lostDrops += cr.Lost - cr.Hazard;
                    hazardDrops += cr.Hazard;
                }
            }
            catch (OperationCanceledException) when (mined > 0)
            {
                stop = _abortReason ?? "stopped";
            }
            int gained = World.Inventory.CountItem(dropItem) - haveBefore;
            string msg = $"mined {mined} {name}, +{gained} {ItemName(dropItem)} (have {World.Inventory.CountItem(dropItem)})"
                         + (lostDrops > 0 ? $"; {lostDrops} drop(s) left near the void" : "")
                         + (hazardDrops > 0 ? $"; {hazardDrops} drop(s) left in lava/water" : "") + (stop.Length > 0 ? "; " + stop : "");
            return mined > 0 ? ActionResult.Success(msg, mined) : ActionResult.Fail(msg);
        });

    private int BestToolId(int blockId)
    {
        var slots = World.Inventory.PlayerSlots();
        int idx = Tools.BestToolIndex(blockId, slots);
        return idx < 0 ? -1 : slots[idx].Id;
    }

    /// <summary>Nearest candidate the last <see cref="PickMineTarget"/> skipped for protection, and how many it skipped.</summary>
    private BlockPos? _lastMineSkip;
    private int _lastMineSkipCount;
    /// <summary>Candidates the last pick left alone because they aren't a resource source (placed floors, walls...).</summary>
    private int _lastMineNonSource;

    private BlockPos? PickMineTarget(IReadOnlySet<int> ids, double radius, HashSet<BlockPos> failed, Func<BlockPos, int, bool>? protect)
    {
        var eye = World.Player.EyePosition;
        var support = SupportBlocks().ToHashSet();
        support.UnionWith(Safety.OtherPlayersSupport(World));
        _lastMineSkip = null;
        _lastMineSkipCount = 0;
        _lastMineNonSource = 0;
        var found = World.FindBlocks(World.Player.Position, radius, (id, _) => ids.Contains(id), 512);
        BlockPos? best = null;
        double bestScore = double.MaxValue;
        foreach (var f in found)
        {
            var p = f.Pos;
            if (failed.Contains(p) || support.Contains(p)) continue;
            if (IsBlockProtected(p, f.Id))
            {
                if (_lastMineSkipCount++ == 0) _lastMineSkip = p; // found nearest first
                continue;
            }
            if (protect?.Invoke(p, f.Id) ?? false) continue;
            if (!IsMineSource(p, f.Id)) { _lastMineNonSource++; continue; }
            if (NextToLava(p) || WouldDropIntoVoid(p)) continue;
            if (WalkwayFloorVeto(p, "") is not null) continue; // never the island floor next to the void (B21)
            if (!IsExposed(p)) continue;
            double score = f.DistanceSquared;
            if (Aiming.FindDigAim(World, eye, p, Options.Reach) is not null) score -= 1000; // in reach right now
            if (score < bestScore) { bestScore = score; best = p; }
        }
        return best;
    }

    private bool IsExposed(BlockPos p)
    {
        foreach (var f in BlockFaceExtensions.All)
        {
            var n = p.Offset(f);
            if (!BlockShapes.HasCollision(World, n.X, n.Y, n.Z) && !BlockShapes.IsLava(World.GetBlock(n))) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ cobblestone generator

    /// <summary>
    /// Stands within reach of a cobblestone generator's output block and repeatedly digs it as it regenerates
    /// (waiting for the block change), collecting the drops. Ignores the protected predicate for that one block.
    /// </summary>
    public Task<ActionResult> HarvestGeneratorAsync(int x, int y, int z, int count, CancellationToken ct = default) =>
        HarvestGeneratorAsync(x, y, z, count, stand: null, ct);

    /// <param name="stand">The stand spot a player taught for this cell (watch_demo "stood at 7,63,8 ... punched 6,64,8"):
    /// the bot walks onto it first (refusing to dig from anywhere else when it can't get there) and stays on it, trusting the
    /// teach over its own lava-distance preference. Null = pick a safe spot itself, once, and stay there.</param>
    public Task<ActionResult> HarvestGeneratorAsync(int x, int y, int z, int count, BlockPos? stand, CancellationToken ct = default) =>
        RunActionAsync("generator", ct, async c =>
        {
            if (count >= Options.Survival.LongMiningCount && await SurvivalGateAsync($"mine {count} blocks", c).ConfigureAwait(false) is { } refused)
                return refused;
            var pos = new BlockPos(x, y, z);
            if (NeverBreak?.Invoke(pos) == true) return ActionResult.Fail(ProtectedRefusal(pos, World.GetBlock(pos)));
            // Never silently do something else: without a pickaxe the generator's output drops nothing, so say so.
            if (Drops.Primary(Blocks.Cobblestone, 0, BestToolId(Blocks.Cobblestone)) is null)
                return ActionResult.Fail("no pickaxe: cobblestone drops nothing without one (get a pickaxe first: withdraw or craft one)");
            int haveBefore = World.Inventory.CountItem(Blocks.Cobblestone);
            int mined = 0, failures = 0;
            string stop = "";
            bool badYield = false;
            int windowStartGained = 0, windowStartMined = 0;
            static bool IsGen(int id) => id is Blocks.Cobblestone or Blocks.Stone;
            try
            {
                if (stand is { } st)
                {
                    if (await GotoStandAsync(st, pos, c).ConfigureAwait(false) is { } no) return no;
                    _trustedStand = (st, pos);
                }
                else
                {
                    // Stand within reach, away from the edge and from the lava (never next to the output block).
                    bool badSpot = Aiming.FindDigAim(World, World.Player.EyePosition, pos, Options.Reach) is null
                                   || OnRimDiggingRim(pos) || LavaNearMe() || LavaPathToMe(pos);
                    if (badSpot && IsGen(World.GetBlock(pos)))
                    {
                        var g = await GotoReachSafelyAsync(pos, generator: true, c).ConfigureAwait(false);
                        if (!g.Ok) return ActionResult.Fail("can't get to a safe spot at the generator: " + g.Message);
                    }
                }
                while (mined < count)
                {
                    if (World.Inventory.RoomFor(new ItemStack(Blocks.Cobblestone, 1, 0)) == 0) { stop = "inventory full"; break; }
                    // Tool broke mid-loop: digging on would yield nothing (the caller fetches a spare / crafts one).
                    if (Drops.Primary(Blocks.Cobblestone, 0, BestToolId(Blocks.Cobblestone)) is null) { stop = "pickaxe broke (no pickaxe left)"; break; }
                    await SurvivalWindowAsync(c).ConfigureAwait(false);
                    if (!await WaitUntilAsync(() => IsGen(World.GetBlock(pos)), TimeSpan.FromSeconds(8), c).ConfigureAwait(false))
                    {
                        stop = $"generator at {Fmt(pos)} stopped producing (now {BlockName(World.GetBlock(pos), 0)})";
                        break;
                    }
                    // Stay put once positioned (live 10-27: a re-walk before every dig fought the water push, "goto: stuck,
                    // re-planning" every 8 s): walk again only when the cell is out of reach, or released lava could reach me.
                    bool move = Aiming.FindDigAim(World, World.Player.EyePosition, pos, Options.Reach) is null
                                || (!OnTrustedStand(pos) && LavaPathToMe(pos));
                    if (move && stand is { } back)
                    {
                        if (await GotoStandAsync(back, pos, c).ConfigureAwait(false) is { } no) { stop = no.Message; break; }
                        move = false;
                    }
                    var r = await DigCoreAsync(pos, c, allowPathing: move, ignoreProtection: true, generator: true).ConfigureAwait(false);
                    if (!r.Ok)
                    {
                        if (++failures >= 3) { stop = r.Message; break; }
                        continue;
                    }
                    failures = 0;
                    mined++;
                    if (mined % YieldWindow == 0)
                    {
                        await CollectDropsCoreAsync(4, 8, c).ConfigureAwait(false);
                        // Picking drops up may step off the taught spot: back onto it before the next dig.
                        if (stand is { } st2 && !OnTrustedStand(pos) && await GotoStandAsync(st2, pos, c).ConfigureAwait(false) is { } off)
                        {
                            stop = off.Message;
                            break;
                        }
                        // Yield guard: a real regen cell gives a cobblestone per dig. Fewer than ~80% over the last window AND
                        // overall = the wrong block (not the gen's output, or drops lost over the edge): stop, never dig on.
                        int gainedNow = World.Inventory.CountItem(Blocks.Cobblestone) - haveBefore;
                        double window = (gainedNow - windowStartGained) / (double)Math.Max(1, mined - windowStartMined);
                        double overall = gainedNow / (double)mined;
                        windowStartGained = gainedNow;
                        windowStartMined = mined;
                        if (window < MinGeneratorYield && overall < MinGeneratorYield)
                        {
                            badYield = true;
                            stop = $"{Fmt(pos)} isn't regenerating cobble for me (yield {overall:P0}: {gainedNow} cobblestone from {mined} digs), need the right cell";
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (mined > 0)
            {
                stop = _abortReason ?? "stopped";
            }
            finally
            {
                _trustedStand = null;
            }
            if (mined > 0)
            {
                try { await CollectDropsCoreAsync(4, 16, c).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* keep the partial result */ }
            }
            int gained = World.Inventory.CountItem(Blocks.Cobblestone) - haveBefore;
            if (badYield) return ActionResult.Fail(stop + $" (dug {mined}, +{gained})", mined);
            string msg = $"generator: dug {mined}, +{gained} cobblestone (have {World.Inventory.CountItem(Blocks.Cobblestone)})" + (stop.Length > 0 ? "; " + stop : "");
            return mined > 0 ? ActionResult.Success(msg, mined) : ActionResult.Fail(msg);
        });

    /// <summary>The taught stand spot of the generator cell being harvested right now (stand, cell), or null.</summary>
    private (BlockPos Stand, BlockPos Cell)? _trustedStand;

    /// <summary>Standing on the taught stand spot for digging <paramref name="cell"/>.</summary>
    private bool OnTrustedStand(BlockPos cell) =>
        _trustedStand is { } t && t.Cell == cell && FeetCell() == t.Stand;

    private BlockPos FeetCell()
    {
        var p = World.Player.Position;
        return new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y + 1e-6), (int)Math.Floor(p.Z));
    }

    /// <summary>Walks onto the taught stand spot; null when there (and the cell is in reach), else the refusal.</summary>
    private async Task<ActionResult?> GotoStandAsync(BlockPos stand, BlockPos cell, CancellationToken ct)
    {
        if (FeetCell() != stand)
        {
            var g = await GotoCoreAsync(new GoalBlock(stand), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(30), MaxReplans = 2 }, ct)
                .ConfigureAwait(false);
            if (!g.Ok || FeetCell() != stand)
                return ActionResult.Fail($"can't get onto the taught stand spot {Fmt(stand)} for the cell {Fmt(cell)} ({g.Message}); not digging from anywhere else");
        }
        if (Aiming.FindDigAim(World, World.Player.EyePosition, cell, Options.Reach) is null)
            return ActionResult.Fail($"from the taught stand spot {Fmt(stand)} the cell {Fmt(cell)} is out of reach: wrong stand spot?");
        return null;
    }

    /// <summary>Digs per yield check of <see cref="HarvestGeneratorAsync"/>.</summary>
    public const int YieldWindow = 8;
    /// <summary>Below this share of digs that gave a cobblestone (window and overall), the cell is the wrong one.</summary>
    public const double MinGeneratorYield = 0.8;
}
