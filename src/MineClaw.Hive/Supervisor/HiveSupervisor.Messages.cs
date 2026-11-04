using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Telemetry;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Db;
using MineClaw.Hive.Ipc;
using MineClaw.World.Claims;
using Microsoft.Extensions.Logging;

namespace MineClaw.Hive.Supervisor;

/// <summary>
/// What main does with each message a clone sends over its link (<see cref="HandleAsync"/>): one handler per message type, looked
/// up by <see cref="HiveMessage.T"/>. A handler answers a request (the reply object) or returns null; a payload that doesn't parse
/// is ignored (null).
/// </summary>
public sealed partial class HiveSupervisor
{
    private delegate object? CloneMessageHandler(HiveSupervisor hive, CloneSlot slot, HiveMessage m);

    /// <summary>The synchronous handlers; the two that wait (lend go, bus) are in <see cref="HandleAsync"/> itself.</summary>
    private static readonly IReadOnlyDictionary<string, CloneMessageHandler> CloneMessages = new Dictionary<string, CloneMessageHandler>(StringComparer.Ordinal)
    {
        [Msg.Hello] = (h, s, _) => h.OnHello(s),
        [Msg.Heartbeat] = (_, _, _) => null,
        [Msg.Snapshot] = (h, s, m) => h.OnSnapshot(s, m),
        [Msg.Usage] = (_, s, m) => OnUsage(s, m),
        [Msg.Progress] = (h, s, m) => m.As<ProgressMsg>() is { } p ? h.OnProgress(s, p) : null,
        [Msg.Result] = (h, s, m) => m.As<ResultMsg>() is { } r ? h.OnResult(s, r) : null,
        [Msg.Claim] = (h, s, m) => m.As<ClaimMsg>() is { } c ? h.OnClaim(s, c) : null,
        [Msg.Release] = (h, s, m) => m.As<ReleaseMsg>() is { } rl ? h.OnRelease(s, rl) : null,
        [Msg.SpendAuth] = (h, s, m) => m.As<SpendAuthMsg>() is { } sa ? h.OnSpendAuth(s, sa) : null,
        [Msg.Spend] = (h, s, m) => m.As<SpendMsg>() is { } sp ? h.OnSpend(s, sp) : null,
        [Msg.ChatAccepted] = (h, s, m) => m.As<ChatAcceptedMsg>() is { } ca ? h.OnChatAccepted(s, ca) : null,
        [Msg.Thread] = (h, s, m) => m.As<ThreadMsg>() is { } th ? h.OnThread(s, th) : null,
        [Msg.Inventory] = (_, s, m) => m.As<InventoryMsg>() is { } inv ? OnInventory(s, inv) : null,
        [Msg.NeedsInput] = (h, s, m) => m.As<NeedsInputMsg>() is { } ni ? h.OnNeedsInput(s, ni) : null,
        [Msg.VoidHold] = (h, s, m) => m.As<VoidHoldMsg>() is { } vh ? h.OnVoidHold(s, vh) : null,
        [Msg.Death] = (h, s, m) => m.As<DeathMsg>() is { } dm ? h.OnDeath(s, dm) : null,
        [Msg.Handoff] = (h, s, m) => m.As<HandoffMsg>() is not null ? h.OnHandoff(s) : null,
        [Msg.HandoffAck] = (h, s, _) => h.OnHandoffAck(s),
        [Msg.Lend] = (h, s, m) => m.As<LendMsg>() is { } lm ? h.Lend(s, lm) : null,
        [Msg.Occupied] = (h, s, m) => m.As<OccupiedMsg>() is { } oc ? h.OnOccupied(s, oc) : null,
        [Msg.Bye] = (h, s, m) => m.As<ByeMsg>() is { } bye ? h.OnBye(s, bye) : null,
    };

    internal async Task<object?> HandleAsync(CloneSlot slot, int gen, HiveMessage m, CancellationToken ct)
    {
        if (gen != slot.Generation)
        {
            if (m.T == Msg.Hello) throw new InvalidOperationException("superseded launch"); // the stale process exits (no parent)
            return null;
        }
        switch (m.T)
        {
            case Msg.LendGo when m.As<LendGoMsg>() is { } lg:
                return await LendGoAsync(slot, lg, ct).ConfigureAwait(false);
            case Msg.Bus when m.As<Hierarchy.Bus.BusEnvelope>() is { } env:
                return Bus is { } hb ? await hb.RouteAsync(env, slot.Name, ct).ConfigureAwait(false) : Hierarchy.Bus.BusReply.Fail("no bus");
            default:
                return CloneMessages.TryGetValue(m.T, out var handle) ? handle(this, slot, m) : null;
        }
    }

    private WelcomeMsg OnHello(CloneSlot slot)
    {
        ReclaimFor(slot);
        var stranded = Store?.Stranded(slot.Name);
        slot.JoinNoted = false;
        slot.VoidHolding = false;
        slot.GoodSavedAt = default;
        try { slot.FirstLogin = Store != null && !Store.History(slot.Name).Joined; } catch (Exception) { slot.FirstLogin = false; }
        lock (slot.Lock)
        {
            slot.Connected = true;
            if (slot.State == CloneState.Starting) slot.State = CloneState.Running;
        }
        UpdateLive();
        _log.LogInformation("hive: {Clone} connected", slot.Name);
        var busToken = RegisterBus(slot);
        return new WelcomeMsg(_main, slot.Task, Arbiter.ToRoster(), Claims.Mirror(), stranded != null, stranded?.Count ?? 0, busToken);
    }

    /// <summary>The off-channel bus: this clone's endpoint delivers over its own link; its token comes with the welcome (null = no bus).</summary>
    private string? RegisterBus(CloneSlot slot)
    {
        if (Bus is not { } bus || slot.Link is not { } blink) return null;
        return bus.Register(slot.Name, async (env, c) =>
            await blink.RequestAsync<Hierarchy.Bus.BusReply>(Msg.Bus, env, TimeSpan.FromSeconds(5), c).ConfigureAwait(false)
            ?? Hierarchy.Bus.BusReply.Fail("no reply"));
    }

    private object? OnSnapshot(CloneSlot slot, HiveMessage m)
    {
        if (m.D is { ValueKind: JsonValueKind.String } sj && BotSnapshot.FromJson(sj.GetString()!) is { } snap)
        {
            slot.Snapshot = VoxelSendGate.Merge(slot.Snapshot, snap);
            SaveVitals(slot, snap);
            NoteInGame(slot, snap);
        }
        return null;
    }

    private static object? OnUsage(CloneSlot slot, HiveMessage m)
    {
        if (m.D is { ValueKind: JsonValueKind.String } uj && JsonSerializer.Deserialize<PromptUsageRecord>(uj.GetString()!, BotSnapshot.Json) is { } rec)
            slot.RaiseUsage(rec);
        return null;
    }

    private object? OnProgress(CloneSlot slot, ProgressMsg p)
    {
        slot.LastProgress = p.Text;
        if (p.Pct is { } pct) slot.Progress = Math.Clamp(pct, 0, 1);
        Post(GameEvent.Notice($"[hive] {slot.Number}: {Trunc(p.Text, 160)}", EventPriority.Background));
        return null;
    }

    private object? OnResult(CloneSlot slot, ResultMsg r)
    {
        slot.Progress = 1;
        slot.ResultIn = true;
        slot.ResultText = r.Text;
        slot.ResultOk = r.Ok;
        slot.AwaitingOrders = !r.Ok && r.Online;
        // Remember a refusal by (clone, intent) with the body it had, so main doesn't re-send the same job unchanged.
        if (r.Ok) Refusals.Clear(slot.Name, slot.Intent);
        else Refusals.Record(slot.Name, slot.Intent, r.Text, VitalsOf(slot), Night());
        // A failure must reach main's brain even while a script sleeps it; "done" stays a low notice.
        Post(r.Ok ? GameEvent.Notice($"[hive] {slot.Number} done: {Trunc(r.Text, 200)}", EventPriority.Low) : FailureNotice(slot, r));
        return null;
    }

    private GameEvent FailureNotice(CloneSlot slot, ResultMsg r) =>
        GameEvent.Notice($"[hive] {slot.Number} failed: {Trunc(r.Text, 200)}" + (r.Online
            ? $"; still online, awaiting orders ({_cfg.FailureGraceSeconds / 60:0.#} min): reassign it (hive spawn name={slot.Number}) or retry with a fix"
            : ""), EventPriority.Normal) with { MustWake = true };

    private ClaimReplyMsg OnClaim(CloneSlot slot, ClaimMsg c)
    {
        if (!ClaimKey.TryParse(c.Key, out var key)) throw new InvalidOperationException("bad claim key " + c.Key);
        var res = Claims.TryClaim(slot.Name, key, c.TtlSeconds > 0 ? TimeSpan.FromSeconds(c.TtlSeconds) : null, c.Note);
        return new ClaimReplyMsg(res.Ok, res.Holder, res.Expires.ToUnixTimeMilliseconds());
    }

    private object? OnRelease(CloneSlot slot, ReleaseMsg rl)
    {
        if (ClaimKey.TryParse(rl.Key, out var rk)) Claims.Release(slot.Name, rk);
        return null;
    }

    private object OnSpendAuth(CloneSlot slot, SpendAuthMsg sa)
    {
        var g = Ledger.Authorize(slot.Name, sa.EstimateUsd);
        if (!g.Ok) _log.LogInformation("hive: refused spend for {Clone}: {Why}", slot.Name, g.Reason);
        return g.ToMsg();
    }

    private object? OnSpend(CloneSlot slot, SpendMsg sp)
    {
        Ledger.Record(slot.Name, sp.Usd);
        return null;
    }

    private object? OnChatAccepted(CloneSlot slot, ChatAcceptedMsg ca)
    {
        if (ca.Spoke) Arbiter.SpokeFromClone(ca.Player, slot.Name);
        else Arbiter.AcceptFromClone(ca.Player, slot.Name);
        return null;
    }

    private object? OnThread(CloneSlot slot, ThreadMsg th)
    {
        if (th.Open) Arbiter.OpenThread(th.Player, slot.Name, TimeSpan.FromSeconds(Math.Clamp(th.TtlSeconds, 5, 900)));
        else Arbiter.CloseThread(th.Player, slot.Name);
        return null;
    }

    private static object? OnInventory(CloneSlot slot, InventoryMsg inv)
    {
        slot.Inventory = inv.Items;
        return null;
    }

    private object? OnNeedsInput(CloneSlot slot, NeedsInputMsg ni)
    {
        slot.LastProgress = ni.Text;
        _log.LogInformation("hive: {Clone} needs input (online, task paused): {Text}", slot.Name, ni.Text);
        Post(GameEvent.Notice($"[hive] {slot.Number} needs input (still online, items kept): {Trunc(ni.Text, 200)} — give it another " +
            "cell or task (hive spawn name=" + slot.Number + "), or recall it", EventPriority.Normal) with { MustWake = true });
        return null;
    }

    private object? OnVoidHold(CloneSlot slot, VoidHoldMsg vh)
    {
        var at = $"{(int)Math.Floor(vh.X)},{(int)Math.Floor(vh.Y)},{(int)Math.Floor(vh.Z)}";
        if (vh.Held) HoldingOverVoid(slot, vh, at);
        else LandedFromVoid(slot, vh, at);
        return null;
    }

    private void HoldingOverVoid(CloneSlot slot, VoidHoldMsg vh, string at)
    {
        slot.VoidHolding = true;
        try
        {
            Store?.MarkVoidSpawn(slot.Name, vh.X, vh.Y, vh.Z);
            if (slot.FirstLogin) Store?.NoteFreshVoidSpawn(slot.Name, vh.X, vh.Y, vh.Z); // where fresh accounts land
        }
        catch (Exception ex) { _log.LogDebug(ex, "void spawn record failed"); }
        _log.LogWarning("hive: {Clone} is holding still over the void at {At}; it asked for {Tp}", slot.Name, at, vh.Asked);
        // Main relays the ask; it runs the /tp itself only when a trusted player asks for it (op policy).
        Post(GameEvent.Notice($"[hive] {slot.Number} spawned over the void at {at} and is holding still (safe, can't move); it asked " +
            $"the trusted players in chat for {vh.Asked}. Relay that ask once if nobody answered; run the /tp only when a trusted " +
            "player asks you to", EventPriority.Normal) with { MustWake = true });
    }

    private void LandedFromVoid(CloneSlot slot, VoidHoldMsg vh, string at)
    {
        slot.VoidHolding = false;
        try
        {
            Store?.ClearVoidSpawn(slot.Name);
            Store?.SaveGoodPosition(slot.Name, vh.X, vh.Y, vh.Z, slot.Snapshot?.Dimension ?? 0);
            slot.GoodSavedAt = _time.GetUtcNow();
        }
        catch (Exception ex) { _log.LogDebug(ex, "void spawn clear failed"); }
        _log.LogInformation("hive: {Clone} is on ground again at {At}; resuming", slot.Name, at);
        Post(GameEvent.Notice($"[hive] {slot.Number} landed on ground at {at} (tp'd); its task resumes", EventPriority.Low));
    }

    private object? OnDeath(CloneSlot slot, DeathMsg dm)
    {
        if (dm.Cause.StartsWith(CloneHost.IntentionalResetCause, StringComparison.Ordinal))
        {
            // A deliberate void step: the clone respawns and continues; not a death to count, claims stay.
            slot.IntentionalResets++;
            _log.LogInformation("hive: {Clone}: {Cause}", slot.Name, dm.Cause);
            return null;
        }
        slot.Deaths++;
        int n = Claims.ReleaseAll(slot.Name);
        slot.Inventory = [];
        // Main's context and state stay untouched: logged and counted only (the clone records the shared experience).
        _log.LogWarning("hive: {Clone} died ({Cause}); lost {Lost}; {N} claim(s) released", slot.Name, dm.Cause, HiveStore.FormatItems(dm.Lost), n);
        return null;
    }

    private HandoffReplyMsg OnHandoff(CloneSlot slot)
    {
        if (Handoff is not { } h) return new HandoffReplyMsg(false, 0, 0, 0, "main can't take items");
        // Stand still first, then read the feet the clone will toss at (night review B24: main walked on).
        var hold = h.HoldStill(HandoffHoldTime);
        if (h.Position is not { } pos) { hold?.Dispose(); return new HandoffReplyMsg(false, 0, 0, 0, "main can't take items"); }
        lock (slot.Lock) { slot.HandoffHold?.Dispose(); slot.HandoffHold = hold; }
        h.BeginCounting(slot.Name);
        return new HandoffReplyMsg(true, pos.X, pos.Y, pos.Z, null);
    }

    private HandoffAckMsg OnHandoffAck(CloneSlot slot)
    {
        int got = Handoff?.Collected(slot.Name) ?? 0;
        lock (slot.Lock) { slot.HandoffHold?.Dispose(); slot.HandoffHold = null; }
        return new HandoffAckMsg(got);
    }

    private object? OnOccupied(CloneSlot slot, OccupiedMsg oc)
    {
        lock (slot.Lock) slot.ExternalKick = true;
        _log.LogWarning("hive: {Clone} was kicked, someone else logged in with its name ({Reason}; {N} item(s) on it)", slot.Name, oc.Reason, oc.Items);
        return null;
    }

    private object? OnBye(CloneSlot slot, ByeMsg bye)
    {
        lock (slot.Lock) { slot.ByeReceived = true; slot.RecallReason ??= bye.Reason; slot.RefusedCode ??= CloneHost.RefusalCode(bye.Reason); }
        _log.LogInformation("hive: {Clone} bye ({Reason}, deposited {Dep}, stranded {N})", slot.Name, bye.Reason, bye.Deposited, bye.Stranded);
        if (!bye.Deposited && bye.Stranded > 0)
            Post(GameEvent.Notice($"[hive] {slot.Number} logged out holding {bye.Stranded} item(s): no storage space", EventPriority.Low));
        return null;
    }
}
