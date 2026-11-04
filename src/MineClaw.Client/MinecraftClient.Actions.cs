using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    private readonly SemaphoreSlim _actionLock = new(1, 1);
    private CancellationTokenSource? _actionCts;
    private volatile string? _abortReason;
    private readonly SemaphoreSlim _chatLock = new(1, 1);
    private DateTime _lastChat = DateTime.MinValue;

    /// <summary>Name of the running action, or null when idle.</summary>
    public string? CurrentAction { get; private set; }

    public bool IsBusy => CurrentAction is not null;

    /// <summary>Runs an action under the action lock with a linked token that <see cref="StopAsync"/> and danger
    /// signals can cancel. Exceptions become failure results.</summary>
    private async Task<ActionResult> RunActionAsync(string name, CancellationToken ct, Func<CancellationToken, Task<ActionResult>> body)
    {
        // A reflex fight gives up on its own within a few seconds (CombatOptions.ZeroHitGiveUpTicks): wait it out instead of
        // failing the job as "busy: fighting ..." (live 10-28: gen jobs, gotos and deposits all failed that way).
        if (CombatBlocks(name) is not null && _combatInstance is { ReflexEngaged: true } reflex)
            await WaitForReflexFightAsync(reflex, ct).ConfigureAwait(false);
        if (CombatBlocks(name) is { } busy) return ActionResult.Fail(busy);
        if (VoidHoldBlocks(name) is { } stuck) return ActionResult.Fail(stuck);
        // Surfacing from under water: jobs resume only with the air full again (the reflex owns the body until then).
        if (_surfacing is not null && name is not ("eat" or "shelter" or "look"))
            await WaitForAirAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        try
        {
            await _actionLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ActionResult.Fail("cancelled while waiting for " + CurrentAction);
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, SessionToken);
        _actionCts = cts;
        _abortReason = null;
        Volatile.Write(ref _actionEditsOk, 0);
        CurrentAction = name;
        // The static tables (Recipes, Smelting, Drops, Items…) answer from this body's version for the whole action, even
        // where the process default differs (tests; P5 facades over GameData.Current). Scoped to this async flow only.
        using var data = ReferenceEquals(MineClaw.Data.Versioning.GameData.Current, Options.Profile.Data) ? null
            : MineClaw.Data.Versioning.GameData.Override(Options.Profile.Data);
        try
        {
            if (!IsPlaying) return ActionResult.Fail(World.Player.IsDead ? "dead" : "not in game");
            return await body(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ActionResult.Fail(_abortReason ?? (ct.IsCancellationRequested ? "cancelled" : "stopped"));
        }
        catch (ActionAbortedException ex)
        {
            return ActionResult.Fail(_abortReason ?? ex.Message);
        }
        catch (Exception ex)
        {
            Log($"{name} failed: {ex}");
            return ActionResult.Fail($"{name} error: {ex.Message}");
        }
        finally
        {
            var d = _driver;
            if (d is not null && Interlocked.CompareExchange(ref _driver, null, d) == d) d.Tcs.TrySetCanceled();
            _idleSneak = false;
            _actionRisk = null;
            CurrentAction = null;
            _actionCts = null;
            _actionLock.Release();
        }
    }

    /// <summary>Cancels the running action (if any) and waits for it to wind down.</summary>
    public async Task<ActionResult> StopAsync(CancellationToken ct = default)
    {
        var name = CurrentAction;
        if (name is null) return ActionResult.Success("idle");
        AbortCurrentAction("stopped");
        try
        {
            await _actionLock.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            _actionLock.Release();
        }
        catch (OperationCanceledException) { /* caller gave up */ }
        return ActionResult.Success("stopped " + name);
    }

    private void AbortCurrentAction(string reason)
    {
        var cts = _actionCts;
        if (cts is null) return;
        _abortReason ??= reason;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    // ------------------------------------------------------------------ chat (independent of the action lock)

    /// <summary>Sends chat: sanitized to the server's allowed characters, split into ≤100-char lines, and spaced by
    /// <see cref="ClientOptions.ChatInterval"/> to avoid spam kicks. Returns the number of lines sent.</summary>
    public async Task<ActionResult> SendChatAsync(string text, CancellationToken ct = default)
    {
        var lines = ChatText.Prepare(text, Options.Profile.Protocol.MaxChatLength); // b1.8.1 100, 1.12.2 256
        if (lines.Count == 0) return ActionResult.Fail("nothing to say after sanitizing");
        if (lines.Count > 6) lines = lines.Take(6).ToList();
        await _chatLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int sent = 0;
            foreach (var line in lines)
            {
                var gap = line.StartsWith('/') && Options.CommandInterval is { } ci ? ci : Options.ChatInterval;
                var wait = _lastChat + gap - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
                var s = _session;
                if (s is null || s.Ended || !s.PositionKnown) return ActionResult.Fail("not connected", sent);
                Send(s, new ChatPacket(line));
                _lastChat = DateTime.UtcNow;
                sent++;
            }
            return ActionResult.Success($"said {sent} line(s)", sent);
        }
        finally
        {
            _chatLock.Release();
        }
    }

    // ------------------------------------------------------------------ looking

    /// <summary>Turns to look at a point (eye-to-point).</summary>
    public Task<ActionResult> LookAtAsync(Vec3d point, CancellationToken ct = default) =>
        RunActionAsync("look", ct, async c =>
        {
            var (yaw, pitch) = PlayerPhysics.YawPitchTo(World.Player.EyePosition, point);
            await LookCoreAsync(yaw, pitch, c).ConfigureAwait(false);
            return ActionResult.Success($"looking at {Fmt(point)}");
        });

    // ------------------------------------------------------------------ movement

    /// <summary>Walks to a block position (feet cell). <paramref name="tolerance"/> 0 = exactly that cell.</summary>
    public Task<ActionResult> GotoAsync(int x, int y, int z, double tolerance = 0, GotoOptions? options = null, CancellationToken ct = default) =>
        GotoAsync(tolerance <= 0 ? new GoalBlock(x, y, z) : new GoalNear(x, y, z, tolerance), options, ct);

    /// <summary>Pathfinds and walks to <paramref name="goal"/>, re-planning on "stuck"/"off path" and performing
    /// the dig/place steps the path needs (only if allowed by <paramref name="options"/>).</summary>
    public Task<ActionResult> GotoAsync(Goal goal, GotoOptions? options = null, CancellationToken ct = default) =>
        RunActionAsync("goto", ct, async c =>
        {
            var o = options ?? GotoOptions.Default;
            if (o.AllowPlace && await SurvivalGateAsync("bridge", c).ConfigureAwait(false) is { } refused) return refused;
            return await GotoCoreAsync(goal, o, c).ConfigureAwait(false);
        });

    /// <param name="lockedDoors">Openables (state blocks) that ignored our clicks this goto: never planned through again.</param>
    private PathOptions BuildPathOptions(GotoOptions o, IReadOnlySet<BlockPos>? lockedDoors = null)
    {
        int scaffold = ScaffoldItem() ?? -1;
        var slots = World.Inventory.PlayerSlots();
        var othersStandOn = Safety.OtherPlayersSupport(World); // hard safety: never dig out from under another player
        return PathOptions.Default with
        {
            AllowBreak = o.AllowBreak,
            // A tunnelling goto never digs a dug design's floor (a stair's tread) out from under its air.
            CanBreak = (id, meta, pos) => !IsBlockProtected(pos, id) && !NextToLava(pos) && !EditBlocked(pos) && !othersStandOn.Contains(pos)
                                          && !(World.KeepAir.Contains(pos.Up(), World.Dimension) && !World.KeepAir.Contains(pos, World.Dimension))
                                          // A narrowed dig (a chop's way up): only those blocks, never one somebody placed.
                                          && (o.BreakOnly is not { } only || only(id, pos) && !World.Placed.IsPlaced(pos, World.Dimension, id)),
            // Never a bridge or pillar block in a cell a dug design keeps air (live 10-30 02:20: goto bridging put cobble on stair step 154,15,276).
            CanPlace = pos => !EditBlocked(pos) && !World.KeepAir.Contains(pos, World.Dimension),
            MaxDrop = o.MaxDrop ?? PathOptions.Default.MaxDrop,
            DigCost = (id, meta) =>
            {
                int idx = MineClaw.Data.Tools.BestToolIndex(id, slots);
                int ticks = Options.Profile.Data.DigTicks(id, idx < 0 ? -1 : slots[idx].Id);
                return ticks == int.MaxValue ? double.PositiveInfinity : ticks + 5;
            },
            AllowPlace = o.AllowPlace && scaffold > 0,
            ScaffoldBudget = o.AllowPlace && scaffold > 0 ? Math.Min(o.ScaffoldBudget, ScaffoldCount()) : 0,
            Timeout = o.PlanTimeout,
            AllowPartial = o.AllowPartial,
            AllowOpenDoors = o.AllowOpenDoors,
            // Not IsBlockProtected: protection means "never break/edit" and the indexer protects every cell of a player build,
            // doors included, which sealed the bot inside the house. Opening is reversible (RestoreDoors puts it back); only
            // openables that ignored our clicks this goto are vetoed.
            CanOpen = (pos, _) => !(lockedDoors?.Contains(pos) ?? false),
            // Learned mechanisms (buttons/levers/plates → doors, piston doors); unknown levers in builds are left alone.
            Mechanisms = World.Mechanisms.View(World.Dimension),
            IsBuild = (pos, id) => IsBlockProtected(pos, id),
            AllowParkour = o.Parkour != ParkourMode.Off,
            CanSprint = CanSprintNow(),
            AllowVoidParkour = VoidParkourAuthorized(o),
            HasClutchBlock = HotbarHasClutchBlock(),
            // A walk from the surface keeps to the surface when it can (caves cost more): the light the server sent says where the sky is.
            SkyLight = (x, y, z) => World.Light.Get(true, x, y, z),
            Covered = o.PreferSurface ? CoveredByGround : null,
            ParkourCache = _parkourCache,
            MaxHeadUnderSteps = o.MaxHeadUnderSteps,
            HeadUnderStepCost = o.HeadUnderStepCost,
            GrowZone = GrowZone,
        };
    }

    /// <summary>The growing zones this body works in (Story Mode sets it per pawn; foot cell x,y,z → inside one), or null. Every goto
    /// crouch-walks past their plants (<see cref="PathOptions.GrowZone"/>). Read on the planner's thread: the setter hands over an
    /// immutable predicate.</summary>
    public Func<int, int, int, bool>? GrowZone { get; set; }

    private readonly Dictionary<(BlockPos From, BlockPos To), DateTime> _recentBlockedJumps = new();

    /// <summary>How long a gap jump that failed (stuck, missed, refused) stays out of new plans.</summary>
    public static readonly TimeSpan BlockedJumpMemory = TimeSpan.FromMinutes(10);

    private HashSet<(BlockPos From, BlockPos To)> RecentBlockedJumps()
    {
        lock (_recentBlockedJumps)
        {
            var now = DateTime.UtcNow;
            foreach (var k in _recentBlockedJumps.Where(kv => now - kv.Value > BlockedJumpMemory).Select(kv => kv.Key).ToList()) _recentBlockedJumps.Remove(k);
            return _recentBlockedJumps.Keys.ToHashSet();
        }
    }

    private async Task<ActionResult> GotoCoreAsync(Goal goal, GotoOptions o, CancellationToken ct)
    {
        // A goal with nothing under it can only be reached by bridging (AllowPlace builds the floor first).
        if (!o.AllowPlace && GoalOverVoid(goal) is { } overVoid) return ActionResult.Fail(overVoid);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(o.Timeout);
        // Jumps that failed in the last few gotos stay avoided (live 10-28, 67: three gotos in a row climbed out of the basement
        // and took the same gap jump that dropped it back down).
        var run = new GotoRun(goal, o, timeout.Token, RecentBlockedJumps()) { ScaffoldBefore = o.AllowPlace ? ScaffoldSnapshot() : null };
        try
        {
            while (true)
                if (await GotoLegAsync(run).ConfigureAwait(false) is { } done) return done;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return ActionResult.Fail($"timed out going to {goal}; at {Fmt(World.Player.Position)}");
        }
    }

    /// <summary>One goto's state across its re-plans.</summary>
    private sealed class GotoRun(Goal goal, GotoOptions o, CancellationToken token, HashSet<(BlockPos From, BlockPos To)> blockedJumps)
    {
        public readonly Goal Goal = goal;
        public readonly GotoOptions O = o;
        public readonly CancellationToken Token = token;
        public readonly HashSet<(BlockPos From, BlockPos To)> BlockedJumps = blockedJumps;
        public readonly HashSet<BlockPos> LockedDoors = new();
        public readonly List<ParkourPlan> Jumps = new();
        /// <summary>Moves the body got stuck on (live 10-28: every re-plan chose the same step off the gen slab into a leaf block at
        /// head height): never planned again this goto; the second stuck at the same spot also tries an escape move.</summary>
        public readonly HashSet<(BlockPos From, BlockPos To)> AvoidMoves = new();
        public readonly StuckNote Stuck = new();
        public int Replans, AirWaits, StuckHere;
        public string? LastFailure;
        public Vec3d? StuckSpot;
        public Dictionary<int, int>? ScaffoldBefore;
        public string Crafted = "";
        public bool CraftedPlanks;
    }

    /// <summary>What the goto built on the way (planks it crafted, scaffold blocks it used).</summary>
    private string GotoBuilt(GotoRun run) => run.ScaffoldBefore is null ? "" : run.Crafted + ScaffoldUsed(run.ScaffoldBefore, ScaffoldSnapshot());

    private BlockPos GotoStartCell(Vec3d start) => Pathfinder.StartCellFor(World, start) ?? start.ToBlockPos();

    /// <summary>One plan and walk; null = re-plan, else the goto's result.</summary>
    private async Task<ActionResult?> GotoLegAsync(GotoRun run)
    {
        var start = World.Player.Position;
        if (run.Goal.IsReached(GotoStartCell(start)))
            return ActionResult.Success($"arrived at {Fmt(start)}{JumpSummary(run.Jumps)}{GotoBuilt(run)}");
        var popts = GotoPathOptions(run);
        // The same search failed a moment ago from this cell (nothing changed near it since): not run again (NoRouteCache).
        var startCell = GotoStartCell(start);
        var routeKey = NoRouteCache.Key(startCell, run.Goal, run.O);
        if (KnownNoRoute(run, routeKey) is { } known) return ActionResult.Fail(known);
        var path = await PathfindGate.RunAsync(() => Pathfinder.FindPath(World, start, run.Goal, popts, run.Token), run.Token).ConfigureAwait(false);
        if (IsNoPath(path)) return await GotoNoPathAsync(run, path, start, startCell, routeKey).ConfigureAwait(false);
        path = await CheckRouteAsync(path, start, run.Goal, run.O, popts, run.Replans, run.Token).ConfigureAwait(false);

        run.Stuck.Step = null;
        var r = await FollowPathAsync(path.Steps, run.O, run.Token, run.BlockedJumps, run.LockedDoors, run.Jumps, run.Stuck).ConfigureAwait(false);
        if (r is null) return GotoWalked(run, path);
        return await GotoLegFailedAsync(run, r).ConfigureAwait(false);
    }

    private PathOptions GotoPathOptions(GotoRun run)
    {
        var popts = BuildPathOptions(run.O, run.LockedDoors);
        if (run.BlockedJumps.Count > 0) popts = popts with { AvoidJumps = run.BlockedJumps.ToHashSet() };
        if (run.AvoidMoves.Count > 0) popts = popts with { AvoidMoves = run.AvoidMoves.ToHashSet() };
        if (CreeperAvoidCells(run.Goal) is { Count: > 0 } blast) popts = popts with { Avoid = blast };
        return popts;
    }

    /// <summary>The cached no-route answer, only for the goto's first search.</summary>
    private string? KnownNoRoute(GotoRun run, string routeKey) =>
        run.Replans == 0 && run.AirWaits == 0 && run.LastFailure is null ? NoRoutes.Get(routeKey) : null;

    private static bool IsNoPath(PathResult path) =>
        path.Status is PathStatus.NoPath or PathStatus.InvalidStart || path.Steps.Count == 0;

    /// <summary>No route: mid-air or embedded (wait and retry), short on blocks to climb with but carrying logs (craft planks, once),
    /// else the failure (cached when it was the first search).</summary>
    private async Task<ActionResult?> GotoNoPathAsync(GotoRun run, PathResult path, Vec3d start, BlockPos startCell, string routeKey)
    {
        if (path.Status == PathStatus.InvalidStart && run.AirWaits < 12)
        {
            // Usually mid-air (a pillar jump, a stuck re-plan mid-hop) or a chunk not yet loaded: wait a moment and
            // retry. Its own budget: landing must never use up the re-plans.
            run.AirWaits++;
            // Embedded in a block (a placement that landed in my own feet cell): jump out on top of it.
            await EscapeEmbeddedAsync(run.Token).ConfigureAwait(false);
            await WaitTicksAsync(10, run.Token).ConfigureAwait(false);
            return null;
        }
        // Short on blocks to climb with but carrying logs: 1 log = 4 planks (2x2 grid, no table), once.
        if (run.O.AllowPlace && !run.CraftedPlanks && await CraftPlanksToBuildAsync(run.Goal, run.O, start, run.Token).ConfigureAwait(false) is { } made)
        {
            run.CraftedPlanks = true;
            run.Crafted = "; " + made;
            run.ScaffoldBefore = ScaffoldSnapshot();
            return null;
        }
        var noRoute = $"no path to {run.Goal} ({path.Reason ?? path.Status.ToString()}{(run.LastFailure is null ? "" : ", last: " + run.LastFailure)})"
                      + NoRouteHint(run.Goal, run.O, start, run.Token);
        if (path.Status == PathStatus.NoPath && run.Replans == 0) NoRoutes.Put(routeKey, startCell, run.Goal, noRoute);
        return ActionResult.Fail(noRoute);
    }

    /// <summary>The walk ended where the path did: arrived when the path reached the goal, else (a partial path) re-plan.</summary>
    private ActionResult? GotoWalked(GotoRun run, PathResult path)
    {
        if (path.Status == PathStatus.Found) return ActionResult.Success($"arrived at {Fmt(World.Player.Position)}{JumpSummary(run.Jumps)}{GotoBuilt(run)}");
        if (++run.Replans > run.O.MaxReplans) return ActionResult.Fail($"only got partway to {run.Goal}; now at {Fmt(World.Player.Position)}");
        return null;
    }

    /// <summary>The walk failed: the void ends the goto; a stuck move is avoided from now on; else re-plan (within the budget).</summary>
    private async Task<ActionResult?> GotoLegFailedAsync(GotoRun run, string r)
    {
        if (r.Contains("void", StringComparison.Ordinal)) return ActionResult.Fail(r);
        if (r == "stuck" && run.Stuck.Step is { } stuckStep) r = await GotoStuckAsync(run, stuckStep).ConfigureAwait(false);
        run.LastFailure = r;
        if (++run.Replans > run.O.MaxReplans) return ActionResult.Fail($"{r} (after {run.Replans - 1} re-plans) at {Fmt(World.Player.Position)}");
        Log($"goto: {r}, re-planning");
        await WaitTicksAsync(2, run.Token).ConfigureAwait(false);
        return null;
    }

    /// <summary>Stuck on a step: the move is never planned again; the second time at the same spot an escape move is tried.</summary>
    private async Task<string> GotoStuckAsync(GotoRun run, PathStep stuckStep)
    {
        var at = World.Player.Position;
        if (run.StuckSpot is { } sp && Math.Abs(sp.X - at.X) + Math.Abs(sp.Y - at.Y) + Math.Abs(sp.Z - at.Z) < 0.4) run.StuckHere++;
        else { run.StuckSpot = at; run.StuckHere = 1; }
        if (Pathfinder.StartCellFor(World, at) is { } fromCell) run.AvoidMoves.Add((fromCell, stuckStep.Pos));
        if (run.Stuck.From is { } prev) run.AvoidMoves.Add((prev, stuckStep.Pos));
        var r = "stuck " + DescribeStuck(at, stuckStep);
        if (run.StuckHere >= 2 && await UnstickAsync(stuckStep, run.Token).ConfigureAwait(false) is { } escaped)
            r += $" ({run.StuckHere}x here; {escaped})";
        return r;
    }

    /// <summary>
    /// The route's shape (<see cref="RouteStats"/>): counted for the perf line, logged for a <see cref="GotoOptions.Diagnose"/> goto
    /// and for every detour. A detour (live 11-04 story-1: "way out of the way underground") is planned once more digging through
    /// natural ground when this body may (<see cref="DigThroughDetours"/>, normal world, the first plan of the goto), and the dug
    /// route replaces it when it costs under 70% of the walk.
    /// </summary>
    private async Task<PathResult> CheckRouteAsync(PathResult path, Vec3d start, Goal goal, GotoOptions o, PathOptions popts, int replans,
        CancellationToken ct)
    {
        var shape = RouteStats.Of(start, path.Steps);
        RouteStats.Note(shape, o.Diagnose);
        if (o.Diagnose is { } label) Log($"goto ({label}): {shape} to {goal}{(shape.IsDetour ? " (detour)" : "")}");
        else if (shape.IsDetour) Log($"goto: detour: {shape} to {goal}");
        if (!shape.IsDetour || replans > 0 || !DigThroughDetours || o.AllowBreak || path.Status != PathStatus.Found
            || MineClaw.Data.Versioning.WorldMode.Current.ScarceTerrain) return path;
        var dugOpts = popts with
        {
            AllowBreak = true,
            Covered = CoveredByGround, // fewer steps under ground win: the dig is worth it only when it shortens the time down there
            CanBreak = (id, meta, pos) => NaturalGround(id) && !World.Placed.IsPlaced(pos, World.Dimension, id) && (popts.CanBreak?.Invoke(id, meta, pos) ?? true),
        };
        PathResult dug;
        try { dug = await PathfindGate.RunAsync(() => Pathfinder.FindPath(World, start, goal, dugOpts, ct), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return path; }
        if (dug.Status != PathStatus.Found || dug.Steps.Count == 0) return path;
        var dugShape = RouteStats.Of(start, dug.Steps);
        int breaks = dug.Steps.Sum(s => s.Break.Count);
        // Taken only when it digs and is clearly shorter (its cost counts the steps under ground, the walk's doesn't: compare lengths).
        if (breaks == 0 || dugShape.Length > shape.Length * 0.6) return path;
        Log($"goto: detour dug short: {dugShape}, {breaks} block(s) to dig (walking: {shape})");
        RouteStats.NoteDugShort();
        return dug;
    }

    /// <summary>Natural ground over foot cell x,y,z: two or more solid blocks (leaves aside) within 8 above its head (a cave, a tunnel, a
    /// trench's overhang), as opposed to the open sky or a tree's crown.</summary>
    internal bool CoveredByGround(int x, int y, int z)
    {
        int n = 0;
        for (int yy = y + 2, top = Math.Min(World.WorldHeight - 1, y + 10); yy <= top; yy++)
        {
            int id = World.GetBlock(x, yy, z);
            if (id == 0 || id is MineClaw.Data.Blocks.Leaves or 161) continue;
            if (BlockShapes.HasCollision(World, x, yy, z) && ++n >= 2) return true;
        }
        return false;
    }

    /// <summary>Follows one path. Returns null when finished, else the failure reason.</summary>
    /// <param name="blockedJumps">Receives a jump move the edge guard kept refusing (landing not verified), so the
    /// re-plan avoids it instead of retrying the same jump forever.</param>
    /// <param name="lockedDoors">Receives doors/gates/hatches that didn't open after the follower's clicks (a protection
    /// plugin, an iron door ignoring its button), so the re-plan routes around them.</param>
    private async Task<string?> FollowPathAsync(IReadOnlyList<PathStep> steps, GotoOptions o, CancellationToken ct,
        HashSet<(BlockPos From, BlockPos To)>? blockedJumps = null, HashSet<BlockPos>? lockedDoors = null,
        List<ParkourPlan>? jumps = null, StuckNote? stuck = null)
    {
        var follower = new PathFollower(steps, new PathFollowerOptions
        {
            AllowSprint = o.AllowSprint, Reach = Options.Reach, RestoreDoors = o.RestoreDoors, CanSprint = CanSprintNow,
            Boosted = () => ClientSpeedFactor() > 1.0001,
        });
        try
        {
            var r = await FollowCoreAsync(follower, ct, blockedJumps, lockedDoors).ConfigureAwait(false);
            if (r == "stuck" && stuck is not null && follower.CurrentStep is { } st)
            {
                stuck.Step = st;
                stuck.From = follower.StepIndex > 0 ? steps[follower.StepIndex - 1].Pos : null;
            }
            return r;
        }
        finally
        {
            jumps?.AddRange(follower.CompletedJumps);
        }
    }

    private async Task<string?> FollowCoreAsync(PathFollower follower, CancellationToken ct,
        HashSet<(BlockPos From, BlockPos To)>? blockedJumps, HashSet<BlockPos>? lockedDoors)
    {
        int pillarRetries = 0;
        while (true)
        {
            FollowerOutput last = default;
            await DriveAsync(phys =>
            {
                if ((_lastGuardActions & GuardActions.CancelledJump) != 0) follower.NoteJumpCancelled(phys);
                if ((_lastGuardActions & GuardActions.ParkourRejected) != 0) follower.NoteParkourRejected();
                var output = follower.Tick(World, phys);
                last = output;
                bool drop = follower.CurrentStep?.Kind == MoveKind.Drop;
                return output.Status == FollowerStatus.Moving
                    ? new DriverStep(output.Input, AllowDrop: drop, Parkour: follower.Commitment,
                        BoostRoom: drop || _speedTarget <= 1 ? 0 : follower.BoostRoom(phys)) // Fast / Faster (MinecraftClient.Speed.cs)
                    : new DriverStep(output.Input, Done: true);
            }, ct).ConfigureAwait(false);

            switch (last.Status)
            {
                case FollowerStatus.Finished:
                    return null;
                case FollowerStatus.Failed:
                    if (follower.BlockedJump is { } bj)
                    {
                        blockedJumps?.Add(bj);
                        lock (_recentBlockedJumps) _recentBlockedJumps[bj] = DateTime.UtcNow;
                    }
                    if (follower.FailedTrigger is { } ft) lockedDoors?.Add(ft); // a mechanism that didn't respond
                    if (follower.CurrentStep is { Opens.Count: > 0 } door)
                        foreach (var tg in door.Opens.Where(t => !t.Done(World))) lockedDoors?.Add(tg.Pos);
                    return last.Reason ?? "path failed";
                case FollowerStatus.NeedsAction when last.Action is { } a:
                {
                    _idleSneak = last.Input.Sneak;
                    var r = a.Kind switch
                    {
                        FollowerActionKind.Break => await DigCoreAsync(a.Pos, ct, allowPathing: false).ConfigureAwait(false),
                        FollowerActionKind.Activate => await ActivateForPathAsync(a.Pos, follower.CurrentStep, ct).ConfigureAwait(false),
                        _ => await PlaceCoreAsync(a.Pos, ScaffoldItem() ?? -1, ct, allowPathing: false).ConfigureAwait(false),
                    };
                    _idleSneak = false;
                    if (r.Ok && a.Kind == FollowerActionKind.Place) RememberTemporaryBlock(a.Pos);
                    // Pillar jump fell back before the block went out: jump again (the follower re-centers), not a failure.
                    if (!r.Ok && a.Kind == FollowerActionKind.Place && r.Message.StartsWith(InTheWay, StringComparison.Ordinal)
                        && ++pillarRetries <= 6)
                        break;
                    if (!r.Ok)
                    {
                        if (a.Kind == FollowerActionKind.Activate && follower.CurrentStep is { } st)
                            foreach (var tg in st.Opens) lockedDoors?.Add(tg.Pos);
                        return $"path step {a.Kind} at {Fmt(a.Pos)} failed: {r.Message}";
                    }
                    break;
                }
                default:
                    return "path follower stopped";
            }
        }
    }

    /// <summary>The step a follower got "stuck" on and the cell it was planned from (null = the path's start).</summary>
    private sealed class StuckNote
    {
        public PathStep? Step;
        public BlockPos? From;
    }

    /// <summary>Where and why the body is stuck: "on cobblestone slab 7,64,6 stepping to 7,64,5: head hits leaves at 7,66,5".</summary>
    private string DescribeStuck(Vec3d at, PathStep step)
    {
        var feet = new BlockPos((int)Math.Floor(at.X), (int)Math.Floor(at.Y + 1e-6), (int)Math.Floor(at.Z));
        // Standing on: the feet cell itself (slab, farmland, soul sand) or the block under it.
        var on = BlockShapes.HasCollision(World, feet.X, feet.Y, feet.Z) ? feet : feet with { Y = feet.Y - 1 };
        var sb = new System.Text.StringBuilder($"on {BlockName(World.GetBlock(on), World.GetMeta(on))} {Fmt(on)} stepping to {Fmt(step.Pos)}");
        var hits = StuckProbe.Obstructions(World, at, step.Target);
        if (hits.Count == 0) return sb.Append(": nothing solid in the way (pushed by a player, mob or flow?)").ToString();
        sb.Append(": ").AppendJoin(", ", hits.Take(3).Select(h =>
            $"{(h.Head ? "head hits" : "blocked by")} {BlockName(World.GetBlock(h.Pos), World.GetMeta(h.Pos))} at {Fmt(h.Pos)}"
            + (World.Placed.IsPlaced(h.Pos, World.Dimension, World.GetBlock(h.Pos)) ? " (placed by us)" : "")));
        return sb.ToString();
    }

    /// <summary>
    /// Stuck twice at the same spot: get the body moving another way before the re-plan. In order: mine a block we placed
    /// that is in the way (self-placed is always minable; never next to lava, never a container, never another player's
    /// floor), a small hop toward the step when only the feet are blocked, else a sneak-walk off in a free direction (the edge
    /// guard still filters every tick). Returns what it did, or null when nothing moved.
    /// </summary>
    private async Task<string?> UnstickAsync(PathStep step, CancellationToken ct)
    {
        var start = World.Player.Position;
        var hits = StuckProbe.Obstructions(World, start, step.Target);
        var support = Safety.OtherPlayersSupport(World);
        foreach (var h in hits)
        {
            int id = World.GetBlock(h.Pos);
            if (!World.Placed.IsPlaced(h.Pos, World.Dimension, id) || World.Info(id).Interactable || NextToLava(h.Pos)
                || EditBlocked(h.Pos) || support.Contains(h.Pos)) continue;
            var dug = await DigCoreAsync(h.Pos, ct, allowPathing: false).ConfigureAwait(false);
            if (dug.Ok) return $"mined our {BlockName(id, 0)} at {Fmt(h.Pos)}";
        }

        double Moved()
        {
            var p = World.Player.Position;
            return Math.Sqrt((p.X - start.X) * (p.X - start.X) + (p.Z - start.Z) * (p.Z - start.Z));
        }
        async Task<bool> Push(float yaw, bool jump, bool sneak, int maxTicks)
        {
            int ticks = 0;
            bool left = false;
            await DriveAsync(phys =>
            {
                ticks++;
                // In water a jump is a swim up (0.04 a tick): held while pushing into the bank, the edge lifts the body out (vanilla's
                // collided-in-liquid hop). One tap only floats it off the floor (Fox 11-04: pawns stuck in 1-deep water channels).
                bool water = phys.InWater;
                if (!phys.OnGround && !water) left = true;
                bool done = Moved() >= 0.5 || ticks > maxTicks || (jump && left && phys.OnGround);
                return new DriverStep(new MoveInput(Forward: 1, Jump: jump && (water || !left && phys.OnGround), Sneak: sneak, Yaw: yaw, Pitch: 0), Done: done);
            }, ct).ConfigureAwait(false);
            await WaitTicksAsync(2, ct).ConfigureAwait(false);
            return Moved() >= 0.3;
        }

        var toward = PlayerPhysics.YawPitchTo(start, step.Target with { Y = start.Y }).Yaw;
        if (hits.Count > 0 && !hits.Any(h => h.Head) && await Push(toward, jump: true, sneak: false, maxTicks: World.Player.Physics.InWater ? 40 : 14).ConfigureAwait(false))
            return $"hopped toward {Fmt(step.Pos)}";
        // Step off any free way but the stuck one (nearest to the step target first: the re-plan continues from there).
        double sdx = step.Target.X - start.X, sdz = step.Target.Z - start.Z, slen = Math.Sqrt(sdx * sdx + sdz * sdz);
        foreach (var (dx, dz) in StuckProbe.FreeDirections(World, start, step.Target))
        {
            if (slen > 1e-6 && dx * sdx + dz * sdz > 0.7 * slen) continue; // that is the blocked move itself
            var yaw = PlayerPhysics.YawPitchTo(start, new Vec3d(start.X + dx, start.Y, start.Z + dz)).Yaw;
            if (await Push(yaw, jump: false, sneak: true, maxTicks: 30).ConfigureAwait(false))
                return "sneaked off " + (dx, dz) switch { (1, 0) => "east", (-1, 0) => "west", (0, 1) => "south", _ => "north" };
        }
        return null;
    }

    /// <summary>
    /// The feet cell holds a solid block while the two above are free: a placement landed in my own body (a race with the
    /// fall back from a pillar jump on a lenient server). Overlapping boxes don't block movement, so one jump lifts me out
    /// on top of it. False when not embedded.
    /// </summary>
    private async Task<bool> EscapeEmbeddedAsync(CancellationToken ct)
    {
        var fc = World.Player.Position.ToBlockPos();
        if (!BlockShapes.HasCollision(World, fc.X, fc.Y, fc.Z) || BlockShapes.HasCollision(World, fc.X, fc.Y + 1, fc.Z)
            || BlockShapes.HasCollision(World, fc.X, fc.Y + 2, fc.Z)) return false;
        Log($"goto: embedded in {BlockName(World.GetBlock(fc), World.GetMeta(fc))} at {Fmt(fc)}: jumping out");
        int ticks = 0;
        bool left = false;
        await DriveAsync(phys =>
        {
            ticks++;
            if (!phys.OnGround) left = true;
            bool done = (left && phys.OnGround) || ticks > 40;
            return new DriverStep(new MoveInput(Jump: !left && phys.OnGround, Yaw: _lookYaw, Pitch: _lookPitch), Done: done);
        }, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Why there is no route, for goals above me ("climb to the top"): re-plans once with pillaring/bridging allowed and
    /// a generous budget to tell "need N blocks to pillar, have M" (retry with build mode = AllowPlace) from truly
    /// unreachable. Empty for other goals.
    /// </summary>
    private string NoRouteHint(Goal goal, GotoOptions o, Vec3d start, CancellationToken ct)
    {
        BlockPos? target = goal switch
        {
            GoalBlock b => b.Target, GoalNear n => n.Target, GoalFiltered { Inner: GoalNear fn } => fn.Target,
            GoalNearPosition np => np.Position.ToBlockPos(), _ => null,
        };
        if (target is not { } t) return "";
        int dy = t.Y - (int)Math.Floor(start.Y + 1e-6);
        int have = ScaffoldCount();
        int logs = ReservedItems.Contains(MineClaw.Data.Blocks.Log) ? 0 : World.Inventory.CountItem(MineClaw.Data.Blocks.Log);
        if (dy < 2 && o.AllowPlace) return "";
        var probe = BuildPathOptions(o) with
        {
            AllowPlace = true, ScaffoldBudget = 64, AllowPartial = false, Timeout = TimeSpan.FromMilliseconds(600),
        };
        PathResult r;
        try { r = Pathfinder.FindPath(World, start, goal, probe, ct); }
        catch (OperationCanceledException) { return ""; }
        string up = dy >= 2 ? $"; it's {dy} blocks up" : "";
        if (!r.Success)
            return $"{up}; unreachable even by pillaring/bridging" + (_protectedRegion is not null ? " (no edits inside spawn protection)" : "");
        int need = Math.Max(1, r.ScaffoldUsed);
        if (o.AllowPlace && have >= need) return up;
        string using_ = have > 0 ? $" ({ScaffoldMix()})" : "";
        if (have >= need)
            return $"{up}; reachable by building: need {need} blocks to pillar/bridge, have {have}{using_} - retry with build mode (allow_place)";
        if (have + logs * 3 >= need)
            return $"{up}; reachable by building: need {need} blocks, have {have}{using_} + {logs} log (crafts into planks) - retry with build mode (allow_place)";
        return $"{up}; need {need} blocks to pillar/bridge, have {have}{using_} (cobblestone/netherrack/stone/planks/logs; never dirt)";
    }

    /// <summary>"12 cobblestone, 4 planks": scaffold held, for hints.</summary>
    private string ScaffoldMix() => string.Join(", ", ScaffoldSnapshot().Where(kv => kv.Value > 0 && !ReservedItems.Contains(kv.Key))
        .Select(kv => $"{kv.Value} {BlockName(kv.Key, 0)}"));

    /// <summary>
    /// Build-mode goto with no route on the blocks held: when the probe says the climb needs more than we carry and logs
    /// would cover it (each log → 4 planks, net +3), crafts just enough planks. False when crafting can't help.
    /// </summary>
    private async Task<string?> CraftPlanksToBuildAsync(Goal goal, GotoOptions o, Vec3d start, CancellationToken ct)
    {
        if (ReservedItems.Contains(MineClaw.Data.Blocks.Log) || ReservedItems.Contains(MineClaw.Data.Blocks.Planks)) return null;
        int logs = World.Inventory.CountItem(MineClaw.Data.Blocks.Log);
        if (logs == 0) return null;
        int have = ScaffoldCount();
        var probe = BuildPathOptions(o) with
        {
            AllowPlace = true, ScaffoldBudget = Math.Min(64, have + logs * 3), AllowPartial = false, Timeout = TimeSpan.FromMilliseconds(600),
        };
        PathResult r;
        try { r = await PathfindGate.RunAsync(() => Pathfinder.FindPath(World, start, goal, probe, ct), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        if (!r.Success || r.ScaffoldUsed <= have) return null;
        int useLogs = Math.Min(logs, (r.ScaffoldUsed - have + 2) / 3);
        var c = await CraftCoreAsync(MineClaw.Data.Blocks.Planks, -1, useLogs * 4, ct).ConfigureAwait(false);
        Log($"goto: short on blocks to build with (need {r.ScaffoldUsed}, have {have}): crafting {useLogs * 4} planks from logs: {c.Message}");
        return c.Ok ? $"crafted {useLogs * 4} planks from {useLogs} log" : null;
    }

    /// <summary>
    /// The follower's right-click step for a door, fence gate, trapdoor hatch, or an iron door's button/lever: the body is
    /// already stopped (idle input), so face the block, right-click it without pathing, and wait for the server's block
    /// change: the clicked block's metadata, or the open bit of the step's openables (an iron door follows its button a
    /// tick or two later). No change is not an error here: the follower re-checks the open bit, clicks again up to its
    /// limit and then fails "won't open (locked?)", which the goto loop turns into a re-plan around that door.
    /// </summary>
    private async Task<ActionResult> ActivateForPathAsync(BlockPos pos, PathStep? step, CancellationToken ct)
    {
        int id = World.GetBlock(pos), meta = World.GetMeta(pos);
        var opens = step?.Opens ?? [];
        var before = opens.Select(t => World.GetMeta(t.Pos)).ToArray();
        bool OpensMoved()
        {
            for (int i = 0; i < opens.Count; i++)
                if (World.GetMeta(opens[i].Pos) != before[i] || World.GetBlock(opens[i].Pos) != opens[i].Id) return true;
            return false;
        }
        var r = await ClickBlockAsync(pos, ct, allowPathing: false).ConfigureAwait(false);
        if (!r.Ok) return r;
        var wait = TimeSpan.FromMilliseconds(Math.Max(400, Options.TickInterval.TotalMilliseconds * 20));
        bool changed = await WaitUntilAsync(() => World.GetBlock(pos) != id || World.GetMeta(pos) != meta || OpensMoved(), wait, ct)
            .ConfigureAwait(false);
        if (changed && opens.Count > 0 && !Openables.IsOpenable(id)) // button/lever pressed: give the door its redstone tick
            await WaitUntilAsync(() => opens.All(t => t.Done(World)), wait, ct).ConfigureAwait(false);
        return ActionResult.Success(changed ? $"toggled {Openables.Name(id)} at {Fmt(pos)}" : $"{Openables.Name(id)} at {Fmt(pos)} didn't change");
    }

    /// <summary>Keeps within <paramref name="distance"/> blocks of a player until cancelled/stopped or the player is lost.</summary>
    public Task<ActionResult> FollowPlayerAsync(string name, double distance = 3, CancellationToken ct = default) =>
        RunActionAsync("follow", ct, async c =>
        {
            var o = GotoOptions.Default with { MaxReplans = 1, PlanTimeout = TimeSpan.FromMilliseconds(500) };
            int lostTicks = 0;
            try
            {
                while (true)
                {
                    c.ThrowIfCancellationRequested();
                    var target = World.Entities.FindPlayer(name);
                    if (target is null)
                    {
                        if (++lostTicks > 100) return ActionResult.Fail($"lost sight of {name}");
                        await WaitTicksAsync(1, c).ConfigureAwait(false);
                        continue;
                    }
                    lostTicks = 0;
                    await SurvivalWindowAsync(c).ConfigureAwait(false);
                    var me = World.Player.Position;
                    double d = me.DistanceTo(target.Position);
                    if (d <= distance + 0.5)
                    {
                        var (yaw, pitch) = PlayerPhysics.YawPitchTo(World.Player.EyePosition, target.Position + new Vec3d(0, 1.6, 0));
                        _lookYaw = yaw;
                        _lookPitch = pitch;
                        await WaitTicksAsync(5, c).ConfigureAwait(false);
                        continue;
                    }
                    // Plan to the target's current spot, follow for up to ~2 s, then re-plan as it moves. Keep a margin
                    // from the void: only stop on cells at least 2 from any edge and prefer paths away from edges, so a
                    // player walking along the rim is followed from the inside rather than mirrored.
                    var goal = GoalFiltered.AwayFromEdge(World, new GoalNearPosition(target.Position, distance + 1));
                    var popts = BuildPathOptions(o) with { EdgeCostMultiplier = 8 };
                    var path = await PathfindGate.RunAsync(() => Pathfinder.FindPath(World, me, goal, popts, c), c).ConfigureAwait(false);
                    var steps = path.Steps;
                    if (path.Status != PathStatus.Found)
                    {
                        // A partial path must still end away from the edge: cut it back to the last safe cell.
                        int keep = -1;
                        for (int i = 0; i < steps.Count; i++)
                            if (Safety.EdgeDistance(World, steps[i].X, steps[i].Y, steps[i].Z, 2) >= 2) keep = i;
                        steps = steps.Take(keep + 1).ToList();
                    }
                    if (steps.Count == 0)
                    {
                        await WaitTicksAsync(10, c).ConfigureAwait(false);
                        continue;
                    }
                    using var slice = CancellationTokenSource.CreateLinkedTokenSource(c);
                    slice.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        var r = await FollowPathAsync(steps, o, slice.Token).ConfigureAwait(false);
                        if (r is not null && r.Contains("void", StringComparison.Ordinal)) return ActionResult.Fail(r);
                    }
                    catch (OperationCanceledException) when (!c.IsCancellationRequested) { /* re-plan */ }
                }
            }
            catch (OperationCanceledException) when (_abortReason == "stopped")
            {
                return ActionResult.Success($"stopped following {name}");
            }
        });
}
