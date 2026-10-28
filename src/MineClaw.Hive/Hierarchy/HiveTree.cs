namespace MineClaw.Hive.Hierarchy;

/// <summary>One node of the ownership tree. The root is main (<see cref="HiveTree.Root"/>).</summary>
public sealed record HiveNode(string Account, CloneKind Kind, BrainKind? Brain, string Owner)
{
    public string? Task { get; init; }
    public string? Script { get; init; }
    public DateTimeOffset Since { get; init; }
    /// <summary>"spawning" | "running" | "releasing".</summary>
    public string State { get; init; } = "spawning";
    public string Short => ShortOf(Account);

    public static string ShortOf(string account) =>
        Chat.ChatArbiter.NumberOf(account) is { } n && account.Length > n.Length && account[account.Length - n.Length - 1] == '_' ? n : account;
}

/// <summary>Data for the Explorer's Live Hive tree and the supervisor's <c>/api/hive/tree</c>.</summary>
public sealed record HiveTreeDto(string Id, string Name, string Kind, string? Brain, string? Owner, string State, string? Task,
    List<HiveTreeDto> Children);

/// <summary>
/// Who owns which clone. Main is always the root (and always the admin root); a hive may own subhive and script bodies, and
/// further hives. Ownership decides who may assign or release a clone and what a release cascades to; the whitelist is never
/// the owner's business (main does every add/remove).
/// </summary>
public sealed class HiveTree(string main)
{
    public const string Root = "main";
    private readonly object _lock = new();
    private readonly Dictionary<string, HiveNode> _nodes = new(StringComparer.OrdinalIgnoreCase);

    public string Main => main;

    /// <summary>"main" or main's login → <see cref="Root"/>; otherwise the account as given.</summary>
    public string Norm(string? id) => string.IsNullOrWhiteSpace(id) || id.Equals(Root, StringComparison.OrdinalIgnoreCase)
        || id.Equals(main, StringComparison.OrdinalIgnoreCase) ? Root : id.Trim();

    public HiveNode? Get(string account) { lock (_lock) return _nodes.GetValueOrDefault(account); }
    public IReadOnlyList<HiveNode> All() { lock (_lock) return _nodes.Values.ToList(); }
    public int Count { get { lock (_lock) return _nodes.Count; } }

    /// <summary>Adds or updates a node. An owner that isn't in the tree (gone meanwhile) means main owns it.</summary>
    public HiveNode Put(HiveNode node)
    {
        lock (_lock)
        {
            var owner = Norm(node.Owner);
            if (owner != Root && (!_nodes.ContainsKey(owner) || owner.Equals(node.Account, StringComparison.OrdinalIgnoreCase) || IsUnder(owner, node.Account)))
                owner = Root; // never a cycle, never an orphan
            var n = node with { Owner = owner };
            _nodes[node.Account] = n;
            return n;
        }
    }

    public void SetState(string account, string state)
    {
        lock (_lock) if (_nodes.TryGetValue(account, out var n)) _nodes[account] = n with { State = state };
    }

    public bool Remove(string account) { lock (_lock) return _nodes.Remove(account); }

    public IReadOnlyList<HiveNode> Children(string owner)
    {
        var o = Norm(owner);
        lock (_lock) return _nodes.Values.Where(n => n.Owner.Equals(o, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Account).ToList();
    }

    /// <summary>True when <paramref name="account"/> is <paramref name="ancestor"/> or somewhere below it.</summary>
    public bool IsUnder(string account, string ancestor)
    {
        var a = Norm(ancestor);
        if (a == Root) return true;
        lock (_lock)
        {
            var cur = account;
            for (int guard = 0; guard < 64 && cur != Root; guard++)
            {
                if (cur.Equals(a, StringComparison.OrdinalIgnoreCase)) return true;
                if (!_nodes.TryGetValue(cur, out var n)) return false;
                cur = n.Owner;
            }
            return false;
        }
    }

    /// <summary>The release order for <paramref name="account"/>: its descendants deepest first, then itself (post-order).</summary>
    public IReadOnlyList<string> ReleaseOrder(string account)
    {
        var list = new List<string>();
        void Walk(string a, int depth)
        {
            if (depth > 64) return;
            foreach (var c in Children(a)) Walk(c.Account, depth + 1);
            list.Add(a);
        }
        Walk(account, 0);
        return list;
    }

    /// <summary>"101 hive opus; 102 script cobble_loop (of 101)" (compact, for <c>hive list</c>); "main alone" when empty.</summary>
    public string Render()
    {
        var parts = new List<string>();
        void Walk(string owner, int depth)
        {
            if (depth > 16) return;
            foreach (var c in Children(owner))
            {
                var what = c.Kind switch
                {
                    CloneKind.Script => $"script {c.Script ?? "?"}",
                    CloneKind.Subhive => "subhive",
                    _ => $"hive {HiveKinds.Name(c.Brain ?? BrainKind.Opus)}",
                };
                parts.Add($"{c.Short} {what}" + (owner != Root ? $" (of {HiveNode.ShortOf(owner)})" : "") + (c.State != "running" ? $" {c.State}" : ""));
                Walk(c.Account, depth + 1);
            }
        }
        Walk(Root, 0);
        return parts.Count == 0 ? "main alone" : string.Join("; ", parts);
    }

    public HiveTreeDto Snapshot(string mainBrain = "opus")
    {
        HiveTreeDto Build(HiveNode n, int depth) => new(n.Short, n.Account, HiveKinds.Name(n.Kind), n.Kind == CloneKind.Hive ? HiveKinds.Name(n.Brain ?? BrainKind.Opus) : null,
            n.Owner == Root ? main : n.Owner, n.State, n.Kind == CloneKind.Script ? n.Script : n.Task,
            depth > 16 ? [] : Children(n.Account).Select(c => Build(c, depth + 1)).ToList());
        return new HiveTreeDto(Root, main, "main", mainBrain, null, "running", null, Children(Root).Select(c => Build(c, 1)).ToList());
    }
}
