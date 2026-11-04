namespace MineClaw.Raptor.Model;

/// <summary>
/// tree_structures.py <c>Tree</c>: all_nodes, root_nodes (= top layer), leaf_nodes (= layer 0), num_layers and
/// layer_to_nodes, plus the bookkeeping used by incremental maintenance. Not thread-safe; <see cref="RaptorIndex"/>
/// serializes access.
/// </summary>
public sealed class RaptorTree
{
    private readonly Dictionary<int, RaptorNode> _all = new();
    private readonly List<SortedSet<int>> _layers = [new()];

    public IReadOnlyDictionary<int, RaptorNode> AllNodes => _all;
    /// <summary>Number of summary layers above the leaves (reference: tree.num_layers).</summary>
    public int NumLayers { get; internal set; }
    public int NextIndex { get; internal set; }
    public int Dimensions { get; internal set; }
    public string EmbeddingModel { get; internal set; } = "";

    // -------- maintenance statistics (MineClaw extension) --------

    public long BuildId { get; internal set; }
    public DateTimeOffset BuiltAt { get; internal set; }
    public int LeavesAtBuild { get; internal set; }
    public int InsertedSinceBuild { get; internal set; }
    public int RemovedSinceBuild { get; internal set; }
    /// <summary>Mean cosine(leaf, parent) over all leaf→parent edges right after the build.</summary>
    public double BaselineLeafParentSimilarity { get; internal set; }
    /// <summary>Standard deviation of those cosines (scale for the drift trigger).</summary>
    public double BaselineLeafParentSimilarityStd { get; internal set; }
    /// <summary>Softmax temperature for incremental soft attachment, fitted to the build (InsertCalibration).</summary>
    public double InsertTemperature { get; internal set; } = InsertCalibration.DefaultTemperature;
    public double InsertedSimilaritySum { get; internal set; }
    public int InsertedSimilarityCount { get; internal set; }
    public long InsertSeq { get; internal set; }

    public IReadOnlyCollection<int> LayerIndices(int layer) => layer < _layers.Count ? _layers[layer] : [];
    public IEnumerable<RaptorNode> Layer(int layer) => LayerIndices(layer).Select(i => _all[i]);
    public IEnumerable<RaptorNode> Leaves => Layer(0);
    public int LeafCount => _layers[0].Count;
    public int Count => _all.Count;
    /// <summary>reference: tree.root_nodes = layer_to_nodes[num_layers].</summary>
    public IEnumerable<RaptorNode> Roots => Layer(NumLayers);
    public RaptorNode this[int index] => _all[index];
    public bool TryGet(int index, out RaptorNode node) => _all.TryGetValue(index, out node!);

    internal void Add(RaptorNode node)
    {
        _all.Add(node.Index, node);
        while (_layers.Count <= node.Layer) _layers.Add(new());
        _layers[node.Layer].Add(node.Index);
        if (node.Index >= NextIndex) NextIndex = node.Index + 1;
    }

    internal void Remove(int index)
    {
        if (!_all.Remove(index, out var node)) return;
        _layers[node.Layer].Remove(index);
        foreach (var c in node.Children) if (_all.TryGetValue(c, out var cn)) cn.Parents.Remove(index);
        foreach (var p in node.Parents) if (_all.TryGetValue(p, out var pn)) pn.Children.Remove(index);
    }

    internal void Link(int parent, int child)
    {
        _all[parent].Children.Add(child);
        _all[child].Parents.Add(parent);
    }

    internal void Unlink(int parent, int child)
    {
        if (_all.TryGetValue(parent, out var p)) p.Children.Remove(child);
        if (_all.TryGetValue(child, out var c)) c.Parents.Remove(parent);
    }

    /// <summary>Rebuilds parent sets from children (after load).</summary>
    internal void RebuildParents()
    {
        foreach (var n in _all.Values) n.Parents.Clear();
        foreach (var n in _all.Values)
            foreach (var c in n.Children)
                if (_all.TryGetValue(c, out var cn)) cn.Parents.Add(n.Index);
    }

    /// <summary>Checks the structural invariants; returns a list of violations (empty when consistent).</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        ValidateLayers(errors);
        foreach (var n in _all.Values)
        {
            ValidateChildren(n, errors);
            ValidateParents(n, errors);
            ValidateShape(n, errors);
        }
        return errors;
    }

    private void ValidateLayers(List<string> errors)
    {
        for (int l = 0; l < _layers.Count; l++)
            foreach (var i in _layers[l])
                if (!_all.TryGetValue(i, out var n) || n.Layer != l) errors.Add($"layer {l} lists #{i} wrongly");
    }

    private void ValidateChildren(RaptorNode n, List<string> errors)
    {
        foreach (var c in n.Children)
        {
            if (!_all.TryGetValue(c, out var cn)) { errors.Add($"#{n.Index} has missing child #{c}"); continue; }
            if (cn.Layer != n.Layer - 1) errors.Add($"#{n.Index} (L{n.Layer}) child #{c} is L{cn.Layer}");
            if (!cn.Parents.Contains(n.Index)) errors.Add($"#{c} lacks parent link to #{n.Index}");
        }
    }

    private void ValidateParents(RaptorNode n, List<string> errors)
    {
        foreach (var p in n.Parents)
            if (!_all.TryGetValue(p, out var pn) || !pn.Children.Contains(n.Index)) errors.Add($"#{n.Index} parent #{p} does not list it");
    }

    private void ValidateShape(RaptorNode n, List<string> errors)
    {
        if (n.Layer > 0 && n.Children.Count == 0) errors.Add($"summary #{n.Index} has no children");
        if (n.Layer < NumLayers && n.Parents.Count == 0) errors.Add($"non-root #{n.Index} (L{n.Layer}) has no parent");
        if (n.Layer > NumLayers) errors.Add($"#{n.Index} above NumLayers");
        if (n.Embedding.Length != Dimensions && Dimensions > 0) errors.Add($"#{n.Index} embedding dims {n.Embedding.Length}");
    }
}
