using MineClaw.Hive.Clone;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;
using Microsoft.Extensions.Logging;

namespace MineClaw.Hive.Supervisor;

/// <summary>
/// The steps of <see cref="SpawnCore"/>: the job (task text, profile, script), the clone (named or the best free fit, a fresh
/// account when none is free), the conversation-ownership check, then the assignment under the slot's lock by its state (a
/// recalled clone queues it, a running one takes it over, an idle one is reserved and launched).
/// </summary>
public sealed partial class HiveSupervisor
{
    /// <summary>What a spawn runs: the task text (trimmed, at most 400 chars), its model profile, a saved script and its code.</summary>
    private sealed record SpawnJob(string Task, string? Profile, string? Script, string? Code)
    {
        /// <summary>The text the intent and the loadout are read from (the task plus the script's code).</summary>
        public string Text => Code != null ? Task + " " + Code : Task;
    }

    /// <summary>The outcome of assigning under the slot's lock: a refusal, or what happens next.</summary>
    private readonly record struct Assignment(string? Refusal, bool Launch = false, bool RespawnQueued = false, TaskMsg? Displaced = null, string? DisplacedFor = null);

    private (SpawnJob? Job, string? Error) PrepareSpawn(string task, HiveSpawnOptions? opts)
    {
        task = (task ?? "").Trim();
        var (profile, profileErr) = ProfileOf(opts);
        if (profileErr != null) return (null, profileErr);
        string? script = null, code = null;
        if (opts is { HasScript: true })
        {
            var prepped = PrepScript(opts, task);
            if (prepped.Error != null) return (null, prepped.Error);
            (task, script, code) = (prepped.Task, prepped.Script, prepped.Code);
        }
        if (task.Length == 0) return (null, "error: task required");
        if (task.Length > 400) task = task[..400];
        if (Ledger.AtRecallLine(out var why)) return (null, "no: budget (" + why + ")");
        return (new SpawnJob(task, profile, script, code), null);
    }

    private (string? Profile, string? Error) ProfileOf(HiveSpawnOptions? opts)
    {
        string? profile = string.IsNullOrWhiteSpace(opts?.Profile) ? null : opts!.Profile!.Trim();
        if (profile != null && (ProfileCheck is not { } check ? "no model profiles here" : check(profile)) is { } badProfile)
            return (null, "error: " + badProfile);
        return (profile, null);
    }

    /// <summary>Saves the script (lint findings go back to the brain; nothing spawns); an empty task becomes "script NAME args".</summary>
    private (string Task, string? Script, string? Code, string? Error) PrepScript(HiveSpawnOptions opts, string task)
    {
        if (ScriptPrep is not { } prep) return (task, null, null, "error: scripts can't be spawned here");
        var (saved, err, c) = prep(opts.Script, opts.Code);
        if (err != null) return (task, null, null, "error: " + err);
        if (task.Length == 0) task = $"script {saved}" + (opts.Args is { Length: > 2 } a ? " " + a : "");
        return (task, saved, c, null);
    }

    /// <summary>A named clone: refused when it already refused this intent with nothing changed, would log in over the void
    /// unconfirmed, or someone else is logged in as it; a warning note when it doesn't fit.</summary>
    private (CloneSlot? Slot, string? FitNote, string? Refusal) PickNamed(string name, bool handover, TaskIntent intent, HiveSpawnOptions? opts)
    {
        var r = Resolve(name);
        if (r == null) return (null, null, $"error: no clone '{name}' (have {string.Join(", ", _slots.Values.Select(s => s.Number))})");
        var slot = _slots[r];
        // The same intent this clone refused, with nothing changed since: don't send it again (main re-sent "fight" 3x).
        if (!handover && Refusals.Blocking(slot.Name, intent, VitalsOf(slot), Night()) is { } refused) return (null, null, RefusedAgain(slot, intent, refused));
        string? fitNote = null;
        if (CloneFitness.Score(slot.Number, VitalsOf(slot), intent, slot.IsLive) is { Blocker: { } b })
            fitNote = $" (warning: {slot.Number} {b})";
        if (opts?.ConfirmVoid != true && !VoidConfirmed(slot) && VoidSpawnOf(slot) != null) return (null, null, "no: " + VoidRefusal(slot));
        if (ExternalOccupant(slot) is { } occupied)
        {
            var (alt, _) = PickFor(intent, except: slot);
            return (null, null, $"no: {slot.Number} {occupied}" + (alt != null ? $"; {alt.Number} is free: give it to {alt.Number}" : ""));
        }
        return (slot, fitNote, null);
    }

    private string RefusedAgain(CloneSlot slot, TaskIntent intent, string refused)
    {
        var (alt, _) = PickFor(intent, except: slot);
        return $"no: not reassigned: {slot.Number} already refused {CloneFitness.Verb(intent)}: {refused}; nothing changed since (hp/food/inventory, day/night). " +
            (alt != null ? $"{alt.Number} fits ({CloneFitness.Score(alt.Number, VitalsOf(alt), intent, alt.IsLive).Summary}): give it to {alt.Number}"
                : "Tell the player what it needs") + "; it reruns if a player says try again or brings what was missing";
    }

    /// <summary>Unnamed: the best fit among free clones, not the first idle slot (live 10-27 15:44: 67 at hp 7/food 0 got "fight").
    /// No fixed roster: with no free slot at all, main allocates a new account (player cap and Hive:MaxClones permitting).
    /// A null slot with no refusal = none free (refused later, after the conversation check).</summary>
    private (CloneSlot? Slot, string? FitNote, string? Refusal) PickUnnamed(TaskIntent intent, HiveSpawnOptions? opts)
    {
        var (best, noFit) = PickFor(intent, confirmVoid: opts?.ConfirmVoid == true);
        if (best == null && Accounts is { } accounts && !_slots.Values.Any(Free))
        {
            var (fresh, allocWhy) = accounts.Allocate(opts);
            if (fresh == null) return (null, null, "no: " + (noFit ?? allocWhy));
            AddSlot(fresh);
            (best, noFit) = PickFor(intent, confirmVoid: opts?.ConfirmVoid == true);
        }
        if (best == null && noFit != null) return (null, null, "no: " + noFit);
        return (best, null, null);
    }

    /// <summary>The clone owning the player's request and the one in their conversation (asking = holding their thread lease).</summary>
    private (CloneSlot? Owner, CloneSlot? Talking, bool Asking) PlayerSlots(string? forPlayer)
    {
        if (forPlayer == null) return (null, null, false);
        var owner = OwnerSlot(forPlayer);
        var talking = ConversationSlot(forPlayer, out var asking);
        return (owner, talking, asking);
    }

    // Ownership covers a player's CONVERSATION about a request, not every task done for them (10-27: "67 already owns
    // PoofimaFox's request" refused a tree clone, both spar clones, and made main recall 67 mid-job). Main may add clones
    // to the same player's new or parallel request (a spar needs two). Refused without handover: an UNNAMED spawn of the
    // very request the clone holding the player's conversation (thread lease or sticky) is on, and any spawn of it while
    // that clone is asking the player about it right now (thread lease: its answer is coming).
    private static string? ConversationRefusal(bool handover, CloneSlot? talking, bool asking, CloneSlot? slot, string? name, string? forPlayer, string task)
    {
        if (handover || talking == null || ReferenceEquals(talking, slot) || talking.Task is not { } talkTask || !SameRequest(talkTask.Text, task)) return null;
        if (asking)
            return $"no: {talking.Number} is asking {forPlayer} about this request right now; let it finish, or op handover to move it to {slot?.Number ?? name ?? "another clone"}";
        if (name == null)
            return $"no: {talking.Number} is talking with {forPlayer} about this request; give it to {talking.Number} (name {talking.Number}), name another clone to add a helper, or op handover";
        return null;
    }

    /// <summary>With handover, the clone that held the player's request (its owner, else the one in their conversation) unless it's this one.</summary>
    private static CloneSlot? PreviousOwner(bool handover, CloneSlot? owner, CloneSlot? talking, CloneSlot slot)
    {
        if (!handover) return null;
        owner ??= talking;
        return owner != null && !ReferenceEquals(owner, slot) ? owner : null;
    }

    private static TaskMsg NewTask(SpawnJob job, HiveSpawnOptions? opts) =>
        new(Guid.NewGuid().ToString("N")[..8], job.Task, Loadout: TaskLoadout.For(job.Text),
            Profile: job.Profile, Script: job.Script, Args: job.Script != null || opts?.Crew != null ? opts?.Args : null, After: job.Script != null ? opts?.After : null,
            Kind: opts?.Kind, Bodies: opts?.Bodies is > 0 ? opts.Bodies : null, Isolation: opts?.Isolation, Crew: opts?.Bodies is > 0 ? opts.Crew : null);

    /// <summary>Logs the task (hive task texts were never logged; 10-27: the ownership refusals couldn't be traced) and resets the
    /// clone's hive order.</summary>
    private void AnnounceTask(CloneSlot slot, TaskMsg t, string? forPlayer, string? name, string task)
    {
        _log.LogInformation("hive: task {Id} for {Clone} ({Player}{Named}): {Text}", t.Id, slot.Name, forPlayer ?? "-", name == null ? ", unnamed" : "", task);
        try { ResetClone?.Invoke(slot.Name); } catch (Exception ex) { _log.LogWarning(ex, "hive: reset of {Clone}'s hive order failed", slot.Name); }
    }

    /// <summary>Assigns the task under the slot's lock, by its state.</summary>
    private Assignment AssignLocked(CloneSlot slot, TaskMsg t, string? forPlayer, string? name, TaskIntent intent, string task)
    {
        lock (slot.Lock)
        {
            switch (slot.State)
            {
                case CloneState.Retired: return new Assignment($"no: {slot.Number} is retired ({slot.Crashes.Count} crashes)");
                case CloneState.Stopping: return QueueRespawn(slot, t, forPlayer, name, intent);
                case CloneState.Running or CloneState.Starting or CloneState.Backoff: return TakeOver(slot, t, forPlayer, name, intent, task);
                default: return ReserveFresh(slot, t, forPlayer, name, intent);
            }
        }
    }

    // Recalled (e.g. "recall 69" and "69: craft picks" in one turn, either order): the task waits for the logout
    // and the clone logs straight back in with it, instead of "got the new task" being lost (10-27 07:19).
    private Assignment QueueRespawn(CloneSlot slot, TaskMsg t, string? forPlayer, string? name, TaskIntent intent)
    {
        if (!slot.BrainRecall || _shuttingDown) return new Assignment($"no: {slot.Number} is logging out");
        slot.RespawnWith = (t, forPlayer, name == null);
        slot.AssignedTurn = MineClaw.Agent.TurnContext.Current;
        slot.Intent = intent;
        return new Assignment(null, RespawnQueued: true);
    }

    private Assignment TakeOver(CloneSlot slot, TaskMsg t, string? forPlayer, string? name, TaskIntent intent, string task)
    {
        // Named into a clone the supervisor just picked for ANOTHER unnamed job (same turn, parallel tool calls):
        // that job moves to a free clone instead of being silently dropped.
        TaskMsg? displaced = null;
        string? displacedFor = null;
        if (name != null && slot.Unnamed && slot.Pending && slot.Task is { } old && !SameRequest(old.Text, task)
            && Now - slot.AssignedAt < TimeSpan.FromMinutes(2))
        {
            displaced = old;
            displacedFor = slot.ForPlayer;
        }
        slot.Task = t;
        slot.Intent = intent;
        slot.ResultIn = false;
        slot.AwaitingOrders = false;
        slot.ResultText = null;
        slot.ForPlayer = forPlayer;
        slot.Unnamed = name == null;
        slot.AssignedAt = Now;
        slot.AssignedTurn = MineClaw.Agent.TurnContext.Current;
        slot.Progress = 0;
        slot.LastProgress = null;
        if (slot.Link is { IsOpen: true } l) _ = l.SendAsync(Msg.Task, t);
        return new Assignment(null, Displaced: displaced, DisplacedFor: displacedFor);
    }

    private Assignment ReserveFresh(CloneSlot slot, TaskMsg t, string? forPlayer, string? name, TaskIntent intent)
    {
        var facts = SlotFacts();
        int pending = _slots.Values.Count(s => s.State is CloneState.Starting && !facts.Online.Contains(s.Name, StringComparer.OrdinalIgnoreCase));
        var d = SlotPolicy.CanSpawn(facts, Regulars, pending, _cfg.MaxPlayersFallback);
        if (!d.Ok) return new Assignment("no: " + d.Reason);
        Reserve(slot, t, forPlayer, name == null);
        slot.Intent = intent;
        return new Assignment(null, Launch: true);
    }

    /// <summary>After the lock: launch a reserved clone, or report the queued respawn / the takeover (moving a displaced job on).</summary>
    private string FinishSpawn(CloneSlot slot, Assignment a, CloneSlot? previous, string? forPlayer, string task, string? fitNote)
    {
        if (a.RespawnQueued)
            return $"ok: {slot.Number} is logging out first (recalled); it logs straight back in with this task (pending until its result arrives)";
        if (a.Launch)
        {
            Launch(slot);
            AfterAssign(slot, previous, forPlayer, task);
            NotifyGroup(slot, forPlayer, task);
            return $"ok: {slot.Number} spawning (its part is pending until its result arrives){fitNote}";
        }
        string moved = a.Displaced != null ? MoveDisplaced(a.Displaced, a.DisplacedFor) : "";
        AfterAssign(slot, previous, forPlayer, task);
        NotifyGroup(slot, forPlayer, task);
        return $"ok: {slot.Number} got the new task (pending until its result arrives){moved}{fitNote}";
    }

    private string MoveDisplaced(TaskMsg displaced, string? displacedFor)
    {
        var r = SpawnCore(displaced.Text, null, handover: false, displacedFor,
            new HiveSpawnOptions(displaced.Profile, displaced.Script, null, displaced.Args, displaced.After));
        return r.StartsWith("ok", StringComparison.Ordinal)
            ? $"; its earlier job ({Trunc(displaced.Text, 40)}) moved: {r[4..]}"
            : $"; its earlier job ({Trunc(displaced.Text, 40)}) was dropped ({r})";
    }
}
