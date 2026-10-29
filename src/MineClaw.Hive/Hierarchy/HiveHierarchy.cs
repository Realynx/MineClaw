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
public sealed record SpawnRequest(string? Kind = null, string? Brain = null, string? Task = null, string? Script = null, string? Code = null,
    string? Args = null, string? After = null, string? Name = null, bool Handover = false, string? Profile = null, int? Bodies = null, bool Reset = false);

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
    /// <summary>Main's own subhive bodies (in this process). Null = <c>hive spawn kind=subhive</c> for main says not available.</summary>
    public ISubhiveHost? Subhives { get; set; }
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
    /// <summary>A new account was allocated (the arbiter/personality learn the name).</summary>
    public event Action<string>? Allocated;

    private string Short(string account) => account.Equals(HiveTree.Root, StringComparison.OrdinalIgnoreCase) ? "main" : HiveNode.ShortOf(account);

    /// <summary>What the hive runs now (the resource ceiling's input): clone processes and subhive bodies anywhere.</summary>
    public ResourceUse Use() => new(Bodies?.Running().Count ?? 0, Tree.All().Count(n => n.Kind == CloneKind.Subhive));

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
        bool body = HiveKinds.ParseKind(options?.Kind).Kind == CloneKind.Subhive;
        if (Resources?.CanAdd(body ? 0 : 1, body ? 1 : 0) is { Ok: false } full) return (null, full.Why);
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
    private (string? Name, string? Why) AllocateBody(string owner, string? task, string? script)
    {
        if (Bodies is not { } b) return (null, "no bodies host");
        if (Resources?.CanAdd(0, 1) is { Ok: false } full) return (null, full.Why);
        Allocation a;
        lock (_lock)
        {
            a = _allocator.Allocate(Facts(b));
            if (!a.Ok) return (null, a.Error);
            Managed.Upsert(a.Name!, x => x);
            var node = Tree.Put(new HiveNode(a.Name!, CloneKind.Subhive, null, owner) { Task = task, Script = script, Since = DateTimeOffset.UtcNow });
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
        if (owner != HiveTree.Root && Tree.Get(owner) is not { Kind: CloneKind.Hive })
            return $"no: {Short(owner)} isn't a hive (only main and hive-kind clones own clones)";
        var (kind, kindErr) = HiveKinds.ParseKind(r.Kind);
        if (kindErr != null) return "error: " + kindErr;
        bool hasScript = !string.IsNullOrWhiteSpace(r.Script) || !string.IsNullOrWhiteSpace(r.Code);
        var (brain, brainProfile) = HiveKinds.ParseBrain(r.Brain);
        string? profile = brainProfile ?? (string.IsNullOrWhiteSpace(r.Profile) ? null : r.Profile.Trim());
        var k = kind ?? DefaultKind(owner, hasScript, brain != null || profile != null, r.Name);

        switch (k)
        {
            case CloneKind.Script when !hasScript:
                return "error: kind script needs script=<saved name> (or code)";
            case CloneKind.Subhive:
                return SpawnSubhives(owner, r);
            case CloneKind.Hive when brain == BrainKind.DeepSeek:
                profile = _cfg.DeepSeekProfile;
                var bad = ProfileCheck is { } check ? check(profile) : "no model profiles here";
                if (bad != null) return $"no: brain deepseek not available ({bad}); use brain opus; spawned nothing";
                break;
            case CloneKind.Hive when brain == BrainKind.Opus:
                profile = null; // the clone's own brain tier: Opus 5.5 (enforced at startup)
                break;
        }
        if (r.Name is { Length: > 0 } named && owner != HiveTree.Root && !OwnedBy(named, owner))
            return $"no: {named} isn't one of {Short(owner)}'s clones";
        int? hostBodies = null;
        string note = "";
        if (k == CloneKind.Hive && r.Bodies is > 0 and var asked)
        {
            // One hive process whose brain drives N bodies in its own process: as many as memory and the player cap allow.
            int want = Math.Min(asked, Math.Max(1, MaxBodiesPerSpawn));
            int fit = Resources?.BodiesThatFit(withHives: 1) ?? int.MaxValue;
            var facts = Facts(bodies);
            var slot = SlotPolicy.CanSpawn(facts.Slots, facts.Regulars, facts.Pending);
            int room = Math.Max(0, slot.Free - slot.Reserved - 1); // after the hive's own login
            int n = Math.Min(want, Math.Min(fit, room));
            if (n <= 0)
                return $"no: no room for {asked} bodies ({(fit <= 0 ? Resources?.CanAdd(1, 1).Why ?? "memory" : $"player cap: {slot.Reason}")}); spawned nothing";
            hostBodies = n;
            if (n < asked)
                note = $"; only {n} of {asked} bodies fit (" + (n == want && asked > want ? $"max {want} per spawn" : fit < want ? $"memory: room for {fit}" : $"player cap: {room} slot(s) after the hive") + ")";
        }
        var opts = new HiveSpawnOptions(profile, r.Script, r.Code, r.Args, r.After) { Kind = HiveKinds.Name(k), Owner = owner, Bodies = hostBodies };
        var text = bodies.Spawn(r.Task ?? "", r.Name, r.Handover, opts);
        if (hostBodies is { } hb && text.StartsWith("ok", StringComparison.Ordinal))
            text += $"; ONE {(brain == BrainKind.DeepSeek ? "DeepSeek" : "Opus")} hive whose brain will drive {hb} subhive bod{(hb == 1 ? "y" : "ies")} " +
                $"(none in game yet: they log in {LaunchGapSeconds:0.#}s apart once it is in)" + note;
        return text;
    }

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
        if (local && Subhives is null)
            return "no: subhive bodies aren't available here (Hive:Subhives:Enabled is off); spawn kind hive or script instead";
        if (!local && (Bus is null || !RemoteSubhives)) return $"no: {Short(owner)} can't host subhive bodies here (no hive bus)";
        // A named body: a new job for it (it stays logged in).
        if (r.Name is { Length: > 0 } named)
        {
            if (Resolve(named) is not { } acc || Tree.Get(acc) is not { Kind: CloneKind.Subhive } node) return $"no: {named} isn't a subhive body";
            if (!Tree.IsUnder(acc, owner)) return $"no: {named} isn't one of {Short(owner)}'s bodies";
            Tree.Put(node with { Task = r.Task ?? node.Task, Script = r.Script ?? node.Script });
            if (node.Owner == HiveTree.Root) return Subhives!.Spawn(owner, acc, r.Task, r.Script);
            _ = Task.Run(() => HostRemoteAsync(node.Owner, [acc], r.Task, r.Script, whitelist: false));
            return $"ok: new job for {node.Short} (its hive {Short(node.Owner)} drives it)";
        }
        int asked = Math.Max(1, r.Bodies ?? 1);
        int want = Math.Min(asked, Math.Max(1, MaxBodiesPerSpawn));
        var got = new List<string>();
        // A hive (re)starting asks for its bodies: the ones it had died with its old process; reuse those accounts first.
        if (r.Reset && !local)
            foreach (var old in Tree.Children(owner).Where(c => c.Kind == CloneKind.Subhive).ToList())
            {
                if (got.Count < want) { got.Add(old.Account); Tree.Put(old with { Task = r.Task ?? old.Task, State = "spawning" }); }
                else Released(old.Account, $"its hive {Short(owner)} restarted with fewer bodies");
            }
        string? why = asked > want ? $"max {want} per spawn" : null;
        while (got.Count < want)
        {
            var (acc, w) = AllocateBody(owner, r.Task, r.Script);
            if (acc == null) { why = w; break; }
            got.Add(acc);
        }
        if (got.Count == 0) return "no: " + why + "; spawned nothing";
        if (local)
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
            if (refused.Count == got.Count) return first!;
            got = got.Except(refused).ToList();
        }
        else
        {
            var accounts = got.ToList();
            _ = Task.Run(() => HostRemoteAsync(owner, accounts, r.Task, r.Script, whitelist: true));
        }
        var names = HiveTree.Ranges(got.Select(HiveNode.ShortOf));
        return $"ok: {got.Count} subhive bod{(got.Count == 1 ? "y" : "ies")} ({names}) " +
            (local ? "your brain drives (body=\"<n>\")" : $"for {Short(owner)}'s brain") +
            $"; none in game yet{(got.Count > 1 ? $", logging in {LaunchGapSeconds:0.#}s apart" : "")}" +
            (got.Count < asked ? $"; only {got.Count} of {asked}: {why}" : "");
    }

    /// <summary>Whitelists each account (confirmed, one at a time) and asks the owning hive to log it in. A refusal releases it.</summary>
    private async Task HostRemoteAsync(string owner, IReadOnlyList<string> accounts, string? task, string? script, bool whitelist)
    {
        foreach (var acc in accounts)
        {
            try
            {
                if (whitelist) await BeforeLaunchAsync(acc).ConfigureAwait(false); // on the whitelist before the hive logs it in
                var reply = await Bus!.PublishAsync(BusTypes.Host, new HostReq(acc, task, script), to: owner).ConfigureAwait(false);
                var text = reply.Ok ? reply.As<string>() ?? "ok" : reply.Error ?? "failed";
                if (!reply.Ok || !text.StartsWith("ok", StringComparison.Ordinal))
                {
                    _log.LogWarning("hive: {Owner} didn't host {Account}: {Text}", owner, acc, text);
                    if (whitelist) Released(acc, "its hive didn't host it: " + text);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "hive: hosting {Account} on {Owner} failed", acc, owner);
                if (whitelist) Released(acc, "hosting failed: " + ex.Message);
            }
        }
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
        var kids = order.Except(roots).ToList();
        return $"ok: releasing {HiveTree.Ranges(roots.Select(Short))}" +
            (kids.Count > 0 ? $" after {kids.Count} child clone(s) ({HiveTree.Ranges(kids.Select(Short))})" : "") + "; each deposits before logout";
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
        switch (e.Type)
        {
            case BusTypes.Spawn when e.As<SpawnReq>() is { } s:
                return Text(Spawn(new SpawnRequest(s.Kind, s.Brain, s.Task, s.Script, null, s.Args, s.After, s.Name, Bodies: s.Bodies, Reset: s.Reset), from));
            case BusTypes.Assign when e.As<AssignReq>() is { } a:
                return Text(Spawn(new SpawnRequest(Task: a.Task, Name: a.To), from));
            case BusTypes.Handover when e.As<AssignReq>() is { } h:
                return Text(Spawn(new SpawnRequest(Task: h.Task, Name: h.To, Handover: true), from));
            case BusTypes.Release when e.As<ReleaseReq>() is { } r:
                return Text(Release(r.Id, from));
            case BusTypes.List:
                return Text(List(from));
            case BusTypes.SubhiveGone when e.As<SubhiveGoneMsg>() is { } g:
                {
                    // Only the hive hosting a body reports it gone; main then takes it off the whitelist.
                    var acc = Resolve(g.Account) ?? g.Account;
                    if (Tree.Get(acc) is not { Kind: CloneKind.Subhive } node || !node.Owner.Equals(from, StringComparison.OrdinalIgnoreCase))
                        return BusReply.Fail($"refused: {g.Account} isn't a body of yours");
                    Released(acc, g.Reason);
                    return Text("ok");
                }
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
