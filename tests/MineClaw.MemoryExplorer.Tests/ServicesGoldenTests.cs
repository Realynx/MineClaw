using System.Security.Cryptography;
using System.Text;
using MineClaw.MemoryExplorer.Services;
using Xunit.Abstractions;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>
/// Pins the Explorer's services byte for byte (WP08 complexity refactor, checkpoint 3): the edit form's prefill per kind, the
/// script line diff over seeded edits, the shard kind of every browser key, the browser rows of every kind over the fixture db
/// (without the times the fixture writes), and the default packs listing over the repo's folder and broken copies of it.
/// Set EXPLORER_GOLDEN_OUT to a directory to write the dumps out.
/// </summary>
public sealed class ServicesGoldenTests(ITestOutputHelper output) : IAsyncLifetime
{
    private FixtureDb _fx = null!;
    public async Task InitializeAsync() => _fx = await FixtureDb.CreateAsync();
    public Task DisposeAsync() { _fx.Dispose(); return Task.CompletedTask; }

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

    private static string Dict(Dictionary<string, string?> d) => string.Join("|", d.Select(kv => $"{kv.Key}={kv.Value?.Replace("\n", "\\n") ?? "null"}"));

    private static string PrefillDump()
    {
        var rows = new[]
        {
            new MemoryRow { Kind = "x", Id = "1", Name = "cobble gen", Text = "a note\r\n• step one\n• step two\nmore", Type = "farm", State = "verified", Dimension = -1, Confidence = 0.756,
                Props = [new("x", "1"), new("y", "64"), new("z", "-3"), new("note", "by the tree"), new("note", "dup"),new("min", "0,0,0"), new("max", "1,1,1"), new("claimant", "Fox"),
                    new("reason", "said so"), new("player", "Robin")] },
            new MemoryRow { Kind = "y", Id = "2", Name = "", Text = "chest of 64 cobble\nannotation: the old one" },
            new MemoryRow { Kind = "z", Id = "3", Name = "n", Text = "", Dimension = 0 },
        };
        var sb = new StringBuilder();
        foreach (var kind in new[] { "fact", "facts", "place", "structure", "Structures", "claim", "trait", "lesson", "observation", "observations", "unknown", "" })
            foreach (var r in rows)
                sb.Append(kind).Append(' ').Append(r.Id).Append(" => ").Append(EditPrefill.IdOf(kind, r)).Append(' ').Append(Dict(EditPrefill.For(kind, r))).Append('\n');
        return sb.ToString();
    }

    private static string DiffDump()
    {
        var r = new Random(91);
        var sb = new StringBuilder();
        string[] words = ["a", "b", "c", "dig()", "move()", "  ", "say('x')  ", "say('x')", ""];
        for (int i = 0; i < 120; i++)
        {
            var a = Enumerable.Range(0, r.Next(0, 14)).Select(_ => words[r.Next(words.Length)]).ToList();
            var b = a.ToList();
            for (int e = r.Next(0, 5); e > 0 && b.Count > 0; e--)
            {
                int at = r.Next(b.Count);
                switch (r.Next(3))
                {
                    case 0: b.RemoveAt(at); break;
                    case 1: b.Insert(at, words[r.Next(words.Length)]); break;
                    default: b[at] = words[r.Next(words.Length)]; break;
                }
            }
            string? A = i % 11 == 0 ? null : string.Join(i % 2 == 0 ? "\n" : "\r\n", a), B = i % 13 == 0 ? null : string.Join("\n", b);
            var lines = ScriptDiff.Lines(A, B);
            foreach (var l in lines) sb.Append(l.Prefix).Append(l.OldText).Append(',').Append(l.NewText).Append(' ').Append(l.Text).Append('|');
            sb.Append('\n');
            foreach (var l in ScriptDiff.Hunks(lines, i % 4)) sb.Append(l.Prefix).Append(l.OldText).Append(',').Append(l.NewText).Append('|');
            sb.Append('\n');
        }
        var big = string.Join("\n", Enumerable.Range(0, 2100).Select(i => "line " + i));
        var bigLines = ScriptDiff.Lines(big, big + "\nx");
        sb.Append(bigLines.Count).Append(' ').Append(bigLines[0].Kind).Append(' ').Append(bigLines[^1].Kind).Append(' ').Append(bigLines[^1].Text).Append('\n');
        return sb.ToString();
    }

    private static string KindDump()
    {
        var sb = new StringBuilder();
        foreach (var k in MemoryQueries.Kinds.Select(k => k.Key).Concat(["claims", "beliefs", "Facts", "", "x"]))
            sb.Append(k).Append('=').Append(ShardService.KindOf(k) ?? "null").Append(' ');
        return sb.ToString();
    }

    private string RowsDump()
    {
        var sb = new StringBuilder();
        using var bank = MemoryBank.OpenAsync(_fx.Path, FixtureDb.Config()).GetAwaiter().GetResult();
        var q = new MemoryQueries(bank.Db);
        foreach (var k in MemoryQueries.Kinds.Select(k => k.Key).Concat(["unknown", ""]))
        {
            var rows = q.Rows(k);
            sb.Append("== ").Append(k).Append(' ').Append(rows.Count).Append('\n');
            foreach (var r in rows)
                sb.Append(r.Kind).Append('|').Append(r.Id).Append('|').Append(r.Name).Append('|').Append(r.Type).Append('|').Append(r.State).Append('|').Append(r.Dimension)
                    .Append('|').Append(r.ConfidenceText).Append('|').Append(r.DocRef).Append('|').Append(r.Updated.HasValue).Append('|').Append(r.Text.Replace("\n", "\\n"))
                    .Append('|').Append(string.Join(",", r.Props.Where(p => !p.Key.Contains("at", StringComparison.OrdinalIgnoreCase) && !p.Key.Contains("time", StringComparison.OrdinalIgnoreCase)
                        && !p.Key.Contains("seen", StringComparison.OrdinalIgnoreCase)).Select(p => p.Key + "=" + p.Value))).Append('\n');
        }
        return sb.ToString();
    }

    private string DefaultsDump()
    {
        var repo = Path.Combine(ExplorerPaths.FindRoot(AppContext.BaseDirectory), "shards", "defaults");
        var dir = Path.Combine(_fx.Dir, "defaults-copy");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(repo)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        var sb = new StringBuilder();
        void List(string name, string d)
        {
            sb.Append("== ").Append(name).Append('\n');
            foreach (var p in ShardService.ListDefaults(d))
                sb.Append(StateDump.Of(p, 2).Replace(d, "<dir>")).Append('\n');
        }
        List("repo copy", dir);
        // Broken copies: a missing pack, a pack that no longer matches index.json, garbage under an indexed name, and two unindexed files.
        var packs = Directory.GetFiles(dir, "*.mcshard").Order(StringComparer.Ordinal).ToList();
        File.Delete(packs[0]);
        File.Copy(packs[1], Path.Combine(dir, "extra-copy.mcshard"));
        File.WriteAllBytes(Path.Combine(dir, "junk.mcshard"), [1, 2, 3]);
        File.Copy(packs[2], packs[1], overwrite: true);
        if (packs.Count > 3) File.WriteAllBytes(packs[3], [9, 9, 9, 9]);
        List("broken copy", dir);
        List("nowhere", Path.Combine(_fx.Dir, "nowhere"));
        return sb.ToString();
    }

    [Fact]
    public void Services_match_the_golden_hashes()
    {
        var rows = new List<(string, string)>
        {
            ("prefill", PrefillDump()),
            ("script-diff", DiffDump()),
            ("shard-kind", KindDump()),
            ("browser-rows", RowsDump()),
            ("default-packs", DefaultsDump()),
        };
        Compare(rows, Golden);
    }

    private const string Golden = """
        prefill 28D0C044E8A0469490BC
        script-diff D032C91D0F2672829B3E
        shard-kind F60959F5D117D51850AF
        browser-rows 1539BBBE3CE537C7B7C5
        default-packs B7E6D1391956335D0B55
        """;
}
