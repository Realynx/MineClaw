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
        RunActionAsync("goto", ct, c => GotoCoreAsync(goal, options ?? GotoOptions.Default, c));

    private PathOptions BuildPathOptions(GotoOptions o)
    {
        int scaffold = ScaffoldItem() ?? -1;
        var slots = World.Inventory.PlayerSlots();
        return PathOptions.Default with
        {
            AllowBreak = o.AllowBreak,
            CanBreak = (id, meta, pos) => !IsBlockProtected(pos, id) && !NextToLava(pos),
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
        try
        {
            while (true)
            {
                var start = World.Player.Position;
                if (goal.IsReached(Pathfinder.StartCellFor(World, start) ?? start.ToBlockPos()))
                    return ActionResult.Success($"arrived at {Fmt(start)}");

                var popts = BuildPathOptions(o);
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
                    return ActionResult.Fail($"no path to {goal} ({path.Reason ?? path.Status.ToString()}{(lastFailure is null ? "" : ", last: " + lastFailure)})");
                }

                var r = await FollowPathAsync(path.Steps, o, token).ConfigureAwait(false);
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
    private async Task<string?> FollowPathAsync(IReadOnlyList<PathStep> steps, GotoOptions o, CancellationToken ct)
    {
        var follower = new PathFollower(steps, new PathFollowerOptions { AllowSprint = o.AllowSprint, Reach = Options.Reach });
        while (true)
        {
            FollowerOutput last = default;
            await DriveAsync(phys =>
            {
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
                    return last.Reason ?? "path failed";
                case FollowerStatus.NeedsAction when last.Action is { } a:
                {
                    _idleSneak = last.Input.Sneak;
                    var r = a.Kind == FollowerActionKind.Break
                        ? await DigCoreAsync(a.Pos, ct, allowPathing: false).ConfigureAwait(false)
                        : await PlaceCoreAsync(a.Pos, ScaffoldItem() ?? -1, ct, allowPathing: false).ConfigureAwait(false);
                    _idleSneak = false;
                    if (!r.Ok) return $"path step {a.Kind} at {Fmt(a.Pos)} failed: {r.Message}";
                    break;
                }
                default:
                    return "path follower stopped";
            }
        }
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
