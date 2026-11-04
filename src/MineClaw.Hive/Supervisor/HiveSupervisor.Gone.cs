using MineClaw.Agent.Events;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;
using Microsoft.Extensions.Logging;

namespace MineClaw.Hive.Supervisor;

/// <summary>
/// A clone's process or link is gone (<see cref="OnGone"/>): under the slot's lock it is detached (holds, link, process, claims to
/// resume) and its next state settled (someone else's login, a refused login, a clean logout or recall, a config error, a crash
/// with backoff); then, outside the lock, its link is closed and its process stopped, its leases go, main hears what happened,
/// and a queued task or a restart follows.
/// </summary>
public sealed partial class HiveSupervisor
{
    /// <summary>What <see cref="OnGone"/> decided under the lock, for the steps after it.</summary>
    private sealed class GoneOutcome
    {
        public required string Reason;
        public int? ExitCode;
        public bool External, Refused, Restart;
        public TimeSpan Backoff;
        public (TaskMsg Task, string? ForPlayer, bool Unnamed)? Respawn;
        public TaskCompletionSource? Gone;
        public List<ItemMsg>? Inventory;
        /// <summary>The detached link and process, closed and stopped once the slot's state has settled (outside its lock).</summary>
        public HiveLink? Link;
        public ICloneProcess? Process;
    }

    private void OnGone(CloneSlot slot, int gen, bool clean, string reason, int? exitCode = null)
    {
        GoneOutcome o;
        lock (slot.Lock)
        {
            if (gen != slot.Generation || slot.State is CloneState.Idle or CloneState.Retired or CloneState.Backoff) return; // already handled
            o = DetachLocked(slot, reason, exitCode);
            SettleLocked(slot, o, clean);
        }
        // After the state settled and outside the lock: disposing the link raises its Closed (this method again), and a kill can
        // run the process's continuations inline. Under the (re-entrant) lock, before the state settled, that nested call passed
        // the check above, settled and relaunched the slot; this call then killed the NEW process and set the slot Idle (the
        // "recall + new task in one turn" relaunch that never came back).
        CloseLinkAndStopProcess(o);
        // Immediately: the dead clone's leases, grants and conversations go.
        int released = Claims.ReleaseAll(slot.Name);
        Ledger.Close(slot.Name);
        Arbiter.Forget(slot.Name);
        UpdateLive();
        NotifyGone(slot, o, clean, released);
        AfterGone(slot, o);
    }

    // caller holds slot.Lock
    private GoneOutcome DetachLocked(CloneSlot slot, string reason, int? exitCode)
    {
        exitCode = slot.RefusedCode ?? exitCode;
        var o = new GoneOutcome
        {
            Reason = reason, ExitCode = exitCode, Refused = exitCode is CloneExitCodes.NotWhitelisted or CloneExitCodes.ServerFull,
            Gone = slot.Gone, Inventory = slot.Inventory,
        };
        slot.RefusedCode = null;
        slot.HandoffHold?.Dispose();
        slot.HandoffHold = null;
        slot.LendHold?.Dispose();
        slot.LendHold = null;
        slot.PendingLend = null;
        slot.Connected = false;
        slot.LastExit = reason;
        o.Link = slot.Link;
        slot.Link = null;
        o.External = slot.ExternalKick || exitCode == CloneExitCodes.ExternalLogin;
        o.Process = slot.Process;
        slot.Process = null;
        slot.ResumeClaims = Claims.Leases().Where(l => l.Owner.Equals(slot.Name, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Key.Id).ToArray();
        slot.ExternalKick = false;
        slot.GoneAt = Now;
        return o;
    }

    private static void CloseLinkAndStopProcess(GoneOutcome o)
    {
        if (o.Link is { } link) _ = link.DisposeAsync().AsTask();
        StopProcess(o.Process, o.External);
    }

    private static void StopProcess(ICloneProcess? proc, bool external)
    {
        if (external && proc is { HasExited: false })
            // The kicked clone is already offline and exits by itself: give it a moment, then make sure.
            _ = Task.Run(async () =>
            {
                try { await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).ConfigureAwait(false); }
                catch (Exception) { }
                proc.Kill();
                proc.Dispose();
            });
        else
        {
            proc?.Kill();
            proc?.Dispose();
        }
    }

    // caller holds slot.Lock
    private void SettleLocked(CloneSlot slot, GoneOutcome o, bool clean)
    {
        if (o.External)
        {
            // Someone else logged in as this clone (a player's viewer): never fight the login, no crash, no restart.
            slot.State = CloneState.Idle;
            slot.ExternalUntil = Now + TimeSpan.FromMinutes(Math.Max(0, _cfg.ExternalLoginCooldownMinutes));
            slot.RespawnWith = null;
        }
        else if (o.Refused)
        {
            // The server refused the login (not whitelisted / full): reconnecting can't fix it. Yield the slot: no crash,
            // no restart, no queued relaunch; a later spawn tries again.
            slot.State = CloneState.Idle;
            slot.RespawnWith = null;
            o.Reason = o.ExitCode == CloneExitCodes.NotWhitelisted ? "the server refused the login: not whitelisted" : "the server refused the login: server full";
        }
        else if (clean || slot.Recalled)
        {
            slot.State = CloneState.Idle;
            o.Respawn = _shuttingDown ? null : slot.RespawnWith;
            slot.RespawnWith = null;
        }
        else if (o.ExitCode == ConfigErrorExitCode)
        {
            // A config error (exit 2) was retried 3x as a "crash" (10-27 live2): fail fast instead.
            slot.State = CloneState.Retired;
            o.Reason += ": configuration error, not restarted (fix the config)";
        }
        else CrashedLocked(slot, o);
    }

    // caller holds slot.Lock: retired after too many crashes in the window; else restarted with backoff while its task is unfinished.
    private void CrashedLocked(CloneSlot slot, GoneOutcome o)
    {
        var now = Now;
        slot.Crashes.Add(now);
        slot.Crashes.RemoveAll(t => now - t > TimeSpan.FromMinutes(_cfg.CrashWindowMinutes));
        if (slot.Crashes.Count >= _cfg.MaxCrashes) slot.State = CloneState.Retired;
        else if (slot.Task != null && !slot.ResultIn && !_shuttingDown) // its last task is unfinished (and not cancelled)
        {
            slot.State = CloneState.Backoff;
            o.Restart = true;
            o.Backoff = TimeSpan.FromSeconds(Math.Min(_cfg.BackoffMaxSeconds, _cfg.BackoffBaseSeconds * Math.Pow(2, slot.Crashes.Count - 1)));
        }
        else slot.State = CloneState.Idle;
    }

    private void NotifyGone(CloneSlot slot, GoneOutcome o, bool clean, int released)
    {
        if (o.External) NotifyExternalLogin(slot, o, released);
        else if (o.Refused) NotifyRefusedLogin(slot, o, released);
        else if (clean && !(slot.Recalled && !slot.ByeReceived))
            Post(GameEvent.Notice($"[hive] {slot.Number} logged out ({slot.RecallReason ?? o.Reason})", EventPriority.Background));
        else NotifyLost(slot, o, released);
    }

    /// <summary>Records the items left on the gone clone; how many (0 when none or the store failed).</summary>
    private int MarkStranded(CloneSlot slot, string why, List<ItemMsg>? inv)
    {
        try { return Store?.MarkStranded(slot.Name, why, inv) ?? 0; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "hive: couldn't record {Clone}'s stranded inventory", slot.Name);
            return 0;
        }
    }

    private void NotifyExternalLogin(CloneSlot slot, GoneOutcome o, int released)
    {
        int stranded = MarkStranded(slot, "kicked: someone else logged in as " + slot.Name, o.Inventory);
        var unfinished = slot.Task is { } ut && !slot.ResultIn ? $"; its task \"{Trunc(ut.Text, 60)}\" is unfinished: give it to another clone" : "";
        Post(GameEvent.Notice($"[hive] slot {slot.Number} is in use by a player's viewer: someone logged in as {slot.Name} and the server kicked ours; " +
            $"not using that slot for {_cfg.ExternalLoginCooldownMinutes:0} min ({released} claim(s) released" + (stranded > 0 ? $", {stranded} item(s) still on it" : "") + ")" + unfinished,
            EventPriority.Normal) with { MustWake = unfinished.Length > 0 });
    }

    private void NotifyRefusedLogin(CloneSlot slot, GoneOutcome o, int released)
    {
        var unfinished = slot.Task is { } ut && !slot.ResultIn ? $"; its task \"{Trunc(ut.Text, 60)}\" is unfinished" : "";
        Post(GameEvent.Notice($"[hive] {slot.Number} couldn't log in: {o.Reason} (not retrying by itself; {released} claim(s) released){unfinished}",
            EventPriority.Normal) with { MustWake = unfinished.Length > 0 });
    }

    private void NotifyLost(CloneSlot slot, GoneOutcome o, int released)
    {
        int stranded = MarkStranded(slot, o.Reason, o.Inventory);
        Post(GameEvent.Notice($"[hive] {slot.Number} lost ({o.Reason}): {released} claim(s) released" +
            (stranded > 0 ? $", {stranded} item(s) stranded" : "") + LostWhat(slot, o),
            slot.State == CloneState.Retired ? EventPriority.Low : EventPriority.Background));
    }

    /// <summary>What happens next to a lost clone: retired (config error or too many crashes), restarting after the backoff, or nothing.</summary>
    private static string LostWhat(CloneSlot slot, GoneOutcome o)
    {
        if (slot.State == CloneState.Retired) return o.ExitCode == ConfigErrorExitCode ? "; retired" : $"; retired after {slot.Crashes.Count} crashes";
        return o.Restart ? $"; restarting in {o.Backoff.TotalSeconds:0}s" : "";
    }

    /// <summary>The slot is free (nothing queued, no restart), the waiters hear it's gone, then the restart or the queued task.</summary>
    private void AfterGone(CloneSlot slot, GoneOutcome o)
    {
        if (!o.Restart && o.Respawn == null && slot.State is CloneState.Idle or CloneState.Retired) FinallyReleased(slot, o.Reason);
        o.Gone?.TrySetResult();
        if (o.Restart) _ = RestartAfterAsync(slot, o.Backoff);
        if (o.Respawn is { } rw) Relaunch(slot, rw.Task, rw.ForPlayer, rw.Unnamed);
    }
}
