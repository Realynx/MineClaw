using MineClaw.Agent.Events;
using MineClaw.Hive.Hierarchy.Bus;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;
using MineClaw.Hive.Supervisor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Hierarchy;

/// <summary>A spawn as the brain (hive tool) or another hive (bus) asks for it.</summary>
/// <param name="Bodies">kind=subhive: how many bodies; kind=hive: how many subhive bodies the new hive hosts and its brain drives.</param>
/// <param name="Reset">A hive (re)starting asks for its bodies: accounts it held before (gone with its old process) are reused first.</param>
/// <param name="Crew">kind=subhive for a hive: its own crew the new bodies join as they log in (passed on in each <see cref="Bus.HostReq"/>).</param>
public sealed record SpawnRequest(string? Kind = null, string? Brain = null, string? Task = null, string? Script = null, string? Code = null,
    string? Args = null, string? After = null, string? Name = null, bool Handover = false, string? Profile = null, int? Bodies = null, bool Reset = false,
    string? Crew = null);

/// <summary>
/// Main's hive hierarchy: the clone kinds (hive / subhive / script), the ownership tree under main, the dynamic accounts (allocate
/// → whitelist add → spawn → work → deposit → disconnect → whitelist remove) and the bus requests other hives send. Main is
/// always the admin root: only main allocates accounts and only main touches the whitelist, whoever owns the clone.
/// <para>Subhive bodies live in their owner's process: main's own (<see cref="Subhives"/>) or a hive-kind clone's, which main asks
/// over the bus (<see cref="BusTypes.Host"/>) once the account is allocated and whitelisted; the hive reports each one gone
/// (<see cref="BusTypes.SubhiveGone"/>) and main takes it off the whitelist.</para>
/// </summary>
public sealed class HiveHierarchy : IHiveAccounts
{
    private readonly HiveAccountsConfig _cfg;
    private readonly string _main;
    private readonly AccountAllocator _allocator;
    private readonly Func<IReadOnlyCollection<string>> _regulars;
    private readonly Func<IReadOnlyCollection<string>> _knownPlayers;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, TaskCompletionSource> _gone = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxPlayersFallback;

    public HiveHierarchy(HiveAccountsConfig cfg, string main, ManagedAccounts managed, WhitelistManager whitelist, int maxClones = 0,
        Func<IReadOnlyCollection<string>>? regulars = null, Func<IReadOnlyCollection<string>>? knownPlayers = null, ILogger? log = null,
        int maxPlayersFallback = 7)
    {
        _cfg = cfg;
        _main = main;
        Managed = managed;
        Whitelist = whitelist;
        Tree = new HiveTree(main);
        _allocator = new AccountAllocator(cfg, managed, main, maxClones, maxPlayersFallback);
        _maxPlayersFallback = maxPlayersFallback;
        _regulars = regulars ?? (() => []);
        _knownPlayers = knownPlayers ?? (() => []);
        _log = log ?? NullLogger.Instance;
    }

    public ManagedAccounts Managed { get; }
    public WhitelistManager Whitelist { get; }
    public HiveTree Tree { get; }
    public IHiveBodies? Bodies { get; set; }
    /// <summary>Main's own subhive bodies (in this process). Null = <c>hive spawn kind=subhive</c> for main says not available.</summary>
    public ISubhiveHost? Subhives { get; set; }
    /// <summary>Hive scripts over main's subhive bodies (<c>hive spawn kind=script bodies=N</c>); null = not available.</summary>
    public IHiveScriptHost? ScriptHives { get; set; }
    /// <summary>
    /// A crew script for a hive spawned straight onto it (<c>brain=… bodies=N script=X</c>): a saved name, or inline code the hive tool
    /// already authorised as the Opus brain's (linted and saved here on main, so a DeepSeek hive only runs saved code) → (name, error).
    /// Null = such spawns are refused.
    /// </summary>
    public Func<string?, string?, (string? Name, string? Error)>? CrewPrep { get; set; }
    private readonly Dictionary<string, string> _scripts = new(StringComparer.OrdinalIgnoreCase); // running hive script id → script name
    /// <summary>Hive-kind clones may host subhive bodies in their own process (accounts from main, over the bus).</summary>
    public bool RemoteSubhives { get; set; }
    /// <summary>Is a model profile usable (null = yes, else why not)? Decides whether a DeepSeek brain is available.</summary>
    public Func<string, string?>? ProfileCheck { get; set; }
    /// <summary>Quiet notices for main's brain (bus status/results/help). Never chat.</summary>
    public Action<GameEvent>? Notice { get; set; }
    public HiveBus? Bus { get; set; }
    public MemoryBusHost? Memory { get; set; }
    /// <summary>The resource-aware ceiling (memory headroom from the cgroup limit); null = only the player cap and the hard cap.</summary>
    public ResourceCeiling? Resources { get; set; }
    /// <summary>Most bodies one spawn may ask for (<c>Hive:Subhives:MaxPerSpawn</c>).</summary>
    public int MaxBodiesPerSpawn { get; set; } = 24;
    /// <summary>Spacing between logins (only for the spawn answer: "logging in 4.5s apart").</summary>
    public double LaunchGapSeconds { get; set; } = 4.5;
    public int LaunchBatch { get; set; } = 10;

    /// <summary>"logging in 10 at a time" / "logging in 4.5s apart" (the spawn reply).</summary>
    private string Pacing(int n) => n <= 1 ? "" : LaunchBatch > 1 ? $", logging in {Math.Min(n, LaunchBatch)} at a time" : $", logging in {LaunchGapSeconds:0.#}s apart";
    /// <summary>A new account was allocated (the arbiter/personality learn the name).</summary>
    public event Action<string>? Allocated;

    private string Short(string account) => account.Equals(HiveTree.Root, StringComparison.OrdinalIgnoreCase) ? "main" : HiveNode.ShortOf(account);

    /// <summary>What the hive runs now (the resource ceiling's input): clone processes and subhive bodies anywhere.</summary>
    public ResourceUse Use() => new(Bodies?.Running().Count ?? 0, Tree.All().Count(n => n.Kind == CloneKind.Subhive), InProcRunning?.Invoke() ?? 0);

    /// <summary>Does a new hive with this per-spawn override and brain profile run inside main's process (<c>Hive:Isolation</c>)?
    /// Null = every hive is a process of its own (the resource ceiling prices it as one).</summary>
    public Func<string?, string?, bool>? RunsInProcess { get; set; }
    /// <summary>How many clones out now run inside main's process (the ceiling prices them as a brain + one client).</summary>
    public Func<int>? InProcRunning { get; set; }

    private bool InProc(string? isolation, string? profile) => RunsInProcess?.Invoke(isolation, profile) ?? false;

    // ================================================================ startup

    /// <summary>
    /// Adopts old fixed-roster names once and removes every stale managed account from the whitelist. Stale means: not running as a
    /// clone process (<paramref name="isRunning"/>), not a node of the ownership tree (a subhive body in any process, a Story Mode pawn
    /// logging in) and not claimed by an owner (<see cref="AddOwner"/>: a saved colony's pawns, spawned or still waiting for a seat).
    /// When <see cref="ExpectClaims"/> was called it first waits for <see cref="ClaimsReady"/> (at most <see cref="ClaimsWait"/>), so
    /// the owners have loaded before anything is judged stale.
    /// </summary>
    public async Task<int> StartAsync(IEnumerable<string> legacyNames, Func<string, bool> isRunning, CancellationToken ct = default)
    {
        if (_cfg.AdoptLegacy && Whitelist.Adopt(legacyNames) is > 0 and var n)
            _log.LogInformation("hive: adopted {N} old fixed clone name(s) into the managed list (Hive:CloneUsernames is ignored now)", n);
        if (!_cfg.ReconcileOnStart) return 0;
        if (Volatile.Read(ref _claims) is { } claims)
            try { await claims.Task.WaitAsync(ClaimsWait, ct).ConfigureAwait(false); }
            catch (TimeoutException) { _log.LogWarning("hive: startup reconcile: the owners didn't load within {S:0}s; reconciling anyway", ClaimsWait.TotalSeconds); }
        return await Whitelist.ReconcileAsync(a => isRunning(a) || Tree.Get(a) != null || OwnedElsewhere(a), ct).ConfigureAwait(false);
    }

    private readonly List<Func<string, bool>> _owners = new();
    private TaskCompletionSource? _claims;

    /// <summary>Longest the startup reconcile waits for <see cref="ClaimsReady"/>.</summary>
    public TimeSpan ClaimsWait { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Something outside the tree owns managed accounts (Story Mode: every pawn of an active colony, online or not). The startup
    /// reconcile never releases or un-whitelists them.
    /// </summary>
    public void AddOwner(Func<string, bool> owns) { lock (_owners) _owners.Add(owns); }

    /// <summary>Is the account claimed by an owner (<see cref="AddOwner"/>)?</summary>
    public bool OwnedElsewhere(string account)
    {
        Func<string, bool>[] owners;
        lock (_owners) owners = _owners.ToArray();
        foreach (var o in owners)
        {
            try { if (o(account)) return true; }
            catch (Exception ex) { _log.LogDebug(ex, "hive: owner check for {Account} failed", account); return true; } // in doubt, keep it
        }
        return false;
    }

    /// <summary>The startup reconcile waits for <see cref="ClaimsReady"/> (main's process: the saved colonies load first).</summary>
    public void ExpectClaims() => Interlocked.CompareExchange(ref _claims, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null);

    /// <summary>Every owner has loaded and registered its accounts: the startup reconcile may run.</summary>
    public void ClaimsReady() => Volatile.Read(ref _claims)?.TrySetResult();

    // ================================================================ a body's login trouble (whitelist kicks, the player cap)

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _notes = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _rewhitelistWarned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Is main op (1.12.2 says; null = unknown)? It decides the message when a whitelist add fails.</summary>
    public Func<bool?>? MainIsOp { get; set; }

    /// <summary>Why a hosted body isn't in game ("not whitelisted — re-adding"); null clears it (it got in).</summary>
    public void NoteBody(string account, string? why)
    {
        if (why == null) _notes.TryRemove(account, out _);
        else _notes[account] = why;
    }

    /// <summary>The last login trouble noted for a body, or null.</summary>
    public string? BodyNote(string account) => _notes.GetValueOrDefault(account);

    /// <summary>
    /// A managed body was kicked "You are not white-listed": main adds it again (the gate's scoped, armed exception; only an account of
    /// the tree or of an owner, never main or a stranger). Null = added (confirmed when replies are fed), else why not: main isn't op,
    /// or the server didn't confirm. The body's note says so (Story Mode shows it as the pawn's offline reason).
    /// </summary>
    public async Task<string?> RewhitelistAsync(string account, CancellationToken ct = default)
    {
        account = (account ?? "").Trim();
        if (!Whitelist.Accepts(account)) return "not a managed account";
        if (Tree.Get(account) == null && !OwnedElsewhere(account)) return "not one of the hive's bodies";
        NoteBody(account, "not whitelisted — re-adding");
        bool ok;
        try { ok = await Whitelist.AddAsync(account, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "hive: re-whitelisting {Account} failed", account); ok = false; }
        if (ok)
        {
            NoteBody(account, "connecting");
            _log.LogInformation("hive: {Account} was kicked as not whitelisted; added it again", account);
            return null;
        }
        var why = MainIsOp?.Invoke() == false
            ? $"main ({_main}) isn't op, so it can't whitelist {account}: op {_main} on the server"
            : $"the server didn't confirm /whitelist add {account}";
        NoteBody(account, "not whitelisted — " + why);
        var now = DateTimeOffset.UtcNow;
        if (!_rewhitelistWarned.TryGetValue(account, out var last) || now - last > TimeSpan.FromMinutes(5))
        {
            _rewhitelistWarned[account] = now;
            _log.LogWarning("hive: {Account} isn't whitelisted and main can't add it: {Why}", account, why);
        }
        return why;
    }

    /// <summary>
    /// The server's seats as the slot policy sees them now: free = max players − online − logging in; kept = one per offline regular.
    /// Null without a bodies host.
    /// </summary>
    public (int Free, int Kept)? Seats()
    {
        if (Bodies is not { } b) return null;
        var f = Facts(b);
        var d = SlotPolicy.CanSpawn(f.Slots, f.Regulars, f.Pending, _maxPlayersFallback);
        return (Math.Max(0, d.Free), d.Reserved);
    }

    /// <summary>
    /// "server full (player cap: 2 free, 2 kept for regulars)" (the regulars' seats would fit it: the owner may choose over_cap) or
    /// "server full (7/7 seats taken)" (nothing would): why one more can't log in.
    /// </summary>
    public static string CapReason(SlotDecision d, SlotFacts f, int fallback)
    {
        int cap = SlotPolicy.Capacity(f, fallback);
        return d.Free <= 0 ? $"server full ({cap - Math.Max(0, d.Free)}/{cap} seats taken)"
            : $"server full (player cap: {d.Free} free, {d.Reserved} kept for regulars)";
    }

    // ================================================================ IHiveAccounts (the supervisor's seam)

    public (string? Name, string? Why) Allocate(HiveSpawnOptions? options)
    {
        if (Bodies is not { } b) return (null, "no bodies host");
        bool body = HiveKinds.ParseKind(options?.Kind).Kind == CloneKind.Subhive;
        if (Resources?.CanAdd(body ? 0 : 1, body ? 1 : 0, inProcess: !body && InProc(options?.Isolation, options?.Profile)) is { Ok: false } full)
            return (null, full.Why);
        Allocation a;
        lock (_lock)
        {
            a = _allocator.Allocate(Facts(b));
            if (a.Ok) Managed.Upsert(a.Name!, x => x); // known from now on: the whitelist exception accepts it
        }
        if (!a.Ok) return (null, a.Error);
        _log.LogInformation("hive: allocated account {Name}{Reused}", a.Name, a.Reused ? " (reused)" : "");
        try { Allocated?.Invoke(a.Name!); } catch (Exception ex) { _log.LogDebug(ex, "hive: allocated hook failed"); }
        return (a.Name, null);
    }

    /// <summary>The allocator's facts: subhive bodies anywhere count as running, and those not on the tab list yet as pending.</summary>
    private AllocationFacts Facts(IHiveBodies b)
    {
        var slots = b.Slots();
        var subs = Tree.All().Where(n => n.Kind == CloneKind.Subhive).Select(n => n.Account).ToList();
        int pendingSubs = subs.Count(n => !slots.Online.Contains(n, StringComparer.OrdinalIgnoreCase));
        return new AllocationFacts(slots, _regulars(), _knownPlayers(), [.. b.Running(), .. subs], b.Pending() + pendingSubs);
    }

    /// <summary>
    /// One subhive account for <paramref name="owner"/>: memory, the hard cap and the player cap permitting; recorded in the tree
    /// and the managed list at once (under the lock, so parallel spawns never get the same name).
    /// </summary>
    private (string? Name, string? Why) AllocateBody(string owner, string? task, string? script, string? crew = null)
    {
        if (Bodies is not { } b) return (null, "no bodies host");
        if (Resources?.CanAdd(0, 1) is { Ok: false } full) return (null, full.Why);
        Allocation a;
        lock (_lock)
        {
            a = _allocator.Allocate(Facts(b));
            if (!a.Ok) return (null, a.Error);
            Managed.Upsert(a.Name!, x => x);
            var node = Tree.Put(new HiveNode(a.Name!, CloneKind.Subhive, null, owner) { Task = task, Script = script, Since = DateTimeOffset.UtcNow, Crew = crew });
            Managed.MarkActive(a.Name!, HiveKinds.Name(CloneKind.Subhive), null, node.Owner);
        }
        _log.LogInformation("hive: allocated subhive account {Name} for {Owner}{Reused}", a.Name, owner, a.Reused ? " (reused)" : "");
        try { Allocated?.Invoke(a.Name!); } catch (Exception ex) { _log.LogDebug(ex, "hive: allocated hook failed"); }
        return (a.Name, null);
    }

    public void Assigned(string account, TaskMsg task, HiveSpawnOptions? options)
    {
        var (kind, _) = HiveKinds.ParseKind(options?.Kind);
        var k = kind ?? (task.Script != null ? CloneKind.Script : CloneKind.Hive);
        BrainKind? brain = k == CloneKind.Hive ? (IsDeepSeek(task.Profile) ? BrainKind.DeepSeek : BrainKind.Opus) : null;
        var existing = Tree.Get(account);
        var owner = options?.Owner ?? existing?.Owner ?? HiveTree.Root;
        var node = Tree.Put(new HiveNode(account, k, brain, owner)
        {
            Task = task.Text, Script = task.Script, Since = DateTimeOffset.UtcNow,
            State = existing?.State == "running" ? "running" : "spawning",
        });
        if (Managed.IsManaged(account)) Managed.MarkActive(account, HiveKinds.Name(k), brain is { } bk ? HiveKinds.Name(bk) : null, node.Owner);
    }

    public void BeforeLaunch(string account)
    {
        Tree.SetState(account, "running");
        if (Whitelist.Accepts(account)) _ = Whitelist.AddAsync(account);
    }

    /// <summary>Longest a launch waits for the whitelist add (send + the server's confirmation) before logging in anyway.</summary>
    public TimeSpan WhitelistWait { get; set; } = TimeSpan.FromSeconds(10);

    public async Task BeforeLaunchAsync(string account, CancellationToken ct = default)
    {
        Tree.SetState(account, "running");
        if (!Whitelist.Accepts(account)) return;
        var add = Whitelist.AddAsync(account, ct);
        try
        {
            if (!await add.WaitAsync(WhitelistWait, ct).ConfigureAwait(false))
                _log.LogWarning("hive: whitelisting {Account} wasn't confirmed; launching anyway (a refused login yields the slot)", account);
        }
        catch (TimeoutException) { _log.LogWarning("hive: whitelisting {Account} took over {S:0}s; launching anyway", account, WhitelistWait.TotalSeconds); }
    }

    public void Released(string account, string reason)
    {
        // An owner that went offline takes its subtree with it: each child deposits first (the recall's logout gate). Subhive
        // bodies lived in its process: they are gone with it (its logout put each one away first).
        foreach (var child in Tree.Children(account))
        {
            if (child.Kind == CloneKind.Subhive)
            {
                Released(child.Account, $"its hive {Short(account)} logged out");
                continue;
            }
            if (child.State != "releasing")
            {
                Tree.SetState(child.Account, "releasing");
                try { Recall(child.Account, $"owner {Short(account)} released"); } catch (Exception ex) { _log.LogWarning(ex, "hive: release of {Child} failed", child.Account); }
            }
        }
        foreach (var child in Tree.Children(account)) Tree.Put(child with { Owner = HiveTree.Root }); // re-parented while they log out
        Tree.Remove(account);
        NoteBody(account, null);
        try
        {
            if (!Managed.IsManaged(account)) return;
            Managed.MarkReleased(account);
            if (Whitelist.Accepts(account) && Managed.Get(account)?.Whitelisted == true) _ = Whitelist.RemoveAsync(account);
            _log.LogInformation("hive: {Account} released ({Reason})", account, reason);
        }
        finally
        {
            // A release cascade waiting on this body goes on only now: its whitelist removal is already under way.
            TaskCompletionSource? gone;
            lock (_gone) _gone.Remove(account, out gone);
            gone?.TrySetResult();
        }
    }

    private bool IsDeepSeek(string? profile) =>
        profile != null && (profile.Equals(_cfg.DeepSeekProfile, StringComparison.OrdinalIgnoreCase) || profile.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase));

    // ================================================================ the brain's API (hive tool) and other hives' (bus)

    /// <param name="owner">The asking hive: main ("main") for the brain tool; a hive's account for a bus request.</param>
    public string Spawn(SpawnRequest r, string owner = HiveTree.Root)
    {
        if (Bodies is not { } bodies) return "error: hive is off";
        owner = Tree.Norm(owner);
        if (NotAHive(owner) is { } notHive) return notHive;
        var (kind, kindErr) = HiveKinds.ParseKind(r.Kind);
        if (kindErr != null) return "error: " + kindErr;
        bool hasScript = !string.IsNullOrWhiteSpace(r.Script) || !string.IsNullOrWhiteSpace(r.Code);
        var (brain, brainProfile) = HiveKinds.ParseBrain(r.Brain);
        string? profile = brainProfile ?? GivenProfile(r);
        bool hasBrain = brain != null || profile != null;
        bool crewHive = IsCrewHive(r, kind, hasScript, hasBrain);
        var k = crewHive ? CloneKind.Hive : kind ?? DefaultKind(owner, hasScript, hasBrain, r.Name);
        var (done, kindProfile) = ByKind(k, r, owner, hasScript, brain, profile);
        if (done != null) return done;
        return SpawnClone(bodies, r, owner, k, brain, kindProfile, crewHive);
    }

    /// <summary>Only main and hive-kind clones own clones; null = the owner may spawn.</summary>
    private string? NotAHive(string owner) => owner != HiveTree.Root && Tree.Get(owner) is not { Kind: CloneKind.Hive }
        ? $"no: {Short(owner)} isn't a hive (only main and hive-kind clones own clones)"
        : null;

    private static string? GivenProfile(SpawnRequest r) => string.IsNullOrWhiteSpace(r.Profile) ? null : r.Profile.Trim();

    /// <summary>
    /// A hive spawned straight onto a script (Fox 10-30: "spawn up its bodies with a script already made, so its bodies just go
    /// straight to work"): a brain (or kind=hive) + bodies + a script = ONE hive whose bodies run the script as they log in, the
    /// coordinator started by C# with no brain turn; its brain supervises (wakes on the crew's result, a failure or chat).
    /// </summary>
    private static bool IsCrewHive(SpawnRequest r, CloneKind? kind, bool hasScript, bool hasBrain) =>
        hasScript && r.Bodies is > 0 && string.IsNullOrWhiteSpace(r.Name) && (kind == CloneKind.Hive || kind == null && hasBrain);

    /// <summary>The kinds spawned their own way (a script hive, subhive bodies) answer here; a hive's brain fixes its profile.
    /// Done = the answer (nothing more to spawn); else the profile to spawn with.</summary>
    private (string? Done, string? Profile) ByKind(CloneKind k, SpawnRequest r, string owner, bool hasScript, BrainKind? brain, string? profile)
    {
        switch (k)
        {
            case CloneKind.Script when hasScript && r.Bodies is > 0 && string.IsNullOrWhiteSpace(r.Name):
                return (SpawnScriptHive(owner, r), profile);
            case CloneKind.Script when !hasScript:
                return ("error: kind script needs script=<saved name> (or code)", profile);
            case CloneKind.Subhive:
                return (SpawnSubhives(owner, r), profile);
            case CloneKind.Hive when brain == BrainKind.DeepSeek:
                var bad = ProfileCheck is { } check ? check(_cfg.DeepSeekProfile) : "no model profiles here";
                return bad != null ? ($"no: brain deepseek not available ({bad}); use brain opus; spawned nothing", profile) : (null, _cfg.DeepSeekProfile);
            case CloneKind.Hive when brain == BrainKind.Opus:
                return (null, null); // the clone's own brain tier: Opus 5.5 (enforced at startup)
            default:
                return (null, profile);
        }
    }

    /// <summary>One clone (a script runner, a hive with its own brain, or a hive driving N bodies in its process).</summary>
    private string SpawnClone(IHiveBodies bodies, SpawnRequest r, string owner, CloneKind k, BrainKind? brain, string? profile, bool crewHive)
    {
        if (NotOwned(r.Name, owner) is { } notOwned) return notOwned;
        var (crew, crewErr) = crewHive ? PrepareCrew(r) : (null, null);
        if (crewErr != null) return crewErr;
        var (hostBodies, note, noRoom) = k == CloneKind.Hive && r.Bodies is > 0 and var asked ? HostBodies(bodies, asked, profile) : (null, "", null);
        if (noRoom != null) return noRoom;
        var text = bodies.Spawn(TaskText(r, crew), r.Name, r.Handover, OptionsFor(r, owner, k, profile, hostBodies, crew));
        if (hostBodies is { } hb && text.StartsWith("ok", StringComparison.Ordinal)) text += HostedNote(hb, brain, crew) + note;
        return text;
    }

    /// <summary>A hive (not main) may only name its own clones; null = fine.</summary>
    private string? NotOwned(string? name, string owner) =>
        name is { Length: > 0 } named && owner != HiveTree.Root && !OwnedBy(named, owner) ? $"no: {named} isn't one of {Short(owner)}'s clones" : null;

    /// <summary>The task text: a crew hive's defaults to "crew NAME".</summary>
    private static string TaskText(SpawnRequest r, string? crew) => crew != null ? r.Task ?? $"crew {crew}" : r.Task ?? "";

    /// <summary>The spawn options: a crew hive's script goes to its bodies (the crew), not the hive itself.</summary>
    private static HiveSpawnOptions OptionsFor(SpawnRequest r, string owner, CloneKind k, string? profile, int? hostBodies, string? crew) =>
        new(profile, crew != null ? null : r.Script, crew != null ? null : r.Code, r.Args, r.After)
            { Kind = HiveKinds.Name(k), Owner = owner, Bodies = hostBodies, Crew = crew };

    /// <summary>The crew script saved for the hive's bodies; an error when it can't be.</summary>
    private (string? Crew, string? Error) PrepareCrew(SpawnRequest r)
    {
        if (CrewPrep is not { } prep) return (null, "no: a hive can't start its bodies on a script here; spawned nothing");
        var (cn, ce) = prep(r.Script, r.Code);
        return cn == null ? (null, "error: " + (ce ?? "script not saved")) : (cn, null);
    }

    /// <summary>One hive process whose brain drives N bodies in its own process: as many as memory and the player cap allow.
    /// A note when fewer than asked fit; a refusal when none does.</summary>
    private (int? Bodies, string Note, string? NoRoom) HostBodies(IHiveBodies bodies, int asked, string? profile)
    {
        int want = Math.Min(asked, Math.Max(1, MaxBodiesPerSpawn));
        bool inproc = InProc(null, profile);
        int fit = Resources?.BodiesThatFit(withHives: 1, inProcess: inproc) ?? int.MaxValue;
        var facts = Facts(bodies);
        var slot = SlotPolicy.CanSpawn(facts.Slots, facts.Regulars, facts.Pending);
        int room = Math.Max(0, slot.Free - slot.Reserved - 1); // after the hive's own login
        int n = Math.Min(want, Math.Min(fit, room));
        if (n <= 0)
            return (null, "", $"no: no room for {asked} bodies ({(fit <= 0 ? Resources?.CanAdd(1, 1, inproc).Why ?? "memory" : $"player cap: {slot.Reason}")}); spawned nothing");
        if (n >= asked) return (n, "", null);
        return (n, $"; only {n} of {asked} bodies fit (" + (n == want && asked > want ? $"max {want} per spawn" : fit < want ? $"memory: room for {fit}" : $"player cap: {room} slot(s) after the hive") + ")", null);
    }

    private string HostedNote(int hb, BrainKind? brain, string? crew) =>
        $"; ONE {(brain == BrainKind.DeepSeek ? "DeepSeek" : "Opus")} hive whose brain will drive {hb} subhive bod{(hb == 1 ? "y" : "ies")} " +
        $"(none in game yet: they log in once it is in{Pacing(hb)})" +
        (crew != null ? $"; they go straight to work on {crew} as they arrive (no brain turn first; its brain wakes on {crew}'s result)" : "");

    /// <summary>
    /// No kind given: a script runs script-only; a named brain (or profile) is its own hive; a named clone keeps its kind; anything
    /// else is a body the asking hive's own brain drives (one brain, many bodies) wherever subhive bodies exist. The old path (one
    /// process with its own brain per helper) is taken only when asked for (kind=hive or a brain).
    /// </summary>
    private CloneKind DefaultKind(string owner, bool hasScript, bool hasBrain, string? name)
    {
        if (hasScript) return CloneKind.Script;
        if (hasBrain) return CloneKind.Hive;
        if (!string.IsNullOrWhiteSpace(name)) return Resolve(name) is { } acc && Tree.Get(acc) is { } n ? n.Kind : CloneKind.Hive;
        return SubhivesFor(owner) ? CloneKind.Subhive : CloneKind.Hive;
    }

    private bool SubhivesFor(string owner) => owner == HiveTree.Root ? Subhives != null : Bus != null && RemoteSubhives;

    /// <summary>
    /// <paramref name="r"/>.Bodies subhive bodies for <paramref name="owner"/>: main hosts its own in this process; a hive-kind clone
    /// hosts its own in its process (main allocates and whitelists each account, then asks the hive over the bus to log it in).
    /// </summary>
    private string SpawnSubhives(string owner, SpawnRequest r)
    {
        bool local = owner == HiveTree.Root;
        if (SubhivesUnavailable(owner, local) is { } unavailable) return unavailable;
        // A named body: a new job for it (it stays logged in).
        if (r.Name is { Length: > 0 } named) return NewSubhiveJob(owner, named, r);
        int asked = Math.Max(1, r.Bodies ?? 1);
        int want = Math.Min(asked, Math.Max(1, MaxBodiesPerSpawn));
        var got = new List<string>();
        // A hive (re)starting asks for its bodies: the ones it had died with its old process; reuse those accounts first.
        if (r.Reset && !local) ReuseOldBodies(owner, r, want, got);
        string? why = AllocateBodies(owner, r.Task, r.Script, null, want, got, asked > want ? $"max {want} per spawn" : null);
        if (got.Count == 0) return "no: " + why + "; spawned nothing";
        if (local)
        {
            var (kept, refusal) = SpawnLocalSubhives(owner, r, got);
            if (refusal != null) return refusal;
            got = kept;
        }
        else
        {
            var accounts = got.ToList();
            // A crew's bodies have no job of their own: the hive's coordinator gives each its lane once it is in.
            _ = Task.Run(() => HostRemoteAsync(owner, accounts, r.Crew != null ? null : r.Task, r.Crew != null ? null : r.Script, whitelist: true, crew: r.Crew));
        }
        return SubhivesSpawnedText(owner, local, got, asked, why);
    }

    /// <summary>Why <paramref name="owner"/> can't host subhive bodies here; null = it can.</summary>
    private string? SubhivesUnavailable(string owner, bool local)
    {
        if (local && Subhives is null)
            return "no: subhive bodies aren't available here (Hive:Subhives:Enabled is off); spawn kind hive or script instead";
        if (!local && (Bus is null || !RemoteSubhives)) return $"no: {Short(owner)} can't host subhive bodies here (no hive bus)";
        return null;
    }

    /// <summary>A new job for the named subhive body of <paramref name="owner"/> (it stays logged in).</summary>
    private string NewSubhiveJob(string owner, string named, SpawnRequest r)
    {
        if (Resolve(named) is not { } acc || Tree.Get(acc) is not { Kind: CloneKind.Subhive } node) return $"no: {named} isn't a subhive body";
        if (!Tree.IsUnder(acc, owner)) return $"no: {named} isn't one of {Short(owner)}'s bodies";
        Tree.Put(node with { Task = r.Task ?? node.Task, Script = r.Script ?? node.Script });
        if (node.Owner == HiveTree.Root) return Subhives!.Spawn(owner, acc, r.Task, r.Script);
        _ = Task.Run(() => HostRemoteAsync(node.Owner, [acc], r.Task, r.Script, whitelist: false));
        return $"ok: new job for {node.Short} (its hive {Short(node.Owner)} drives it)";
    }

    /// <summary>Reuses a restarting hive's old body accounts (up to <paramref name="want"/>); the rest are released.</summary>
    private void ReuseOldBodies(string owner, SpawnRequest r, int want, List<string> got)
    {
        foreach (var old in Tree.Children(owner).Where(c => c.Kind == CloneKind.Subhive).ToList())
        {
            if (got.Count < want) { got.Add(old.Account); Tree.Put(old with { Task = r.Task ?? old.Task, State = "spawning" }); }
            else Released(old.Account, $"its hive {Short(owner)} restarted with fewer bodies");
        }
    }

    /// <summary>Allocates fresh body accounts until <paramref name="got"/> holds <paramref name="want"/>: the allocator's reason when
    /// it stopped short, else <paramref name="why"/>.</summary>
    private string? AllocateBodies(string owner, string? task, string? script, string? crew, int want, List<string> got, string? why)
    {
        while (got.Count < want)
        {
            var (acc, w) = AllocateBody(owner, task, script, crew);
            if (acc == null) return w;
            got.Add(acc);
        }
        return why;
    }

    /// <summary>Spawns each body in main's process: the ones that spawned, or the first refusal when none did.</summary>
    private (List<string> Kept, string? Refusal) SpawnLocalSubhives(string owner, SpawnRequest r, List<string> got)
    {
        var refused = new List<string>();
        string? first = null;
        foreach (var acc in got)
        {
            var t = Subhives!.Spawn(owner, acc, r.Task, r.Script);
            if (t.StartsWith("ok", StringComparison.Ordinal)) continue;
            refused.Add(acc);
            first ??= t;
            Released(acc, "not spawned: " + t);
        }
        if (refused.Count == got.Count) return (got, first!);
        return (got.Except(refused).ToList(), null);
    }

    private string SubhivesSpawnedText(string owner, bool local, List<string> got, int asked, string? why)
    {
        var names = HiveTree.Ranges(got.Select(HiveNode.ShortOf));
        return $"ok: {got.Count} subhive bod{(got.Count == 1 ? "y" : "ies")} ({names}) " +
            (local ? "your brain drives (body=\"<n>\")" : $"for {Short(owner)}'s brain") +
            $"; none in game yet{Pacing(got.Count)}" +
            (got.Count < asked ? $"; only {got.Count} of {asked}: {why}" : "");
    }

    /// <summary>
    /// A hive script: <paramref name="r"/>.Bodies subhive bodies in main's process (allocated, whitelisted before login, logging in a
    /// launch gap apart, as <see cref="SpawnSubhives"/>) and ONE coordinator script over them with no brain
    /// (<see cref="ScriptHives"/>). Its <c>bodies()</c> are its accounts online now; when it ends (or <c>release &lt;id&gt;</c>) they
    /// deposit and log out, and main's brain hears its one result line. A hive-kind clone runs a hive script over its own bodies with
    /// <c>script{name}</c> instead (its process hosts them).
    /// </summary>
    private string SpawnScriptHive(string owner, SpawnRequest r)
    {
        if (ScriptHiveRefusal(owner) is { } refusal) return refusal;
        var host = ScriptHives!;
        var (name, err) = host.Prepare(r.Script, r.Code);
        if (name == null) return "error: " + (err ?? "script not saved");
        string id = ReserveScriptId(name);
        int asked = Math.Max(1, r.Bodies ?? 1);
        int want = Math.Min(asked, Math.Max(1, MaxBodiesPerSpawn));
        var got = new List<string>();
        string? why = AllocateBodies(owner, "hive script " + id, name, id, want, got, asked > want ? $"max {want} per spawn" : null);
        string? first = SpawnCrewBodies(owner, got);
        if (got.Count == 0)
        {
            lock (_lock) _scripts.Remove(id);
            return "no: " + (first ?? why) + "; spawned nothing";
        }
        var started = host.Start(id, name, r.Args, () => Members(id), (ok, line) => ScriptEnded(id, ok, line));
        if (!started.StartsWith("ok", StringComparison.Ordinal)) return NotStarted(id, started);
        _log.LogInformation("hive: hive script {Id} ({Script}) over {N} bodies: {Names}", id, name, got.Count, string.Join(", ", got));
        return ScriptHiveStarted(id, got, asked, why);
    }

    /// <summary>The hive script's ok line: its bodies, the pacing, how it ends, and why fewer bodies than asked.</summary>
    private string ScriptHiveStarted(string id, List<string> got, int asked, string? why)
    {
        var names = HiveTree.Ranges(got.Select(HiveNode.ShortOf));
        return $"ok: hive script {id}: ONE script drives {got.Count} bod{(got.Count == 1 ? "y" : "ies")} ({names}), no brain; " +
            $"none in game yet{Pacing(got.Count)}; when it ends they deposit and log out " +
            $"(release {id} stops it)" + (got.Count < asked ? $"; only {got.Count} of {asked}: {why}" : "");
    }

    /// <summary>The coordinator didn't start: the id is freed and its bodies released; the host's answer as an error.</summary>
    private string NotStarted(string id, string started)
    {
        lock (_lock) _scripts.Remove(id);
        ReleaseMembers(id, "hive script didn't start");
        return started.StartsWith("error", StringComparison.Ordinal) ? started : "error: " + started;
    }

    /// <summary>Only main runs hive scripts, and only with subhive bodies and a script host; null = it may.</summary>
    private string? ScriptHiveRefusal(string owner)
    {
        if (owner != HiveTree.Root)
            return "no: run the hive script over your own bodies: script{name} (bodies() are yours); spawned nothing";
        if (Subhives is null || ScriptHives is null)
            return "no: hive scripts need subhive bodies here (Hive:Subhives:Enabled is off); spawned nothing";
        return null;
    }

    /// <summary>A free id for a new run of <paramref name="name"/> ("chop_crew", then "chop_crew#2", ...), recorded as running.</summary>
    private string ReserveScriptId(string name)
    {
        lock (_lock)
        {
            string id = name;
            for (int i = 2; _scripts.ContainsKey(id); i++) id = $"{name}#{i}";
            _scripts[id] = name;
            return id;
        }
    }

    /// <summary>Logs each crew body in with no job of its own (the coordinator gives each its lane once it is in); the ones refused
    /// leave <paramref name="got"/> and are released. The first refusal, if any.</summary>
    private string? SpawnCrewBodies(string owner, List<string> got)
    {
        string? first = null;
        foreach (var acc in got.ToList())
        {
            var t = Subhives!.Spawn(owner, acc, null, null);
            if (t.StartsWith("ok", StringComparison.Ordinal)) continue;
            first ??= t;
            got.Remove(acc);
            Released(acc, "not spawned: " + t);
        }
        return first;
    }

    // ================================================================ groups (Story Mode colonies: docs/web/story-mode.md)

    /// <summary>
    /// <paramref name="n"/> script-only bodies of a named group owned by main, in main's process (a Story Mode colony, "story-1"):
    /// each allocated (accounts it had before first: <paramref name="prefer"/>, so a colony resuming after a restart keeps its pawns),
    /// whitelisted before its login and logged in by main's subhive host with no job; the group's director starts each one's lane.
    /// The tree records them as group members (<see cref="HiveNode.Crew"/> = the group), so main's brain and crews never drive them
    /// and a release goes through the usual deposit → logout → whitelist remove.
    /// </summary>
    public (IReadOnlyList<string> Accounts, string? Why) SpawnGroup(string group, int n, string script, IReadOnlyList<string>? prefer = null)
    {
        if (Bodies is not { } b) return ([], "hive is off");
        if (Subhives is null) return ([], "subhive bodies aren't available here (Hive:Subhives:Enabled is off)");
        var got = new List<string>();
        string? why = null;
        var want = new Queue<string>((prefer ?? []).Where(p => !string.IsNullOrWhiteSpace(p)));
        int target = Math.Min(Math.Max(1, n), Math.Max(1, MaxBodiesPerSpawn));
        while (got.Count < target && AddGroupBody(b, group, script, want, got, out why)) { }
        if (got.Count > 0) _log.LogInformation("hive: group {Group}: {N} script-only bodies ({Names})", group, got.Count, string.Join(", ", got));
        return (got, got.Count < n ? why ?? (n > target ? $"max {target} per spawn" : null) : null);
    }

    /// <summary>Allocates and logs in one more group body (added to <paramref name="got"/>); false, with why, when it can't.</summary>
    private bool AddGroupBody(IHiveBodies b, string group, string script, Queue<string> want, List<string> got, out string? why)
    {
        why = null;
        if (Resources?.CanAdd(0, 1) is { Ok: false } full) { why = full.Why; return false; }
        var (name, refused) = AllocateGroupBody(b, group, script, want);
        if (name == null) { why = refused; return false; }
        try { Allocated?.Invoke(name); } catch (Exception ex) { _log.LogDebug(ex, "hive: allocated hook failed"); }
        var t = Subhives!.Spawn(HiveTree.Root, name, null, null); // no job of its own: the director starts its lane once it is in
        if (!t.StartsWith("ok", StringComparison.Ordinal)) { why = t; Released(name, "not spawned: " + t); return false; }
        got.Add(name);
        return true;
    }

    /// <summary>One more account for the group (the room allocated; a pawn account it had before takes the room when free), recorded
    /// as a group member. (null, why) when there is no room.</summary>
    private (string? Name, string? Why) AllocateGroupBody(IHiveBodies b, string group, string script, Queue<string> want)
    {
        lock (_lock)
        {
            var a = _allocator.Allocate(Facts(b));
            if (!a.Ok) return (null, a.Error);
            var name = PreferredAccount(b, want) ?? a.Name!;
            PutGroupMember(name, group, script);
            return (name, null);
        }
    }

    /// <summary>The first preferred account that is free (not in the tree, not online, not active), dequeuing the ones passed over.</summary>
    private string? PreferredAccount(IHiveBodies b, Queue<string> want)
    {
        var online = b.Slots().Online;
        while (want.Count > 0)
        {
            var p = want.Dequeue();
            if (Tree.Get(p) == null && !online.Contains(p, StringComparer.OrdinalIgnoreCase) && Managed.Get(p) is not { Active: true }) return p;
        }
        return null;
    }

    /// <summary>Records <paramref name="account"/> as a script-only member of <paramref name="group"/> owned by main (caller holds the lock).</summary>
    private void PutGroupMember(string account, string group, string script)
    {
        Managed.Upsert(account, x => x);
        Tree.Put(new HiveNode(account, CloneKind.Subhive, null, HiveTree.Root) { Task = "story " + group, Script = script, Since = DateTimeOffset.UtcNow, Crew = group });
        Managed.MarkActive(account, HiveKinds.Name(CloneKind.Script), null, HiveTree.Root);
    }

    /// <summary>
    /// Brings exactly these accounts of a group in (Story Mode's "Bring online" and the colony's seat retry). An account hosted already
    /// (its login loops: a kick, a backoff) is whitelisted again and retried now; one that isn't is put back in the tree and logged in,
    /// the player cap permitting. <paramref name="overCap"/> lets it take the seats kept for offline regulars (the owner's explicit
    /// choice: a regular may find the server full until a body leaves; the yield check spares it). Never allocates another account.
    /// Returns (account, null = started | why not).
    /// </summary>
    public IReadOnlyList<(string Account, string? Why)> BringGroup(string group, IReadOnlyList<string> accounts, string script, bool overCap = false)
    {
        if (Bodies is not { } b) return accounts.Select(a => (a, (string?)"hive is off")).ToList();
        if (Subhives is not { } host) return accounts.Select(a => (a, (string?)"subhive bodies aren't available here (Hive:Subhives:Enabled is off)")).ToList();
        var res = new List<(string, string?)>();
        foreach (var raw in accounts)
        {
            var acc = (raw ?? "").Trim();
            res.Add((acc, BringOne(b, host, group, acc, script, overCap)));
        }
        var started = res.Where(r => r.Item2 == null).Select(r => r.Item1).ToList();
        if (started.Count > 0)
            _log.LogInformation("hive: group {Group}: bringing {Names} in{Over}", group, string.Join(", ", started), overCap ? " (on the regulars' seats)" : "");
        return res;
    }

    /// <summary>One account of <see cref="BringGroup"/>: null = started (or retried), else why not.</summary>
    private string? BringOne(IHiveBodies b, ISubhiveHost host, string group, string acc, string script, bool overCap)
    {
        if (!Whitelist.Accepts(acc)) return "not a managed account";
        if (Tree.Get(acc) is { } node) return RetryHosted(host, node, group, acc, overCap);
        if (AdmitGroupMember(b, group, acc, script, overCap) is { } why) return why;
        NoteBody(acc, "connecting");
        if (overCap) host.AllowOverCap(acc);
        var t = host.Spawn(HiveTree.Root, acc, null, null);
        if (t.StartsWith("ok", StringComparison.Ordinal)) return null;
        Released(acc, "not spawned: " + t);
        return t;
    }

    /// <summary>A group account hosted already: its login is looping. Put it back on the whitelist, then cut the reconnect wait short.</summary>
    private string? RetryHosted(ISubhiveHost host, HiveNode node, string group, string acc, bool overCap)
    {
        if (!string.Equals(node.Crew, group, StringComparison.OrdinalIgnoreCase)) return $"it belongs to {node.Crew ?? Short(node.Owner)} now";
        if (node.State == "releasing") return "it is logging out";
        if (overCap) host.AllowOverCap(acc);
        _ = Task.Run(async () =>
        {
            try { await RewhitelistAsync(acc).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "hive: re-whitelisting {Account} failed", acc); }
            host.RetryNow(acc);
        });
        return null;
    }

    /// <summary>Puts a group account back in the tree when it may log in now (not online as someone else, a seat, memory); else why not.</summary>
    private string? AdmitGroupMember(IHiveBodies b, string group, string acc, string script, bool overCap)
    {
        lock (_lock)
        {
            var f = Facts(b);
            if (f.Slots.Online.Contains(acc, StringComparer.OrdinalIgnoreCase)) return "someone else is logged in as it";
            if (SlotPolicy.CanSpawn(f.Slots, overCap ? [] : f.Regulars, f.Pending, _maxPlayersFallback) is { Ok: false } d)
                return CapReason(d, f.Slots, _maxPlayersFallback);
            if (Resources?.CanAdd(0, 1) is { Ok: false } full) return full.Why;
            PutGroupMember(acc, group, script);
            return null;
        }
    }

    /// <summary>A group's bodies deposit and log out (whitelist remove follows each logout). Returns how many go.</summary>
    public int ReleaseGroup(string group, string reason = "released") => ReleaseMembers(group, reason);

    /// <summary>The accounts a group (or hive script) owns now, any state.</summary>
    public IReadOnlyList<string> GroupMembers(string group) => Members(group);

    /// <summary>The accounts a hive script owns (any state).</summary>
    private List<string> Members(string id) =>
        Tree.All().Where(n => string.Equals(n.Crew, id, StringComparison.OrdinalIgnoreCase)).Select(n => n.Account).ToList();

    /// <summary>The hive script a name means ("chop_crew", "chop_crew#2"), if one runs.</summary>
    private string? ScriptId(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();
        lock (_lock) return _scripts.Keys.FirstOrDefault(k => k.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Running hive scripts (id → script name), for status and tests.</summary>
    public IReadOnlyDictionary<string, string> HiveScripts { get { lock (_lock) return new Dictionary<string, string>(_scripts, StringComparer.OrdinalIgnoreCase); } }

    /// <summary>Its bodies deposit and log out (each once: a body already releasing is left to that release).</summary>
    private int ReleaseMembers(string id, string reason)
    {
        var order = Members(id).Where(a => Tree.Get(a)?.State != "releasing").ToList();
        foreach (var a in order) Tree.SetState(a, "releasing");
        if (order.Count > 0) _ = Task.Run(() => CascadeAsync(order, reason));
        return order.Count;
    }

    private void ScriptEnded(string id, bool ok, string line)
    {
        lock (_lock) _scripts.Remove(id);
        int n = ReleaseMembers(id, $"hive script {id} ended");
        _log.LogInformation("hive: hive script {Id} {State}: {Line}; releasing {N} bodies", id, ok ? "done" : "failed", line, n);
        Quiet($"[hive] hive script {id} {(ok ? "done" : "failed")}: {Trunc(line, 200)}" + (n > 0 ? $"; its {n} bod{(n == 1 ? "y" : "ies")} deposit and log out" : ""),
            EventPriority.Normal, wake: true);
    }

    /// <summary>Whitelists each account (confirmed, one at a time) and asks the owning hive to log it in. A refusal releases it.</summary>
    private async Task HostRemoteAsync(string owner, IReadOnlyList<string> accounts, string? task, string? script, bool whitelist, string? crew = null)
    {
        foreach (var acc in accounts)
        {
            try { await HostOneRemoteAsync(owner, acc, task, script, whitelist, crew).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "hive: hosting {Account} on {Owner} failed", acc, owner);
                if (whitelist) Released(acc, "hosting failed: " + ex.Message);
            }
        }
    }

    private async Task HostOneRemoteAsync(string owner, string acc, string? task, string? script, bool whitelist, string? crew)
    {
        if (whitelist) await BeforeLaunchAsync(acc).ConfigureAwait(false); // on the whitelist before the hive logs it in
        var reply = await Bus!.PublishAsync(BusTypes.Host, new HostReq(acc, task, script, crew), to: owner).ConfigureAwait(false);
        var text = reply.Ok ? reply.As<string>() ?? "ok" : reply.Error ?? "failed";
        if (reply.Ok && text.StartsWith("ok", StringComparison.Ordinal)) return;
        _log.LogWarning("hive: {Owner} didn't host {Account}: {Text}", owner, acc, text);
        if (whitelist) Released(acc, "its hive didn't host it: " + text);
    }

    private bool OwnedBy(string name, string owner)
    {
        var acc = Resolve(name);
        return acc != null && !acc.Equals(owner, StringComparison.OrdinalIgnoreCase) && Tree.IsUnder(acc, owner);
    }

    /// <summary>"101" / account → the account in the tree.</summary>
    public string? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim().TrimStart('#');
        return Tree.All().FirstOrDefault(x => x.Account.Equals(n, StringComparison.OrdinalIgnoreCase) || x.Short.Equals(n, StringComparison.OrdinalIgnoreCase))?.Account;
    }

    /// <summary>The bodies' status plus the tree (only when someone besides main is out).</summary>
    public string List(string? owner = null)
    {
        var o = Tree.Norm(owner);
        if (o != HiveTree.Root)
        {
            var mine = Tree.All().Where(n => !n.Account.Equals(o, StringComparison.OrdinalIgnoreCase) && Tree.IsUnder(n.Account, o)).ToList();
            if (mine.Count == 0) return "no clones of yours";
            var subs = mine.Where(n => n.Kind == CloneKind.Subhive).ToList();
            var parts = mine.Except(subs).Select(n => $"{n.Short} {HiveKinds.Name(n.Kind)} {n.State}").ToList();
            if (subs.Count > 0) parts.Insert(0, $"{subs.Count} subhive bod{(subs.Count == 1 ? "y" : "ies")}: {HiveTree.Ranges(subs.Select(n => n.Short))}");
            return string.Join("; ", parts);
        }
        var bodies = Bodies?.List() ?? "";
        return Tree.Count == 0 ? bodies : $"{bodies} | tree: {Tree.Render()}";
    }

    /// <summary>
    /// Releases a clone and its subtree: deepest children first (each level in parallel), each depositing its inventory before it
    /// logs out; whitelist removal follows each logout (<see cref="Released"/>). "all" = every clone the requester owns.
    /// </summary>
    public string Release(string id, string requester = HiveTree.Root, string reason = "released")
    {
        if (Bodies is not { } bodies) return "error: hive is off";
        requester = Tree.Norm(requester);
        // A hive script by its id: the coordinator stops and its bodies deposit and log out (its end reports to the brain).
        if (requester == HiveTree.Root && ScriptId(id) is { } sid)
        {
            int n = ReleaseMembers(sid, reason);
            ScriptHives?.Stop(sid, reason);
            return $"ok: stopping hive script {sid}; its {n} bod{(n == 1 ? "y" : "ies")} deposit and log out";
        }
        var (roots, answer) = ReleaseRoots(bodies, id, requester, reason);
        if (roots == null) return answer!;
        if (roots.Count == 0) return requester == HiveTree.Root ? bodies.Recall("all", reason) : "no clones of yours";

        var order = roots.SelectMany(Tree.ReleaseOrder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var a in order) Tree.SetState(a, "releasing");
        StopScriptsLeaving(order, reason);
        _ = Task.Run(() => CascadeAsync(order, reason));
        var kids = order.Except(roots).ToList();
        return $"ok: releasing {HiveTree.Ranges(roots.Select(Short))}" +
            (kids.Count > 0 ? $" after {kids.Count} child clone(s) ({HiveTree.Ranges(kids.Select(Short))})" : "") + "; each deposits before logout";
    }

    /// <summary>The clones a release starts from ("all" = the requester's children), or (null, the answer) when it ends here: not the
    /// requester's, unknown, or an old slot main recalls plainly.</summary>
    private (List<string>? Roots, string? Answer) ReleaseRoots(IHiveBodies bodies, string id, string requester, string reason)
    {
        bool all = string.IsNullOrWhiteSpace(id) || id.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);
        if (all) return (Tree.Children(requester).Select(c => c.Account).ToList(), null);
        if (Resolve(id) is { } acc)
        {
            if (requester != HiveTree.Root && !acc.Equals(requester, StringComparison.OrdinalIgnoreCase) && !Tree.IsUnder(acc, requester))
                return (null, $"no: {Short(acc)} isn't one of {Short(requester)}'s clones");
            return ([acc], null);
        }
        return (null, requester == HiveTree.Root ? bodies.Recall(id, reason) : $"no: no clone '{id}'"); // not in the tree (an old slot): plain recall
    }

    /// <summary>Every hive script whose bodies all go stops too (after they are marked: its end doesn't release them a second time).</summary>
    private void StopScriptsLeaving(List<string> order, string reason)
    {
        List<string> scripts;
        lock (_lock) scripts = _scripts.Keys.ToList();
        foreach (var sid in scripts)
            if (Members(sid) is { Count: > 0 } ms && ms.All(m => order.Contains(m, StringComparer.OrdinalIgnoreCase))) ScriptHives?.Stop(sid, reason);
    }

    private int Depth(string account)
    {
        int d = 0;
        for (var cur = Tree.Get(account); cur != null && cur.Owner != HiveTree.Root && d < 64; cur = Tree.Get(cur.Owner)) d++;
        return d;
    }

    private async Task CascadeAsync(IReadOnlyList<string> order, string reason)
    {
        if (Bodies is null) return;
        var timeout = TimeSpan.FromSeconds(Math.Max(5, _cfg.ReleaseChildSeconds));
        foreach (var level in order.GroupBy(Depth).OrderByDescending(g => g.Key).ToList())
        {
            var waits = new List<Task>();
            foreach (var a in level)
            {
                var wait = WaitGoneAsync(a, timeout); // before the recall: a quick logout can't be missed
                try { Recall(a, reason); } catch (Exception ex) { _log.LogWarning(ex, "hive: recall of {Account} failed", a); }
                waits.Add(wait);
            }
            try { await Task.WhenAll(waits).ConfigureAwait(false); } catch (Exception ex) { _log.LogDebug(ex, "hive: release wait"); }
        }
    }

    /// <summary>Planned logout of one clone, routed by kind: a subhive body through its host (main's, or its hive's over the bus).</summary>
    private void Recall(string account, string reason)
    {
        if (Tree.Get(account) is { Kind: CloneKind.Subhive } n)
        {
            if (n.Owner == HiveTree.Root)
            {
                var t = Subhives?.Release(account);
                if (t == null || !t.StartsWith("ok", StringComparison.Ordinal)) Released(account, reason); // not online: nothing to put away
            }
            else _ = RemoteReleaseAsync(n.Owner, account, reason);
            return;
        }
        Bodies?.Recall(account, reason);
    }

    private async Task RemoteReleaseAsync(string owner, string account, string reason)
    {
        try
        {
            var reply = Bus is { } bus ? await bus.PublishAsync(BusTypes.Release, new ReleaseReq(account), to: owner).ConfigureAwait(false) : null;
            if (reply is not { Ok: true }) Released(account, reason + " (its hive didn't answer)"); // its process is gone: so is the body
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "hive: release of {Account} via {Owner} failed", account, owner);
            Released(account, reason);
        }
    }

    private Task WaitGoneAsync(string account, TimeSpan timeout)
    {
        if (Tree.Get(account) is not { Kind: CloneKind.Subhive }) return Bodies?.WaitGoneAsync(account, timeout) ?? Task.CompletedTask;
        TaskCompletionSource tcs;
        lock (_gone)
            if (!_gone.TryGetValue(account, out tcs!)) _gone[account] = tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Tree.Get(account) == null) tcs.TrySetResult(); // released meanwhile
        return Task.WhenAny(tcs.Task, Task.Delay(timeout));
    }

    public HiveTreeDto Snapshot() => Tree.Snapshot();

    // ================================================================ bus requests (main is the router and the admin root)

    public async Task<BusReply> HandleBusAsync(BusEnvelope e, CancellationToken ct)
    {
        var from = Tree.Norm(e.From);
        if (SpawnBusRequest(e, from) is { } spawned) return spawned;
        if (BusNotice(e, from) is { } noticed) return noticed;
        switch (e.Type)
        {
            case BusTypes.SubhiveGone when e.As<SubhiveGoneMsg>() is { } g:
                return SubhiveGone(g, from);
            case BusTypes.Whitelist when e.As<WhitelistReq>() is { } w:
                return await WhitelistForAsync(w, from, ct).ConfigureAwait(false);
            case BusTypes.MemWrite:
                return Memory is { } m ? await m.HandleAsync(e, ct).ConfigureAwait(false) : BusReply.Fail("no shared memory host");
            default:
                return BusReply.Fail($"main doesn't take {e.Type}");
        }
    }

    /// <summary>The clone requests (spawn, assign, handover, release, list); null = not one of those.</summary>
    private BusReply? SpawnBusRequest(BusEnvelope e, string from) => e.Type switch
    {
        BusTypes.Spawn when e.As<SpawnReq>() is { } s =>
            Text(Spawn(new SpawnRequest(s.Kind, s.Brain, s.Task, s.Script, null, s.Args, s.After, s.Name, Bodies: s.Bodies, Reset: s.Reset, Crew: s.Crew), from)),
        BusTypes.Assign when e.As<AssignReq>() is { } a => Text(Spawn(new SpawnRequest(Task: a.Task, Name: a.To), from)),
        BusTypes.Handover when e.As<AssignReq>() is { } h => Text(Spawn(new SpawnRequest(Task: h.Task, Name: h.To, Handover: true), from)),
        BusTypes.Release when e.As<ReleaseReq>() is { } r => Text(Release(r.Id, from)),
        BusTypes.List => Text(List(from)),
        _ => null,
    };

    /// <summary>A hive's status, result, call for help or observation, posted as a quiet notice; null = not one of those.</summary>
    private BusReply? BusNotice(BusEnvelope e, string from)
    {
        var (text, priority) = e.Type switch
        {
            BusTypes.Status when e.As<StatusMsg>() is { } st => ($"[hive bus] {Short(from)}: {Trunc(st.Text)}" + (st.Pct is { } p ? $" {p:P0}" : ""), EventPriority.Background),
            BusTypes.Result when e.As<ResultBusMsg>() is { } rs => ($"[hive bus] {Short(from)} {(rs.Ok ? "done" : "failed")}: {Trunc(rs.Text)}", EventPriority.Low),
            BusTypes.NeedHelp when e.As<NeedHelpMsg>() is { } nh => ($"[hive bus] {Short(from)} needs help: {Trunc(nh.Text)}", EventPriority.Normal),
            BusTypes.Observation when e.As<ObservationMsg>() is { } ob => ($"[hive bus] {Short(from)} saw: {Trunc(ob.Text)}", EventPriority.Background),
            _ => ((string?)null, EventPriority.Background),
        };
        if (text == null) return null;
        Quiet(text, priority, wake: e.Type == BusTypes.NeedHelp);
        return Text("ok");
    }

    /// <summary>Only the hive hosting a body reports it gone; main then takes it off the whitelist.</summary>
    private BusReply SubhiveGone(SubhiveGoneMsg g, string from)
    {
        var acc = Resolve(g.Account) ?? g.Account;
        if (Tree.Get(acc) is not { Kind: CloneKind.Subhive } node || !node.Owner.Equals(from, StringComparison.OrdinalIgnoreCase))
            return BusReply.Fail($"refused: {g.Account} isn't a body of yours");
        Released(acc, g.Reason);
        return Text("ok");
    }

    /// <summary>Hives never whitelist on their own: main does it, only for a managed account in the asker's subtree.</summary>
    private async Task<BusReply> WhitelistForAsync(WhitelistReq w, string from, CancellationToken ct)
    {
        var acc = Resolve(w.Name) ?? w.Name;
        if (!Whitelist.Accepts(acc) || (from != HiveTree.Root && !Tree.IsUnder(acc, from)))
            return BusReply.Fail($"refused: {w.Name} isn't a managed account of yours");
        bool ok = w.Op.Equals("remove", StringComparison.OrdinalIgnoreCase) ? await Whitelist.RemoveAsync(acc, ct).ConfigureAwait(false)
            : w.Op.Equals("add", StringComparison.OrdinalIgnoreCase) && await Whitelist.AddAsync(acc, ct).ConfigureAwait(false);
        return ok ? Text($"ok: whitelist {w.Op} {Short(acc)}") : BusReply.Fail("whitelist " + w.Op + " failed");
    }

    private static BusReply Text(string s) => s.StartsWith("error", StringComparison.Ordinal) ? BusReply.Fail(s) : BusReply.Of(s);
    private static string Trunc(string s, int n = 160) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private void Quiet(string text, EventPriority p, bool wake = false)
    {
        try { Notice?.Invoke(GameEvent.Notice(text, p) with { MustWake = wake }); } catch (Exception ex) { _log.LogDebug(ex, "hive: bus notice failed"); }
    }
}
