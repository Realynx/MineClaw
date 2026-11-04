using System.Security.Cryptography;
using System.Text;
using MineClaw.MemoryExplorer.Controls;
using Xunit.Abstractions;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>
/// Pins the Explorer's pure logic byte for byte (WP08 complexity refactor): the script highlighter over a corpus and seeded
/// fuzz, and the RAPTOR graph model (parents, weights, categories, times, roots, layout, topic terms) over seeded trees.
/// Set EXPLORER_GOLDEN_OUT to a directory to write the dumps out (to diff after a change).
/// </summary>
public sealed class ExplorerGoldenTests(ITestOutputHelper output)
{
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..20];

    private void Compare(List<(string Name, string Dump)> rows, string expected)
    {
        var outDir = Environment.GetEnvironmentVariable("EXPLORER_GOLDEN_OUT");
        var actual = new StringBuilder();
        foreach (var (n, dump) in rows)
        {
            if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, n + ".txt"), dump);
            actual.Append(n).Append(' ').Append(Hash(dump)).Append('\n');
        }
        var want = string.Join("\n", expected.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0)) + "\n";
        if (want != actual.ToString()) output.WriteLine(actual.ToString());
        Assert.Equal(want, actual.ToString());
    }

    private const string Js = """
        // a script
        const x = 1.5e3 + .25 + 0x1f; let s = "a \"q\" b", t = 'c\\', u = `multi
        line ${x} template` + me.inv("cobblestone");
        /* block
           comment */ async function go() { await wait(20); if (x > 2) return skill.mine({ id: 4 }); }
        forEach(bodies(), b => b.say('hi')); /* one-line */ new Date().getTime ();
        $weird_name1 = obj.forEach; ok(); this.retry(3) // trailing
        "unterminated
        `open template
        still open` done
        """;

    private const string Lua = """
        -- lua comment
        local x = 10 --[[ block
        still ]] local y = "str\"ing" .. 'x' .. [[raw]]
        function go(n) if n > 0 then return skill("mine", n) else return nil end end
        for i = 1, 3 do say(i) end -- tail
        --[[ unterminated
        a.b:c() ok() expect(1)
        """;

    private static string Dump(IReadOnlyList<IReadOnlyList<CodeSpan>> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            foreach (var s in line) sb.Append((int)s.Kind).Append(':').Append(s.Text.Length).Append(':').Append(s.Text).Append('\u0001');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void Highlighter_and_graph_model_match_the_golden_hashes()
    {
        var rows = new List<(string, string)>
        {
            ("hl-js", Dump(CodeHighlighter.Lines(Js, lua: false))),
            ("hl-lua", Dump(CodeHighlighter.Lines(Lua, lua: true))),
            ("hl-js-as-lua", Dump(CodeHighlighter.Lines(Js, lua: true))),
            ("hl-lua-as-js", Dump(CodeHighlighter.Lines(Lua, lua: false))),
            ("hl-empty", Dump(CodeHighlighter.Lines(null, false)) + Dump(CodeHighlighter.Lines("", true))),
        };
        // Seeded fuzz over the characters the lexer cares about.
        const string alphabet = "/*-[]`'\"\\. 0123abc_$(xyz)\t{}=;,ok me";
        var rnd = new Random(808);
        var sb = new StringBuilder();
        foreach (bool lua in new[] { false, true })
            for (int doc = 0; doc < 300; doc++)
            {
                var code = new StringBuilder();
                int len = rnd.Next(0, 160);
                for (int i = 0; i < len; i++) code.Append(rnd.Next(14) == 0 ? '\n' : alphabet[rnd.Next(alphabet.Length)]);
                sb.Append(Dump(CodeHighlighter.Lines(code.ToString(), lua))).Append("--\n");
            }
        rows.Add(("hl-fuzz", sb.ToString()));

        // Graph models over seeded trees.
        sb.Clear();
        string[] kinds = ["episode", "gamedata", "recipe", "skill", "routine", "player", "chat", "claim", "structure", "world", "waypoint", "selfnote",
            "self", "identity", "journal", "knowledge", "EPISODE", null!];
        string[] sources = ["activity", "journal", "episode", "chat", null!];
        for (int seed = 0; seed < 40; seed++)
        {
            var r = new Random(seed * 31 + 7);
            var specs = new List<NodeSpec>();
            int leaves = r.Next(0, 40), layers = r.Next(1, 4);
            var below = new List<int>();
            for (int i = 0; i < leaves; i++)
            {
                specs.Add(new NodeSpec(i, 0, [], $"leaf {i} text\r\nline two {new string('x', r.Next(0, 300))}", r.Next(0, 90), r.Next(5) == 0,
                    kinds[r.Next(kinds.Length)], null, sources[r.Next(sources.Length)], r.Next(3) == 0 ? null : $"title {i}",
                    r.Next(4) == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(r.Next(4) == 0 ? 1000 : 1_800_000_000 + r.Next(0, 100_000))));
                below.Add(i);
            }
            int next = 1000;
            for (int layer = 1; layer <= layers && below.Count > 0; layer++)
            {
                var above = new List<int>();
                int clusters = Math.Max(1, below.Count / r.Next(2, 5));
                for (int c = 0; c < clusters; c++)
                {
                    var kids = below.Where(_ => r.Next(clusters) == 0).ToList();
                    if (r.Next(6) == 0 && below.Count > 0) kids.Add(below[r.Next(below.Count)]);
                    if (r.Next(10) == 0) kids.Add(99_999); // a child that isn't in the tree
                    specs.Add(new NodeSpec(next, layer, kids, $"summary {next}", r.Next(50, 200), r.Next(4) == 0,
                        Updated: r.Next(3) == 0 ? DateTimeOffset.FromUnixTimeSeconds(1_800_050_000) : null));
                    above.Add(next++);
                }
                below = above;
            }
            if (seed % 3 == 0) specs = specs.OrderBy(s => (s.Index * 7919) % 101).ToList();
            var m = GraphModel.FromSpecs(specs, layers + seed % 2);
            TopicLabeler.Label(m);
            sb.Append("model ").Append(seed).Append(' ').Append(m.NumLayers).Append(' ').Append(string.Join(",", m.TopLevel)).Append(' ')
                .Append(string.Join(",", m.LayerCounts)).Append(' ').Append(m.MinTime).Append(' ').Append(m.MaxTime).Append(' ').Append(m.TotalLeaves).Append('\n');
            foreach (var g in m.Nodes)
                sb.Append(g.Index).Append('/').Append(g.Layer).Append('/').Append(g.Slot).Append(" p").Append(g.Parent).Append(" c").Append(string.Join(",", g.Children))
                    .Append(" o").Append(string.Join(",", g.OtherParents)).Append(" n").Append(g.ChildCount).Append(" l").Append(g.Leaves).Append('/').Append(g.LeafCount)
                    .Append(' ').Append(g.Category).Append(' ').Append(g.Newest).Append(' ').Append(g.TimeRank.ToString("R")).Append(" r").Append(g.Root)
                    .Append(' ').Append(g.Title).Append(" | ").Append(g.Preview).Append(" | ").Append(string.Join(",", g.Terms)).Append('\n');
            var L = m.Layout;
            for (int s = 0; s < m.Nodes.Length; s++) sb.Append(L.X0[s].ToString("R")).Append(',').Append(L.X1[s].ToString("R")).Append(',').Append(L.Row[s]).Append(';');
            sb.Append('\n').Append(L.Rows).Append(' ').Append(string.Join("|", L.RowSlots.Select(rs => string.Join(",", rs))))
                .Append(' ').Append(string.Join("|", L.LeafRuns.Select(lr => $"{lr.X0:R},{lr.X1:R},{lr.Owner}"))).Append('\n');
        }
        foreach (var k in kinds)
            foreach (var s in sources)
                sb.Append(GraphModel.Categorize(k, null, s)).Append(' ');
        rows.Add(("graph", sb.ToString()));
        Compare(rows, Golden);
    }

    private const string Golden = """
        hl-js 60C3DB5254292249FE93
        hl-lua EAFB20AD0EEAE0C94944
        hl-js-as-lua D9AC8F8FEB338A93C793
        hl-lua-as-js 1EEAC8F475A208366C62
        hl-empty 75A11DA44C802486BC6F
        hl-fuzz 6C9B9B8A2796BB65A9A4
        graph 85980176CA11DAD959E1
        """;
}
