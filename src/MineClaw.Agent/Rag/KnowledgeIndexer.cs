using System.Text;
using System.Text.RegularExpressions;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Rag;

public sealed record KnowledgeChunk(string File, int Index, string Title, string Text);

public sealed record KnowledgeLoad(string Core, int Files, int Chunks, int Reindexed, bool CoreTruncated);

/// <summary>
/// Splits knowledge markdown into retrieval chunks and extracts the "core rules" kept in the cached prompt.
/// <para><b>Core convention:</b> text between <c>&lt;!-- core --&gt;</c> and <c>&lt;!-- /core --&gt;</c> (or end of file)
/// goes into the system prompt verbatim; a file without the marker contributes its first section (text before
/// its second heading). Core text is not indexed (it is always in context). Everything else is chunked by
/// heading into ~150–300-token pieces: big sections split at paragraph/bullet boundaries, small sibling
/// sections merged. Other HTML comments are dropped.</para>
/// </summary>
public static partial class KnowledgeChunker
{
    [GeneratedRegex(@"<!--\s*core\s*-->(?<body>.*?)(?:<!--\s*/core\s*-->|\z)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CoreBlock();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"^(?<hashes>#{1,6})\s+(?<title>.+?)\s*#*\s*$")]
    private static partial Regex Heading();

    private sealed class Section
    {
        public List<string> Path = new();
        public StringBuilder Body = new();
        public string Title => string.Join(" > ", Path);
    }

    public static (string Core, List<KnowledgeChunk> Chunks) Parse(string fileName, string markdown, int minTokens = 150, int maxTokens = 300)
    {
        var text = markdown.Replace("\r\n", "\n");
        var core = new StringBuilder();
        bool hasMarker = false;
        text = CoreBlock().Replace(text, m =>
        {
            hasMarker = true;
            var body = Comment().Replace(m.Groups["body"].Value, "").Trim();
            if (body.Length > 0) core.Append(core.Length > 0 ? "\n\n" : "").Append(body);
            return "\n";
        });
        text = Comment().Replace(text, "");

        var sections = SplitSections(text);
        if (!hasMarker && sections.Count > 0)
        {
            // No marker: the first section with content is core.
            int first = sections.FindIndex(s => s.Body.ToString().Trim().Length > 0);
            if (first >= 0)
            {
                var s = sections[first];
                core.Append(s.Path.Count > 0 ? "## " + s.Path[^1] + "\n" : "").Append(s.Body.ToString().Trim());
                sections.RemoveAt(first);
            }
        }

        int maxChars = (int)(maxTokens * 3.5), minChars = (int)(minTokens * 3.5);
        var pieces = new List<(string Title, string Text)>();
        foreach (var s in sections)
        {
            var body = s.Body.ToString().Trim();
            if (body.Length == 0) continue;
            if (body.Length <= maxChars) { pieces.Add((s.Title, body)); continue; }
            int part = 1;
            foreach (var p in SplitBody(body, maxChars)) pieces.Add(($"{s.Title} ({part++})", p));
        }

        // Merge small consecutive pieces while they fit.
        var merged = new List<(string Title, string Text)>();
        foreach (var p in pieces)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if ((last.Text.Length < minChars || p.Text.Length < minChars) && last.Text.Length + p.Text.Length + 2 <= maxChars)
                {
                    merged[^1] = (last.Title + "; " + LastSegment(p.Title), last.Text + "\n" + p.Text);
                    continue;
                }
            }
            merged.Add(p);
        }

        var chunks = merged.Select((m, i) => new KnowledgeChunk(fileName, i, TextUtil.Truncate(m.Title, 120), m.Text)).ToList();
        return (core.ToString().Trim(), chunks);
    }

    private static string LastSegment(string title)
    {
        int i = title.LastIndexOf(" > ", StringComparison.Ordinal);
        return i >= 0 ? title[(i + 3)..] : title;
    }

    private static List<Section> SplitSections(string text)
    {
        var list = new List<Section>();
        var stack = new List<(int Level, string Title)>();
        var cur = new Section();
        list.Add(cur);
        foreach (var line in text.Split('\n'))
        {
            var m = Heading().Match(line);
            if (m.Success)
            {
                int level = m.Groups["hashes"].Value.Length;
                while (stack.Count > 0 && stack[^1].Level >= level) stack.RemoveAt(stack.Count - 1);
                stack.Add((level, m.Groups["title"].Value.Trim()));
                cur = new Section { Path = stack.Select(s => s.Title).ToList() };
                list.Add(cur);
                continue;
            }
            cur.Body.Append(line).Append('\n');
        }
        return list;
    }

    /// <summary>Splits at blank lines / bullets, packing blocks up to <paramref name="maxChars"/> (hard-splits huge blocks at sentences).</summary>
    private static IEnumerable<string> SplitBody(string body, int maxChars)
    {
        var blocks = new List<string>();
        var current = new StringBuilder();
        foreach (var line in body.Split('\n'))
        {
            bool boundary = line.Trim().Length == 0 || line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* ") || Regex.IsMatch(line, @"^\s*\d+[.)]\s");
            if (boundary && current.Length > 0) { blocks.Add(current.ToString().Trim()); current.Clear(); }
            if (line.Trim().Length > 0) current.Append(line).Append('\n');
        }
        if (current.Length > 0) blocks.Add(current.ToString().Trim());

        var expanded = new List<string>();
        foreach (var b in blocks)
        {
            if (b.Length <= maxChars) { expanded.Add(b); continue; }
            var sb = new StringBuilder();
            foreach (var sentence in Regex.Split(b, @"(?<=[.!?])\s+"))
            {
                if (sb.Length > 0 && sb.Length + sentence.Length + 1 > maxChars) { expanded.Add(sb.ToString().Trim()); sb.Clear(); }
                if (sentence.Length > maxChars)
                {
                    for (int i = 0; i < sentence.Length; i += maxChars) expanded.Add(sentence.Substring(i, Math.Min(maxChars, sentence.Length - i)));
                    continue;
                }
                sb.Append(sentence).Append(' ');
            }
            if (sb.Length > 0) expanded.Add(sb.ToString().Trim());
        }

        var pack = new StringBuilder();
        foreach (var b in expanded)
        {
            if (pack.Length > 0 && pack.Length + b.Length + 1 > maxChars) { yield return pack.ToString().Trim(); pack.Clear(); }
            pack.Append(b).Append('\n');
        }
        if (pack.Length > 0) yield return pack.ToString().Trim();
    }
}

/// <summary>Keeps the knowledge chunks in <see cref="RagIndex"/> in sync with <c>docs/knowledge/*.md</c> (re-index on file hash change).</summary>
public static class KnowledgeIndexer
{
    public const string Kind = "knowledge";

    public static KnowledgeLoad Sync(RagIndex index, string? directory, RagOptions options)
    {
        var db = index.Db;
        var core = new StringBuilder();
        int files = 0, chunks = 0, reindexed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            foreach (var file in Directory.GetFiles(directory, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                var text = File.ReadAllText(file);
                seen.Add(name);
                files++;
                var (fileCore, fileChunks) = KnowledgeChunker.Parse(name, text, options.ChunkMinTokens, options.ChunkMaxTokens);
                if (fileCore.Length > 0) core.Append(core.Length > 0 ? "\n\n" : "").Append(fileCore);
                chunks += fileChunks.Count;

                var hash = VectorMath.Hash(text.Replace("\r\n", "\n") + $"|{options.ChunkMinTokens}|{options.ChunkMaxTokens}");
                var known = db.Scalar("SELECT hash FROM knowledge_files WHERE path = $p", ("$p", name)) as string;
                if (known == hash) continue;

                var keep = new HashSet<string>(fileChunks.Select(c => Ref(c)));
                db.Tx(() =>
                {
                    index.DeleteByPrefix($"kb:{name}:", keep);
                    index.UpsertMany(fileChunks.Select(c => new RagDoc(Ref(c), Kind, c.Title, c.Text) { Source = name, Weight = 1.0 }));
                    db.Exec("INSERT INTO knowledge_files(path, hash, indexed) VALUES ($p, $h, $t) ON CONFLICT(path) DO UPDATE SET hash = excluded.hash, indexed = excluded.indexed",
                        ("$p", name), ("$h", hash), ("$t", AgentDb.ToUnixMs(DateTimeOffset.UtcNow)));
                });
                reindexed++;
            }
        }

        // Files that disappeared.
        foreach (var gone in db.Query("SELECT path FROM knowledge_files", r => r.GetString(0)).Where(p => !seen.Contains(p)).ToList())
        {
            index.DeleteByPrefix($"kb:{gone}:");
            db.Exec("DELETE FROM knowledge_files WHERE path = $p", ("$p", gone));
        }

        var coreText = core.ToString();
        bool truncated = coreText.Length > options.MaxCoreChars;
        if (truncated) coreText = coreText[..options.MaxCoreChars];
        return new KnowledgeLoad(coreText, files, chunks, reindexed, truncated);
    }

    private static string Ref(KnowledgeChunk c) => $"kb:{c.File}:{c.Index}";
}
