using System.Security.Cryptography;
using System.Text;
using MineClaw.Raptor.Model;

namespace MineClaw.Raptor;

/// <summary>
/// Summaries of an existing tree keyed by a hash of their exact cluster text (utils.get_text of the children).
/// A rebuild that reproduces an identical cluster reuses the summary and its embedding instead of paying for a new
/// summarizer call (README D11). Dirty or placeholder summaries are never reused.
/// </summary>
public sealed class SummaryCache
{
    private readonly Dictionary<string, (string Text, float[] Embedding)> _map = new(StringComparer.Ordinal);

    public int Count => _map.Count;

    public static string Hash(string context) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context)));

    public void Add(string context, string text, float[] embedding) => _map[Hash(context)] = (text, embedding);

    public bool TryGet(string context, out (string Text, float[] Embedding) entry) => _map.TryGetValue(Hash(context), out entry);

    public static SummaryCache FromTree(RaptorTree tree)
    {
        var cache = new SummaryCache();
        foreach (var n in tree.AllNodes.Values)
        {
            if (n.IsLeaf || n.Dirty || n.SummaryPending || n.Children.Count == 0) continue;
            var context = RaptorTreeBuilder.GetText(n.Children.Select(c => tree[c]));
            cache.Add(context, n.Text, n.Embedding);
        }
        return cache;
    }
}
