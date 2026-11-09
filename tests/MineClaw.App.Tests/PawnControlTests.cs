using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.App.Supervisor;
using MineClaw.Client;

namespace MineClaw.App.Tests;

public sealed class PawnControlTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Body : IPawnControlBody
    {
        public string Name => "maid_chan_101";
        public bool Online { get; set; } = true;
        public int Begins, Ends, Actions;
        public readonly List<PawnControlInput> Inputs = [];
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldAction;
        public TaskCompletionSource? DiscreteAction;
        public PawnControlState State() => new(2, 64, 3, 15, -20, 0, 20, 18, 2, true);
        public PawnControlInventory Inventory() => new(0, "player", "Inventory", 46, 9, 9, 36, [], null);
        public void Begin() => Begins++;
        public void End() => Ends++;
        public void Input(PawnControlInput input) => Inputs.Add(input);
        public async Task<ActionResult> ActionAsync(PawnControlAction action, CancellationToken ct, CancellationToken holdRelease = default)
        {
            Actions++;
            Started.TrySetResult();
            if (DiscreteAction is { } discrete) await discrete.Task.WaitAsync(ct);
            using var held = CancellationTokenSource.CreateLinkedTokenSource(ct, holdRelease);
            if (HoldAction) await Task.Delay(Timeout.Infinite, held.Token);
            return ActionResult.Success(action.Kind);
        }
    }

    [Fact]
    public void Sessions_are_exclusive_owner_bound_and_acquire_retries_are_idempotent()
    {
        var body = new Body();
        using var sessions = new PawnControlSessions(_ => body, startTimer: false);
        var first = sessions.Acquire("101", "alice", "tab-a");
        Assert.Equal(first.Session, sessions.Acquire("101", "alice", "tab-a").Session);
        Assert.Equal(1, body.Begins);
        Assert.Equal(409, Assert.Throws<PawnControlException>(() => sessions.Acquire("101", "bob", "tab-b")).Status);
        Assert.Throws<PawnControlException>(() => sessions.Acquire("101", "alice", "tab-b"));
        Assert.Throws<PawnControlException>(() => sessions.Input("101", "bob", first.Session, new(1, 1, 0, 0, 0)));
        Assert.Throws<PawnControlException>(() => sessions.Release("101", "alice", "wrong-token"));
        sessions.Release("101", "alice", first.Session);
        Assert.Equal(1, body.Ends);
        Assert.NotEqual(first.Session, sessions.Acquire("101", "bob", "tab-b").Session);
        Assert.Throws<PawnControlException>(() => sessions.Input("101", "alice", first.Session, new(2, 1, 0, 0, 0)));
    }

    [Fact]
    public void Delayed_input_cannot_restore_old_keys_or_extend_expiry_and_deadman_stops_once()
    {
        var body = new Body(); var clock = new Clock();
        using var sessions = new PawnControlSessions(_ => body, clock, startTimer: false);
        var lease = sessions.Acquire("101", "alice", "tab");
        var moved = sessions.Input("101", "alice", lease.Session, new(2, 1, 0, 90, 0, Jump: true));
        clock.Now += TimeSpan.FromMilliseconds(200);
        Assert.Equal(moved.ExpiresAt, sessions.Input("101", "alice", lease.Session, new(1, -1, 0, 0, 0)).ExpiresAt);
        Assert.Single(body.Inputs);
        clock.Now += TimeSpan.FromMilliseconds(151);
        sessions.Sweep(); sessions.Sweep();
        Assert.Equal(2, body.Inputs.Count);
        Assert.Equal(0, body.Inputs.Last().Forward);
        Assert.False(body.Inputs.Last().Jump);
        clock.Now += TimeSpan.FromSeconds(3);
        sessions.Sweep();
        Assert.Equal(1, body.Ends);
        Assert.Throws<PawnControlException>(() => sessions.Input("101", "alice", lease.Session, new(3, 1, 0, 0, 0)));
    }

    [Fact]
    public async Task Held_primary_keeps_dig_progress_and_release_cancels_before_reacquire()
    {
        var body = new Body { HoldAction = true };
        using var sessions = new PawnControlSessions(_ => body, startTimer: false);
        var lease = sessions.Acquire("101", "alice", "tab");
        var digging = sessions.ActionAsync("101", "alice", lease.Session, new(1, "attack"));
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(digging, sessions.ActionAsync("101", "alice", lease.Session, new(1, "attack")));
        Assert.Same(digging, sessions.ActionAsync("101", "alice", lease.Session, new(2, "attack")));
        Assert.Equal(1, body.Actions);
        sessions.Release("101", "alice", lease.Session);
        Assert.False((await digging.WaitAsync(TimeSpan.FromSeconds(2))).Ok);
        Assert.Equal(1, body.Ends);
    }

    [Fact]
    public async Task Mouse_release_preserves_the_initial_discrete_click_but_stops_held_use()
    {
        var click = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new Body { HoldAction = true, DiscreteAction = click };
        using var sessions = new PawnControlSessions(_ => body, startTimer: false);
        var lease = sessions.Acquire("101", "alice", "tab");
        var pending = sessions.ActionAsync("101", "alice", lease.Session, new(1, "use"));
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True((await sessions.ActionAsync("101", "alice", lease.Session, new(2, "release"))).Ok);
        Assert.False(pending.IsCompleted); // an initial chest click/server acknowledgement still has its lifetime token
        click.SetResult();
        Assert.False((await pending.WaitAsync(TimeSpan.FromSeconds(1))).Ok); // subsequent continuous hold ends immediately
        await Assert.ThrowsAsync<PawnControlException>(() => sessions.ActionAsync("101", "alice", lease.Session, new(1, "use")));
        body.HoldAction = false;
        Assert.True((await sessions.ActionAsync("101", "alice", lease.Session, new(3, "use"))).Ok);
        Assert.Equal(2, body.Actions);
    }

    [Fact]
    public async Task Long_held_actions_acknowledge_promptly_and_heartbeats_report_completion()
    {
        var body = new Body { HoldAction = true };
        using var sessions = new PawnControlSessions(_ => body, startTimer: false);
        var lease = sessions.Acquire("101", "alice", "tab");
        var accepted = await sessions.AcknowledgeActionAsync("101", "alice", lease.Session, new(1, "use"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(accepted.Ok);
        Assert.True(accepted.Pending);
        Assert.Equal(new PawnControlActionStatus(1, true), sessions.Input("101", "alice", lease.Session, new(1, 0, 0, 0, 0)).Action);
        await sessions.ActionAsync("101", "alice", lease.Session, new(2, "inventory"));
        Assert.Equal(new PawnControlActionStatus(1, true), sessions.Input("101", "alice", lease.Session, new(2, 0, 0, 0, 0)).Action);
        await sessions.ActionAsync("101", "alice", lease.Session, new(3, "cancel"));
        Assert.Equal(new PawnControlActionStatus(3, false, true, "action cancelled"),
            sessions.Input("101", "alice", lease.Session, new(3, 0, 0, 0, 0)).Action);
        Assert.Equal(1, body.Actions);
    }

    [Fact]
    public async Task Cancel_stops_held_use_without_releasing_session_and_completed_actions_do_not_replay()
    {
        var body = new Body { HoldAction = true };
        using var sessions = new PawnControlSessions(_ => body, startTimer: false);
        var lease = sessions.Acquire("101", "alice", "tab");
        var usingItem = sessions.ActionAsync("101", "alice", lease.Session, new(1, "use"));
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var inventory = await sessions.ActionAsync("101", "alice", lease.Session, new(2, "inventory"));
        Assert.NotNull(inventory.Inventory);
        Assert.False(usingItem.IsCompleted);
        Assert.True((await sessions.ActionAsync("101", "alice", lease.Session, new(2, "cancel"))).Ok);
        Assert.False((await usingItem).Ok);
        Assert.Equal(0, body.Ends);
        body.HoldAction = false;
        var drop = sessions.ActionAsync("101", "alice", lease.Session, new(3, "drop"));
        Assert.True((await drop).Ok);
        Assert.Same(drop, sessions.ActionAsync("101", "alice", lease.Session, new(3, "drop")));
        Assert.Equal(2, body.Actions);
        await Assert.ThrowsAsync<PawnControlException>(() => sessions.ActionAsync("101", "alice", lease.Session, new(1, "drop")));
    }

    [Fact]
    public void Offline_and_shutdown_release_ai_ownership()
    {
        var body = new Body();
        using var sessions = new PawnControlSessions(_ => body, startTimer: false);
        sessions.Acquire("101", "alice", "tab");
        body.Online = false;
        sessions.Sweep();
        Assert.Equal(1, body.Ends);
        body.Online = true;
        sessions.Acquire("101", "alice", "tab");
        sessions.Dispose();
        Assert.Equal(2, body.Ends);
    }

    [Fact]
    public async Task Routes_require_bridge_identity_and_validate_control_requests()
    {
        var body = new Body();
        using var sessions = new PawnControlSessions(_ => body, startTimer: false);
        using var routes = new SupervisorPawnControlRoutes(new SupervisorOptions { Token = "secret", Viewer = { Enabled = true } }, sessions);
        Task<SupervisorReply?> Send(string op, object input, string? roles = "story", string token = "secret") => routes.TryHandleAsync(new("POST",
            "/api/bodies/101/control/" + op, new Dictionary<string, string>(), token, JsonSerializer.Serialize(input, SupervisorJson.Options),
            roles == null ? null : new Dictionary<string, string> { ["X-MCX-User"] = "alice", ["X-MCX-Roles"] = roles }));
        Assert.Equal(403, (await Send("acquire", new { requestId = "a" }, roles: null))!.Status);
        Assert.Equal(403, (await Send("acquire", new { requestId = "a" }, roles: "viewer"))!.Status);
        Assert.Equal(403, (await Send("acquire", new { requestId = "a" }, token: "bad"))!.Status);
        var acquired = await Send("acquire", new { requestId = "a" });
        Assert.Equal(200, acquired!.Status);
        var reply = JsonNode.Parse(acquired.Text)!;
        var session = reply["session"]!.GetValue<string>();
        Assert.Equal(350, reply["input_timeout_ms"]!.GetValue<int>());
        Assert.Equal(2, reply["state"]!["held_slot"]!.GetValue<int>());
        Assert.Equal(400, (await Send("input", new { session, seq = 1, forward = 2 }))!.Status);
        Assert.Equal(400, (await Send("action", new { session, seq = 1, kind = "dig" }))!.Status);
        Assert.Equal(400, (await Send("action", new { session, seq = 1, kind = "inventory_click", slot = 4 }))!.Status);
        Assert.Equal(200, (await Send("action", new { session, seq = 1, kind = "inventory_click", slot = -999, windowId = 0 }))!.Status);
        Assert.Equal(200, (await Send("release", new { session }))!.Status);
    }
}
