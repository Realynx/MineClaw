using System.Diagnostics;
using MineClaw.Client.Trees;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>Knobs for <see cref="MinecraftClient.ChopTreeAsync"/>.</summary>
public sealed record TreeChopOptions
{
    public double SearchRadius { get; init; } = 24;
    /// <summary>Leaves broken by hand for the 1-in-20 sapling chance (b1.8.1 BlockLeaves; no apples from leaves in b1.8.1).</summary>
    public int MaxLeafBreaks { get; init; } = 64;
    public TimeSpan LeafPhase { get; init; } = TimeSpan.FromSeconds(75);
    /// <summary>Wait for the rest of the crown to decay when no sapling came yet (bounded).</summary>
    public TimeSpan DecayWait { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Stop waiting for decay once this many saplings were collected.</summary>
    public int WantSaplings { get; init; } = 2;
    /// <summary>Blocks the bot may pillar up next to the trunk for logs above reach (cobblestone/netherrack only, never dirt).</summary>
    public int MaxPillar { get; init; } = 2;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    public static TreeChopOptions Default { get; } = new();
}

/// <summary>What one <see cref="MinecraftClient.ChopTreeAsync"/> did (raised as <see cref="MinecraftClient.TreeChopped"/>).</summary>
public sealed record TreeChopReport(string Wood, BlockPos Base, BlockPos Soil, int LogsTotal, int LogsFelled, int Logs, int Saplings, int Apples,
    int LostDrops, BlockPos? Replanted, string? ReplantProblem, bool Ok, string Message);

public sealed partial class MinecraftClient
{
    /// <summary>Raised (on the event dispatcher) after every chop_tree, success or not.</summary>
    public event Action<TreeChopReport>? TreeChopped;

    /// <summary>Cheap blocks a short tree pillar may use (never dirt: unrenewable on Skyblock).</summary>
    private static readonly int[] PillarBlocks = [Blocks.Cobblestone, Blocks.Netherrack];

    private sealed class ChopState(TreeInfo tree)
    {
        public TreeInfo Tree { get; } = tree;
        public int LogsBefore, SaplingsBefore, ApplesBefore, Felled, LeavesBroken;
        public int TrunkEdge;
        public bool PlacedSapling;
        public readonly List<BlockPos> Pillar = new();
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
            var tree = ResolveTree(at, wood, o, out var why);
            if (tree is null) return ActionResult.Fail(why!);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(c);
            limit.CancelAfter(o.Timeout);
            var st = new ChopState(tree)
            {
                LogsBefore = World.Inventory.CountItem(Blocks.Log, tree.Wood),
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
            return Finish(st, stop, planted, problem);
        });

    // ------------------------------------------------------------------ target

    private TreeInfo? ResolveTree(BlockPos? at, int? wood, TreeChopOptions o, out string? why)
    {
        why = null;
        TreeInfo? tree;
        if (at is { } p)
        {
            var log = TreeScan.LogNear(World, p);
            tree = log is { } l ? TreeScan.Analyze(World, l) : null;
            if (tree is null) { why = $"no tree at {Fmt(p)}"; return null; }
        }
        else
        {
            var trees = TreeScan.FindTrees(World, World.Player.Position, o.SearchRadius, wood)
                .Where(t => t.Logs.Any(l => !IsBlockProtected(l, Blocks.Log)) && (t.OnSoil || t.Leaves.Count >= 3)).ToList();
            tree = trees.OrderBy(t => t.OnSoil ? 0 : 1).ThenBy(t => t.Base.Center.DistanceSquaredTo(World.Player.Position)).FirstOrDefault();
            if (tree is null)
            {
                why = $"no {(wood is { } w ? TreeScan.WoodName(w) + " " : "")}tree within {o.SearchRadius:0}";
                return null;
            }
        }
        if (!tree.OnSoil && tree.Leaves.Count < 3)
        {
            why = $"logs at {Fmt(tree.Base)} sit on {BlockName(tree.SoilId, 0)} with no leaves: a build, not a tree";
            return null;
        }
        if (tree.Logs.All(l => IsBlockProtected(l, Blocks.Log)))
        {
            why = $"the {tree.WoodName} tree at {Fmt(tree.Base)} is protected";
            return null;
        }
        return tree;
    }

    // ------------------------------------------------------------------ felling

    private List<BlockPos> RemainingLogs(ChopState st) =>
        st.Tree.Logs.Where(l => World.GetBlock(l) == Blocks.Log && !st.Failed.Contains(l) && !IsBlockProtected(l, Blocks.Log)).ToList();

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
                   + (st.Pillar.Count == 0 && PillarBlock() is null ? "; no cobblestone to pillar" : "");
        }
    }

    /// <summary>Standing cell rules: solid footing that isn't part of the tree, no lava, and away from the void
    /// (strict: 2+ from any edge; relaxed: never closer to the void than the trunk itself).</summary>
    private bool TreeStandOk(ChopState st, int x, int y, int z, bool strict)
    {
        if (!Safety.IsStandable(World, x, y, z) || Safety.LavaNear(World, x, y, z)) return false;
        var under = new BlockPos(x, y - 1, z);
        if (st.Tree.Logs.Contains(under) || st.Tree.Leaves.Contains(under) || World.GetBlock(under) is Blocks.Leaves or Blocks.Log) return false;
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
            .Where(p => World.GetBlock(p) == Blocks.Leaves && !st.Failed.Contains(p) && !IsBlockProtected(p, Blocks.Leaves)
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
        var target = remaining.Where(l => l.Center.DistanceTo(World.Player.EyePosition) <= Options.Reach + JumpEyeBoost + 0.9)
            .OrderBy(l => l.Y).Cast<BlockPos?>().FirstOrDefault();
        if (target is not { } t) return false;
        int id = World.GetBlock(t);
        int held = await SelectToolForAsync(id, ct).ConfigureAwait(false);
        int ticks = Tools.DigTicks(id, held, onGround: true);
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
        if (World.GetBlock(feet) == block) st.Pillar.Add(feet);
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

    /// <summary>Steps off the pillar and digs it back (it's our own cobblestone).</summary>
    private async Task CleanupPillarAsync(ChopState st, CancellationToken ct)
    {
        if (st.Pillar.Count == 0) return;
        var bottom = st.Pillar.OrderBy(p => p.Y).First();
        Goal ground = new GoalFiltered(new GoalNear(bottom, 3),
            (x, y, z) => y == bottom.Y && TreeStandOk(st, x, y, z, strict: true) && !st.Pillar.Contains(new BlockPos(x, y - 1, z)), "ground by the pillar");
        await GotoCoreAsync(ground, GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(20), MaxReplans = 2, AllowPartial = false }, ct).ConfigureAwait(false);
        foreach (var p in st.Pillar.OrderByDescending(p => p.Y).ToList())
        {
            var r = await DigCoreAsync(p, ct).ConfigureAwait(false);
            if (r.Ok) st.Pillar.Remove(p);
            else Log($"chop: couldn't take the pillar block back: {r.Message}");
        }
    }

    // ------------------------------------------------------------------ saplings

    private int SaplingsGained(ChopState st) => World.Inventory.CountItem(Blocks.Sapling) - st.SaplingsBefore;

    private List<BlockPos> LeavesLeft(ChopState st) =>
        st.Tree.Leaves.Where(p => World.GetBlock(p) == Blocks.Leaves).ToList();

    private Vec3d TreeCenter(ChopState st) => new(st.Tree.Base.X + 0.5, st.Tree.Base.Y + 0.5, st.Tree.Base.Z + 0.5);

    private async Task CollectAroundTreeAsync(ChopState st, CancellationToken ct, int max = 32)
    {
        await CollectDropsCoreAsync(7, max, ct, TreeCenter(st), st.LostIds).ConfigureAwait(false);
    }

    /// <summary>Breaks the tree's reachable leaves (none over the void), then waits a bounded time for decay; collects drops.</summary>
    private async Task GatherSaplingsAsync(ChopState st, TreeChopOptions o, CancellationToken ct)
    {
        await CollectAroundTreeAsync(st, ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        var skip = new HashSet<BlockPos>();
        int sinceCollect = 0, misses = 0;
        while (sw.Elapsed < o.LeafPhase && st.LeavesBroken < o.MaxLeafBreaks)
        {
            var cands = LeavesLeft(st).Where(p => !skip.Contains(p) && !DropsIntoVoid(p) && !IsBlockProtected(p, Blocks.Leaves)).ToList();
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

        // The rest decays by itself (random ticks): wait a little for the sapling chance, picking up safe drops.
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < o.DecayWait && SaplingsGained(st) < o.WantSaplings && LeavesLeft(st).Count > 0)
        {
            await WaitUntilAsync(() => false, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            await CollectAroundTreeAsync(st, ct, 16).ConfigureAwait(false);
        }
        if (wait.Elapsed > TimeSpan.Zero) await CollectAroundTreeAsync(st, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ replanting

    /// <summary>b1.8.1 BlockSapling.canBlockStay: dirt/grass below and (full light ≥ 8 or sees the sky). Plus room for
    /// the tree to grow (4 cells of air/leaves above), otherwise the sapling never becomes a tree.</summary>
    private string? ReplantProblem(TreeInfo tree)
    {
        var cell = tree.Base;
        int soil = World.GetBlock(tree.Soil);
        if (soil is not (Blocks.Dirt or Blocks.Grass)) return $"the dirt under the tree at {Fmt(tree.Soil)} is gone ({BlockName(soil, 0)})";
        int here = World.GetBlock(cell);
        if (!Aiming.IsReplaceable(World, here)) return $"{BlockName(here, World.GetMeta(cell))} on the dirt at {Fmt(cell)}";
        for (int dy = 1; dy <= 4; dy++)
        {
            int id = World.GetBlock(cell.Up(dy));
            if (id != 0 && id != Blocks.Leaves && BlockShapes.HasCollision(World, cell.X, cell.Y + dy, cell.Z))
                return $"no room to grow ({BlockName(id, 0)} at {Fmt(cell.Up(dy))})";
        }
        if (!SeesSky(cell) && Math.Max(World.GetSkyLight(cell.X, cell.Y, cell.Z), World.GetBlockLight(cell.X, cell.Y, cell.Z)) < 8)
            return $"too dark at {Fmt(cell)} (needs light 8+)";
        return null;
    }

    /// <summary>Vanilla canBlockSeeTheSky: nothing with light opacity above (leaves and water count, glass/torches don't).</summary>
    private bool SeesSky(BlockPos p)
    {
        for (int y = p.Y + 1; y < WorldState.Height; y++)
        {
            int id = World.GetBlock(p.X, y, p.Z);
            if (id == 0 || id == Blocks.Glass) continue;
            var b = Blocks.Get(id);
            if (id == Blocks.Leaves || b.Liquid || b.Solid) return false;
        }
        return true;
    }

    private async Task<(BlockPos? Planted, string? Problem)> ReplantAsync(ChopState st, CancellationToken ct)
    {
        var tree = st.Tree;
        if (World.GetBlock(tree.Base) == Blocks.Sapling) return (tree.Base, null); // someone (or decay timing) beat us to it
        if (RemainingLogs(st).Any(l => l == tree.Base)) return (null, "the trunk is still standing");
        if (ReplantProblem(tree) is { } p) return (null, p);
        int? damage = World.Inventory.CountItem(Blocks.Sapling, tree.Wood) > 0 ? tree.Wood
            : World.Inventory.FindSlots(Blocks.Sapling) is { Count: > 0 } any ? World.Inventory.GetPlayerSlot(any[0]).Damage : null;
        if (damage is null) return (null, "no sapling");
        var r = await PlaceCoreAsync(tree.Base, Blocks.Sapling, ct, allowPathing: true, damage: damage).ConfigureAwait(false);
        if (!r.Ok) return (null, "placing the sapling failed: " + r.Message);
        st.PlacedSapling = true;
        return World.GetBlock(tree.Base) == Blocks.Sapling ? (tree.Base, null) : (null, "the sapling popped off");
    }

    // ------------------------------------------------------------------ result

    private ActionResult Finish(ChopState st, string? stop, BlockPos? planted, string? problem)
    {
        var t = st.Tree;
        int logs = World.Inventory.CountItem(Blocks.Log, t.Wood) - st.LogsBefore;
        int saplings = Math.Max(0, World.Inventory.CountItem(Blocks.Sapling) - st.SaplingsBefore + (st.PlacedSapling ? 1 : 0));
        int apples = World.Inventory.CountItem(Items.Apple) - st.ApplesBefore;
        int left = RemainingLogs(st).Count + st.Failed.Count(f => World.GetBlock(f) == Blocks.Log);
        string counts = $"{logs} log{(logs == 1 ? "" : "s")}, {saplings} sapling{(saplings == 1 ? "" : "s")}"
                        + (apples > 0 ? $", {apples} apple{(apples == 1 ? "" : "s")}" : "");
        string lost = st.Lost > 0 ? $"; {st.Lost} drop(s) lost near the void" : "";
        bool allDown = left == 0;
        bool ok = allDown && stop is null && planted is not null;
        string msg;
        if (ok) msg = $"chopped {t.WoodName} tree ({counts}), replanted at {Fmt(planted!.Value)}{lost}";
        else if (!allDown || stop is not null)
            msg = $"{stop ?? $"{left} log(s) left"}; chopped {st.Felled}/{t.Logs.Count} {t.WoodName} logs ({counts})"
                  + (planted is { } pl ? $", replanted at {Fmt(pl)}" : ", not replanted") + lost;
        else if (problem == "no sapling")
            msg = $"no sapling dropped, NOT replanted (chopped {t.WoodName} tree: {counts}; dirt at {Fmt(t.Soil)} is free){lost}";
        else msg = $"replant blocked: {problem} (chopped {t.WoodName} tree: {counts}, sapling kept){lost}";
        var report = new TreeChopReport(t.WoodName, t.Base, t.Soil, t.Logs.Count, st.Felled, logs, saplings, apples, st.Lost, planted, problem, ok, msg);
        Log("chop: " + msg);
        Raise(() => TreeChopped?.Invoke(report));
        return ok ? ActionResult.Success(msg, logs) : ActionResult.Fail(msg, logs);
    }
}
