using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Telemetry;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Db;
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
    public List<DateTimeOffset> Crashes { get; } = new();
    public BotSnapshot? Snapshot { get; internal set; }
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
}

/// <summary>
/// The hive supervisor (main process only). Spawns clones as separate OS processes (<c>--clone name --parent-pipe pipe</c>),
/// one named pipe each; watches heartbeats (both ways) and process exit; restarts crashed clones with exponential backoff
/// and retires one after <see cref="HiveConfig.MaxCrashes"/> crashes in the window. Hosts the claim board, chat arbiter
/// state and budget ledger for the clones. When a pipe closes the clone is treated as gone: its claims and grants are
/// released at once, an unplanned exit records its last inventory as stranded, and the main bot gets a quiet event.
/// A dying clone never touches the main process: everything here is message passing and process handles.
/// </summary>
public sealed class HiveSupervisor : IAsyncDisposable
{
    private readonly HiveConfig _cfg;
    private readonly string _main;
    private readonly IProcessLauncher _launcher;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, CloneSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _tick;
    private readonly Timer _policy;
    private readonly object _spawnGate = new();
    private int _rosterPush, _claimsPush;
    private volatile bool _shuttingDown;

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

    /// <summary>Events for the main bot's queue (quiet ones are Background: context only, never a wake-up).</summary>
    public event Action<GameEvent>? Notice;
    /// <summary>Console /as lines etc. are forwarded to live clones so every process arbitrates the same message.</summary>
    public IReadOnlyCollection<CloneSlot> Slots => _slots.Values;
    public CloneSlot? Slot(string name) => _slots.GetValueOrDefault(name);

    private DateTimeOffset Now => _time.GetUtcNow();

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

    private string SpawnCore(string task, string? name, bool handover, string? forPlayer, HiveSpawnOptions? opts = null)
    {
        if (!_cfg.Enabled) return "error: hive is off";
        if (_shuttingDown) return "error: shutting down";
        task = (task ?? "").Trim();
        string? profile = string.IsNullOrWhiteSpace(opts?.Profile) ? null : opts!.Profile!.Trim();
        if (profile != null && (ProfileCheck is not { } check ? "no model profiles here" : check(profile)) is { } badProfile)
            return "error: " + badProfile;
        string? script = null, code = null;
        if (opts is { HasScript: true })
        {
            if (ScriptPrep is not { } prep) return "error: scripts can't be spawned here";
            var (saved, err, c) = prep(opts.Script, opts.Code);
            if (err != null) return "error: " + err; // lint findings go back to the brain; nothing spawns
            (script, code) = (saved, c);
            if (task.Length == 0) task = $"script {script}" + (opts.Args is { Length: > 2 } a ? " " + a : "");
        }
        if (task.Length == 0) return "error: task required";
        if (task.Length > 400) task = task[..400];
        if (Ledger.AtRecallLine(out var why)) return "no: budget (" + why + ")";

        CloneSlot? slot;
        if (name != null)
        {
            var r = Resolve(name);
            if (r == null) return $"error: no clone '{name}' (have {string.Join(", ", _slots.Values.Select(s => s.Number))})";
            slot = _slots[r];
        }
        else slot = _slots.Values.FirstOrDefault(s => s.State == CloneState.Idle);

        // Ownership covers a player's CONVERSATION about a request, not every task done for them (10-27: "67 already owns
        // PoofimaFox's request" refused a tree clone, both spar clones, and made main recall 67 mid-job). Main may add clones
        // to the same player's new or parallel request (a spar needs two). Refused without handover: an UNNAMED spawn of the
        // very request the clone holding the player's conversation (thread lease or sticky) is on, and any spawn of it while
        // that clone is asking the player about it right now (thread lease: its answer is coming).
        CloneSlot? owner = forPlayer != null ? OwnerSlot(forPlayer) : null;
        bool asking = false;
        CloneSlot? talking = forPlayer != null ? ConversationSlot(forPlayer, out asking) : null;
        if (!handover && talking != null && !ReferenceEquals(talking, slot) && talking.Task is { } talkTask && SameRequest(talkTask.Text, task))
        {
            if (asking)
                return $"no: {talking.Number} is asking {forPlayer} about this request right now; let it finish, or op handover to move it to {slot?.Number ?? name ?? "another clone"}";
            if (name == null)
                return $"no: {talking.Number} is talking with {forPlayer} about this request; give it to {talking.Number} (name {talking.Number}), name another clone to add a helper, or op handover";
        }
        if (slot == null) return "no: all clones busy or retired";
        if (handover && owner == null) owner = talking;
        CloneSlot? previous = handover && owner != null && !ReferenceEquals(owner, slot) ? owner : null;

        var t = new TaskMsg(Guid.NewGuid().ToString("N")[..8], task, Loadout: TaskLoadout.For(code != null ? task + " " + code : task),
            Profile: profile, Script: script, Args: script != null ? opts?.Args : null, After: script != null ? opts?.After : null);
        // Hive task texts were never logged (10-27: the ownership refusals couldn't be traced).
        _log.LogInformation("hive: task {Id} for {Clone} ({Player}{Named}): {Text}", t.Id, slot.Name, forPlayer ?? "-", name == null ? ", unnamed" : "", task);
        try { ResetClone?.Invoke(slot.Name); } catch (Exception ex) { _log.LogWarning(ex, "hive: reset of {Clone}'s hive order failed", slot.Name); }
        TaskMsg? displaced = null;
        string? displacedFor = null;
        bool launch = false, respawnQueued = false;
        lock (slot.Lock)
        {
            switch (slot.State)
            {
                case CloneState.Retired: return $"no: {slot.Number} is retired ({slot.Crashes.Count} crashes)";
                case CloneState.Stopping:
                    // Recalled (e.g. "recall 69" and "69: craft picks" in one turn, either order): the task waits for the logout
                    // and the clone logs straight back in with it, instead of "got the new task" being lost (10-27 07:19).
                    if (!slot.BrainRecall || _shuttingDown) return $"no: {slot.Number} is logging out";
                    slot.RespawnWith = (t, forPlayer, name == null);
                    slot.AssignedTurn = MineClaw.Agent.TurnContext.Current;
                    respawnQueued = true;
                    break;
                case CloneState.Running or CloneState.Starting or CloneState.Backoff:
                    // Named into a clone the supervisor just picked for ANOTHER unnamed job (same turn, parallel tool calls):
                    // that job moves to a free clone instead of being silently dropped.
                    if (name != null && slot.Unnamed && slot.Pending && slot.Task is { } old && !SameRequest(old.Text, task)
                        && Now - slot.AssignedAt < TimeSpan.FromMinutes(2))
                    {
                        displaced = old;
                        displacedFor = slot.ForPlayer;
                    }
                    slot.Task = t;
                    slot.ResultIn = false;
                    slot.ResultText = null;
                    slot.ForPlayer = forPlayer;
                    slot.Unnamed = name == null;
                    slot.AssignedAt = Now;
                    slot.AssignedTurn = MineClaw.Agent.TurnContext.Current;
                    slot.Progress = 0;
                    slot.LastProgress = null;
                    if (slot.Link is { IsOpen: true } l) _ = l.SendAsync(Msg.Task, t);
                    break;
                default:
                    var facts = SlotFacts();
                    int pending = _slots.Values.Count(s => s.State is CloneState.Starting && !facts.Online.Contains(s.Name, StringComparer.OrdinalIgnoreCase));
                    var d = SlotPolicy.CanSpawn(facts, Regulars, pending, _cfg.MaxPlayersFallback);
                    if (!d.Ok) return "no: " + d.Reason;
                    Reserve(slot, t, forPlayer, name == null);
                    launch = true;
                    break;
            }
        }
        if (respawnQueued)
            return $"ok: {slot.Number} is logging out first (recalled); it logs straight back in with this task (pending until its result arrives)";
        if (launch)
        {
            Launch(slot);
            AfterAssign(slot, previous, forPlayer, task);
            NotifyGroup(slot, forPlayer, task);
            return $"ok: {slot.Number} spawning (its part is pending until its result arrives)";
        }
        string moved = "";
        if (displaced != null)
        {
            var r = SpawnCore(displaced.Text, null, handover: false, displacedFor,
                new HiveSpawnOptions(displaced.Profile, displaced.Script, null, displaced.Args, displaced.After));
            moved = r.StartsWith("ok", StringComparison.Ordinal)
                ? $"; its earlier job ({Trunc(displaced.Text, 40)}) moved: {r[4..]}"
                : $"; its earlier job ({Trunc(displaced.Text, 40)}) was dropped ({r})";
        }
        AfterAssign(slot, previous, forPlayer, task);
        NotifyGroup(slot, forPlayer, task);
        return $"ok: {slot.Number} got the new task (pending until its result arrives){moved}";
    }

    // caller holds slot.Lock: a fresh outing for an Idle/Backoff slot (reserved now: nobody else picks it; Launch runs later).
    private void Reserve(CloneSlot slot, TaskMsg t, string? forPlayer, bool unnamed)
    {
        slot.Task = t;
        slot.ResultIn = false;
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
        if (previous == null || forPlayer == null) return;
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
    public string GuardSpeech(string text)
    {
        text = GuardArrivals(text ?? "");
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
            if (s.State == CloneState.Backoff) { s.State = CloneState.Idle; s.RespawnWith = null; return; }
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
                _ => "idle",
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
                if (s.Progress > 0) line += $" {s.Progress:P0}";
                if (s.Snapshot?.Pos is { } p) line += $" @{p}";
            }
            if (s.Deaths > 0) line += $" died x{s.Deaths}";
            return line;
        });
        var (d, _) = Ledger.ClonesSpend();
        return string.Join(" | ", parts) + $" | clones today ${d:0.00}";
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
        return w switch
        {
            "" => "working",
            "mine" or "dig" or "harvest" or "grind" => "mining",
            "chop" or "cut" => "chopping",
            "craft" or "make" => "crafting",
            "build" or "place" => "building",
            "smelt" or "cook" => "smelting",
            "organize" or "organise" or "sort" => "sorting",
            "deposit" or "store" or "stash" => "storing",
            "fetch" or "get" or "bring" or "collect" or "gather" => "fetching",
            "farm" or "plant" => "farming",
            _ => w.EndsWith('e') ? w[..^1] + "ing" : w + "ing",
        };
    }

    private static string CloneActivity(CloneSlot s)
    {
        if (s.State == CloneState.Stopping) return "logging out";
        var task = s.Snapshot?.Task;
        if (string.IsNullOrWhiteSpace(task) || task.StartsWith("idle", StringComparison.OrdinalIgnoreCase)) return "idle";
        return "busy: " + Trunc(task, 30);
    }

    // ================================================================ process lifecycle

    private readonly object _joinGate = new();
    private DateTimeOffset _nextJoin;

    /// <summary>
    /// The b1.8.1 server's connection throttle rejects logins from one IP less than ~4 s apart (10-27: two clones launched
    /// in the same millisecond, one got "server closed the connection" and lost 6-10 s). Launches are spaced by
    /// <see cref="HiveConfig.LaunchGapSeconds"/>: returns how long this launch waits for its turn.
    /// </summary>
    internal TimeSpan JoinDelay()
    {
        var gap = TimeSpan.FromSeconds(Math.Max(0, _cfg.LaunchGapSeconds));
        lock (_joinGate)
        {
            var now = Now;
            var at = _nextJoin > now ? _nextJoin : now;
            _nextJoin = at + gap;
            return at - now;
        }
    }

    private void Launch(CloneSlot slot)
    {
        var pipe = PipeNames.For(slot.Name, _cfg.PipeDirectory);
        System.IO.Pipes.NamedPipeServerStream server;
        int gen;
        lock (slot.Lock)
        {
            gen = ++slot.Generation;
            slot.State = CloneState.Starting;
            slot.Connected = false;
            slot.ByeReceived = false;
            slot.LaunchedAt = Now;
            slot.Gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server = PipeNames.Server(pipe);
        }
        var wait = JoinDelay();
        if (wait <= TimeSpan.Zero) { StartProcess(slot, gen, pipe, server); return; }
        _log.LogInformation("hive: {Clone} waits {S:0.0}s for its login turn (server join throttle)", slot.Name, wait.TotalSeconds);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(wait, _time, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { server.Dispose(); return; }
            bool go;
            lock (slot.Lock) go = gen == slot.Generation && slot.State == CloneState.Starting;
            if (go) { StartProcess(slot, gen, pipe, server); return; }
            server.Dispose();
            OnGone(slot, gen, clean: true, "recalled before launch");
        });
    }

    private void StartProcess(CloneSlot slot, int gen, string pipe, System.IO.Pipes.NamedPipeServerStream server)
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
        _log.LogInformation("hive: launched {Clone} (pid {Pid}, pipe {Pipe})", slot.Name, proc.Id, pipe);

        _ = Task.Run(async () =>
        {
            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            startCts.CancelAfter(TimeSpan.FromSeconds(_cfg.StartTimeoutSeconds));
            var exited = proc.WaitForExitAsync(_cts.Token);
            try
            {
                var connect = server.WaitForConnectionAsync(startCts.Token);
                if (await Task.WhenAny(connect, exited).ConfigureAwait(false) == exited || !server.IsConnected)
                {
                    server.Dispose();
                    OnGone(slot, gen, clean: false, $"exited before connecting (code {proc.ExitCode})", proc.ExitCode);
                    return;
                }
                await connect.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                proc.Kill();
                OnGone(slot, gen, clean: false, "didn't connect in time");
                return;
            }
            var link = new HiveLink(server, slot.Name, _log);
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

    private void OnGone(CloneSlot slot, int gen, bool clean, string reason, int? exitCode = null)
    {
        bool restart = false;
        (TaskMsg Task, string? ForPlayer, bool Unnamed)? respawn = null;
        TimeSpan backoff = default;
        TaskCompletionSource? gone;
        List<ItemMsg>? inv;
        lock (slot.Lock)
        {
            if (gen != slot.Generation || slot.State is CloneState.Idle or CloneState.Retired or CloneState.Backoff) return; // already handled
            slot.HandoffHold?.Dispose();
            slot.HandoffHold = null;
            gone = slot.Gone;
            inv = slot.Inventory;
            slot.Connected = false;
            slot.LastExit = reason;
            var link = slot.Link;
            slot.Link = null;
            if (link != null) _ = link.DisposeAsync().AsTask();
            slot.Process?.Kill();
            slot.Process?.Dispose();
            slot.Process = null;
            slot.ResumeClaims = Claims.Leases().Where(l => l.Owner.Equals(slot.Name, StringComparison.OrdinalIgnoreCase))
                .Select(l => l.Key.Id).ToArray();
            if (clean || slot.Recalled)
            {
                slot.State = CloneState.Idle;
                respawn = _shuttingDown ? null : slot.RespawnWith;
                slot.RespawnWith = null;
            }
            else if (exitCode == ConfigErrorExitCode)
            {
                // A config error (exit 2) was retried 3x as a "crash" (10-27 live2): fail fast instead.
                slot.State = CloneState.Retired;
                reason += ": configuration error, not restarted (fix the config)";
            }
            else
            {
                var now = Now;
                slot.Crashes.Add(now);
                slot.Crashes.RemoveAll(t => now - t > TimeSpan.FromMinutes(_cfg.CrashWindowMinutes));
                if (slot.Crashes.Count >= _cfg.MaxCrashes) slot.State = CloneState.Retired;
                else if (slot.Task != null && !slot.ResultIn && !_shuttingDown) // its last task is unfinished (and not cancelled)
                {
                    slot.State = CloneState.Backoff;
                    restart = true;
                    backoff = TimeSpan.FromSeconds(Math.Min(_cfg.BackoffMaxSeconds, _cfg.BackoffBaseSeconds * Math.Pow(2, slot.Crashes.Count - 1)));
                }
                else slot.State = CloneState.Idle;
            }
        }
        // Immediately: the dead clone's leases, grants and conversations go.
        int released = Claims.ReleaseAll(slot.Name);
        Ledger.Close(slot.Name);
        Arbiter.Forget(slot.Name);
        UpdateLive();
        if (clean && !(slot.Recalled && !slot.ByeReceived))
        {
            Post(GameEvent.Notice($"[hive] {slot.Number} logged out ({slot.RecallReason ?? reason})", EventPriority.Background));
        }
        else
        {
            int stranded = 0;
            try { stranded = Store?.MarkStranded(slot.Name, reason, inv) ?? 0; }
            catch (Exception ex) { _log.LogWarning(ex, "hive: couldn't record {Clone}'s stranded inventory", slot.Name); }
            var what = slot.State == CloneState.Retired ? (exitCode == ConfigErrorExitCode ? "; retired" : $"; retired after {slot.Crashes.Count} crashes")
                : restart ? $"; restarting in {backoff.TotalSeconds:0}s" : "";
            Post(GameEvent.Notice($"[hive] {slot.Number} lost ({reason}): {released} claim(s) released" +
                (stranded > 0 ? $", {stranded} item(s) stranded" : "") + what,
                slot.State == CloneState.Retired ? EventPriority.Low : EventPriority.Background));
        }
        gone?.TrySetResult();
        if (restart) _ = RestartAfterAsync(slot, backoff);
        if (respawn is { } rw) Relaunch(slot, rw.Task, rw.ForPlayer, rw.Unnamed);
    }

    /// <summary>A recalled clone that was given a task meanwhile logs straight back in with it.</summary>
    private void Relaunch(CloneSlot slot, TaskMsg t, string? forPlayer, bool unnamed)
    {
        string? why = null;
        lock (_spawnGate)
        {
            lock (slot.Lock)
            {
                if (slot.State != CloneState.Idle) why = "slot busy";
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
            Post(GameEvent.Notice($"[hive] {slot.Number} logged out but couldn't log back in for its new task ({why}): {Trunc(t.Text, 60)}", EventPriority.Low));
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
            if (slot.State != CloneState.Backoff || slot.Recalled || _shuttingDown) { if (slot.State == CloneState.Backoff) slot.State = CloneState.Idle; return; }
            // Resume: the same unfinished task, its loadout and the leases it held.
            if (slot.Task != null)
                slot.Task = slot.Task with { Restarted = true, Loadout = slot.Task.Loadout ?? TaskLoadout.For(slot.Task.Text), Claims = slot.ResumeClaims };
            if (!SlotPolicy.CanSpawn(SlotFacts(), Regulars, 0, _cfg.MaxPlayersFallback).Ok || Ledger.AtRecallLine(out _))
            {
                slot.State = CloneState.Idle;
                Post(GameEvent.Notice($"[hive] {slot.Number} not restarted (no slot/budget)", EventPriority.Background));
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

    internal async Task<object?> HandleAsync(CloneSlot slot, int gen, HiveMessage m, CancellationToken ct)
    {
        if (gen != slot.Generation)
        {
            if (m.T == Msg.Hello) throw new InvalidOperationException("superseded launch"); // the stale process exits (no parent)
            return null;
        }
        switch (m.T)
        {
            case Msg.Hello:
                {
                    ReclaimFor(slot);
                    var stranded = Store?.Stranded(slot.Name);
                    lock (slot.Lock)
                    {
                        slot.Connected = true;
                        if (slot.State == CloneState.Starting) slot.State = CloneState.Running;
                    }
                    UpdateLive();
                    _log.LogInformation("hive: {Clone} connected", slot.Name);
                    return new WelcomeMsg(_main, slot.Task, Arbiter.ToRoster(), Claims.Mirror(), stranded != null, stranded?.Count ?? 0);
                }
            case Msg.Heartbeat:
                return null;
            case Msg.Snapshot:
                if (m.D is { ValueKind: JsonValueKind.String } sj && BotSnapshot.FromJson(sj.GetString()!) is { } snap) slot.Snapshot = VoxelSendGate.Merge(slot.Snapshot, snap);
                return null;
            case Msg.Usage:
                if (m.D is { ValueKind: JsonValueKind.String } uj && JsonSerializer.Deserialize<PromptUsageRecord>(uj.GetString()!, BotSnapshot.Json) is { } rec)
                    slot.RaiseUsage(rec);
                return null;
            case Msg.Progress when m.As<ProgressMsg>() is { } p:
                slot.LastProgress = p.Text;
                if (p.Pct is { } pct) slot.Progress = Math.Clamp(pct, 0, 1);
                Post(GameEvent.Notice($"[hive] {slot.Number}: {Trunc(p.Text, 160)}", EventPriority.Background));
                return null;
            case Msg.Result when m.As<ResultMsg>() is { } r:
                slot.Progress = 1;
                slot.ResultIn = true;
                slot.ResultText = r.Text;
                slot.ResultOk = r.Ok;
                Post(GameEvent.Notice($"[hive] {slot.Number} {(r.Ok ? "done" : "failed")}: {Trunc(r.Text, 200)}", EventPriority.Low));
                return null;
            case Msg.Claim when m.As<ClaimMsg>() is { } c:
                {
                    if (!ClaimKey.TryParse(c.Key, out var key)) throw new InvalidOperationException("bad claim key " + c.Key);
                    var res = Claims.TryClaim(slot.Name, key, c.TtlSeconds > 0 ? TimeSpan.FromSeconds(c.TtlSeconds) : null, c.Note);
                    return new ClaimReplyMsg(res.Ok, res.Holder, res.Expires.ToUnixTimeMilliseconds());
                }
            case Msg.Release when m.As<ReleaseMsg>() is { } rl:
                if (ClaimKey.TryParse(rl.Key, out var rk)) Claims.Release(slot.Name, rk);
                return null;
            case Msg.SpendAuth when m.As<SpendAuthMsg>() is { } sa:
                {
                    var g = Ledger.Authorize(slot.Name, sa.EstimateUsd);
                    if (!g.Ok) _log.LogInformation("hive: refused spend for {Clone}: {Why}", slot.Name, g.Reason);
                    return g.ToMsg();
                }
            case Msg.Spend when m.As<SpendMsg>() is { } sp:
                Ledger.Record(slot.Name, sp.Usd);
                return null;
            case Msg.ChatAccepted when m.As<ChatAcceptedMsg>() is { } ca:
                Arbiter.AcceptFromClone(ca.Player, slot.Name);
                return null;
            case Msg.Thread when m.As<ThreadMsg>() is { } th:
                if (th.Open) Arbiter.OpenThread(th.Player, slot.Name, TimeSpan.FromSeconds(Math.Clamp(th.TtlSeconds, 5, 900)));
                else Arbiter.CloseThread(th.Player, slot.Name);
                return null;
            case Msg.Inventory when m.As<InventoryMsg>() is { } inv:
                slot.Inventory = inv.Items;
                return null;
            case Msg.Death when m.As<DeathMsg>() is { } dm:
                {
                    slot.Deaths++;
                    int n = Claims.ReleaseAll(slot.Name);
                    slot.Inventory = [];
                    // Main's context and state stay untouched: logged and counted only (the clone records the shared experience).
                    _log.LogWarning("hive: {Clone} died ({Cause}); lost {Lost}; {N} claim(s) released", slot.Name, dm.Cause, HiveStore.FormatItems(dm.Lost), n);
                    return null;
                }
            case Msg.Handoff when m.As<HandoffMsg>() is { } ho:
                {
                    if (Handoff is not { } h) return new HandoffReplyMsg(false, 0, 0, 0, "main can't take items");
                    // Stand still first, then read the feet the clone will toss at (night review B24: main walked on).
                    var hold = h.HoldStill(HandoffHoldTime);
                    if (h.Position is not { } pos) { hold?.Dispose(); return new HandoffReplyMsg(false, 0, 0, 0, "main can't take items"); }
                    lock (slot.Lock) { slot.HandoffHold?.Dispose(); slot.HandoffHold = hold; }
                    h.BeginCounting(slot.Name);
                    return new HandoffReplyMsg(true, pos.X, pos.Y, pos.Z, null);
                }
            case Msg.HandoffAck:
                {
                    int got = Handoff?.Collected(slot.Name) ?? 0;
                    lock (slot.Lock) { slot.HandoffHold?.Dispose(); slot.HandoffHold = null; }
                    return new HandoffAckMsg(got);
                }
            case Msg.Bye when m.As<ByeMsg>() is { } bye:
                lock (slot.Lock) { slot.ByeReceived = true; slot.RecallReason ??= bye.Reason; }
                _log.LogInformation("hive: {Clone} bye ({Reason}, deposited {Dep}, stranded {N})", slot.Name, bye.Reason, bye.Deposited, bye.Stranded);
                if (!bye.Deposited && bye.Stranded > 0)
                    Post(GameEvent.Notice($"[hive] {slot.Number} logged out holding {bye.Stranded} item(s): no storage space", EventPriority.Low));
                return null;
            default:
                await Task.CompletedTask.ConfigureAwait(false);
                return null;
        }
    }

    // ================================================================ broadcasts, heartbeats, policies

    /// <summary>Sends a notification to every connected clone.</summary>
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

    private void Tick()
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
