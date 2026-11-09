using System.Security.Cryptography;
using MineClaw.Client;

namespace MineClaw.App.Supervisor;

public sealed record PawnControlState(double X, double Y, double Z, float Yaw, float Pitch, int Dim, int Hp, int Food, int HeldSlot, bool OnGround, int WindowId = 0);
public sealed record PawnControlItem(int Id, int Meta, int Count);
public sealed record PawnControlSlot(int Slot, int Id, int Meta, int Count);
public sealed record PawnControlInventory(int WindowId, string Kind, string Title, int SlotCount, int ContainerSlots, int MainStart, int HotbarStart,
    IReadOnlyList<PawnControlSlot> Slots, PawnControlItem? Cursor);
public sealed record PawnControlInput(long Seq, float Forward, float Strafe, float Yaw, float Pitch, bool Jump = false, bool Sneak = false, bool Sprint = false, int? Hotbar = null);
public sealed record PawnControlTarget(int X, int Y, int Z);
public sealed record PawnControlAction(long Seq, string Kind, PawnControlTarget? Target = null, int? EntityId = null, int? Face = null,
    int? Slot = null, int? Button = null, bool Shift = false, bool WholeStack = false, int? WindowId = null);
public sealed record PawnControlReply(string Session, string Body, DateTimeOffset ExpiresAt, int InputTimeoutMs, int LeaseTimeoutMs,
    PawnControlState State, PawnControlInventory? Inventory = null, PawnControlActionStatus? Action = null);
public sealed record PawnControlActionStatus(long Seq, bool Pending, bool? Ok = null, string? Message = null);
public sealed record PawnControlActionReply(bool Ok, string Message, PawnControlState State, PawnControlInventory? Inventory = null, bool Pending = false);

/// <summary>A live pawn adapter; tests use a body with no game connection.</summary>
public interface IPawnControlBody
{
    string Name { get; }
    bool Online { get; }
    PawnControlState State();
    PawnControlInventory Inventory();
    void Begin();
    void Input(PawnControlInput input);
    Task<ActionResult> ActionAsync(PawnControlAction action, CancellationToken ct, CancellationToken holdRelease = default);
    Func<CancellationToken, CancellationToken, Task<ActionResult>> PrepareAction(PawnControlAction action) =>
        (ct, holdRelease) => ActionAsync(action, ct, holdRelease);
    void End();
}

public sealed class PawnControlException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Exclusive browser ownership with independent input/action ordering. Neither a lost HTTP response
/// nor a hidden/disconnected browser can leave movement keys or the colony's AI suspended indefinitely.</summary>
public sealed class PawnControlSessions : IDisposable
{
    public const int InputTimeoutMs = 350, LeaseTimeoutMs = 3000;
    private sealed class Lease(IPawnControlBody body, string owner, string request, DateTimeOffset now)
    {
        public readonly IPawnControlBody Body = body;
        public readonly string Owner = owner, Request = request, Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        public DateTimeOffset Expires = now.AddMilliseconds(LeaseTimeoutMs), LastInput = now;
        public long InputSeq = -1, ActionSeq = -1, RunningSeq = -1;
        public bool Stopped;
        public CancellationTokenSource? ActionCancel;
        public CancellationTokenSource? HoldRelease;
        public Task<PawnControlActionReply>? Action;
        public PawnControlAction? ActionRequest;
        public SocketAttachment? Socket;
        public readonly HashSet<string> Aliases = new(StringComparer.OrdinalIgnoreCase) { body.Name };
    }

    internal sealed class SocketAttachment(string body, string owner, string session)
    {
        internal readonly string Body = body, Owner = owner, Session = session;
        internal readonly CancellationTokenSource Closed = new();
    }

    private sealed class Acquisition(IPawnControlBody body, string owner, string request, string alias)
    {
        public readonly IPawnControlBody Body = body;
        public readonly string Owner = owner, Request = request, Alias = alias;
        public readonly TaskCompletionSource<PawnControlReply> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Acquisition> _acquiring = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, IPawnControlBody?> _resolve;
    private readonly TimeProvider _time;
    private readonly ITimer? _timer;
    private bool _disposed;

    public PawnControlSessions(Func<string, IPawnControlBody?> resolve, TimeProvider? time = null, bool startTimer = true)
    {
        _resolve = resolve;
        _time = time ?? TimeProvider.System;
        if (startTimer) _timer = _time.CreateTimer(_ => Sweep(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    public PawnControlReply Acquire(string body, string owner, string requestId) => AcquireAsync(body, owner, requestId).GetAwaiter().GetResult();

    public Task<PawnControlReply> AcquireAsync(string body, string owner, string requestId)
    {
        // Resolving membership and interrupting the director can wait behind a long colony reconciliation.
        // Neither may run under the monitor used by another pawn's 20 Hz inputs or the deadman timer.
        var live = _resolve(body) ?? throw new PawnControlException(404, "This body is not an available colony pawn.");
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SweepLocked();
            if (_leases.TryGetValue(live.Name, out var existing))
            {
                if (existing.Owner != owner || existing.Request != requestId) throw new PawnControlException(409, "This pawn is already controlled by another browser session.");
                existing.Aliases.Add(body);
                return Task.FromResult(Reply(existing, inventory: true));
            }
            if (_acquiring.TryGetValue(live.Name, out var pending))
            {
                if (pending.Owner != owner || pending.Request != requestId) throw new PawnControlException(409, "This pawn is already being taken over by another browser session.");
                return pending.Done.Task;
            }
            var acquisition = new Acquisition(live, owner, requestId, body);
            _acquiring.Add(live.Name, acquisition);
            _ = Task.Run(() => FinishAcquisition(acquisition));
            return acquisition.Done.Task;
        }
    }

    private void FinishAcquisition(Acquisition acquisition)
    {
        var live = acquisition.Body;
        try
        {
            lock (_lock) ObjectDisposedException.ThrowIf(_disposed, this);
            if (!live.Online) throw new PawnControlException(409, "The pawn is offline.");
            live.Begin();
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var lease = new Lease(live, acquisition.Owner, acquisition.Request, _time.GetUtcNow());
                lease.Aliases.Add(acquisition.Alias);
                var reply = Reply(lease, inventory: true);
                _leases.Add(live.Name, lease);
                _acquiring.Remove(live.Name);
                acquisition.Done.TrySetResult(reply);
            }
        }
        catch (Exception ex)
        {
            try { live.End(); } catch { }
            acquisition.Done.TrySetException(ex);
        }
        finally
        {
            lock (_lock)
                if (_acquiring.TryGetValue(live.Name, out var current) && ReferenceEquals(current, acquisition)) _acquiring.Remove(live.Name);
        }
    }

    public PawnControlReply Input(string body, string owner, string session, PawnControlInput input)
    {
        lock (_lock)
        {
            var lease = Find(body, owner, session);
            if (input.Seq <= lease.InputSeq) return Reply(lease); // delayed packets never restore old keys or renew a lease
            lease.Body.Input(input);
            lease.InputSeq = input.Seq;
            lease.LastInput = _time.GetUtcNow();
            lease.Expires = lease.LastInput.AddMilliseconds(LeaseTimeoutMs);
            lease.Stopped = false;
            return Reply(lease);
        }
    }

    public Task<PawnControlActionReply> ActionAsync(string body, string owner, string session, PawnControlAction action)
    {
        lock (_lock)
        {
            var lease = Find(body, owner, session);
            if (action.Kind == "inventory") return Task.FromResult(new PawnControlActionReply(true, "inventory", lease.Body.State(), lease.Body.Inventory()));
            if (action.Kind == "release")
            {
                if (action.Seq < lease.ActionSeq) throw new PawnControlException(409, "Stale control action.");
                lease.ActionSeq = action.Seq;
                try { lease.HoldRelease?.Cancel(); } catch (ObjectDisposedException) { }
                return Task.FromResult(new PawnControlActionReply(true, "action released", lease.Body.State()));
            }
            if (action.Seq == lease.ActionSeq && lease.Action != null) return lease.Action;
            if (action.Seq <= lease.ActionSeq) throw new PawnControlException(409, "Stale control action.");
            lease.ActionSeq = action.Seq;
            lease.RunningSeq = action.Seq;
            if (lease.Action is { IsCompleted: false } running && action.Kind is "attack" or "dig" or "use"
                && lease.HoldRelease is { IsCancellationRequested: false }
                && lease.ActionRequest is { } last && (action with { Seq = last.Seq }) == last) return running;
            try { lease.ActionCancel?.Cancel(); } catch (ObjectDisposedException) { }
            lease.ActionRequest = action;
            var previous = lease.Action;
            var cancel = new CancellationTokenSource();
            var holdRelease = new CancellationTokenSource();
            lease.ActionCancel = cancel;
            lease.HoldRelease = holdRelease;
            var perform = lease.Body.PrepareAction(action);
            // Run off the route lock: a client interaction can wait for a server window or block acknowledgement.
            lease.Action = Task.Run(async () =>
            {
                try
                {
                    if (previous != null) try { await previous.ConfigureAwait(false); } catch { }
                    cancel.Token.ThrowIfCancellationRequested();
                    var result = action.Kind == "cancel" ? ActionResult.Success("action cancelled")
                        : action.Kind == "inventory" ? ActionResult.Success("inventory")
                        : await perform(cancel.Token, holdRelease.Token).ConfigureAwait(false);
                    return new PawnControlActionReply(result.Ok, result.Message, lease.Body.State(), lease.Body.Inventory());
                }
                catch (OperationCanceledException) { return new PawnControlActionReply(false, "action cancelled", lease.Body.State()); }
                catch (Exception ex) { return new PawnControlActionReply(false, ex.Message, lease.Body.State()); }
                finally { cancel.Dispose(); holdRelease.Dispose(); }
            });
            return lease.Action;
        }
    }

    /// <summary>Held digging and item use can outlive an HTTP request. Acknowledge their start promptly;
    /// the input heartbeat reports completion, while the original task remains cancellable and deduplicated.</summary>
    public async Task<PawnControlActionReply> AcknowledgeActionAsync(string body, string owner, string session, PawnControlAction action, CancellationToken ct)
    {
        var running = ActionAsync(body, owner, session, action);
        if (await Task.WhenAny(running, Task.Delay(40, ct)).ConfigureAwait(false) == running)
            return await running.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var lease = Find(body, owner, session);
            return new(true, "action started", lease.Body.State(), Pending: true);
        }
    }

    public PawnControlState Release(string body, string owner, string session)
    {
        lock (_lock)
        {
            var lease = Find(body, owner, session);
            End(lease);
            return lease.Body.State();
        }
    }

    /// <summary>A socket binds an existing lease; opening it never renews idle input or lifetime.
    /// Replacement revokes the old connection before it can submit or release the new connection's work.</summary>
    internal SocketAttachment Attach(string body, string owner, string session)
    {
        lock (_lock)
        {
            var lease = Find(body, owner, session);
            if (lease.Socket is { } previous)
            {
                previous.Closed.Cancel();
                try { lease.ActionCancel?.Cancel(); } catch (ObjectDisposedException) { }
                try { lease.HoldRelease?.Cancel(); } catch (ObjectDisposedException) { }
                var state = lease.Body.State();
                lease.Body.Input(new PawnControlInput(lease.InputSeq, 0, 0, state.Yaw, state.Pitch));
                lease.Stopped = true;
            }
            return lease.Socket = new SocketAttachment(body, owner, session);
        }
    }

    private Lease Attached(SocketAttachment socket)
    {
        var lease = Find(socket.Body, socket.Owner, socket.Session);
        if (!ReferenceEquals(lease.Socket, socket)) throw new PawnControlException(409, "Control socket was replaced.");
        return lease;
    }

    internal PawnControlReply SocketState(SocketAttachment socket)
    {
        lock (_lock) return Reply(Attached(socket));
    }

    internal void SocketInput(SocketAttachment socket, PawnControlInput input)
    {
        lock (_lock) { Attached(socket); Input(socket.Body, socket.Owner, socket.Session, input); }
    }

    internal Task<PawnControlActionReply> SocketActionAsync(SocketAttachment socket, PawnControlAction action, CancellationToken ct)
    {
        lock (_lock)
        {
            Attached(socket);
            // AcknowledgeActionAsync registers the action synchronously before its first await. Thus a following release
            // frame is applied in wire order without waiting for a long dig/use acknowledgement.
            return AcknowledgeActionAsync(socket.Body, socket.Owner, socket.Session, action, ct);
        }
    }

    internal void Detach(SocketAttachment socket)
    {
        lock (_lock)
        {
            var lease = _leases.GetValueOrDefault(socket.Body) ?? _leases.Values.FirstOrDefault(l => l.Aliases.Contains(socket.Body));
            if (lease != null && ReferenceEquals(lease.Socket, socket)) End(lease);
            socket.Closed.Cancel();
        }
    }

    private Lease Find(string body, string owner, string token)
    {
        SweepLocked();
        // Alias resolution was captured on acquire. A heartbeat must never re-enter the colony's roster lock.
        var lease = _leases.GetValueOrDefault(body) ?? _leases.Values.FirstOrDefault(l => l.Aliases.Contains(body));
        if (lease == null || lease.Owner != owner || !SameToken(lease.Token, token)) throw new PawnControlException(409, "Control session expired or belongs to another browser. Take control again.");
        return lease;
    }

    private static bool SameToken(string a, string b) => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
    private PawnControlReply Reply(Lease lease, bool inventory = false) => new(lease.Token, lease.Body.Name, lease.Expires, InputTimeoutMs, LeaseTimeoutMs,
        lease.Body.State(), inventory ? lease.Body.Inventory() : null, ActionStatus(lease));

    private static PawnControlActionStatus? ActionStatus(Lease lease) => lease.Action is not { } action ? null
        : action.IsCompletedSuccessfully ? new(lease.RunningSeq, false, action.Result.Ok, action.Result.Message)
        : new(lease.RunningSeq, !action.IsCompleted, action.IsCompleted ? false : null,
            action.IsFaulted ? "action failed" : action.IsCanceled ? "action cancelled" : null);

    internal void Sweep() { lock (_lock) { if (!_disposed) SweepLocked(); } }
    private void SweepLocked()
    {
        var now = _time.GetUtcNow();
        foreach (var lease in _leases.Values.ToArray())
        {
            try
            {
                if (!lease.Body.Online || now >= lease.Expires) { End(lease); continue; }
                if (lease.Stopped || now - lease.LastInput < TimeSpan.FromMilliseconds(InputTimeoutMs)) continue;
                var state = lease.Body.State();
                lease.Body.Input(new PawnControlInput(lease.InputSeq, 0, 0, state.Yaw, state.Pitch));
                lease.Stopped = true;
                try { lease.ActionCancel?.Cancel(); } catch (ObjectDisposedException) { }
            }
            catch { End(lease); } // A disconnect racing the tick still releases its AI ownership.
        }
    }

    private void End(Lease lease)
    {
        _leases.Remove(lease.Body.Name);
        lease.Socket?.Closed.Cancel();
        try { lease.ActionCancel?.Cancel(); } catch (ObjectDisposedException) { }
        try { lease.Body.End(); } catch { /* an offline transport must not kill the expiry timer */ }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            foreach (var lease in _leases.Values.ToArray()) End(lease);
        }
    }
}
