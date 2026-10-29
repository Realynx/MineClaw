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

    /// <summary>
    /// Standing cells from which a drop at <paramref name="item"/> is picked up without going near an edge or lava. Reach is
    /// mc-dev's (EntityHuman.java:308, <see cref="ItemEntityRules.InPickupRange"/>): the player box grown by (1, 0, 1) from
    /// the spot's real feet height (a slab stand is half a block higher: an item on the floor beside it is out of range), with
    /// the body anywhere inside its foot cell. Live 10-28: 630 gen drops "not chased" from 880 digs; they lay in the gen's
    /// water corners (6.2,64.1,7.9 / 5.3,64.1,6.8) 1.375 from the pit 7,63,8 and the nook 4,64,5, i.e. in reach (1.425), but
    /// the old ±1.3-from-the-cell-centre test wrote them off. Flowing water is no hazard to stand in (b1.8.1 water doesn't hurt);
    /// lava within a block still is.
    /// </summary>
    private Goal SafePickupGoal(Vec3d item)
    {
        var cell = new BlockPos((int)Math.Floor(item.X), (int)Math.Floor(item.Y + 0.1), (int)Math.Floor(item.Z));
        return new GoalFiltered(new GoalNear(cell, 2), (x, y, z) =>
                PickupPoint(x, y, z, item) is not null
                && Safety.EdgeDistance(World, x, y, z, 2) >= 2 && !Safety.LavaNear(World, x, y, z)
                && !Trees.Wood.IsLeaves(World.GetBlock(x, y - 1, z)), // never walk out onto a crown for a drop
            "safe pickup spot");
    }

    /// <summary>
    /// Where in foot cell (x,y,z) the body touches the drop at <paramref name="item"/> (mc-dev pickup box), the cell centre
    /// first, else the point of the cell nearest the item (the box stays inside the cell); null = not from this cell.
    /// </summary>
    private Vec3d? PickupPoint(int x, int y, int z, Vec3d item)
    {
        if (StandFeet(x, y, z) is not { } feet) return null;
        // Wading is fine, but never with the head under the gen's falling water (it pushes the body about next to the cells).
        if (BlockShapes.IsWater(World.GetBlock(x, (int)Math.Floor(feet + PlayerPhysics.EyeHeight), z))) return null;
        var centre = new Vec3d(x + 0.5, feet, z + 0.5);
        if (ItemEntityRules.InPickupRange(centre, item)) return centre;
        const double Inset = PlayerPhysics.Width / 2 + 0.02;
        var edge = new Vec3d(Math.Clamp(item.X, x + Inset, x + 1 - Inset), feet, Math.Clamp(item.Z, z + Inset, z + 1 - Inset));
        return ItemEntityRules.InPickupRange(edge, item) ? edge : null;
    }

    /// <summary>
    /// Arrived on a pickup spot but the drop is still a little out of the pickup box (it lies off the cell centre): sneak toward
    /// it inside the foot cell (sneaking never steps off an edge) until it is in range, at most ~12 ticks.
    /// </summary>
    private async Task LeanToDropAsync(int id, CancellationToken ct)
    {
        var start = FeetCell();
        int ticks = 0;
        await DriveAsync(phys =>
        {
            ticks++;
            var e = World.Entities.Get(id);
            var p = phys.Position;
            var at = new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y + 1e-6), (int)Math.Floor(p.Z));
            float yaw = e is null ? phys.Yaw : PlayerPhysics.YawPitchTo(p, new Vec3d(e.Position.X, p.Y, e.Position.Z)).Yaw;
            bool done = e is null || ticks > 12 || ItemEntityRules.InPickupRange(p, e.Position) || at.X != start.X || at.Z != start.Z;
            return done
                ? new DriverStep(new MoveInput(Sneak: true, Yaw: yaw, Pitch: 40), Done: true)
                : new DriverStep(new MoveInput(Forward: 1, Sneak: true, Yaw: yaw, Pitch: 40));
        }, ct).ConfigureAwait(false);
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
            if (PickupStandable(x, y, z) && goal.IsReached(x, y, z)) return null;
        if (Safety.LavaNear(World, cx, cy, cz)) return "in lava/water (hazard)";
        return HazardAround(cx, cy, cz) ? "in the gen's water, no safe spot in pickup reach (hazard)" : "next to the void";
    }

    /// <summary>How long a fresh drop with no pickup spot yet may still move (regen push-out, water flow) before it is written off.</summary>
    public TimeSpan DropSettleTime { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A foot cell a drop can be fetched from: plain ground (<see cref="Safety.IsStandable"/>), or ON a bottom slab in that
    /// cell (feet at y + 0.5; the pathfinder walks these). Live 10-27 21:13: the gen's drops lay on the slab beside the cell
    /// (4.1,63.6,5.7) and were written off as "in lava/water" because a slab never counted as somewhere to stand.
    /// </summary>
    private bool PickupStandable(int x, int y, int z) => StandFeet(x, y, z) is not null;

    /// <summary>Feet height of a player standing in foot cell (x,y,z): full ground below (y), a bottom slab in the cell (y + 0.5),
    /// or a bottom slab in the cell below (y - 0.5, the pathfinder's view of the same spot); null = nobody stands there.</summary>
    private double? StandFeet(int x, int y, int z)
    {
        if (Safety.IsStandable(World, x, y, z)) return y;
        if (!World.IsLoaded(x, z) || y < 2 || y + 2 >= World.WorldHeight) return null;
        static bool Half(double top) => top > 0 && top <= 0.5 + 1e-9;
        double feet;
        if (BlockShapes.HasCollision(World, x, y, z))
        {
            double top = BlockShapes.CollisionTop(World, x, y, z);
            if (!Half(top)) return null;
            feet = y + top;
        }
        else if (BlockShapes.HasCollision(World, x, y - 1, z) && Half(BlockShapes.CollisionTop(World, x, y - 1, z)))
            feet = y - 1 + BlockShapes.CollisionTop(World, x, y - 1, z);
        else return null;
        // The slab's own cell is floor(feet); the body (1.8 tall) fills the cells above it.
        int y0 = (int)Math.Floor(feet), y1 = (int)Math.Floor(feet + 1.8);
        for (int yy = y0 + 1; yy <= y1; yy++)
            if (BlockShapes.HasCollision(World, x, yy, z) || Safety.IsHazard(World, World.GetBlock(x, yy, z))) return null;
        return World.IsOverVoid(x, y0, z) ? null : feet;
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
        var settled = new HashSet<int>();
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
                if (LostDropReason(drop.Position) is not null && DateTime.UtcNow - drop.SpawnedAt < DropSettleTime && settled.Add(id))
                {
                    // A fresh gen drop is still moving: it lies in the just-dug cell until the regen pushes it out (mc-dev
                    // Entity.g pushOutOfBlocks) and the water carries it to a corner. Give it a moment to settle, then look again.
                    var left = DropSettleTime - (DateTime.UtcNow - drop.SpawnedAt);
                    await WaitUntilAsync(() => World.Entities.Get(id) is not { } e || LostDropReason(e.Position) is null, left, ct).ConfigureAwait(false);
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
                // The spot touches the drop only off its centre (a corner of the gen's water): lean into range.
                if (World.Entities.Get(id) is { } d2 && !ItemEntityRules.InPickupRange(World.Player.Position, d2.Position))
                    await LeanToDropAsync(id, ct).ConfigureAwait(false);
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
        var b = Options.Profile.Data.BlockByName(blockName);
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
                            stop += $"; left {_lastMineNonSource} placed {name} alone (floors/walls/builds): mine only takes "
                                    + (MineClaw.Data.Versioning.WorldMode.Current.GeneratorStone ? "generator cells, " : "natural stone/dirt, ")
                                    + "ores, sand/gravel, tree logs and my own blocks"
                                    + (firstId is Blocks.Cobblestone or Blocks.Stone && MineClaw.Data.Versioning.WorldMode.Current.GeneratorStone ? " (for cobble use harvest_generator)" : "")
                                    + (firstId is Blocks.Crops ? " (for wheat use the harvest_wheat skill: ripe only, replants)" : "")
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
        // Exposed cells only count toward the cap: in natural ground the 512 nearest stone are all buried under my feet.
        var found = World.FindBlocks(World.Player.Position, radius, (id, _) => ids.Contains(id), IsExposed, 512);
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
        HarvestGeneratorAsync([new BlockPos(x, y, z)], count, stand, ct);

    /// <summary>
    /// Live 10-27 21:12-21:17: the harvester hammered one cell of a three-cell gen while a player mined it too, counted only the
    /// cobble it picked up (drops washed into the gen's water were never chased) and its yield guard then "proved" all three
    /// real cells wrong. Now: the first of <paramref name="cells"/> positions the bot; every cell of the same generator in reach
    /// from there is dug in rotation (least recently dug first, each only once it holds cobble/stone again, never at air or
    /// liquid); a dig counts as productive when its drop appears (picked up, washed away or taken by someone), so lost drops
    /// never blame the cell; a cell that another player dug (a block change we didn't cause) is shared use, never "not
    /// regenerating"; a cell that really stops is inspected (lava/water/obsidian next to it) and the cause named.
    /// </summary>
    public Task<ActionResult> HarvestGeneratorAsync(IReadOnlyList<BlockPos> cells, int count, BlockPos? stand, CancellationToken ct = default) =>
        RunActionAsync("generator", ct, c => HarvestCoreAsync(cells, count, stand, c));

    private static bool IsGenOutput(int id) => id is Blocks.Cobblestone or Blocks.Stone;

    private async Task<ActionResult> HarvestCoreAsync(IReadOnlyList<BlockPos> cells, int count, BlockPos? stand, CancellationToken c)
    {
        if (cells.Count == 0) return ActionResult.Fail("no generator cell given");
        if (count >= Options.Survival.LongMiningCount && await SurvivalGateAsync($"mine {count} blocks", c).ConfigureAwait(false) is { } refused)
            return refused;
        var pos = cells[0];
        if (NeverBreak?.Invoke(pos) == true) return ActionResult.Fail(ProtectedRefusal(pos, World.GetBlock(pos)));
        var all = cells.Distinct().Where(p => p == pos || NeverBreak?.Invoke(p) != true).ToList();
        // Never silently do something else: without a pickaxe the generator's output drops nothing, so say so.
        if (Drops.Primary(Blocks.Cobblestone, 0, BestToolId(Blocks.Cobblestone)) is null)
            return ActionResult.Fail("no pickaxe: cobblestone drops nothing without one (get a pickaxe first: withdraw or craft one)");
        int haveBefore = World.Inventory.CountItem(Blocks.Cobblestone);
        int mined = 0, failures = 0;
        string stop = "";
        bool badYield = false;
        int windowStartProduced = 0, windowStartMined = 0;
        var watch = new GenWatch(this, all);
        var perCell = new Dictionary<BlockPos, int>();
        var liquidSince = new Dictionary<BlockPos, long>();
        var stuckNotes = new List<string>();
        var lastDug = new Dictionary<BlockPos, long>();
        long seq = 0;
        // A taught stand another bot's (or player's) body is on: walking into it only pushes both of us around (live 10-28:
        // main "stuck at 7.5,64.5,6.3, can't reach the stand spot 6,64,4" with 1337 standing there). Dig from a free spot.
        string? standNote = null;
        if (stand is { } taken && OccupantOf(taken) is { } who)
        {
            standNote = $"stand {Fmt(taken)} is taken by {who}: dug from a free spot";
            stand = null;
        }
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
                if (badSpot && IsGenOutput(World.GetBlock(pos)))
                {
                    var g = await GotoReachSafelyAsync(pos, generator: true, c).ConfigureAwait(false);
                    if (!g.Ok) return ActionResult.Fail(UnreachableCellText(pos, g.Message));
                }
            }
            while (mined < count)
            {
                if (World.Inventory.RoomFor(new ItemStack(Blocks.Cobblestone, 1, 0)) == 0) { stop = "inventory full"; break; }
                // Tool broke mid-loop: digging on would yield nothing (the caller fetches a spare / crafts one).
                if (Drops.Primary(Blocks.Cobblestone, 0, BestToolId(Blocks.Cobblestone)) is null) { stop = "pickaxe broke (no pickaxe left)"; break; }
                await SurvivalWindowAsync(c).ConfigureAwait(false);
                // The cells this spot works: the first one always (it may walk for it), the others only while in reach.
                var work = all.Where(p => p == pos || CellInReach(p)).ToList();
                // A cell that stays liquid far past the regen transient is out of the rotation while others still work.
                if (work.Count > 1 && work.FirstOrDefault(p => LiquidFor(p, liquidSince) > GeneratorLiquidTimeout) is { } dead && dead != default)
                {
                    stuckNotes.Add(LiquidStuckText(dead, liquidSince));
                    all.Remove(dead);
                    if (dead == pos) pos = all[0];
                    continue;
                }
                // Wait for a cell that holds cobble/stone again (the regen block update), never swinging at air or liquid. Flowing
                // water or lava in a just-dug cell is the normal transient before they meet (live 21:5x "4,64,6 turned to water"):
                // keep waiting while it is liquid, up to GeneratorLiquidTimeout; only then is it stuck.
                if (!work.Any(p => IsGenOutput(World.GetBlock(p))))
                {
                    bool ready = await WaitUntilAsync(() => { NoteLiquid(work, liquidSince); return work.Any(p => IsGenOutput(World.GetBlock(p))); },
                        GeneratorRegenWait, c).ConfigureAwait(false);
                    while (!ready && work.Any(p => IsLiquidCell(p) && LiquidFor(p, liquidSince) < GeneratorLiquidTimeout))
                        ready = await WaitUntilAsync(() => { NoteLiquid(work, liquidSince); return work.Any(p => IsGenOutput(World.GetBlock(p))); },
                            TimeSpan.FromMilliseconds(500), c).ConfigureAwait(false);
                    if (!ready)
                    {
                        var stuck = work.Where(p => IsLiquidCell(p) && LiquidFor(p, liquidSince) >= GeneratorLiquidTimeout).ToList();
                        stop = stuck.Count > 0 && !watch.Shared
                            ? $"generator at {Fmt(pos)} stopped producing: " + string.Join("; ", stuck.Select(p => LiquidStuckText(p, liquidSince)))
                            : watch.StoppedText(pos, work, World.Entities);
                        break;
                    }
                }
                NoteLiquid(work, liquidSince);
                var pick = work.Where(p => IsGenOutput(World.GetBlock(p))).OrderBy(p => lastDug.GetValueOrDefault(p, -1)).First();
                // Stay put once positioned (live 10-27: a re-walk before every dig fought the water push, "goto: stuck,
                // re-planning" every 8 s): walk again only when the cell is out of reach, or released lava could reach me.
                bool move = Aiming.FindDigAim(World, World.Player.EyePosition, pick, Options.Reach) is null
                            || (!OnTrustedStand(pick) && pick == pos && LavaPathToMe(pick));
                if (move && stand is { } back)
                {
                    if (await GotoStandAsync(back, pick, c).ConfigureAwait(false) is { } no) { stop = no.Message; break; }
                    move = false;
                }
                watch.Digging(pick);
                var r = await DigCoreAsync(pick, c, allowPathing: move && pick == pos, ignoreProtection: true, generator: true).ConfigureAwait(false);
                watch.Dug(pick);
                lastDug[pick] = ++seq;
                if (!r.Ok)
                {
                    // Someone else took the block first (shared gen): not our failure, try the next ready cell.
                    if (watch.OthersDug(pick) > 0 && World.GetBlock(pick) == 0) continue;
                    if (++failures >= 3) { stop = r.Message; break; }
                    continue;
                }
                failures = 0;
                mined++;
                perCell[pick] = perCell.GetValueOrDefault(pick) + 1;
                // Drops drift with the gen's water: pick them up every few digs, not only at the yield check.
                if (mined % (YieldWindow / 2) == 0 && mined % YieldWindow != 0)
                {
                    await CollectDropsCoreAsync(4, 8, c).ConfigureAwait(false);
                    if (stand is { } st1 && !OnTrustedStand(pos) && await GotoStandAsync(st1, pos, c).ConfigureAwait(false) is { } off1)
                    {
                        stop = off1.Message;
                        break;
                    }
                }
                if (mined % YieldWindow == 0)
                {
                    await CollectDropsCoreAsync(4, 8, c).ConfigureAwait(false);
                    // Picking drops up may step off the taught spot: back onto it before the next dig.
                    if (stand is { } st2 && !OnTrustedStand(pos) && await GotoStandAsync(st2, pos, c).ConfigureAwait(false) is { } off)
                    {
                        stop = off.Message;
                        break;
                    }
                    // Yield guard: a real regen cell gives a drop per dig. Productive = the drops that appeared at the cells (picked
                    // up, washed into the water, or taken by someone else), never just what reached the inventory. Fewer than ~80%
                    // over the last window AND overall = the wrong block: stop, never dig on. Shared use never counts against it.
                    int produced = Math.Max(World.Inventory.CountItem(Blocks.Cobblestone) - haveBefore, watch.Drops);
                    double window = (produced - windowStartProduced) / (double)Math.Max(1, mined - windowStartMined);
                    double overall = produced / (double)mined;
                    windowStartProduced = produced;
                    windowStartMined = mined;
                    if (window < MinGeneratorYield && overall < MinGeneratorYield && !watch.Shared && watch.Sharer(World.Entities) is null)
                    {
                        badYield = true;
                        stop = $"{Fmt(pos)} isn't regenerating cobble for me (yield {overall:P0}: {produced} drops from {mined} digs), need the right cell";
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
            watch.Dispose();
        }
        if (mined > 0)
        {
            try { await CollectDropsCoreAsync(4, 16, c).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* keep the partial result */ }
        }
        int gained = World.Inventory.CountItem(Blocks.Cobblestone) - haveBefore;
        if (badYield) return ActionResult.Fail(stop + $" (dug {mined}, +{gained})", mined);
        string cellsText = perCell.Count > 1 ? " across " + string.Join(", ", perCell.Select(kv => $"{Fmt(kv.Key)} x{kv.Value}")) : "";
        int lost = Math.Max(0, Math.Min(mined, watch.Drops) - gained - watch.TakenByOthers);
        string extras = (standNote != null ? "; " + standNote : "")
                        + (lost > 0 ? $"; {lost} drop(s) washed into the gen's water/lava (not collected)" : "")
                        + (stuckNotes.Count > 0 ? "; " + string.Join("; ", stuckNotes) + " (left it out)" : "")
                        + (watch.SharedText(World.Entities) is { } shared ? "; " + shared : "");
        string msg = $"generator: dug {mined}{cellsText}, +{gained} cobblestone (have {World.Inventory.CountItem(Blocks.Cobblestone)})" + extras
                     + (stop.Length > 0 ? "; " + stop : "");
        return mined > 0 ? ActionResult.Success(msg, mined) : ActionResult.Fail(msg);
    }

    /// <summary>The other player (hive bot or human) whose body is in the foot cell <paramref name="cell"/> (or its head cell),
    /// or null: a spot taken by someone else's body is no place to walk to.</summary>
    internal string? OccupantOf(BlockPos cell)
    {
        foreach (var e in World.Entities.All())
        {
            if (e.Kind != EntityKind.Player || e.Id == World.Entities.SelfId || e.Id == World.Player.EntityId) continue;
            var box = AABB.ForPlayer(e.Position);
            if (box.MaxX > cell.X && box.MinX < cell.X + 1 && box.MaxZ > cell.Z && box.MinZ < cell.Z + 1
                && box.MaxY > cell.Y && box.MinY < cell.Y + 2) return e.Name ?? "someone";
        }
        return null;
    }

    /// <summary>How long harvest_generator waits for a dug cell's regen block update before looking closer.</summary>
    public TimeSpan GeneratorRegenWait { get; set; } = TimeSpan.FromSeconds(8);
    /// <summary>A dug cell still holding water/lava after this long is stuck (not the regen transient): reported, left out.</summary>
    public TimeSpan GeneratorLiquidTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private bool IsLiquidCell(BlockPos p) => BlockShapes.IsLiquid(World.GetBlock(p));

    private void NoteLiquid(IEnumerable<BlockPos> cells, Dictionary<BlockPos, long> since)
    {
        long now = Environment.TickCount64;
        lock (since)
            foreach (var p in cells)
                if (IsLiquidCell(p)) since.TryAdd(p, now);
                else since.Remove(p);
    }

    private static TimeSpan LiquidFor(BlockPos p, Dictionary<BlockPos, long> since)
    {
        lock (since) return since.TryGetValue(p, out var t) ? TimeSpan.FromMilliseconds(Environment.TickCount64 - t) : TimeSpan.Zero;
    }

    /// <summary>"4,64,6 has been water for 30 s: the lava side may be blocked".</summary>
    private string LiquidStuckText(BlockPos p, Dictionary<BlockPos, long> since)
    {
        int id = World.GetBlock(p);
        bool water = BlockShapes.IsWater(id);
        return $"{Fmt(p)} has been {(water ? "water" : "lava")} for {LiquidFor(p, since).TotalSeconds:0} s: the {(water ? "lava" : "water")} side may be blocked";
    }

    /// <summary>
    /// Per-harvest evidence from the packet thread: drops that appeared at the cells right after our digs, cells another
    /// player dug (a cell turning to air that we didn't just dig), drops another player picked up.
    /// </summary>
    private sealed class GenWatch : IDisposable
    {
        private readonly MinecraftClient _c;
        private readonly HashSet<BlockPos> _cells;
        private readonly object _lock = new();
        private readonly Dictionary<BlockPos, long> _ourDig = new();
        private readonly Dictionary<BlockPos, int> _others = new();
        private readonly HashSet<int> _drops = new();
        private (BlockPos Cell, long At)? _last;
        private int _taken;
        private readonly Action<Entity> _onSpawn;
        private readonly Action<BlockChange> _onChange;
        private readonly Action<int, int, ItemStack?> _onCollect;
        private readonly Action<int, int> _onSwing;
        private readonly Dictionary<string, int> _swings = new(StringComparer.OrdinalIgnoreCase);

        public GenWatch(MinecraftClient c, IEnumerable<BlockPos> cells)
        {
            _c = c;
            _cells = cells.ToHashSet();
            _onSpawn = OnSpawn;
            _onChange = OnChange;
            _onCollect = OnCollect;
            c.World.Entities.Spawned += _onSpawn;
            c.World.BlockChanged += _onChange;
            c.ItemCollected += _onCollect;
            _onSwing = OnSwing;
            c.EntityAnimated += _onSwing;
        }

        /// <summary>Another player swinging at the gen (0x12 arm swing within 5 blocks of a cell): they're mining it too.</summary>
        private void OnSwing(int entityId, int anim)
        {
            if (anim != 1 || entityId == _c.World.Player.EntityId) return;
            if (_c.World.Entities.Get(entityId) is not { Kind: EntityKind.Player, Name: { Length: > 0 } name } e) return;
            if (!_cells.Any(p => e.Position.DistanceTo(new Vec3d(p.X + 0.5, p.Y + 0.5, p.Z + 0.5)) <= 5)) return;
            lock (_lock) _swings[name] = _swings.GetValueOrDefault(name) + 1;
        }

        /// <summary>The player who swung at the gen most during this harvest (3+ swings), or null.</summary>
        private string? Swinger()
        {
            lock (_lock) return _swings.Where(kv => kv.Value >= 3).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        }

        /// <summary>Evidence that someone else mines these cells: blocks we didn't break, or their swings at the gen.</summary>
        public bool Shared => OthersDugTotal > 0 || Swinger() != null;

        private static long Now => Environment.TickCount64;

        /// <summary>Per cell: our current dig may still turn it to air once (the prediction or the server's echo).</summary>
        private readonly Dictionary<BlockPos, int> _ownBudget = new();

        public void Digging(BlockPos p) { lock (_lock) { _ownBudget[p] = 1; _ourDig[p] = long.MaxValue; _last = (p, Now); } }
        public void Dug(BlockPos p) { lock (_lock) { _ourDig[p] = Now; _last = (p, Now); } }

        /// <summary>Drops (cobble/stone) that spawned at a cell within 2.5 s of our dig there.</summary>
        public int Drops { get { lock (_lock) return _drops.Count; } }
        public int TakenByOthers { get { lock (_lock) return _taken; } }
        public int OthersDug(BlockPos p) { lock (_lock) return _others.GetValueOrDefault(p); }
        public int OthersDugTotal { get { lock (_lock) return _others.Values.Sum(); } }

        private void OnSpawn(Entity e)
        {
            if (e.Kind != EntityKind.Item || !IsGenOutput(e.Item.Id)) return;
            lock (_lock)
            {
                if (_last is not { } l || Now - l.At > 2500) return;
                var center = new Vec3d(l.Cell.X + 0.5, l.Cell.Y + 0.5, l.Cell.Z + 0.5);
                if (e.Position.DistanceTo(center) <= 2.0) _drops.Add(e.Id);
            }
        }

        private void OnChange(BlockChange ch)
        {
            if (!_cells.Contains(ch.Pos) || !IsGenOutput(ch.OldId) || ch.NewId != 0) return;
            lock (_lock)
            {
                // Our own dig turns the cell to air once (prediction or echo, while it runs or right after); any other cobble→air
                // transition there is someone else's dig.
                bool ours = _ourDig.TryGetValue(ch.Pos, out var t) && (t == long.MaxValue || Now - t <= 600);
                if (ours && _ownBudget.GetValueOrDefault(ch.Pos) > 0) { _ownBudget[ch.Pos]--; return; }
                _others[ch.Pos] = _others.GetValueOrDefault(ch.Pos) + 1;
            }
        }

        private void OnCollect(int collector, int item, ItemStack? _)
        {
            if (collector == _c.World.Player.EntityId) return;
            lock (_lock) if (_drops.Contains(item)) _taken++;
        }

        /// <summary>The nearest other player within 5 blocks of the cells (sharing the gen), or null.</summary>
        public string? Sharer(EntityTracker entities)
        {
            if (_cells.Count == 0) return null;
            var center = new Vec3d(_cells.Average(p => p.X) + 0.5, _cells.Average(p => p.Y) + 0.5, _cells.Average(p => p.Z) + 0.5);
            return entities.Nearby(center, 5, e => e.Kind == EntityKind.Player && e.Id != _c.World.Player.EntityId && e.Name is { Length: > 0 })
                .OrderBy(e => e.Position.DistanceSquaredTo(center)).FirstOrDefault()?.Name;
        }

        /// <summary>"PoofimaFox is mining this gen too (dug 3 cells' blocks before me)" or null.</summary>
        public string? SharedText(EntityTracker entities)
        {
            int n = OthersDugTotal;
            var who = Swinger() ?? Sharer(entities);
            if (!Shared) return null; // nearby but not mining it: nothing to say
            return $"{who ?? "someone"} is mining this gen too" + (n > 0 ? $" (took {n} block(s) before me)" : " (racing me for the same blocks)");
        }

        /// <summary>
        /// Why no cell came back within the wait, with the concrete cause: shared use (another player dug them), or the gen
        /// itself (no lava / no water next to the cell any more, obsidian where lava was). "stopped producing" stays in the
        /// text (callers stop on it).
        /// </summary>
        public string StoppedText(BlockPos primary, IReadOnlyList<BlockPos> work, EntityTracker entities)
        {
            var w = _c.World;
            string now = string.Join(", ", work.Select(p => $"{Fmt(p)} {_c.BlockName(w.GetBlock(p), 0)}"));
            if (Shared)
                return $"generator at {Fmt(primary)} shared: {Swinger() ?? Sharer(entities) ?? "someone"} is mining the same cells; waiting gave nothing for 8 s (now {now})";
            var causes = new List<string>();
            foreach (var p in work)
            {
                bool lava = false, water = false;
                BlockPos? obsidian = null;
                foreach (var f in BlockFaceExtensions.All)
                {
                    var n = p.Offset(f);
                    int id = w.GetBlock(n);
                    lava |= BlockShapes.IsLava(id);
                    water |= BlockShapes.IsWater(id);
                    if (id == Blocks.Obsidian) obsidian ??= n;
                }
                if (!lava) causes.Add($"no lava next to {Fmt(p)} any more" + (obsidian is { } o ? $" (obsidian at {Fmt(o)}: the lava turned to obsidian)" : ""));
                else if (!water) causes.Add($"no water next to {Fmt(p)} any more");
            }
            string why = causes.Count > 0 ? string.Join("; ", causes) : "lava and water are still next to it";
            return $"generator at {Fmt(primary)} stopped producing (now {now}): {why}";
        }

        public void Dispose()
        {
            _c.World.Entities.Spawned -= _onSpawn;
            _c.World.BlockChanged -= _onChange;
            _c.ItemCollected -= _onCollect;
            _c.EntityAnimated -= _onSwing;
        }
    }

    /// <summary>
    /// "6,64,8 is enclosed: no safe spot reaches it (nearest standable spot 7,63,8 is 4.9 away, reach 4.5)": the exact reason a
    /// generator cell can't be worked from anywhere, so the brain can ask a player once for a stand spot.
    /// </summary>
    private string UnreachableCellText(BlockPos cell, string gotoMessage)
    {
        BlockPos? best = null;
        double bestD = double.MaxValue;
        for (int x = cell.X - 8; x <= cell.X + 8; x++)
        for (int y = cell.Y - 4; y <= cell.Y + 4; y++)
        for (int z = cell.Z - 8; z <= cell.Z + 8; z++)
        {
            if (StandFeet(x, y, z) is not { } feet) continue;
            var eye = new Vec3d(x + 0.5, feet + PlayerPhysics.EyeHeight, z + 0.5);
            var cx = Math.Clamp(eye.X, cell.X, cell.X + 1.0);
            var cy = Math.Clamp(eye.Y, cell.Y, cell.Y + 1.0);
            var cz = Math.Clamp(eye.Z, cell.Z, cell.Z + 1.0);
            double d = eye.DistanceTo(new Vec3d(cx, cy, cz));
            if (d < bestD) { bestD = d; best = new BlockPos(x, y, z); }
        }
        var near = best is { } b
            ? $"nearest standable spot {Fmt(b)} is {bestD:0.#} away (reach {Options.Reach:0.#})" + (bestD <= Options.Reach ? " but no safe path/aim from it" : "")
            : "no standable spot within 8";
        return $"can't get to a safe spot at the generator: {Fmt(cell)} is enclosed ({EnclosureText(cell)}; {near}; {gotoMessage}); ask a player once where to stand";
    }

    /// <summary>
    /// Exactly what boxes the cell in, side by side: "N water, S lava, E cobblestone wall, W void, above: planks roof" (the
    /// cell's 4 neighbours at its level and the block over it). A side a player could stand beside is "open".
    /// </summary>
    public string EnclosureText(BlockPos cell)
    {
        string Name(int id) => id == 0 ? "air" : Blocks.Exists(id) ? Blocks.Get(id).Name : "block " + id;
        string Side(int dx, int dz)
        {
            int x = cell.X + dx, z = cell.Z + dz;
            int id = World.GetBlock(x, cell.Y, z);
            if (Blocks.IsLava(id)) return "lava";
            if (Blocks.IsWater(id)) return "water";
            if (id != 0 && BlockShapes.HasCollision(World, x, cell.Y, z)) return Name(id) + " wall";
            for (int y = cell.Y; y >= cell.Y - 3; y--)
                if (StandFeet(x, y, z) is not null) return "open";
            return Safety.IsDeadlyColumn(World, x, cell.Y, z, 3) ? "void" : "no floor";
        }
        var parts = new[] { ("N", 0, -1), ("S", 0, 1), ("E", 1, 0), ("W", -1, 0) }.Select(t => $"{t.Item1} {Side(t.Item2, t.Item3)}").ToList();
        int up = World.GetBlock(cell.X, cell.Y + 1, cell.Z);
        if (up != 0 && !Blocks.IsLava(up) && !Blocks.IsWater(up)) parts.Add($"above: {Name(up)}");
        return string.Join(", ", parts);
    }

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
        if (!CellInReach(cell))
            return ActionResult.Fail($"from the taught stand spot {Fmt(stand)} the cell {Fmt(cell)} is out of reach: wrong stand spot?");
        return null;
    }

    /// <summary>A gen cell is workable from here: aimable now, or (while it is air/liquid between regens, so nothing to aim at)
    /// its aim points are within reach.</summary>
    private bool CellInReach(BlockPos cell)
    {
        var eye = World.Player.EyePosition;
        if (Aiming.FindDigAim(World, eye, cell, Options.Reach) is not null) return true;
        if (IsGenOutput(World.GetBlock(cell))) return false;
        double dx = eye.X - Math.Clamp(eye.X, cell.X + 0.1, cell.X + 0.9), dy = eye.Y - Math.Clamp(eye.Y, cell.Y + 0.1, cell.Y + 0.9),
            dz = eye.Z - Math.Clamp(eye.Z, cell.Z + 0.1, cell.Z + 0.9);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) <= Options.Reach;
    }

    /// <summary>Digs per yield check of <see cref="HarvestGeneratorAsync"/>.</summary>
    public const int YieldWindow = 8;
    /// <summary>Below this share of digs that gave a cobblestone (window and overall), the cell is the wrong one.</summary>
    public const double MinGeneratorYield = 0.8;
}
