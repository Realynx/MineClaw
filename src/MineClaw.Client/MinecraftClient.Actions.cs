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
        if (CombatBlocks(name) is { } busy) return ActionResult.Fail(busy);
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
        var lines = ChatText.Prepare(text);
        if (lines.Count == 0) return ActionResult.Fail("nothing to say after sanitizing");
        if (lines.Count > 6) lines = lines.Take(6).ToList();
        await _chatLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int sent = 0;
            foreach (var line in lines)
            {
                var wait = _lastChat + Options.ChatInterval - DateTime.UtcNow;
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
        return PathOptions.Default with
        {
            AllowBreak = o.AllowBreak,
            CanBreak = (id, meta, pos) => !IsBlockProtected(pos, id) && !NextToLava(pos) && !EditBlocked(pos),
            CanPlace = pos => !EditBlocked(pos),
            DigCost = (id, meta) =>
            {
                int idx = MineClaw.Data.Tools.BestToolIndex(id, slots);
                int ticks = MineClaw.Data.Tools.DigTicks(id, idx < 0 ? -1 : slots[idx].Id);
                return ticks == int.MaxValue ? double.PositiveInfinity : ticks + 5;
            },
            AllowPlace = o.AllowPlace && scaffold > 0,
            ScaffoldBudget = o.AllowPlace && scaffold > 0 ? Math.Min(o.ScaffoldBudget, World.Inventory.CountItem(scaffold)) : 0,
            Timeout = o.PlanTimeout,
            AllowPartial = o.AllowPartial,
            AllowOpenDoors = o.AllowOpenDoors,
            CanOpen = (pos, id) => !IsBlockProtected(pos, id) && !(lockedDoors?.Contains(pos) ?? false),
        };
    }

    private async Task<ActionResult> GotoCoreAsync(Goal goal, GotoOptions o, CancellationToken ct)
    {
        // A goal with nothing under it can only be reached by bridging (AllowPlace builds the floor first).
        if (!o.AllowPlace && GoalOverVoid(goal) is { } overVoid) return ActionResult.Fail(overVoid);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(o.Timeout);
        var token = timeout.Token;
        int replans = 0;
        string? lastFailure = null;
        var blockedJumps = new HashSet<(BlockPos From, BlockPos To)>();
        var lockedDoors = new HashSet<BlockPos>();
        try
        {
            while (true)
            {
                var start = World.Player.Position;
                if (goal.IsReached(Pathfinder.StartCellFor(World, start) ?? start.ToBlockPos()))
                    return ActionResult.Success($"arrived at {Fmt(start)}");

                var popts = BuildPathOptions(o, lockedDoors);
                if (blockedJumps.Count > 0) popts = popts with { AvoidJumps = blockedJumps.ToHashSet() };
                var path = await Task.Run(() => Pathfinder.FindPath(World, start, goal, popts, token), token).ConfigureAwait(false);
                if (path.Status is PathStatus.NoPath or PathStatus.InvalidStart || path.Steps.Count == 0)
                {
                    if (path.Status == PathStatus.InvalidStart && replans < o.MaxReplans)
                    {
                        // Usually mid-air or a chunk not yet loaded: wait a moment and retry.
                        replans++;
                        await WaitTicksAsync(10, token).ConfigureAwait(false);
                        continue;
                    }
                    return ActionResult.Fail($"no path to {goal} ({path.Reason ?? path.Status.ToString()}{(lastFailure is null ? "" : ", last: " + lastFailure)})"
                                             + NoRouteHint(goal, o, start, token));
                }

                var r = await FollowPathAsync(path.Steps, o, token, blockedJumps, lockedDoors).ConfigureAwait(false);
                if (r is null)
                {
                    if (path.Status == PathStatus.Found) return ActionResult.Success($"arrived at {Fmt(World.Player.Position)}");
                    if (++replans > o.MaxReplans) return ActionResult.Fail($"only got partway to {goal}; now at {Fmt(World.Player.Position)}");
                    continue;
                }
                lastFailure = r;
                if (r.Contains("void", StringComparison.Ordinal)) return ActionResult.Fail(r);
                if (++replans > o.MaxReplans) return ActionResult.Fail($"{r} (after {replans - 1} re-plans) at {Fmt(World.Player.Position)}");
                Log($"goto: {r}, re-planning");
                await WaitTicksAsync(2, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return ActionResult.Fail($"timed out going to {goal}; at {Fmt(World.Player.Position)}");
        }
    }

    /// <summary>Follows one path. Returns null when finished, else the failure reason.</summary>
    /// <param name="blockedJumps">Receives a jump move the edge guard kept refusing (landing not verified), so the
    /// re-plan avoids it instead of retrying the same jump forever.</param>
    /// <param name="lockedDoors">Receives doors/gates/hatches that didn't open after the follower's clicks (a protection
    /// plugin, an iron door ignoring its button), so the re-plan routes around them.</param>
    private async Task<string?> FollowPathAsync(IReadOnlyList<PathStep> steps, GotoOptions o, CancellationToken ct,
        HashSet<(BlockPos From, BlockPos To)>? blockedJumps = null, HashSet<BlockPos>? lockedDoors = null)
    {
        var follower = new PathFollower(steps, new PathFollowerOptions
        {
            AllowSprint = o.AllowSprint, Reach = Options.Reach, RestoreDoors = o.RestoreDoors,
        });
        while (true)
        {
            FollowerOutput last = default;
            await DriveAsync(phys =>
            {
                if ((_lastGuardActions & GuardActions.CancelledJump) != 0) follower.NoteJumpCancelled(phys);
                var output = follower.Tick(World, phys);
                last = output;
                bool drop = follower.CurrentStep?.Kind == MoveKind.Drop;
                return output.Status == FollowerStatus.Moving
                    ? new DriverStep(output.Input, AllowDrop: drop)
                    : new DriverStep(output.Input, Done: true);
            }, ct).ConfigureAwait(false);

            switch (last.Status)
            {
                case FollowerStatus.Finished:
                    return null;
                case FollowerStatus.Failed:
                    if (follower.BlockedJump is { } bj) blockedJumps?.Add(bj);
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

    /// <summary>
    /// Why there is no route, for goals above me ("climb to the top"): re-plans once with pillaring/bridging allowed and
    /// a generous budget to tell "need N blocks to pillar, have M" (retry with build mode = AllowPlace) from truly
    /// unreachable. Empty for other goals.
    /// </summary>
    private string NoRouteHint(Goal goal, GotoOptions o, Vec3d start, CancellationToken ct)
    {
        BlockPos? target = goal switch { GoalBlock b => b.Target, GoalNear n => n.Target, _ => null };
        if (target is not { } t) return "";
        int dy = t.Y - (int)Math.Floor(start.Y + 1e-6);
        int have = ScaffoldItem() is { } sc ? World.Inventory.CountItem(sc) : 0;
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
        return have >= need
            ? $"{up}; reachable by building: need {need} blocks to pillar/bridge, have {have} - retry with build mode (allow_place)"
            : $"{up}; need {need} blocks to pillar/bridge, have {have} (cobblestone/dirt/netherrack/planks/stone)";
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
                    var path = await Task.Run(() => Pathfinder.FindPath(World, me, goal, popts, c), c).ConfigureAwait(false);
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
