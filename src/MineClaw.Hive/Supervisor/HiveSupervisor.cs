using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Telemetry;
using MineClaw.Data;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Db;
using MineClaw.Hive.Inventory;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;
using MineClaw.World;
using MineClaw.World.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Supervisor;

public enum CloneState { Idle, Starting, Running, Stopping, Backoff, Retired }

/// <summary>Main-side receiver for item handoffs (the main bot's position + its collect events).</summary>
public interface IHandoffReceiver
{
    Vec3d? Position { get; }
    /// <summary>Starts counting items this bot picks up; returns a counter read by <see cref="Collected"/>.</summary>
    void BeginCounting(string from);
    int Collected(string from);
    /// <summary>
    /// Stand still for at most <paramref name="max"/> (dispose = move again). Night review B24: 67 tossed 29 items at the feet
    /// main had offered while main walked on chopping; main collected 28. Null = can't hold.
    /// </summary>
    IDisposable? HoldStill(TimeSpan max) => null;
}

/// <summary>One configured clone: its process, pipe, task and health.</summary>
public sealed class CloneSlot(string name)
{
    internal readonly object Lock = new();
    public string Name { get; } = name;
    public string Number => ChatArbiter.NumberOf(Name) ?? Name;
    public CloneState State { get; internal set; } = CloneState.Idle;
    public TaskMsg? Task { get; internal set; }
    /// <summary>The clone's result event for <see cref="Task"/> arrived (until then the task is pending: nothing to report).</summary>
    public bool ResultIn { get; internal set; }
    public string? ResultText { get; internal set; }
    /// <summary>The result event said ok (false = failed); meaningful only while <see cref="ResultIn"/>.</summary>
    public bool ResultOk { get; internal set; }
    /// <summary>The player whose request this task serves (from the turn that spawned it), if any.</summary>
    public string? ForPlayer { get; internal set; }
    /// <summary>The task was given without naming this clone (main let the supervisor pick a free one).</summary>
    public bool Unnamed { get; internal set; }
    public DateTimeOffset AssignedAt { get; internal set; }
    /// <summary>Lease keys the clone held when it was lost: re-granted to it on restart when still free.</summary>
    public string[]? ResumeClaims { get; internal set; }
    /// <summary>A task was given and its result hasn't arrived yet.</summary>
    public bool Pending => Task != null && !ResultIn && State is not (CloneState.Idle or CloneState.Retired);
    public double Progress { get; internal set; }
    public string? LastProgress { get; internal set; }
    public DateTimeOffset Started { get; internal set; }
    public DateTimeOffset LaunchedAt { get; internal set; }
    public bool Connected { get; internal set; }
    public bool Recalled { get; internal set; }
    public string? RecallReason { get; internal set; }
    public bool ByeReceived { get; internal set; }
    public string? LastExit { get; internal set; }
    public int Deaths { get; internal set; }
    /// <summary>Deliberate void steps (into_void) that reset it: not deaths.</summary>
    public int IntentionalResets { get; internal set; }
    public List<DateTimeOffset> Crashes { get; } = new();
    public BotSnapshot? Snapshot { get; internal set; }
    /// <summary>This session's first in-game snapshot was recorded (joined_at); reset on every hello.</summary>
    internal bool JoinNoted;
    /// <summary>This login is the clone's first ever (nothing in the store before its hello).</summary>
    internal bool FirstLogin;
    internal DateTimeOffset InGameSince;
    internal DateTimeOffset GoodSavedAt;
    /// <summary>The clone reported holding still over the void (no good position while it lasts).</summary>
    internal bool VoidHolding;
    /// <summary>A trusted player said they'd /tp this clone (or to log it in anyway): its void spot doesn't block a login until then.</summary>
    internal DateTimeOffset VoidConfirmedUntil;
    /// <summary>Its task failed and it stays online, idle and safe, for the grace period: free for a new assignment.</summary>
    internal bool AwaitingOrders;
    public List<ItemMsg>? Inventory { get; internal set; }
    internal ICloneProcess? Process;
    internal HiveLink? Link;
    internal int Generation;
    internal int Probing;
    internal TaskCompletionSource? Gone;
    internal event Action<PromptUsageRecord>? Usage;
    internal void RaiseUsage(PromptUsageRecord r) => Usage?.Invoke(r);
    public bool IsLive => State is CloneState.Running or CloneState.Stopping && Connected;
    /// <summary>The brain turn that gave <see cref="Task"/> (a recall in that same turn keeps it for the next login).</summary>
    internal object? AssignedTurn;
    /// <summary>Main standing still for this clone's hand-off toss (offer → confirmation, or the clone gone).</summary>
    internal IDisposable? HandoffHold;
    /// <summary>A task given while this clone was logging out (or recalled in the turn that gave it): relaunch with it.</summary>
    internal (TaskMsg Task, string? ForPlayer, bool Unnamed)? RespawnWith;
    /// <summary>The recall came from the brain (hive recall), not budget/slot policy or shutdown.</summary>
    internal bool BrainRecall;
    /// <summary>Main standing still for a hand-off TO this clone (lend offer → toss), and what it offered.</summary>
    internal IDisposable? LendHold;
    internal HiveLending.Pick? PendingLend;
    /// <summary>What <see cref="Task"/> asks of the body (fight, mine...): the key of the refused-assignment memory.</summary>
    internal TaskIntent Intent;
    /// <summary>Last hp/food written to the store (only changes are saved).</summary>
    internal (int Hp, int Food)? SavedVitals;
    /// <summary>Someone else logged in as this clone (a player's viewer): the server kicked ours; no launch before this.</summary>
    public DateTimeOffset ExternalUntil { get; internal set; }
    /// <summary>Since when the tab list shows this login while it isn't ours (default = not seen).</summary>
    public DateTimeOffset ExternalSince { get; internal set; }
    /// <summary>This outing ended with a duplicate-login kick (the clone's occupied notice or its exit code).</summary>
    internal bool ExternalKick;
    /// <summary>This outing ended with the server refusing the login (the clone's bye or its exit code): not whitelisted / full.</summary>
    internal int? RefusedCode;
    /// <summary>When our last outing ended (our own logout may linger on the tab list for a moment).</summary>
    internal DateTimeOffset GoneAt;
}

/// <summary>
/// The hive supervisor (main process only). Starts clones through its launcher: by default as hives inside this process (the App's
/// in-process launcher, an in-memory link), else as separate OS processes (<c>--clone name --parent-pipe pipe</c>),
/// one named pipe each; watches heartbeats (both ways) and process exit; restarts crashed clones with exponential backoff
/// and retires one after <see cref="HiveConfig.MaxCrashes"/> crashes in the window. Hosts the claim board, chat arbiter
/// state and budget ledger for the clones. When a pipe closes the clone is treated as gone: its claims and grants are
/// released at once, an unplanned exit records its last inventory as stranded, and the main bot gets a quiet event.
/// A dying clone never touches the main process: everything here is message passing and process handles.
/// </summary>
public sealed partial class HiveSupervisor : IAsyncDisposable
{
    private readonly HiveConfig _cfg;
    private readonly string _main;
    private readonly IProcessLauncher _launcher;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    // Copy-on-write: dynamic accounts add slots (AddSlot) while other threads enumerate them; a published dictionary is never
    // mutated, and insertion order (hive list) is kept.
    private volatile Dictionary<string, CloneSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _slotsGate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _tick;
    private readonly Timer _policy;
    private readonly object _spawnGate = new();
    private int _rosterPush, _claimsPush;
    private volatile bool _shuttingDown;
    private readonly object _chorusGate = new();
    private readonly Queue<DateTimeOffset> _choruses = new();
    private DateTimeOffset? _lastChorus;

    /// <summary>Main and its locally hosted bodies. Remote hive bodies receive the same message over their host's IPC.</summary>
    public Func<IReadOnlyList<ChorusSpeaker>>? ChorusSpeakers { get; set; }

    /// <summary>Main-only fan-out; name is all, a login/short name, or comma-separated names.</summary>
    public async Task<string> ChorusAsync(string? text, string? name = null, bool includeMain = true)
    {
        if (HiveChorus.Prepare(text, out var clean) is { } error) return error;
        if (_shuttingDown) return "error: hive is shutting down";
        ChorusMsg message;
        lock (_chorusGate)
        {
            var now = _time.GetUtcNow();
            var window = TimeSpan.FromSeconds(Math.Max(1, _cfg.ChorusWindowSeconds));
            while (_choruses.TryPeek(out var at) && now - at >= window) _choruses.Dequeue();
            if (_lastChorus is { } last && now - last < TimeSpan.FromSeconds(Math.Max(1, _cfg.ChorusIntervalSeconds))
                || _choruses.Count >= Math.Max(1, _cfg.ChorusMaxPerWindow))
                return "error: chorus rate limit; try later";
            _lastChorus = now;
            _choruses.Enqueue(now);
            var names = string.IsNullOrWhiteSpace(name) || name.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
                ? null : name.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            message = new ChorusMsg(clean, now.ToUnixTimeMilliseconds() + HiveChorus.LeadMilliseconds, names, includeMain);
        }
        // Schedule before awaiting any link; one slow link must not move another body's fire time.
        _ = SayChorusAsync(message);
        var sends = _slots.Values.Where(s => s.IsLive && s.Link is { IsOpen: true }).Select(async s =>
        {
            try { await s.Link!.SendAsync(Msg.Chorus, message, _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "chorus: {Clone} disconnected during fan-out", s.Name); }
        });
        await Task.WhenAll(sends).ConfigureAwait(false);
        return $"ok: chorus scheduled at {message.FireAtMs}; text={clean}";
    }

    private async Task SayChorusAsync(ChorusMsg message)
    {
        try { await HiveChorus.SayAtAsync(message, ChorusSpeakers?.Invoke() ?? [], _time, _cts.Token).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "chorus: local chat failed"); }
    }

    public HiveSupervisor(HiveConfig cfg, string main, IProcessLauncher launcher, ClaimBoard claims, BudgetLedger ledger, ChatArbiter arbiter,
        HiveStore? store, Func<SlotFacts> slots, IReadOnlyList<string> regulars, ILogger? log = null, TimeProvider? time = null)
    {
        _cfg = cfg;
        _main = main;
        _launcher = launcher;
        Claims = claims;
        Ledger = ledger;
        Arbiter = arbiter;
        Store = store;
        SlotFacts = slots;
        Regulars = regulars;
        _log = log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        foreach (var c in cfg.Clones()) _slots[c] = new CloneSlot(c);
        claims.Changed += () => _ = PushClaimsAsync();
        arbiter.Changed += () => _ = PushRosterAsync();
        ledger.Spent += () => _ = CheckBudgetAsync();
        _tick = new Timer(_ => Tick(), null, cfg.Heartbeat, cfg.Heartbeat);
        _policy = new Timer(_ => _ = PolicyAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public ClaimBoard Claims { get; }
    public BudgetLedger Ledger { get; }
    public ChatArbiter Arbiter { get; }
    public HiveStore? Store { get; }
    public Func<SlotFacts> SlotFacts { get; }
    public IReadOnlyList<string> Regulars { get; }
    public IHandoffReceiver? Handoff { get; set; }
    /// <summary>Main's inventory for hand-offs TO clones (a loadout tool main has spare). Null = main can't give items.</summary>
    public IHiveDonor? Donor { get; set; }

    /// <summary>Events for the main bot's queue (quiet ones are Background: context only, never a wake-up).</summary>
    public event Action<GameEvent>? Notice;
    /// <summary>Console /as lines etc. are forwarded to live clones so every process arbitrates the same message.</summary>
    public IReadOnlyCollection<CloneSlot> Slots => _slots.Values;
    public CloneSlot? Slot(string name) => _slots.GetValueOrDefault(name);

    /// <summary>Dynamic accounts (allocate, whitelist, release). Null = only the slots given at construction (tests, old configs).</summary>
    public IHiveAccounts? Accounts { get; set; }

    /// <summary>The off-channel hive bus (main routes); null = no bus. Clones get their token in the welcome.</summary>
    public Hierarchy.Bus.HiveBus? Bus { get; set; }

    /// <summary>A slot for an account (an allocated one, or a managed one restored at startup); the arbiter learns the name.</summary>
    public CloneSlot AddSlot(string name)
    {
        CloneSlot? slot;
        lock (_slotsGate)
        {
            if (!_slots.TryGetValue(name.Trim(), out slot))
            {
                slot = new CloneSlot(name.Trim());
                _slots = new Dictionary<string, CloneSlot>(_slots, StringComparer.OrdinalIgnoreCase) { [slot.Name] = slot };
            }
        }
        Arbiter.AddClone(slot.Name);
        return slot;
    }

    /// <summary>Completes once the clone is offline (Idle/Retired) or after <paramref name="timeout"/>.</summary>
    public async Task WaitGoneAsync(string name, TimeSpan timeout)
    {
        if (Resolve(name) is not { } n || !_slots.TryGetValue(n, out var slot)) return;
        Task? gone;
        lock (slot.Lock) gone = slot.State is CloneState.Idle or CloneState.Retired ? null : slot.Gone?.Task;
        if (gone == null) return;
        try { await gone.WaitAsync(timeout, _time).ConfigureAwait(false); } catch (TimeoutException) { }
    }

    private void FinallyReleased(CloneSlot slot, string reason)
    {
        try { Accounts?.Released(slot.Name, reason); } catch (Exception ex) { _log.LogWarning(ex, "hive: release hook for {Clone} failed", slot.Name); }
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>
    /// Where an OFFLINE clone was last left holding over the void (its saved login spot), "x,y,z"; null = fine. A live clone is
    /// already in (holding or not), so a new task for it needs no login.
    /// </summary>
    private string? VoidSpawnOf(CloneSlot s)
    {
        if (s.IsLive) return null;
        try
        {
            if (Store is not { } st) return null;
            var h = st.History(s.Name);
            if (h.Void is { } v) return Block(v);
            // Never in game: its first login lands at the server's fresh-player spawn, over the void here (seen before).
            if (!h.Joined && st.FreshVoidSpawn() is { } f) return Block(f);
            return null;
        }
        catch (Exception ex) { _log.LogDebug(ex, "hive: void spawn of {Clone} unreadable", s.Name); return null; }
    }

    private static string Block((double X, double Y, double Z) p) => $"{(int)Math.Floor(p.X)},{(int)Math.Floor(p.Y)},{(int)Math.Floor(p.Z)}";

    private bool NeverJoined(CloneSlot s)
    {
        try { return Store is { } st && !s.IsLive && !st.History(s.Name).Joined; }
        catch (Exception) { return false; }
    }

    private string VoidRefusal(CloneSlot s) => NeverJoined(s)
        ? $"{s.Number} never joined: its first login lands at the void spawn {VoidSpawnOf(s)} and needs a /tp; ask a trusted player to " +
          $"/tp it when it's in (/tp {s.Name} {_main}); once they say they will, spawn it again (or use another clone)"
        : $"{s.Number}'s saved login spot is over the void at {VoidSpawnOf(s)} (it held there and nobody /tp'd it), so it would spawn " +
          $"there again; ask a trusted player to /tp it when it's in (/tp {s.Name} {_main}); once they say they will, spawn it again " +
          "(or use another clone)";

    private bool VoidConfirmed(CloneSlot s) => _time.GetUtcNow() < s.VoidConfirmedUntil;

    private static readonly System.Text.RegularExpressions.Regex TpConsent = new(
        @"\b(?:tp|teleport|/tp|bring\s+(?:it|them|him|her)\s+(?:up|over|here)|log\s+(?:it|them)\s+in|spawn\s+(?:it|them)|go\s+ahead|confirm(?:ed)?)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// What an offline clone's next login means (hive list): never joined (the fresh-player spawn, a /tp needed), left over the
    /// void, or logged out at a known safe spot. "Idle" alone is never said for a clone that has not been in game.
    /// </summary>
    private string OfflineState(CloneSlot s)
    {
        try
        {
            if (Store is not { } st) return "idle";
            var h = st.History(s.Name);
            if (!h.Joined)
                return st.FreshVoidSpawn() is { } f
                    ? $"never joined; first login lands at void spawn {Block(f)} and needs a /tp"
                    : MineClaw.Data.Versioning.WorldMode.Current.HasVoidBelow ? "never joined; first login lands at the world spawn (maybe over the void: have a /tp ready)"
                    : "never joined; first login lands at the world spawn";
            if (h.Void is { } v) return $"logged out over the void at {Block(v)}; next login lands there and needs a /tp";
            if (h.Good is { } g) return $"idle (logged out safe at {Block((g.X, g.Y, g.Z))}{(g.Dimension == -1 ? " nether" : "")})";
            return "idle (logged out, position unknown)";
        }
        catch (Exception ex) { _log.LogDebug(ex, "hive: history of {Clone} unreadable", s.Name); return "idle"; }
    }

    private void Post(GameEvent e)
    {
        _log.LogInformation("hive: {Event}", e.Format());
        try { Notice?.Invoke(e); } catch (Exception ex) { _log.LogWarning(ex, "hive notice handler failed"); }
    }

    /// <summary>"67" / "maid_chan_67" / "maid chan 67" → the configured clone login.</summary>
    public string? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim().TrimStart('#');
        if (_slots.ContainsKey(n)) return _slots[n].Name;
        var squashed = new string(n.Where(char.IsLetterOrDigit).ToArray());
        return _slots.Values.FirstOrDefault(s => s.Number.Equals(n, StringComparison.OrdinalIgnoreCase)
            || new string(s.Name.Where(char.IsLetterOrDigit).ToArray()).Equals(squashed, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    // ================================================================ spawn / recall / list

    /// <param name="forPlayer">The player whose request this is (default: the speaker of the current turn).</param>
    /// <param name="handover">Explicitly move a request another clone owns (talking with that player / doing its job) to this
    /// clone; the previous owner is told. Without it main never duplicates or reassigns a clone's conversation.</param>
    public string Spawn(string task, string? name = null, bool handover = false, string? forPlayer = null, HiveSpawnOptions? options = null)
    {
        forPlayer ??= MineClaw.Agent.TurnContext.Current?.Speaker;
        // Serialized: the brain's parallel tool calls ("one clone mines, another chops") must never pick the same idle slot.
        lock (_spawnGate) return SpawnCore(task, name, handover, forPlayer, options);
    }

    /// <summary>Validates a model profile for a clone's brain: null = usable, else the refusal. Null hook = no profiles here.</summary>
    public Func<string, string?>? ProfileCheck { get; set; }

    /// <summary>
    /// Readies a script spawn on main: (saved-script name or null, inline code or null, author) → (saved name, error, code). Inline
    /// code is linted (errors block the spawn) and saved so the clone runs it by name. Null hook = scripts can't be spawned.
    /// </summary>
    public Func<string?, string?, (string? Name, string? Error, string? Code)>? ScriptPrep { get; set; }

    /// <summary>A clone gets a new (non-resume) assignment: drop the standing order an earlier hive script left for it.</summary>
    public Action<string>? ResetClone { get; set; }

    /// <summary>Main's world is at night (null = unknown): a day/night change clears remembered refusals.</summary>
    public Func<bool?>? IsNight { get; set; }

    /// <summary>Refused assignments by (clone, intent) (live 10-27: "fight mobs" re-sent to 67 three times).</summary>
    public AssignFailureMemory Refusals { get; } = new();

    /// <summary>
    /// A clone's last known body: the live snapshot (hp, food, position) and items while it is out, else what the store kept
    /// from its last session (hp/food survive a logout; items were deposited).
    /// </summary>
    public CloneVitals VitalsOf(CloneSlot s)
    {
        var snap = s.Snapshot;
        IReadOnlyList<ItemMsg>? items = s.IsLive ? s.Inventory : null;
        if (snap is { Hp: > 0 } sn)
            return new CloneVitals(sn.Hp, sn.Food, items, sn.Pos?.X, sn.Pos?.Y, sn.Pos?.Z, sn.Dimension, sn.At);
        try
        {
            if (Store?.Vitals(s.Name) is { } v) return new CloneVitals(v.Hp, v.Food, items, v.X, v.Y, v.Z, v.Dimension, v.At);
        }
        catch (Exception ex) { _log.LogDebug(ex, "hive: vitals of {Clone} unreadable", s.Name); }
        return CloneVitals.Unknown with { Items = items };
    }

    /// <summary>A player's line (main's addressed chat): a retry or new information clears matching refusals.</summary>
    public void NotePlayerChat(string? player, string text)
    {
        // A trusted player agreeing to /tp a clone whose login lands over the void ("I'll tp 420", "log 1337 in, I'll tp it",
        // "if they keep falling, tp them to you", 07:52:15): its next login goes ahead for 10 min. A line naming no clone covers
        // every clone that is blocked that way.
        if (player != null && Regulars.Contains(player, StringComparer.OrdinalIgnoreCase) && TpConsent.IsMatch(text ?? ""))
        {
            var blocked = _slots.Values.Where(sl => !sl.IsLive && VoidSpawnOf(sl) != null).ToList();
            var named = blocked.Where(sl => Mentions(text!, sl)).ToList();
            foreach (var sl in named.Count > 0 ? named : blocked)
            {
                sl.VoidConfirmedUntil = _time.GetUtcNow() + TimeSpan.FromMinutes(10);
                _log.LogInformation("hive: {Player} will /tp {Clone}; its void login is allowed for 10 min", player, sl.Name);
            }
        }
        int n = Refusals.OnPlayerChat(text, clone => _slots.TryGetValue(clone, out var sl) && Mentions(text, sl));
        if (n > 0) _log.LogInformation("hive: {N} refused assignment(s) cleared by {Player}: {Text}", n, player ?? "?", text);
    }

    private bool? Night() { try { return IsNight?.Invoke(); } catch (Exception) { return null; } }

    private (double X, double Z)? MainXZ() { try { return Handoff?.Position is { } p ? (p.X, p.Z) : null; } catch (Exception) { return null; } }

    /// <summary>The login is on the server right now besides the tab list: a spawned named player (0x14). Null = tab list only.</summary>
    public Func<string, bool>? SeenOnServer { get; set; }

    private bool OnServer(string name)
    {
        try
        {
            if (SlotFacts().Online.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
            return SeenOnServer?.Invoke(name) == true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Why an offline slot can't be launched because someone else holds the account (null = free): a duplicate-login kick within
    /// <see cref="HiveConfig.ExternalLoginCooldownMinutes"/>, or the name is on the server's player list while no process of ours
    /// is in it (a player's viewer). Never fight the login: logging in would kick them. The first sighting tells main once.
    /// </summary>
    internal string? ExternalOccupant(CloneSlot s)
    {
        // A subhive account is online in its owner's host, not this supervisor's process slot. It is still ours.
        if (Accounts is MineClaw.Hive.Hierarchy.HiveHierarchy hierarchy && hierarchy.Tree.Get(s.Name) is { } node
            && (node.Kind == MineClaw.Hive.Hierarchy.CloneKind.Subhive || node.Owner != MineClaw.Hive.Hierarchy.HiveTree.Root))
            return $"managed by hive {node.Owner} (one of ours; use its host, not another login)";
        var now = Now;
        if (now < s.ExternalUntil)
            return $"is in use by a player's viewer (someone else logged in as {s.Name}); left alone for {Math.Max(1, (int)Math.Ceiling((s.ExternalUntil - now).TotalMinutes))} more min";
        if (s.State is not (CloneState.Idle or CloneState.Backoff)) return null; // ours: starting, out or logging out
        bool online = now - s.GoneAt >= TimeSpan.FromSeconds(Math.Max(0, _cfg.OwnLogoutGraceSeconds)) && OnServer(s.Name);
        if (!online)
        {
            s.ExternalSince = default;
            return null;
        }
        if (s.ExternalSince == default)
        {
            s.ExternalSince = now;
            Post(GameEvent.Notice($"[hive] slot {s.Number} is in use by a player's viewer ({s.Name} is online but isn't one of ours); using other clones", EventPriority.Background));
        }
        return $"is in use by a player's viewer ({s.Name} is online but isn't one of ours)";
    }

    /// <summary>Logged in with nothing to finish: free for a new job right where it is (a crew build's default helpers).</summary>
    public static bool IdleOnline(CloneSlot s) => s.IsLive && Free(s);

    /// <summary>A free clone (offline idle, or logged in with nothing to finish) that can take a new job.</summary>
    private static bool Free(CloneSlot s) => s.State == CloneState.Idle
        // Online and free: no task, or asleep after a script that ended with after: sleep. A clone whose result just came in is
        // depositing before its logout: not free.
        || (s.State == CloneState.Running && s.Connected && !s.Recalled && (s.Task == null || (s.ResultIn && (s.Task.SleepsAfter || s.AwaitingOrders))));

    /// <summary>
    /// Picks the best free clone for an unnamed task: scored on hp, food, tools and gear for the intent and distance to main;
    /// clones that can't safely do it (blocker) or refused this intent with nothing changed since are skipped. Null pick =
    /// none qualifies, with the reason and a fix for the brain.
    /// </summary>
    internal (CloneSlot? Slot, string? Why) PickFor(TaskIntent intent, CloneSlot? except = null, bool confirmVoid = false)
    {
        var free = _slots.Values.Where(s => Free(s) && !ReferenceEquals(s, except)).ToList();
        // Someone else is logged in as it (a player's viewer): never kick them by logging ours in.
        var taken = free.Where(s => ExternalOccupant(s) != null).ToList();
        free = free.Except(taken).ToList();
        if (free.Count == 0 && taken.Count > 0)
            return (null, string.Join("; ", taken.Select(s => $"{s.Number} {ExternalOccupant(s)}")) + "; no other clone is free");
        // Never log a clone in where it was left holding over the void (it would spawn there again) unless a player confirmed.
        var stuck = confirmVoid ? [] : free.Where(s => VoidSpawnOf(s) != null && !VoidConfirmed(s)).ToList();
        free = free.Except(stuck).ToList();
        if (free.Count == 0) return (null, stuck.Count > 0 ? string.Join("; ", stuck.Select(VoidRefusal)) : null);
        var main = MainXZ();
        bool? night = Night();
        var scored = free.Select(s =>
        {
            var v = VitalsOf(s);
            var fit = CloneFitness.Score(s.Number, v, intent, online: s.IsLive, main?.X, main?.Z);
            var refused = Refusals.Blocking(s.Name, intent, v, night);
            return (Slot: s, Fit: fit, Refused: refused);
        }).OrderByDescending(x => x.Fit.Score).ToList();
        var ok = scored.FirstOrDefault(x => x.Fit.Blocker == null && x.Refused == null);
        if (ok.Slot != null)
        {
            if (scored.Count > 1)
                _log.LogInformation("hive: picked {Clone} for {Intent} ({Fits})", ok.Slot.Name, intent,
                    string.Join("; ", scored.Select(x => $"{x.Slot.Number} {x.Fit.Score:0.0} {x.Fit.Summary}" + (x.Fit.Blocker != null ? " BLOCKED" : x.Refused != null ? " REFUSED" : ""))));
            return (ok.Slot, null);
        }
        var why = string.Join("; ", scored.Select(x => x.Refused != null
            ? $"{x.Slot.Number} already refused {CloneFitness.Verb(intent)}: {x.Refused}, and nothing changed since"
            : $"{x.Slot.Number} {x.Fit.Blocker}"));
        bool food = scored.Any(x => (x.Fit.Blocker ?? x.Refused ?? "").Contains("food", StringComparison.OrdinalIgnoreCase));
        var fix = food ? "fix: a clone needs food first (toss it bread or cooked meat, or put some in a chest), then spawn again"
            : "fix: tell the player what's missing, or wait until it changes";
        return (null, $"no clone fits {CloneFitness.Verb(intent)}: {why}; {fix}");
    }

    private string SpawnCore(string task, string? name, bool handover, string? forPlayer, HiveSpawnOptions? opts = null)
    {
        if (!_cfg.Enabled) return "error: hive is off";
        if (_shuttingDown) return "error: shutting down";
        var (job, jobError) = PrepareSpawn(task, opts);
        if (job == null) return jobError!;
        var intent = CloneFitness.IntentOf(job.Text);
        var (slot, fitNote, refusal) = name != null ? PickNamed(name, handover, intent, opts) : PickUnnamed(intent, opts);
        if (refusal != null) return refusal;
        var (owner, talking, asking) = PlayerSlots(forPlayer);
        if (ConversationRefusal(handover, talking, asking, slot, name, forPlayer, job.Task) is { } busy) return busy;
        if (slot == null) return "no: all clones busy or retired";
        var previous = PreviousOwner(handover, owner, talking, slot);
        var t = NewTask(job, opts);
        AnnounceTask(slot, t, forPlayer, name, job.Task);
        var assignment = AssignLocked(slot, t, forPlayer, name, intent, job.Task);
        if (assignment.Refusal != null) return assignment.Refusal;
        try { Accounts?.Assigned(slot.Name, t, opts); } catch (Exception ex) { _log.LogWarning(ex, "hive: assigned hook for {Clone} failed", slot.Name); }
        return FinishSpawn(slot, assignment, previous, forPlayer, job.Task, fitNote);
    }

    // caller holds slot.Lock: a fresh outing for an Idle/Backoff slot (reserved now: nobody else picks it; Launch runs later).
    private void Reserve(CloneSlot slot, TaskMsg t, string? forPlayer, bool unnamed)
    {
        slot.Task = t;
        slot.ResultIn = false;
        slot.AwaitingOrders = false;
        slot.ResultText = null;
        slot.ForPlayer = forPlayer;
        slot.Unnamed = unnamed;
        slot.AssignedAt = Now;
        slot.AssignedTurn = MineClaw.Agent.TurnContext.Current;
        slot.Progress = 0;
        slot.LastProgress = null;
        slot.Recalled = false;
        slot.BrainRecall = false;
        slot.RecallReason = null;
        slot.ResumeClaims = null;
        slot.RespawnWith = null;
        slot.Crashes.Clear();
        slot.Started = Now;
        slot.State = CloneState.Starting;
    }

    /// <summary>
    /// Multi-clone jobs: clones already out on the same player's request (same request, or either task names the other
    /// clone) hear that <paramref name="slot"/> joined it, with its task, so a re-planned role reaches them (10-27: 69 kept
    /// waiting for its spar partner 420 after main had sent 67 instead). A partner their task names who is gone is replaced.
    /// </summary>
    private void NotifyGroup(CloneSlot slot, string? forPlayer, string task)
    {
        foreach (var mate in _slots.Values)
        {
            if (ReferenceEquals(mate, slot) || !mate.Pending || mate.Task is not { } mt || mate.Link is not { IsOpen: true } link) continue;
            if (forPlayer != null && mate.ForPlayer != null && !mate.ForPlayer.Equals(forPlayer, StringComparison.OrdinalIgnoreCase)) continue;
            if (!SameRequest(mt.Text, task) && !Mentions(task, mate) && !Mentions(mt.Text, slot)) continue;
            var gone = _slots.Values.Where(o => !ReferenceEquals(o, mate) && !ReferenceEquals(o, slot) && Mentions(mt.Text, o)
                && !(o.IsLive || o.Pending)).Select(o => o.Number).ToList();
            var text = $"[hive update from main] {slot.Number} is on your job too: \"{Trunc(task, 160)}\"" +
                (gone.Count > 0 ? $"; {string.Join(", ", gone)} is offline, so {slot.Number} takes that part." : ".") +
                " Work with it; if a partner is missing, ask main (hive_report progress), not the player.";
            _ = link.SendAsync(Msg.Note, new NoteMsg(text));
            _log.LogInformation("hive: told {Mate} that {Clone} joined its job", mate.Name, slot.Name);
        }
    }

    /// <summary>The task text names this clone ("spar with 69", "maid_chan_69's partner"), never inside a coordinate.</summary>
    internal static bool Mentions(string text, CloneSlot s) =>
        System.Text.RegularExpressions.Regex.IsMatch(text ?? "", $@"(?<![0-9A-Za-z_.,-]){System.Text.RegularExpressions.Regex.Escape(s.Number)}(?![0-9A-Za-z_]|[.,]\s?-?[0-9])")
        || (text ?? "").Contains(s.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Two task texts describe the same request ("build 2 more rows of planks on the west edge" vs "...of cobble on the
    /// west edge"): content-word overlap (Jaccard) of at least 0.6, after dropping coordinates, stopwords and delegate
    /// boilerplate (chest, food, deposit, storage...: 10-27 tree and spar tasks matched on those). "mine cobble at the gen" vs
    /// "chop trees" = different.
    /// </summary>
    public static bool SameRequest(string a, string b)
    {
        static HashSet<string> Words(string t) => System.Text.RegularExpressions.Regex.Matches((t ?? "").ToLowerInvariant(), "[a-z]{3,}")
            .Select(m => m.Value.TrimEnd('s')).Select(w => Synonyms.GetValueOrDefault(w, w)).Where(w => w.Length >= 3 && !StopWords.Contains(w)).ToHashSet();
        var x = Words(a);
        var y = Words(b);
        if (x.Count == 0 || y.Count == 0) return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        int inter = x.Count(y.Contains);
        return inter * 5 >= x.Union(y).Count() * 3;
    }

    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["cobblestone"] = "cobble", ["generator"] = "gen", ["pickaxe"] = "pick", ["wooden"] = "wood", ["tree"] = "log",
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "then", "your", "you", "from", "into", "that", "thi", "all", "night", "there", "here", "some",
        "more", "keep", "until", "when", "while", "near", "next", "other", "one", "two", "please", "can", "get", "use", "its",
        // delegate boilerplate: nearly every task mentions the house chest, food, the loadout and the deposit at the end
        "chest", "food", "deposit", "storage", "stash", "store", "inventory", "report", "first", "done", "hive", "take", "need",
        "finished", "item", "back", "eat", "have", "will", "what", "them", "they", "out", "any", "each", "also", "just", "too",
        "maid", "chan", "clone", "main", "player", "poofimafox",
    };

    /// <summary>The clone holding a player's conversation (thread lease, else sticky partner) while it's out; <paramref name="asking"/> = thread lease.</summary>
    public CloneSlot? ConversationSlot(string player, out bool asking)
    {
        asking = false;
        var thread = Arbiter.ThreadOwner(player);
        var o = thread ?? Arbiter.ConversationOwner(player);
        if (o == null || Resolve(o) is not { } on || _slots[on] is not { } s || s.State is not (CloneState.Running or CloneState.Starting) || !(s.IsLive || s.Pending))
            return null;
        asking = thread != null;
        return s;
    }

    /// <summary>The clone that owns a player's request right now: holds their thread/sticky, or has a pending job for them.</summary>
    public CloneSlot? OwnerSlot(string player)
    {
        if (Arbiter.ConversationOwner(player) is { } o && Resolve(o) is { } on && _slots[on] is { } s
            && s.State is CloneState.Running or CloneState.Starting && (s.IsLive || s.Pending))
            return s;
        return _slots.Values.FirstOrDefault(x => x.Pending && x.ForPlayer != null && x.ForPlayer.Equals(player, StringComparison.OrdinalIgnoreCase)
            && x.State is CloneState.Running or CloneState.Starting);
    }

    private void AfterAssign(CloneSlot slot, CloneSlot? previous, string? forPlayer, string task)
    {
        if (forPlayer == null) return;
        // The player's next few lines continue with the clone now doing their request (user requirement 10-27).
        if (previous == null) { Arbiter.Delegated(forPlayer, slot.Name); return; }
        // Explicit handover: the player's conversation moves to the new clone and the old owner is told to drop it.
        Arbiter.HandOver(forPlayer, slot.Name);
        lock (previous.Lock) previous.ForPlayer = null;
        var note = new TaskMsg(Guid.NewGuid().ToString("N")[..8],
            $"main handed {forPlayer}'s request ({Trunc(task, 80)}) to {slot.Number}. Drop your part of it; stay idle unless told otherwise.");
        if (previous.Link is { IsOpen: true } pl) _ = pl.SendAsync(Msg.Task, note);
        Post(GameEvent.Notice($"[hive] handed {forPlayer}'s request from {previous.Number} to {slot.Number}", EventPriority.Low));
    }

    /// <summary>
    /// Main's speech guard: a sentence reporting a clone's result while that clone's task is still pending is replaced by
    /// "still working" (the result is reportable only after its result event arrives). Null-safe; returns the text to say.
    /// </summary>
    /// <summary>
    /// Where main sees a clone right now: the player within a few blocks of it in main's own world ("PoofimaFox"), "" when it is
    /// in view but near nobody, null when main can't see it. Wired by the host; null = nobody's whereabouts are confirmable.
    /// </summary>
    public Func<string, string?>? NearPlayer { get; set; }

    private static readonly System.Text.RegularExpressions.Regex Whereabouts = new(
        @"\b(?:is|'s|are|now|right)\b[^.!?]{0,24}?\b(?:next to|beside|by|near|with|behind)\s+(?:u|you|ya|ur|your|him|her|them|fox\w*|robin\w*|kiwi|poof\w*)\b|\b(?:is|'s)\s+(?:right\s+)?here\b|\b(?:arrived|got here|made it|is there|'s there|reached (?:u|you|him|her))\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Live 10-29: after "maid tell 420 to come here" main said "420 is right next to u now foxy~" with no order sent and no
    /// result back (420 was in a pool). A sentence placing a live clone next to someone, here or arrived passes only when main
    /// sees that clone beside a player; otherwise the claim becomes what is true: told it (an order went out this turn) or not
    /// with them yet.
    /// </summary>
    internal string GuardWhereabouts(string text)
    {
        if (text.Length == 0 || !Whereabouts.IsMatch(text)) return text;
        var live = _slots.Values.Where(s => s.IsLive || s.State is CloneState.Starting).ToList();
        if (live.Count == 0) return text;
        var turn = MineClaw.Agent.TurnContext.Current;
        var parts = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?~])\s+").Select(s =>
        {
            if (!Whereabouts.IsMatch(s)) return s;
            foreach (var slot in live)
            {
                var who = new System.Text.RegularExpressions.Regex(@"(?<![A-Za-z0-9_])(?:" + System.Text.RegularExpressions.Regex.Escape(slot.Number) + "|" +
                    System.Text.RegularExpressions.Regex.Escape(slot.Name) + @")(?![A-Za-z0-9_])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!who.IsMatch(s)) continue;
                if (NearPlayer?.Invoke(slot.Name) is { Length: > 0 }) return s; // main sees it beside a player: true
                bool toldNow = turn != null && ReferenceEquals(slot.AssignedTurn, turn);
                _log.LogInformation("hive: dropped an unconfirmed whereabouts claim about {Clone}: {Text}", slot.Number, s.Trim());
                return toldNow ? $"told {slot.Number} to come, I'll say when it's there." : $"{slot.Number} isn't with you yet.";
            }
            return s;
        });
        return string.Join(" ", parts.Where(p => p.Length > 0)).Trim();
    }

    public string GuardSpeech(string text)
    {
        text = GuardArrivals(text ?? "");
        text = GuardWhereabouts(text);
        var pending = _slots.Values.Where(s => s.Pending).ToList();
        if (pending.Count == 0 || string.IsNullOrWhiteSpace(text)) return text;
        var refs = pending.SelectMany(s => new[] { s.Number, s.Name }).Concat(["clone", "clones", "the other maid", "my sister", "lil me"]);
        var who = new System.Text.RegularExpressions.Regex(@"(?<![A-Za-z0-9_])(?:" + string.Join("|", refs.Distinct().Select(System.Text.RegularExpressions.Regex.Escape)) + @")(?![A-Za-z0-9_])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        bool replaced = false;
        var parts = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?])\s+").Select(s =>
        {
            var m = who.Match(s);
            if (!m.Success) return s;
            // Only the clause about the clone (and what follows it in the sentence) is a claim; main's own part stays.
            int cut = new[] { s.LastIndexOf(", ", m.Index, StringComparison.Ordinal), s.LastIndexOf(" and ", m.Index, StringComparison.OrdinalIgnoreCase) }.Max();
            var tail = cut > 0 ? s[cut..] : s;
            // The clone must be the one reporting: "67 has the picks", never main's own "the only helpers I've got are 67
            // and 69" (07:54:53, night review B17: that clause was dropped as a pending-result claim).
            if (!ClaimsResult.IsMatch(s[m.Index..]) || StillWorking.IsMatch(tail)) return s;
            _log.LogInformation("hive: dropped a pending-result claim from main's chat: {Text}", tail.Trim());
            var keep = cut > 0 ? s[..cut].TrimEnd(',', ' ') + "." : "";
            if (replaced) return keep;
            replaced = true;
            var note = pending.Count == 1 ? $"{pending[0].Number}'s still on its part, I'll tell you when it reports." : "the clones are still on their parts.";
            return (keep + " " + note).Trim();
        });
        return string.Join(" ", parts.Where(p => p.Length > 0)).Trim();
    }

    /// <summary>
    /// Night review B20 (07:52:17): "420 is spawning back in" was sent 350 ms after 420 reported "failed: spawned ... in the
    /// void". A clone spawned in THIS turn whose outing already failed (failed result, or retired) isn't coming:
    /// the arrival clause becomes what really happened. Only this turn's spawn: a respawn in the same response as the line
    /// (tools run after the text) still has an older turn's failure on the slot and is left alone.
    /// </summary>
    private string GuardArrivals(string text)
    {
        var turn = MineClaw.Agent.TurnContext.Current;
        if (turn == null || text.Length == 0 || !Arriving.IsMatch(text)) return text;
        var gone = new List<(CloneSlot Slot, string Note)>();
        foreach (var s in _slots.Values)
            lock (s.Lock)
            {
                if (!ReferenceEquals(s.AssignedTurn, turn)) continue;
                string? note = s.ResultIn && !s.ResultOk ? $"{s.Number} couldn't get in"
                    : s.State == CloneState.Retired ? $"{s.Number} couldn't get in"
                    : null; // Idle may just be a reserved launch waiting for the login gap: not a failure
                if (note != null) gone.Add((s, note));
            }
        if (gone.Count == 0) return text;
        var parts = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?])\s+").Select(sentence =>
        {
            foreach (var (slot, note) in gone)
            {
                var who = new System.Text.RegularExpressions.Regex(@"(?<![A-Za-z0-9_,])(?:" + System.Text.RegularExpressions.Regex.Escape(slot.Number) + "|"
                    + System.Text.RegularExpressions.Regex.Escape(slot.Name) + @")(?![A-Za-z0-9_]|,\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var m = who.Match(sentence);
                if (!m.Success) continue;
                var claim = Arriving.Match(sentence, m.Index);
                if (!claim.Success) continue;
                // The clause runs from the clone's name to the next clause break after the claim.
                var brk = System.Text.RegularExpressions.Regex.Match(sentence[(claim.Index + claim.Length)..], @",\s|\s(?:and|but|so)\s|[.!?]");
                int end = brk.Success ? claim.Index + claim.Length + brk.Index : sentence.Length;
                _log.LogInformation("hive: dropped a stale arrival claim from main's chat: {Text} ({Note})", sentence[m.Index..end].Trim(), note);
                sentence = sentence[..m.Index] + note + sentence[end..];
            }
            return sentence;
        });
        return string.Join(" ", parts).Trim();
    }

    private static readonly System.Text.RegularExpressions.Regex Arriving = new(
        @"\b(?:re)?spawn(?:ing|s)?\b(?!\s+(?:point|spot|area))|\b(?:logging|coming|popping|hopping|getting|joining)\s+(?:back\s+)?(?:in|on)\b|\bjoining\b|\bon\s+(?:its|her|their)\s+way\s+in\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex ClaimsResult = new(
        @"\b(?:dropped|dropped off|mined|got|put|deposited|stashed|did|done|finished|made|crafted|brought|delivered|has (?:the|\d|a|it|them)|ready|is back|came back|completed?)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex StillWorking = new(
        @"\b(?:will|gonna|going to|is (?:mining|going|working|heading|on)|still|working on|on (?:it|its way)|when (?:it|she|they)|once (?:it|she|they)|should|soon|doing|started|starting|spawn(?:ing|ed)?|sent)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>An update for a live clone's running task (context for its brain; the task stays). False = not connected.</summary>
    public bool Note(string name, string text)
    {
        if (Resolve(name) is not { } n || _slots[n].Link is not { IsOpen: true } link) return false;
        _ = link.SendAsync(Msg.Note, new NoteMsg(text));
        return true;
    }

    public string Recall(string? nameOrAll, string reason = "recalled")
    {
        var targets = string.IsNullOrWhiteSpace(nameOrAll) || nameOrAll.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
            ? _slots.Values.Where(s => s.State is not (CloneState.Idle or CloneState.Retired)).ToList()
            : Resolve(nameOrAll) is { } n ? [_slots[n]] : null;
        if (targets == null) return $"error: no clone '{nameOrAll}'";
        if (targets.Count == 0) return "ok: no clones out";
        var turn = MineClaw.Agent.TurnContext.Current;
        var kept = new List<string>();
        foreach (var s in targets)
        {
            lock (s.Lock)
            {
                s.BrainRecall = true;
                // "recall 69" + "69: new task" in one turn (parallel, either order): the new task survives the logout.
                if (turn != null && ReferenceEquals(s.AssignedTurn, turn) && s.Pending && s.Task is { } t && s.RespawnWith == null)
                {
                    s.RespawnWith = (t, s.ForPlayer, s.Unnamed);
                    kept.Add(s.Number);
                }
            }
            RecallSlot(s, reason);
        }
        return "ok: recalling " + string.Join(", ", targets.Select(s => s.Number)) +
            (kept.Count > 0 ? $" ({string.Join(", ", kept)} logs back in with the task given this turn)" : "");
    }

    private void RecallSlot(CloneSlot s, string reason, double? deadlineSeconds = null)
    {
        HiveLink? link;
        lock (s.Lock)
        {
            s.Recalled = true;
            s.RecallReason = reason;
            if (s.State is CloneState.Idle or CloneState.Retired) return;
            if (s.State == CloneState.Backoff) { s.State = CloneState.Idle; s.RespawnWith = null; FinallyReleased(s, reason); return; }
            s.State = CloneState.Stopping;
            link = s.Link;
        }
        if (link is { IsOpen: true }) _ = link.SendAsync(Msg.Recall, new RecallMsg(reason, deadlineSeconds));
        else if (!s.Connected) s.Process?.Kill(); // never connected: nothing to deposit
    }

    /// <summary>Main shutting down: recall everyone, wait for them to deposit and log out, kill stragglers (recorded as stranded).</summary>
    public async Task RecallAllAsync(string reason, TimeSpan timeout)
    {
        _shuttingDown = true;
        var live = _slots.Values.Where(s => s.State is not (CloneState.Idle or CloneState.Retired)).ToList();
        if (live.Count == 0) return;
        var waits = live.Select(s => s.Gone?.Task ?? Task.CompletedTask).ToList();
        foreach (var s in live) RecallSlot(s, reason, Math.Max(5, timeout.TotalSeconds - 5));
        try { await Task.WhenAll(waits).WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            foreach (var s in live.Where(s => s.State is not (CloneState.Idle or CloneState.Retired)))
            {
                _log.LogWarning("hive: {Clone} didn't log out in time; killing it", s.Name);
                s.Process?.Kill();
            }
            try { await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (TimeoutException) { }
        }
    }

    /// <summary>Terse status line for <c>list_clones</c>.</summary>
    public string List()
    {
        var parts = _slots.Values.Select(s =>
        {
            string st = s.State switch
            {
                CloneState.Running when s.Connected => "out",
                CloneState.Running or CloneState.Starting => "starting",
                CloneState.Stopping => "logging out",
                CloneState.Backoff => "restarting",
                CloneState.Retired => $"retired ({s.Crashes.Count} crashes)",
                _ => ExternalOccupant(s) is { } occupied ? occupied : OfflineState(s),
            };
            var line = $"{s.Number} {st}";
            if (s.State is not (CloneState.Idle or CloneState.Retired) && s.Task is { Script: { } script } st2)
            {
                // "67: script cobble_loop 42% (brain asleep, profile haiku)"
                bool asleep = s.Snapshot?.Brain is not { } b || b.StartsWith("asleep", StringComparison.Ordinal);
                line = $"{s.Number}: script {script} {s.Progress * 100:0}% (brain {(asleep ? "asleep" : "awake")}, profile {st2.Profile ?? "default"})" +
                    (st != "out" ? $" {st}" : "") + (s.ResultIn ? $" (result: {Trunc(s.ResultText ?? "", 60)})" : "");
            }
            else if (s.State is not (CloneState.Idle or CloneState.Retired) && s.Task != null)
            {
                if (s.Task.Profile != null) line += $" (profile {s.Task.Profile})";
                line += $": {Trunc(s.Task.Text, 50)}" + (s.Pending ? " (pending, no result yet)" : s.ResultIn ? $" (result: {Trunc(s.ResultText ?? "", 60)})" : "");
                if (s.Progress > 0) line += $" {Percent.Of(s.Progress)}";
                if (s.Snapshot?.Pos is { } p) line += $" @{p}";
            }
            if (s.Deaths > 0) line += $" died x{s.Deaths}";
            return line;
        });
        var (d, _) = Ledger.ClonesSpend();
        return string.Join(" | ", parts) + $" | clones today ${d:0.00}";
    }

    /// <summary>Persists a clone's hp/food/position when they change (main restarts keep knowing who is fit for what).</summary>
    private void SaveVitals(CloneSlot slot, BotSnapshot snap)
    {
        if (Store is null || snap.Hp <= 0 || slot.SavedVitals == (snap.Hp, snap.Food)) return;
        try
        {
            Store.SaveVitals(slot.Name, snap.Hp, snap.Food, snap.Pos?.X, snap.Pos?.Y, snap.Pos?.Z, snap.Dimension);
            slot.SavedVitals = (snap.Hp, snap.Food);
        }
        catch (Exception ex) { _log.LogDebug(ex, "hive: saving {Clone}'s vitals failed", slot.Name); }
    }

    /// <summary>
    /// Join history from the snapshot stream: the first in-game snapshot records joined_at; after 5 s in game without a void
    /// hold (detected after ~2 s) the position is a known good one, saved at most every 30 s.
    /// </summary>
    private void NoteInGame(CloneSlot slot, BotSnapshot snap)
    {
        if (Store is null || snap.Status != BotStatus.Online || snap.Pos is not { } p) return;
        var now = _time.GetUtcNow();
        try
        {
            if (!slot.JoinNoted)
            {
                slot.JoinNoted = true;
                slot.InGameSince = now;
                Store.NoteJoined(slot.Name);
                return;
            }
            if (slot.VoidHolding || now - slot.InGameSince < TimeSpan.FromSeconds(5) || now - slot.GoodSavedAt < TimeSpan.FromSeconds(30)) return;
            Store.SaveGoodPosition(slot.Name, p.X, p.Y, p.Z, snap.Dimension);
            slot.GoodSavedAt = now;
        }
        catch (Exception ex) { _log.LogDebug(ex, "hive: join history of {Clone} not saved", slot.Name); }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>
    /// The main brain's per-turn hive line (uncached tail), only while clones are online:
    /// "[hive: 1 clone online: maid_chan_67 (idle)]", plus a split reminder when the turn holds a group ask.
    /// </summary>
    /// <param name="named">Clones a multi-bot line named: they answer their own part, main coordinates.</param>
    public string? HintLine(bool groupAsk = false, IReadOnlyCollection<string>? named = null)
    {
        var live = _slots.Values.Where(s => s.IsLive || s.Pending).ToList();
        if (live.Count == 0) return null;
        string line;
        if (live.Any(s => s.Pending))
            // "[hive: 67 mining (pending)]": its result isn't in, so there is nothing to report about it yet.
            line = $"[hive: {string.Join(", ", live.Select(s => s.Pending ? $"{s.Number} {Verb(s.Task!.Text)} (pending)" : $"{s.Number} {CloneActivity(s)}"))}] pending = no result yet, don't report it";
        else
            line = $"[hive: {live.Count} clone{(live.Count == 1 ? "" : "s")} online: {string.Join(", ", live.Select(s => $"{s.Name} ({CloneActivity(s)})"))}]";
        if (groupAsk) line += " group ask: split it, hive spawn name+task for the clone's part; reply once";
        if (named is { Count: > 0 })
        {
            var nums = named.Select(n => Resolve(n) is { } r ? _slots[r].Number : null).Where(n => n != null).ToList();
            if (nums.Count > 0) line += $"; {string.Join(" and ", nums)} heard it too and answer their own part: coordinate, don't repeat them";
        }
        return line;
    }

    /// <summary>"mine 10 cobble at the gen" -> "mining".</summary>
    internal static string Verb(string task)
    {
        var w = System.Text.RegularExpressions.Regex.Match(task ?? "", @"[A-Za-z]+").Value.ToLowerInvariant();
        if (w.Length == 0) return "working";
        if (Verbs.TryGetValue(w, out var known)) return known;
        return w.EndsWith('e') ? w[..^1] + "ing" : w + "ing";
    }

    /// <summary>A task's first word → what the clone is doing (words not listed get "-ing").</summary>
    private static readonly Dictionary<string, string> Verbs = new(StringComparer.Ordinal)
    {
        ["mine"] = "mining", ["dig"] = "mining", ["harvest"] = "mining", ["grind"] = "mining",
        ["chop"] = "chopping", ["cut"] = "chopping",
        ["craft"] = "crafting", ["make"] = "crafting",
        ["build"] = "building", ["place"] = "building",
        ["smelt"] = "smelting", ["cook"] = "smelting",
        ["organize"] = "sorting", ["organise"] = "sorting", ["sort"] = "sorting",
        ["deposit"] = "storing", ["store"] = "storing", ["stash"] = "storing",
        ["fetch"] = "fetching", ["get"] = "fetching", ["bring"] = "fetching", ["collect"] = "fetching", ["gather"] = "fetching",
        ["farm"] = "farming", ["plant"] = "farming",
    };

    private static string CloneActivity(CloneSlot s)
    {
        if (s.State == CloneState.Stopping) return "logging out";
        var task = s.Snapshot?.Task;
        if (string.IsNullOrWhiteSpace(task) || task.StartsWith("idle", StringComparison.OrdinalIgnoreCase)) return "idle";
        return "busy: " + Trunc(task, 30);
    }

    // ================================================================ process lifecycle

    private MineClaw.Bot.Bodies.JoinPacer? _pacer;

    /// <summary>
    /// The b1.8.1 server's connection throttle rejects logins from one IP less than ~4 s apart (10-27: two clones launched
    /// in the same millisecond, one got "server closed the connection" and lost 6-10 s). Launches are spaced by
    /// <see cref="HiveConfig.LaunchGapSeconds"/> (1.12.2: <see cref="HiveConfig.LaunchBatch"/> at a time): returns how long this launch waits.
    /// </summary>
    internal TimeSpan JoinDelay()
    {
        var pacer = _pacer ??= new MineClaw.Bot.Bodies.JoinPacer(() => Now);
        return pacer.Next(TimeSpan.FromSeconds(Math.Max(0, _cfg.LaunchGapSeconds)), _cfg.LaunchBatch);
    }

    private void Launch(CloneSlot slot)
    {
        // Main whitelists the account right before its login (it's off the whitelist whenever it isn't out); the login waits
        // for the server's confirmation (bounded by the accounts seam), else it would be refused "not white-listed".
        Task whitelisted;
        try { whitelisted = Accounts?.BeforeLaunchAsync(slot.Name, _cts.Token) ?? Task.CompletedTask; }
        catch (Exception ex) { _log.LogWarning(ex, "hive: whitelist hook for {Clone} failed", slot.Name); whitelisted = Task.CompletedTask; }
        // The launcher picks the transport: a named pipe for a clone process, an in-memory link for a hive in this process.
        var transport = _launcher.TransportFor(slot.Name);
        var pipe = transport.NewAddress(slot.Name, _cfg.PipeDirectory);
        IHiveEndpoint server;
        int gen;
        lock (slot.Lock)
        {
            gen = ++slot.Generation;
            slot.State = CloneState.Starting;
            slot.Connected = false;
            slot.ByeReceived = false;
            slot.LaunchedAt = Now;
            slot.Gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server = transport.Listen(pipe);
        }
        var wait = JoinDelay();
        if (wait <= TimeSpan.Zero && whitelisted.IsCompleted) { StartProcess(slot, gen, pipe, server); return; }
        if (wait > TimeSpan.Zero) _log.LogInformation("hive: {Clone} waits {S:0.0}s for its login turn (server join throttle)", slot.Name, wait.TotalSeconds);
        _ = Task.Run(async () =>
        {
            try
            {
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, _cts.Token).ConfigureAwait(false);
                try { await whitelisted.ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "hive: whitelisting {Clone} failed", slot.Name); }
            }
            catch (OperationCanceledException) { server.Dispose(); return; }
            bool go;
            lock (slot.Lock) go = gen == slot.Generation && slot.State == CloneState.Starting;
            if (go) { StartProcess(slot, gen, pipe, server); return; }
            server.Dispose();
            OnGone(slot, gen, clean: true, "recalled before launch");
        });
    }

    private void StartProcess(CloneSlot slot, int gen, string pipe, IHiveEndpoint server)
    {
        ICloneProcess proc;
        try { proc = _launcher.Start(slot.Name, pipe); }
        catch (Exception ex)
        {
            server.Dispose();
            _log.LogWarning(ex, "hive: can't start {Clone}", slot.Name);
            OnGone(slot, gen, clean: false, "start failed: " + ex.Message);
            return;
        }
        lock (slot.Lock) slot.Process = proc;
        if (proc.InProcess) _log.LogInformation("hive: launched {Clone} in this process (hive #{Id}, {Link})", slot.Name, proc.Id, pipe);
        else _log.LogInformation("hive: launched {Clone} (pid {Pid}, pipe {Pipe})", slot.Name, proc.Id, pipe);

        _ = Task.Run(async () =>
        {
            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            startCts.CancelAfter(TimeSpan.FromSeconds(_cfg.StartTimeoutSeconds));
            var exited = proc.WaitForExitAsync(_cts.Token);
            Stream stream;
            try
            {
                var connect = server.AcceptAsync(startCts.Token);
                if (await Task.WhenAny(connect, exited).ConfigureAwait(false) == exited || connect.IsFaulted)
                {
                    server.Dispose();
                    if (connect.IsCompletedSuccessfully) connect.Result.Dispose();
                    OnGone(slot, gen, clean: false, $"exited before connecting (code {proc.ExitCode})", proc.ExitCode);
                    return;
                }
                stream = await connect.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                proc.Kill();
                OnGone(slot, gen, clean: false, "didn't connect in time");
                return;
            }
            var link = new HiveLink(stream, slot.Name, _log);
            link.Handler = (m, ct) => HandleAsync(slot, gen, m, ct);
            bool current;
            lock (slot.Lock)
            {
                // A superseded launch (this slot was launched again meanwhile) must never take over the slot's link: the
                // live clone would then get no heartbeats or tasks and give up on main ("main silent for 16s").
                current = gen == slot.Generation;
                if (current) slot.Link = link;
            }
            if (!current)
            {
                _log.LogWarning("hive: a superseded launch of {Clone} connected (pid {Pid}); closing it", slot.Name, proc.Id);
                await link.DisposeAsync().ConfigureAwait(false);
                proc.Kill();
                return;
            }
            link.Closed += reason => OnGone(slot, gen, slot.ByeReceived, slot.ByeReceived ? "logged out" : reason);
            link.Start();
            link.StartHeartbeat(_cfg.Heartbeat); // dedicated thread: a busy main (Opus turn, tools, db) never stalls it
            try { await exited.ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            // Process gone: the pipe closes with it, but don't wait for the read loop to notice.
            OnGone(slot, gen, slot.ByeReceived, slot.ByeReceived ? "logged out" : $"exited (code {proc.ExitCode})", proc.ExitCode);
        });
    }

    /// <summary>The clone's exit code for a configuration error (App startup check): retrying can't help.</summary>
    public const int ConfigErrorExitCode = 2;

    /// <summary>A recalled clone that was given a task meanwhile logs straight back in with it.</summary>
    private void Relaunch(CloneSlot slot, TaskMsg t, string? forPlayer, bool unnamed)
    {
        string? why = null;
        lock (_spawnGate)
        {
            lock (slot.Lock)
            {
                if (slot.State != CloneState.Idle) why = "slot busy";
                else if (ExternalOccupant(slot) is { } occupied) why = occupied;
                else if (Ledger.AtRecallLine(out var b)) why = "budget: " + b;
                else
                {
                    var facts = SlotFacts();
                    int pending = _slots.Values.Count(s => s.State is CloneState.Starting && !facts.Online.Contains(s.Name, StringComparer.OrdinalIgnoreCase));
                    var d = SlotPolicy.CanSpawn(facts, Regulars, pending, _cfg.MaxPlayersFallback);
                    if (!d.Ok) why = d.Reason;
                    else Reserve(slot, t with { Id = Guid.NewGuid().ToString("N")[..8] }, forPlayer, unnamed);
                }
            }
        }
        if (why != null)
        {
            Post(GameEvent.Notice($"[hive] {slot.Number} logged out but couldn't log back in for its new task ({why}): {Trunc(t.Text, 60)}", EventPriority.Normal) with { MustWake = true });
            return;
        }
        _log.LogInformation("hive: {Clone} logs back in with the task it was given while logging out", slot.Name);
        Launch(slot);
    }

    private async Task RestartAfterAsync(CloneSlot slot, TimeSpan backoff)
    {
        try { await Task.Delay(backoff, _time, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        lock (slot.Lock)
        {
            if (slot.State != CloneState.Backoff || slot.Recalled || _shuttingDown) { if (slot.State == CloneState.Backoff) { slot.State = CloneState.Idle; FinallyReleased(slot, "not restarted"); } return; }
            // Resume: the same unfinished task, its loadout and the leases it held.
            if (slot.Task != null)
                slot.Task = slot.Task with { Restarted = true, Loadout = slot.Task.Loadout ?? TaskLoadout.For(slot.Task.Text), Claims = slot.ResumeClaims };
            if (!SlotPolicy.CanSpawn(SlotFacts(), Regulars, 0, _cfg.MaxPlayersFallback).Ok || Ledger.AtRecallLine(out _))
            {
                slot.State = CloneState.Idle;
                FinallyReleased(slot, "not restarted (no slot/budget)");
                Post(GameEvent.Notice($"[hive] {slot.Number} not restarted (no slot/budget)", EventPriority.Background));
                return;
            }
            if (ExternalOccupant(slot) is { } occupied)
            {
                slot.State = CloneState.Idle;
                FinallyReleased(slot, "not restarted: " + occupied);
                Post(GameEvent.Notice($"[hive] {slot.Number} not restarted: it {occupied}" + (slot.Task is { } ut ? $"; its task \"{Trunc(ut.Text, 60)}\" needs another clone" : ""),
                    EventPriority.Normal));
                return;
            }
        }
        _log.LogInformation("hive: restarting {Clone} after {S:0}s backoff", slot.Name, backoff.TotalSeconds);
        Launch(slot);
    }

    /// <summary>A restarted clone gets back the leases it held when it was lost (those still free).</summary>
    private void ReclaimFor(CloneSlot slot)
    {
        string[]? keys;
        lock (slot.Lock) { keys = slot.Task?.Restarted == true ? slot.ResumeClaims : null; slot.ResumeClaims = null; }
        if (keys is not { Length: > 0 }) return;
        int n = 0;
        foreach (var id in keys)
            if (ClaimKey.TryParse(id, out var key) && Claims.TryClaim(slot.Name, key).Ok) n++;
        _log.LogInformation("hive: {Clone} restarted: re-granted {N}/{Total} claim(s)", slot.Name, n, keys.Length);
    }

    private void UpdateLive() => Arbiter.SetLive(_slots.Values.Where(s => s.IsLive).Select(s => s.Name));

    // ================================================================ messages from a clone

    /// <summary>Longest main stands still for one hand-off (the clone walks over, tosses, waits 2 s, confirms).</summary>
    public static readonly TimeSpan HandoffHoldTime = TimeSpan.FromSeconds(15);

    // ================================================================ hand-offs to clones (lend)

    /// <summary>
    /// A clone lacks an item its storage doesn't have (live 3, 14:16:58: 67 "no pickaxe, no axe" while main carried 2 stone
    /// pickaxes): main offers what it has spare (keeping the one in hand / its best one) and stands still until the clone,
    /// next to it, says go; otherwise the reply names other live hive bots carrying a spare (hive_inventory / live snapshots).
    /// </summary>
    internal LendReplyMsg Lend(CloneSlot slot, LendMsg lm)
    {
        var ids = lm.Ids is { Length: > 0 } only ? only : HiveLending.Resolve(lm.Item);
        if (ids == null) return new LendReplyMsg(false, 0, 0, null, 0, 0, 0, $"unknown item '{lm.Item}'");
        var holders = SpareHolders(slot, ids);
        if (Donor is not { } d) return new LendReplyMsg(false, 0, 0, null, 0, 0, 0, "main can't hand items over", holders);
        var pick = HiveLending.Spare(d.Items(), lm.Item, Math.Max(1, lm.Count), d.HeldId, d.Reserved, ids);
        if (pick == null)
        {
            int has = HiveLending.Have(d.Items(), ids);
            return new LendReplyMsg(false, 0, 0, null, 0, 0, 0, has > 0 ? $"main has only {has} {lm.Item} (it keeps one)" : $"main has no {lm.Item}", holders);
        }
        var hold = d.HoldStill(HandoffHoldTime);
        if (d.Position is not { } pos) { hold?.Dispose(); return new LendReplyMsg(false, 0, 0, null, 0, 0, 0, "main can't hand items over now", holders); }
        lock (slot.Lock)
        {
            slot.LendHold?.Dispose();
            slot.LendHold = hold;
            slot.PendingLend = pick;
        }
        _log.LogInformation("hive: offering {Clone} {N} {Item}", slot.Name, pick.Count, pick.Name);
        return new LendReplyMsg(true, pick.Id, pick.Count, pick.Name, pos.X, pos.Y, pos.Z, null);
    }

    private async Task<LendDoneMsg> LendGoAsync(CloneSlot slot, LendGoMsg go, CancellationToken ct)
    {
        HiveLending.Pick? pick;
        IDisposable? hold;
        lock (slot.Lock) { pick = slot.PendingLend; hold = slot.LendHold; slot.PendingLend = null; slot.LendHold = null; }
        try
        {
            if (pick == null || Donor is not { } d) return new LendDoneMsg(0, "nothing offered");
            int tossed;
            try { tossed = await d.TossAsync(pick.Id, pick.Count, new Vec3d(go.X, go.Y, go.Z), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "hive: toss to {Clone} failed", slot.Name); tossed = 0; }
            _log.LogInformation("hive: tossed {N}/{Want} {Item} to {Clone}", tossed, pick.Count, pick.Name, slot.Name);
            if (tossed > 0) Post(GameEvent.Notice($"[hive] handed {slot.Number} {tossed} {pick.Name} (its loadout)", EventPriority.Background));
            return new LendDoneMsg(tossed, tossed > 0 ? null : "toss failed");
        }
        finally { hold?.Dispose(); }
    }

    /// <summary>"69 (2 stone axe)": other live clones carrying at least two of the kind (one spare).</summary>
    private string[] SpareHolders(CloneSlot asker, int[] ids)
    {
        var list = new List<string>();
        foreach (var s in _slots.Values)
        {
            if (ReferenceEquals(s, asker) || !s.IsLive) continue;
            List<ItemMsg>? inv = s.Inventory;
            if (inv == null) try { inv = Store?.Inventory(s.Name).ToList(); } catch (Exception) { }
            var mine = (inv ?? []).Where(i => ids.Contains(i.Id)).ToList();
            int n = mine.Sum(i => i.Count);
            if (n >= 2) list.Add($"{s.Number} ({n} {mine[0].Name})");
        }
        return list.ToArray();
    }

    // ================================================================ broadcasts, heartbeats, policies

    /// <summary>Sends a notification to every connected clone.</summary>
    /// <summary>
    /// "Send me your view": the live clone flushes its world memory to the shared db (fresh columns, stamped with its name)
    /// and answers with its eye pose. Null when it isn't connected or doesn't answer within <paramref name="timeout"/>.
    /// </summary>
    public async Task<CloneViewMsg?> RequestViewAsync(string name, TimeSpan timeout, CancellationToken ct = default)
    {
        if (Resolve(name) is not { } n || !_slots.TryGetValue(n, out var slot)) return null;
        HiveLink? link;
        lock (slot.Lock) link = slot.Connected ? slot.Link : null;
        if (link is not { IsOpen: true }) return null;
        try { return await link.RequestAsync<CloneViewMsg>(Msg.View, null, timeout, ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogDebug(ex, "view request to {Clone} failed", n);
            return null;
        }
    }

    /// <summary>A live clone's vision history (the sheets its models got; null when it isn't connected or doesn't answer in time).</summary>
    public Task<VisionListMsg?> RequestVisionListAsync(string name, long since, TimeSpan timeout, CancellationToken ct = default) =>
        RequestCloneAsync<VisionListMsg>(name, Msg.VisionList, new VisionListReq(since), timeout, ct);

    /// <summary>One sheet of a clone's vision history: the exact PNG its model got (null = gone, offline or no answer).</summary>
    public Task<VisionPngMsg?> RequestVisionPngAsync(string name, long id, TimeSpan timeout, CancellationToken ct = default) =>
        RequestCloneAsync<VisionPngMsg>(name, Msg.VisionPng, new VisionPngReq(id), timeout, ct);

    /// <summary>A live clone's brain context (a page since a version, or one turn); null = offline, no capture or no answer in time.</summary>
    public Task<MineClaw.Agent.Telemetry.BrainContextReply?> RequestBrainContextAsync(string name, MineClaw.Agent.Telemetry.BrainContextQuery query, TimeSpan timeout,
        CancellationToken ct = default) =>
        RequestCloneAsync<MineClaw.Agent.Telemetry.BrainContextReply>(name, Msg.BrainContext, query, timeout, ct);

    private async Task<T?> RequestCloneAsync<T>(string name, string type, object payload, TimeSpan timeout, CancellationToken ct) where T : class
    {
        if (Resolve(name) is not { } n || !_slots.TryGetValue(n, out var slot)) return null;
        HiveLink? link;
        lock (slot.Lock) link = slot.Connected ? slot.Link : null;
        if (link is not { IsOpen: true }) return null;
        try { return await link.RequestAsync<T>(type, payload, timeout, ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogDebug(ex, "{Type} request to {Clone} failed", type, n);
            return null;
        }
    }

    /// <summary>A live clone's script debugger snapshot (null when it isn't connected, runs no script or doesn't answer in time).</summary>
    public async Task<Scripting.ScriptDebugSnapshot?> RequestScriptDebugAsync(string name, bool code, TimeSpan timeout, CancellationToken ct = default)
    {
        if (Resolve(name) is not { } n || !_slots.TryGetValue(n, out var slot)) return null;
        HiveLink? link;
        lock (slot.Lock) link = slot.Connected ? slot.Link : null;
        if (link is not { IsOpen: true }) return null;
        try { return await link.RequestAsync<Scripting.ScriptDebugSnapshot>(Msg.ScriptDebug, new ScriptDebugReq(code), timeout, ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogDebug(ex, "script debug request to {Clone} failed", n);
            return null;
        }
    }

    public async Task BroadcastAsync(string type, object? payload)
    {
        foreach (var s in _slots.Values)
            if (s.Link is { IsOpen: true } l && s.Connected) await l.SendAsync(type, payload).ConfigureAwait(false);
    }

    private async Task PushClaimsAsync()
    {
        if (Interlocked.Exchange(ref _claimsPush, 1) == 1) return;
        try { await Task.Delay(50).ConfigureAwait(false); Volatile.Write(ref _claimsPush, 0); await BroadcastAsync(Msg.Claims, Claims.Mirror()).ConfigureAwait(false); }
        finally { Volatile.Write(ref _claimsPush, 0); }
    }

    private async Task PushRosterAsync()
    {
        if (Interlocked.Exchange(ref _rosterPush, 1) == 1) return;
        try { await Task.Delay(20).ConfigureAwait(false); Volatile.Write(ref _rosterPush, 0); await BroadcastAsync(Msg.Roster, Arbiter.ToRoster()).ConfigureAwait(false); }
        finally { Volatile.Write(ref _rosterPush, 0); }
    }

    /// <summary>Forward a console /as line so every live clone's arbiter sees the same message.</summary>
    public Task InjectAsync(string raw) => BroadcastAsync(Msg.Inject, new InjectMsg(raw));

    /// <summary>Forward a web-viewer line (<see cref="InjectMsg.Origin"/> set) to every live clone, the same way.</summary>
    public Task InjectAsync(InjectMsg msg) => BroadcastAsync(Msg.Inject, msg);

    private void Tick()
    {
        // A timer callback: an exception here would end the whole process (and, with in-process hives, every hive in it).
        try { TickSlots(); }
        catch (Exception ex) { _log.LogWarning(ex, "hive: heartbeat tick failed"); }
    }

    private void TickSlots()
    {
        var now = Now;
        foreach (var s in _slots.Values)
        {
            HiveLink? l;
            ICloneProcess? p;
            CloneState st;
            lock (s.Lock) { l = s.Link; p = s.Process; st = s.State; }
            // Heartbeats go out from each link's own thread; here: any frame = alive, long silence -> ping probe -> kill.
            if (st is CloneState.Running or CloneState.Stopping && l is { IsOpen: true } && now - l.LastReceived > _cfg.HeartbeatTimeout
                && Interlocked.CompareExchange(ref s.Probing, 1, 0) == 0)
                _ = ProbeAsync(s, l, p);
        }
    }

    private async Task ProbeAsync(CloneSlot s, HiveLink l, ICloneProcess? p)
    {
        try
        {
            _log.LogInformation("hive: {Clone} silent for {S:0}s; probing", s.Name, (DateTimeOffset.UtcNow - l.LastReceived).TotalSeconds);
            if (await l.ProbeAsync(_cfg.PingTimeout, _cts.Token).ConfigureAwait(false)) return;
            if (!l.IsOpen || !ReferenceEquals(s.Link, l)) return;
            _log.LogWarning("hive: {Clone} silent for {S:0}s and no answer to a ping; killing it", s.Name, (DateTimeOffset.UtcNow - l.LastReceived).TotalSeconds);
            p?.Kill();
        }
        catch (Exception ex) { _log.LogDebug(ex, "hive probe failed"); }
        finally { Volatile.Write(ref s.Probing, 0); }
    }

    private async Task PolicyAsync()
    {
        try
        {
            await CheckBudgetAsync().ConfigureAwait(false);
            var running = _slots.Values.Where(s => s.State == CloneState.Running && s.Connected).ToList();
            if (running.Count == 0) return;
            var yield = SlotPolicy.MustYield(SlotFacts(), Regulars, running.Select(s => new CloneProgress(s.Name, s.Progress, s.Started)), _cfg.MaxPlayersFallback);
            foreach (var n in yield)
            {
                _log.LogInformation("hive: {Clone} logs out to free a slot for a regular", n);
                RecallSlot(_slots[n], "making room for a player");
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "hive policy check failed"); }
    }

    private Task CheckBudgetAsync()
    {
        if (!_slots.Values.Any(s => s.State is CloneState.Running or CloneState.Starting or CloneState.Backoff)) return Task.CompletedTask;
        if (Ledger.AtRecallLine(out var why)) RecallBudget(why);
        return Task.CompletedTask;
    }

    /// <summary>Budget mode dropped (or the recall line was hit): clones go first, furthest from finishing first.</summary>
    public void RecallBudget(string why)
    {
        var order = SlotPolicy.YieldOrder(_slots.Values.Where(s => s.State is CloneState.Running or CloneState.Starting or CloneState.Backoff)
            .Select(s => new CloneProgress(s.Name, s.Progress, s.Started))).ToList();
        if (order.Count == 0) return;
        _log.LogWarning("hive: budget ({Why}): recalling {Clones}", why, string.Join(", ", order.Select(o => o.Name)));
        foreach (var c in order) RecallSlot(_slots[c.Name], "budget");
        Post(GameEvent.Notice($"[hive] budget: recalling {string.Join(", ", order.Select(o => _slots[o.Name].Number))}", EventPriority.Background));
    }

    /// <summary>Order in which clones are recalled when the budget drops (exposed for tests and list output).</summary>
    /// <summary>Clones out now that run inside this process (in-process hives: a brain + one client each, not a whole process).</summary>
    public int RunningInProcess() =>
        _slots.Values.Count(s => s.State is not (CloneState.Idle or CloneState.Retired) && s.Process is { InProcess: true });

    public IReadOnlyList<string> BudgetRecallOrder() =>
        SlotPolicy.YieldOrder(_slots.Values.Where(s => s.State is CloneState.Running or CloneState.Starting)
            .Select(s => new CloneProgress(s.Name, s.Progress, s.Started))).Select(c => c.Name).ToList();

    public async ValueTask DisposeAsync()
    {
        _shuttingDown = true;
        _tick.Dispose();
        _policy.Dispose();
        _cts.Cancel();
        foreach (var s in _slots.Values)
        {
            s.Process?.Kill();
            if (s.Link is { } l) await l.DisposeAsync().ConfigureAwait(false);
        }
    }
}
