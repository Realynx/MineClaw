using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MineClaw.Client;

namespace MineClaw.App.Supervisor;

/// <summary>Everything after <c>WindowId</c> describes one game tick (<see cref="HumanControlSample"/>) and is left out when null.
/// A state that is not the sample of a fresh tick carries only <c>Stopped</c> of them, or none (a body that does not report ticks).
/// <c>Armor</c> (points, 0..20) and <c>Air</c> (ticks, 300 = full) are for the HUD and travel with or without a tick.</summary>
public sealed record PawnControlState(double X, double Y, double Z, float Yaw, float Pitch, int Dim, int Hp, int Food, int HeldSlot, bool OnGround, int WindowId = 0,
    long? Tick = null, long? AckSeq = null, double? AckWaitMs = null, double? Vx = null, double? Vy = null, double? Vz = null,
    int? Teleports = null, long? Impulses = null, bool? UsingItem = null, bool? Stopped = null, PawnControlEffects? Effects = null,
    PawnControlDig? Dig = null, PawnControlAim? Aim = null, PawnControlBroke? Broke = null, int? Armor = null, int? Air = null);
/// <summary>Potion amplifiers that change how the pawn moves: 0 = level I, -1 = not active.</summary>
public sealed record PawnControlEffects(int Speed, int Slowness, int JumpBoost);
public sealed record PawnControlDig(int X, int Y, int Z, int Face, double Progress, int Id, int Meta);
/// <summary>The block under the pawn's crosshair. <see cref="Nothing"/> (no block within reach) travels as an explicit null; a
/// null member means "not reported" and is left out like every other null.</summary>
[JsonConverter(typeof(PawnControlAimJson))]
public sealed record PawnControlAim(int X, int Y, int Z, int Face)
{
    public static readonly PawnControlAim Nothing = new(0, 0, 0, -1);
}
public sealed record PawnControlBroke(long Count, int X, int Y, int Z, int Id, int Meta);
public sealed record PawnControlItem(int Id, int Meta, int Count);
public sealed record PawnControlSlot(int Slot, int Id, int Meta, int Count);
/// <param name="Version">The body's inventory version, read before the snapshot (so the slots are at least this new); the browser keeps
/// the inventory with the higher version whichever way it came. Null when the body does not count its changes.</param>
public sealed record PawnControlInventory(int WindowId, string Kind, string Title, int SlotCount, int ContainerSlots, int MainStart, int HotbarStart,
    IReadOnlyList<PawnControlSlot> Slots, PawnControlItem? Cursor, long? Version = null);
public sealed record PawnControlInput(long Seq, float Forward, float Strafe, float Yaw, float Pitch, bool Jump = false, bool Sneak = false, bool Sprint = false, int? Hotbar = null);
public sealed record PawnControlTarget(int X, int Y, int Z);
/// <param name="Repeat">The browser re-sends a held <c>attack</c> every 250 ms with this set: a dig carries on, a mob is not hit again.</param>
public sealed record PawnControlAction(long Seq, string Kind, PawnControlTarget? Target = null, int? EntityId = null, int? Face = null,
    int? Slot = null, int? Button = null, bool Shift = false, bool WholeStack = false, int? WindowId = null, bool Repeat = false);
/// <param name="Reach">How far the pawn's dig and use ray reaches, in blocks.</param>
public sealed record PawnControlReply(string Session, string Body, DateTimeOffset ExpiresAt, int InputTimeoutMs, int LeaseTimeoutMs,
    PawnControlState State, PawnControlInventory? Inventory = null, PawnControlActionStatus? Action = null, double? Reach = null);
/// <param name="Code">How the action ended, for the browser to act on without reading <paramref name="Message"/>
/// (<see cref="PawnControlSessions.ActionCode"/>).</param>
public sealed record PawnControlActionStatus(long Seq, bool Pending, bool? Ok = null, string? Message = null, string? Code = null);
public sealed record PawnControlActionReply(bool Ok, string Message, PawnControlState State, PawnControlInventory? Inventory = null, bool Pending = false,
    string? Code = null);

/// <summary>Writes <see cref="PawnControlAim.Nothing"/> as null, which the serializer would otherwise drop with the other nulls.</summary>
internal sealed class PawnControlAimJson : JsonConverter<PawnControlAim>
{
    public override bool HandleNull => true;

    public override PawnControlAim Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return PawnControlAim.Nothing;
        using var doc = JsonDocument.ParseValue(ref reader);
        var o = doc.RootElement;
        return new(o.GetProperty("x").GetInt32(), o.GetProperty("y").GetInt32(), o.GetProperty("z").GetInt32(), o.GetProperty("face").GetInt32());
    }

    public override void Write(Utf8JsonWriter writer, PawnControlAim value, JsonSerializerOptions options)
    {
        if (value is null || ReferenceEquals(value, PawnControlAim.Nothing)) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteNumber("z", value.Z);
        writer.WriteNumber("face", value.Face);
        writer.WriteEndObject();
    }
}

/// <summary>A live pawn adapter; tests use a body with no game connection.</summary>
public interface IPawnControlBody
{
    string Name { get; }
    bool Online { get; }
    PawnControlState State();
    PawnControlInventory Inventory();
    /// <summary>How far the pawn's dig and use ray reaches, in blocks; null when the body does not say.</summary>
    double? Reach => null;
    void Begin();
    void Input(PawnControlInput input);
    /// <summary>The session halts the pawn itself (its browser went quiet, or its socket was replaced): every key up, the look kept.
    /// <paramref name="seq"/> is the last input sequence the session accepted.</summary>
    void Stop(long seq)
    {
        var state = State();
        Input(new PawnControlInput(seq, 0, 0, state.Yaw, state.Pitch));
    }
    Task<ActionResult> ActionAsync(PawnControlAction action, CancellationToken ct, CancellationToken holdRelease = default);
    Func<CancellationToken, CancellationToken, Task<ActionResult>> PrepareAction(PawnControlAction action) =>
        (ct, holdRelease) => ActionAsync(action, ct, holdRelease);
    void End();
    /// <summary>Completes once the body has a state newer than <paramref name="afterTick"/> (its next game tick), or after about
    /// <paramref name="max"/> when no tick comes. A body that does not report ticks waits a tick's length.</summary>
    Task NextStateAsync(long afterTick, TimeSpan max, CancellationToken ct) => Task.Delay(50, ct);
    /// <summary>The pawn is dead: when it is no longer <see cref="Online"/>, this tells a death from a disconnect.</summary>
    bool Dead => false;
    /// <summary>Grows whenever the inventory changes; null when the body does not say, and then no state frame carries it.</summary>
    long? InventoryVersion => null;
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
        /// <summary>Why the server ended the takeover (<see cref="EndReasons"/>), set before <see cref="Closed"/> is cancelled;
        /// null when the browser ended it or the socket simply went away.</summary>
        internal volatile string? EndReason;
        /// <summary>The inventory version the last state frame on this socket carried; null = none sent yet.</summary>
        internal long? InventoryVersion;
    }

    /// <summary>The contract's PAWN_CONTROL_END_REASONS: the pawn died, the lease ran out, another connection took the session,
    /// the pawn left the game or the colony.</summary>
    internal static class EndReasons
    {
        public const string Died = "died", Expired = "expired", Replaced = "replaced", Offline = "offline";
    }

    /// <summary>The closing error frame's message for an end reason; the browser shows it as is.</summary>
    internal static string EndMessage(string reason) => reason switch
    {
        EndReasons.Died => "Your pawn died. Take control again once it has respawned.",
        EndReasons.Expired => "Control session expired. Take control again.",
        EndReasons.Replaced => "Control socket was replaced.",
        _ => "The pawn is offline.",
    };

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
            if (action.Kind == "inventory") return Task.FromResult(new PawnControlActionReply(true, "inventory", lease.Body.State(), Inventory(lease.Body)));
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
                // A repeat of the click is the same click: it must not cancel and restart the dig it continues.
                && lease.ActionRequest is { } last && (action with { Seq = last.Seq, Repeat = last.Repeat }) == last) return running;
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
                    return Ended(result.Ok, result.Message, lease.Body.State(), Inventory(lease.Body));
                }
                catch (OperationCanceledException) { return Ended(false, MinecraftClient.HumanActionCancelledMessage, lease.Body.State()); }
                catch (Exception ex) { return Ended(false, ex.Message, lease.Body.State()); }
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
                previous.EndReason ??= EndReasons.Replaced;
                previous.Closed.Cancel();
                try { lease.ActionCancel?.Cancel(); } catch (ObjectDisposedException) { }
                try { lease.HoldRelease?.Cancel(); } catch (ObjectDisposedException) { }
                lease.Body.Stop(lease.InputSeq);
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

    /// <summary>The state frame. It carries the inventory whenever its version changed since this socket's last frame, in the same
    /// frame (never a second one): 1 to 2 KB, so not on every tick.</summary>
    internal PawnControlReply SocketState(SocketAttachment socket)
    {
        lock (_lock)
        {
            var lease = Attached(socket);
            // Read before the snapshot: a change in between is sent again with the next frame, never lost.
            var version = lease.Body.InventoryVersion;
            bool inventory = version is { } v && v != socket.InventoryVersion;
            var reply = Reply(lease, inventory);
            if (inventory) socket.InventoryVersion = reply.Inventory?.Version ?? version;
            return reply;
        }
    }

    /// <summary>Waits for the attached body's next tick, at most <paramref name="max"/>. The body is fetched under the lock that every
    /// pawn's inputs and the sweep share, and awaited outside it.</summary>
    internal async Task NextStateAsync(SocketAttachment socket, long afterTick, TimeSpan max, CancellationToken ct)
    {
        IPawnControlBody body;
        lock (_lock) body = Attached(socket).Body;
        try { await body.NextStateAsync(afterTick, max, ct).WaitAsync(max, ct).ConfigureAwait(false); }
        catch (TimeoutException) { } // no tick (riding, dead, frozen): the state still flows
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
        lease.Body.State(), inventory ? Inventory(lease.Body) : null, ActionStatus(lease), lease.Body.Reach);

    /// <summary>The body's inventory labelled with its version, read first: a change during the snapshot only makes the slots newer
    /// than the label, never older, so a later, higher label always reaches the browser.</summary>
    private static PawnControlInventory Inventory(IPawnControlBody body)
    {
        var version = body.InventoryVersion;
        var inventory = body.Inventory();
        return version is null ? inventory : inventory with { Version = version };
    }

    private static PawnControlActionStatus? ActionStatus(Lease lease) => lease.Action is not { } action ? null
        : action.IsCompletedSuccessfully ? new(lease.RunningSeq, false, action.Result.Ok, action.Result.Message, action.Result.Code)
        : new(lease.RunningSeq, !action.IsCompleted, action.IsCompleted ? false : null,
            action.IsFaulted ? "action failed" : action.IsCanceled ? "action cancelled" : null);

    /// <summary>The one place an action's ending becomes a code (the contract's PAWN_CONTROL_ACTION_CODES): from the client's named
    /// messages, so its results stay plain text. <c>dug</c> = the block broke; <c>placed</c> = the game server filled the cell a held
    /// block was placed into; <c>aim_changed</c> = a dig stopped because the aim,
    /// the held item or the input changed; <c>cancelled</c> = a newer action or a release replaced it.</summary>
    internal static string? ActionCode(bool ok, string? message) => (ok, message) switch
    {
        (true, MinecraftClient.HumanDugMessage) => "dug",
        (true, MinecraftClient.HumanPlacedMessage) => "placed",
        (false, MinecraftClient.HumanDigStoppedMessage) => "aim_changed",
        (false, MinecraftClient.HumanActionCancelledMessage) => "cancelled",
        _ => null,
    };

    private static PawnControlActionReply Ended(bool ok, string message, PawnControlState state, PawnControlInventory? inventory = null) =>
        new(ok, message, state, inventory, Code: ActionCode(ok, message));

    internal void Sweep() { lock (_lock) { if (!_disposed) SweepLocked(); } }
    private void SweepLocked()
    {
        var now = _time.GetUtcNow();
        foreach (var lease in _leases.Values.ToArray())
        {
            try
            {
                if (!lease.Body.Online) { End(lease, lease.Body.Dead ? EndReasons.Died : EndReasons.Offline); continue; }
                if (now >= lease.Expires) { End(lease, EndReasons.Expired); continue; }
                if (lease.Stopped || now - lease.LastInput < TimeSpan.FromMilliseconds(InputTimeoutMs)) continue;
                lease.Body.Stop(lease.InputSeq);
                lease.Stopped = true;
                try { lease.ActionCancel?.Cancel(); } catch (ObjectDisposedException) { }
            }
            catch { End(lease, EndReasons.Offline); } // A disconnect racing the tick still releases its AI ownership.
        }
    }

    /// <param name="reason">Why the server ends it (<see cref="EndReasons"/>); null when the browser asked or its socket left.</param>
    private void End(Lease lease, string? reason = null)
    {
        _leases.Remove(lease.Body.Name);
        if (reason != null && lease.Socket is { } socket) socket.EndReason ??= reason;
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
