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
            string lost = r.Lost > 0 ? $"; {r.Lost} drop(s) left near the void ({r.LostItems} item(s) lost)" : "";
            return r.Entities > 0
                ? ActionResult.Success($"picked up {r.Items} item(s){lost}", r.Items)
                : ActionResult.Fail("no reachable drops nearby" + lost);
        });

    private readonly record struct CollectResult(int Entities, int Items, int Lost, int LostItems);

    /// <summary>Standing cells from which a drop at <paramref name="item"/> is picked up without going near an edge.</summary>
    private Goal SafePickupGoal(Vec3d item)
    {
        var cell = new BlockPos((int)Math.Floor(item.X), (int)Math.Floor(item.Y + 0.1), (int)Math.Floor(item.Z));
        return new GoalFiltered(new GoalNear(cell, 2), (x, y, z) =>
                Math.Abs(x + 0.5 - item.X) <= 1.1 && Math.Abs(z + 0.5 - item.Z) <= 1.1 && Math.Abs(y - item.Y) <= 1.2
                && Safety.EdgeDistance(World, x, y, z, 2) >= 2 && !Safety.LavaNear(World, x, y, z),
            "safe pickup spot");
    }

    /// <summary>Whether some standable cell can pick up the drop at <paramref name="item"/> safely.</summary>
    private bool HasSafePickupSpot(Vec3d item)
    {
        if (World.IsOverVoid((int)Math.Floor(item.X), (int)Math.Floor(item.Y + 0.1), (int)Math.Floor(item.Z))) return false;
        var goal = SafePickupGoal(item);
        int cx = (int)Math.Floor(item.X), cy = (int)Math.Floor(item.Y + 0.1), cz = (int)Math.Floor(item.Z);
        for (int x = cx - 2; x <= cx + 2; x++)
        for (int y = cy - 1; y <= cy + 1; y++)
        for (int z = cz - 2; z <= cz + 2; z++)
            if (Safety.IsStandable(World, x, y, z) && goal.IsReached(x, y, z)) return true;
        return false;
    }

    private async Task<CollectResult> CollectDropsCoreAsync(double radius, int maxEntities, CancellationToken ct)
    {
        var skip = new HashSet<int>();
        int picked = 0, lost = 0, lostItems = 0;
        int startCount = TotalItems();
        var origin = World.Player.Position;
        for (int guard = 0; guard < maxEntities * 2 && picked < maxEntities; guard++)
        {
            var me = World.Player.Position;
            var drop = World.Entities.Nearby(origin, radius, e => e.Kind == EntityKind.Item && !skip.Contains(e.Id)
                    && World.Inventory.RoomFor(e.Item) > 0)
                .OrderBy(e => e.Position.DistanceSquaredTo(me)).FirstOrDefault();
            if (drop is null) break;
            int id = drop.Id;
            bool inRange = Math.Abs(drop.Position.X - me.X) <= 1.2 && Math.Abs(drop.Position.Z - me.Z) <= 1.2
                           && Math.Abs(drop.Position.Y - me.Y) <= 1.2;
            if (!inRange)
            {
                if (!HasSafePickupSpot(drop.Position))
                {
                    skip.Add(id);
                    lost++;
                    lostItems += drop.Item.Count;
                    Log($"collect: not chasing {drop.DisplayName} at {Fmt(drop.Position)}: over or next to the void");
                    continue;
                }
                var g = await GotoCoreAsync(SafePickupGoal(drop.Position), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(15), MaxReplans = 1 }, ct)
                    .ConfigureAwait(false);
                if (!g.Ok && World.Entities.Get(id) is not null) { skip.Add(id); continue; }
            }
            if (await WaitUntilAsync(() => World.Entities.Get(id) is null, TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false)) picked++;
            else skip.Add(id);
        }
        return new CollectResult(picked, Math.Max(0, TotalItems() - startCount), lost, lostItems);
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
            int mined = 0, failures = 0, lostDrops = 0;
            string stop = "";
            try
            {
                while (mined < count)
                {
                    if (World.Inventory.RoomFor(new ItemStack((short)dropItem, 1, 0)) == 0) { stop = "inventory full"; break; }
                    await SurvivalWindowAsync(c).ConfigureAwait(false);
                    var target = PickMineTarget(blockIds, radius, failed, protect);
                    if (target is null) { stop = $"no more reachable {name} within {radius:0}"; break; }
                    var r = await DigCoreAsync(target.Value, c).ConfigureAwait(false);
                    if (!r.Ok)
                    {
                        failed.Add(target.Value);
                        if (++failures >= 8) { stop = "too many failures, last: " + r.Message; break; }
                        continue;
                    }
                    mined++;
                    lostDrops += (await CollectDropsCoreAsync(5, 4, c).ConfigureAwait(false)).Lost;
                }
            }
            catch (OperationCanceledException) when (mined > 0)
            {
                stop = _abortReason ?? "stopped";
            }
            int gained = World.Inventory.CountItem(dropItem) - haveBefore;
            string msg = $"mined {mined} {name}, +{gained} {ItemName(dropItem)} (have {World.Inventory.CountItem(dropItem)})"
                         + (lostDrops > 0 ? $"; {lostDrops} drop(s) left near the void" : "") + (stop.Length > 0 ? "; " + stop : "");
            return mined > 0 ? ActionResult.Success(msg, mined) : ActionResult.Fail(msg);
        });

    private int BestToolId(int blockId)
    {
        var slots = World.Inventory.PlayerSlots();
        int idx = Tools.BestToolIndex(blockId, slots);
        return idx < 0 ? -1 : slots[idx].Id;
    }

    private BlockPos? PickMineTarget(IReadOnlySet<int> ids, double radius, HashSet<BlockPos> failed, Func<BlockPos, int, bool>? protect)
    {
        var eye = World.Player.EyePosition;
        var support = SupportBlocks().ToHashSet();
        var found = World.FindBlocks(World.Player.Position, radius, (id, _) => ids.Contains(id), 512);
        BlockPos? best = null;
        double bestScore = double.MaxValue;
        foreach (var f in found)
        {
            var p = f.Pos;
            if (failed.Contains(p) || support.Contains(p)) continue;
            if (IsBlockProtected(p, f.Id) || (protect?.Invoke(p, f.Id) ?? false)) continue;
            if (NextToLava(p) || WouldDropIntoVoid(p)) continue;
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
        RunActionAsync("generator", ct, async c =>
        {
            if (count >= Options.Survival.LongMiningCount && await SurvivalGateAsync($"mine {count} blocks", c).ConfigureAwait(false) is { } refused)
                return refused;
            var pos = new BlockPos(x, y, z);
            int haveBefore = World.Inventory.CountItem(Blocks.Cobblestone);
            int mined = 0, failures = 0;
            string stop = "";
            static bool IsGen(int id) => id is Blocks.Cobblestone or Blocks.Stone;
            try
            {
                // Stand within reach, away from the edge and from the lava (never next to the output block).
                bool badSpot = Aiming.FindDigAim(World, World.Player.EyePosition, pos, Options.Reach) is null
                               || OnRimDiggingRim(pos) || LavaNearMe() || LavaPathToMe(pos);
                if (badSpot && IsGen(World.GetBlock(pos)))
                {
                    var g = await GotoReachSafelyAsync(pos, generator: true, c).ConfigureAwait(false);
                    if (!g.Ok) return ActionResult.Fail("can't get to a safe spot at the generator: " + g.Message);
                }
                while (mined < count)
                {
                    if (World.Inventory.RoomFor(new ItemStack(Blocks.Cobblestone, 1, 0)) == 0) { stop = "inventory full"; break; }
                    await SurvivalWindowAsync(c).ConfigureAwait(false);
                    if (!await WaitUntilAsync(() => IsGen(World.GetBlock(pos)), TimeSpan.FromSeconds(8), c).ConfigureAwait(false))
                    {
                        stop = $"generator at {Fmt(pos)} stopped producing (now {BlockName(World.GetBlock(pos), 0)})";
                        break;
                    }
                    var r = await DigCoreAsync(pos, c, allowPathing: true, ignoreProtection: true, generator: true).ConfigureAwait(false);
                    if (!r.Ok)
                    {
                        if (++failures >= 3) { stop = r.Message; break; }
                        continue;
                    }
                    failures = 0;
                    mined++;
                    if (mined % 8 == 0) await CollectDropsCoreAsync(4, 8, c).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (mined > 0)
            {
                stop = _abortReason ?? "stopped";
            }
            if (mined > 0)
            {
                try { await CollectDropsCoreAsync(4, 16, c).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* keep the partial result */ }
            }
            int gained = World.Inventory.CountItem(Blocks.Cobblestone) - haveBefore;
            string msg = $"generator: dug {mined}, +{gained} cobblestone (have {World.Inventory.CountItem(Blocks.Cobblestone)})" + (stop.Length > 0 ? "; " + stop : "");
            return mined > 0 ? ActionResult.Success(msg, mined) : ActionResult.Fail(msg);
        });
}
