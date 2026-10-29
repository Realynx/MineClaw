using MineClaw.Agent.Events;
using MineClaw.Hive.Hierarchy.Bus;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Supervisor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Hierarchy;

/// <summary>A spawn as the brain (hive tool) or another hive (bus) asks for it.</summary>
public sealed record SpawnRequest(string? Kind = null, string? Brain = null, string? Task = null, string? Script = null, string? Code = null,
    string? Args = null, string? After = null, string? Name = null, bool Handover = false, string? Profile = null);

/// <summary>
/// Main's hive hierarchy: the clone kinds (hive / subhive / script), the ownership tree under main, the dynamic accounts (allocate
/// → whitelist add → spawn → work → deposit → disconnect → whitelist remove) and the bus requests other hives send. Main is
/// always the admin root: only main allocates accounts and only main touches the whitelist, whoever owns the clone.
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
        _regulars = regulars ?? (() => []);
        _knownPlayers = knownPlayers ?? (() => []);
        _log = log ?? NullLogger.Instance;
    }

    public ManagedAccounts Managed { get; }
    public WhitelistManager Whitelist { get; }
    public HiveTree Tree { get; }
    public IHiveBodies? Bodies { get; set; }
    public ISubhiveHost? Subhives { get; set; }
    /// <summary>Is a model profile usable (null = yes, else why not)? Decides whether a DeepSeek brain is available.</summary>
    public Func<string, string?>? ProfileCheck { get; set; }
    /// <summary>Quiet notices for main's brain (bus status/results/help). Never chat.</summary>
    public Action<GameEvent>? Notice { get; set; }
    public HiveBus? Bus { get; set; }
    public MemoryBusHost? Memory { get; set; }
    /// <summary>A new account was allocated (the arbiter/personality learn the name).</summary>
    public event Action<string>? Allocated;

    private string Short(string account) => account.Equals(HiveTree.Root, StringComparison.OrdinalIgnoreCase) ? "main" : HiveNode.ShortOf(account);

    // ================================================================ startup

    /// <summary>Adopts old fixed-roster names once and removes every stale managed account from the whitelist.</summary>
    public async Task<int> StartAsync(IEnumerable<string> legacyNames, Func<string, bool> isRunning, CancellationToken ct = default)
    {
        if (_cfg.AdoptLegacy && Whitelist.Adopt(legacyNames) is > 0 and var n)
            _log.LogInformation("hive: adopted {N} old fixed clone name(s) into the managed list (Hive:CloneUsernames is ignored now)", n);
        return _cfg.ReconcileOnStart ? await Whitelist.ReconcileAsync(isRunning, ct).ConfigureAwait(false) : 0;
    }

    // ================================================================ IHiveAccounts (the supervisor's seam)

    public (string? Name, string? Why) Allocate(HiveSpawnOptions? options)
    {
        if (Bodies is not { } b) return (null, "no bodies host");
        Allocation a;
        lock (_lock)
        {
            a = _allocator.Allocate(new AllocationFacts(b.Slots(), _regulars(), _knownPlayers(), b.Running(), b.Pending()));
            if (a.Ok) Managed.Upsert(a.Name!, x => x); // known from now on: the whitelist exception accepts it
        }
        if (!a.Ok) return (null, a.Error);
        _log.LogInformation("hive: allocated account {Name}{Reused}", a.Name, a.Reused ? " (reused)" : "");
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
        // An owner that went offline takes its subtree with it: each child deposits first (the recall's logout gate).
        foreach (var child in Tree.Children(account))
            if (child.State != "releasing")
            {
                Tree.SetState(child.Account, "releasing");
                try { Bodies?.Recall(child.Account, $"owner {Short(account)} released"); } catch (Exception ex) { _log.LogWarning(ex, "hive: release of {Child} failed", child.Account); }
            }
        foreach (var child in Tree.Children(account)) Tree.Put(child with { Owner = HiveTree.Root }); // re-parented while they log out
        Tree.Remove(account);
        if (!Managed.IsManaged(account)) return;
        Managed.MarkReleased(account);
        if (Whitelist.Accepts(account) && Managed.Get(account)?.Whitelisted == true) _ = Whitelist.RemoveAsync(account);
        _log.LogInformation("hive: {Account} released ({Reason})", account, reason);
    }

    private bool IsDeepSeek(string? profile) =>
        profile != null && (profile.Equals(_cfg.DeepSeekProfile, StringComparison.OrdinalIgnoreCase) || profile.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase));

    // ================================================================ the brain's API (hive tool) and other hives' (bus)

    /// <param name="owner">The asking hive: main ("main") for the brain tool; a hive's account for a bus request.</param>
    public string Spawn(SpawnRequest r, string owner = HiveTree.Root)
    {
        if (Bodies is not { } bodies) return "error: hive is off";
        owner = Tree.Norm(owner);
        if (owner != HiveTree.Root && Tree.Get(owner) is not { Kind: CloneKind.Hive })
            return $"no: {Short(owner)} isn't a hive (only main and hive-kind clones own clones)";
        var (kind, kindErr) = HiveKinds.ParseKind(r.Kind);
        if (kindErr != null) return "error: " + kindErr;
        bool hasScript = !string.IsNullOrWhiteSpace(r.Script) || !string.IsNullOrWhiteSpace(r.Code);
        var k = kind ?? (hasScript ? CloneKind.Script : CloneKind.Hive);
        var (brain, brainProfile) = HiveKinds.ParseBrain(r.Brain);
        string? profile = brainProfile ?? (string.IsNullOrWhiteSpace(r.Profile) ? null : r.Profile.Trim());

        switch (k)
        {
            case CloneKind.Script when !hasScript:
                return "error: kind script needs script=<saved name> (or code)";
            case CloneKind.Subhive:
                if (Subhives is not { } sub)
                    return "no: subhive bodies aren't available here (they need Bodies:Mode=inproc, one brain driving many bodies); spawn kind hive or script instead";
                var (acc, why) = Allocate(null);
                if (acc == null) return "no: " + why;
                return sub.Spawn(owner, acc, r.Task, r.Script);
            case CloneKind.Hive when brain == BrainKind.DeepSeek:
                profile = _cfg.DeepSeekProfile;
                var bad = ProfileCheck is { } check ? check(profile) : "no model profiles here";
                if (bad != null) return $"no: brain deepseek not available ({bad}); use brain opus";
                break;
            case CloneKind.Hive when brain == BrainKind.Opus:
                profile = null; // the clone's own brain tier: Opus 5.5 (enforced at startup)
                break;
        }
        if (r.Name is { Length: > 0 } named && owner != HiveTree.Root && !OwnedBy(named, owner))
            return $"no: {named} isn't one of {Short(owner)}'s clones";
        var opts = new HiveSpawnOptions(profile, r.Script, r.Code, r.Args, r.After) { Kind = HiveKinds.Name(k), Owner = owner };
        return bodies.Spawn(r.Task ?? "", r.Name, r.Handover, opts);
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
            return mine.Count == 0 ? "no clones of yours" : string.Join("; ", mine.Select(n => $"{n.Short} {HiveKinds.Name(n.Kind)} {n.State}"));
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
        List<string> roots;
        if (string.IsNullOrWhiteSpace(id) || id.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            roots = Tree.Children(requester).Select(c => c.Account).ToList();
        else if (Resolve(id) is { } acc)
        {
            if (requester != HiveTree.Root && !acc.Equals(requester, StringComparison.OrdinalIgnoreCase) && !Tree.IsUnder(acc, requester))
                return $"no: {Short(acc)} isn't one of {Short(requester)}'s clones";
            roots = [acc];
        }
        else if (requester == HiveTree.Root) return bodies.Recall(id, reason); // not in the tree (an old slot): plain recall
        else return $"no: no clone '{id}'";
        if (roots.Count == 0) return requester == HiveTree.Root ? bodies.Recall("all", reason) : "no clones of yours";

        var order = roots.SelectMany(Tree.ReleaseOrder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var a in order) Tree.SetState(a, "releasing");
        _ = Task.Run(() => CascadeAsync(order, reason));
        int kids = order.Count - roots.Count;
        return $"ok: releasing {string.Join(", ", roots.Select(Short))}" +
            (kids > 0 ? $" after {kids} child clone(s) ({string.Join(", ", order.Except(roots).Select(Short))})" : "") + "; each deposits before logout";
    }

    private int Depth(string account)
    {
        int d = 0;
        for (var cur = Tree.Get(account); cur != null && cur.Owner != HiveTree.Root && d < 64; cur = Tree.Get(cur.Owner)) d++;
        return d;
    }

    private async Task CascadeAsync(IReadOnlyList<string> order, string reason)
    {
        if (Bodies is not { } bodies) return;
        var timeout = TimeSpan.FromSeconds(Math.Max(5, _cfg.ReleaseChildSeconds));
        foreach (var level in order.GroupBy(Depth).OrderByDescending(g => g.Key))
        {
            var waits = new List<Task>();
            foreach (var a in level)
            {
                try { bodies.Recall(a, reason); } catch (Exception ex) { _log.LogWarning(ex, "hive: recall of {Account} failed", a); }
                waits.Add(bodies.WaitGoneAsync(a, timeout));
            }
            try { await Task.WhenAll(waits).ConfigureAwait(false); } catch (Exception ex) { _log.LogDebug(ex, "hive: release wait"); }
        }
    }

    public HiveTreeDto Snapshot() => Tree.Snapshot();

    // ================================================================ bus requests (main is the router and the admin root)

    public async Task<BusReply> HandleBusAsync(BusEnvelope e, CancellationToken ct)
    {
        var from = Tree.Norm(e.From);
        switch (e.Type)
        {
            case BusTypes.Spawn when e.As<SpawnReq>() is { } s:
                return Text(Spawn(new SpawnRequest(s.Kind, s.Brain, s.Task, s.Script, null, s.Args, s.After, s.Name), from));
            case BusTypes.Assign when e.As<AssignReq>() is { } a:
                return Text(Spawn(new SpawnRequest(Task: a.Task, Name: a.To), from));
            case BusTypes.Handover when e.As<AssignReq>() is { } h:
                return Text(Spawn(new SpawnRequest(Task: h.Task, Name: h.To, Handover: true), from));
            case BusTypes.Release when e.As<ReleaseReq>() is { } r:
                return Text(Release(r.Id, from));
            case BusTypes.List:
                return Text(List(from));
            case BusTypes.Whitelist when e.As<WhitelistReq>() is { } w:
                {
                    // Hives never whitelist on their own: main does it, only for a managed account in the asker's subtree.
                    var acc = Resolve(w.Name) ?? w.Name;
                    if (!Whitelist.Accepts(acc) || (from != HiveTree.Root && !Tree.IsUnder(acc, from)))
                        return BusReply.Fail($"refused: {w.Name} isn't a managed account of yours");
                    bool ok = w.Op.Equals("remove", StringComparison.OrdinalIgnoreCase) ? await Whitelist.RemoveAsync(acc, ct).ConfigureAwait(false)
                        : w.Op.Equals("add", StringComparison.OrdinalIgnoreCase) ? await Whitelist.AddAsync(acc, ct).ConfigureAwait(false) : false;
                    return ok ? Text($"ok: whitelist {w.Op} {Short(acc)}") : BusReply.Fail("whitelist " + w.Op + " failed");
                }
            case BusTypes.Status when e.As<StatusMsg>() is { } st:
                Quiet($"[hive bus] {Short(from)}: {Trunc(st.Text)}" + (st.Pct is { } p ? $" {p:P0}" : ""), EventPriority.Background);
                return Text("ok");
            case BusTypes.Result when e.As<ResultBusMsg>() is { } rs:
                Quiet($"[hive bus] {Short(from)} {(rs.Ok ? "done" : "failed")}: {Trunc(rs.Text)}", EventPriority.Low);
                return Text("ok");
            case BusTypes.NeedHelp when e.As<NeedHelpMsg>() is { } nh:
                Quiet($"[hive bus] {Short(from)} needs help: {Trunc(nh.Text)}", EventPriority.Normal, wake: true);
                return Text("ok");
            case BusTypes.Observation when e.As<ObservationMsg>() is { } ob:
                Quiet($"[hive bus] {Short(from)} saw: {Trunc(ob.Text)}", EventPriority.Background);
                return Text("ok");
            case BusTypes.MemWrite:
                return Memory is { } m ? await m.HandleAsync(e, ct).ConfigureAwait(false) : BusReply.Fail("no shared memory host");
            default:
                return BusReply.Fail($"main doesn't take {e.Type}");
        }
    }

    private static BusReply Text(string s) => s.StartsWith("error", StringComparison.Ordinal) ? BusReply.Fail(s) : BusReply.Of(s);
    private static string Trunc(string s, int n = 160) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private void Quiet(string text, EventPriority p, bool wake = false)
    {
        try { Notice?.Invoke(GameEvent.Notice(text, p) with { MustWake = wake }); } catch (Exception ex) { _log.LogDebug(ex, "hive: bus notice failed"); }
    }
}
