using System.Text;
using System.Text.RegularExpressions;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Rag;

public sealed record KnowledgeChunk(string File, int Index, string Title, string Text);

public sealed record KnowledgeLoad(string Core, int Files, int Chunks, int Reindexed, bool CoreTruncated)
{
    /// <summary>Docs on disk left out because their pack marker isn't among the db's active packs (e.g. skyblock in a plain world).</summary>
    public IReadOnlyList<string> Excluded { get; init; } = [];

    /// <summary>Set when the bot's game version isn't the db's knowledge pack version (e.g. a 1.12.2 bot on a b1.8.1 db): only docs of
    /// the game version were read, and the db's pack needs a swap (or a fresh-world reset).</summary>
    public string? VersionMismatch { get; init; }
}

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
        // No marker: the first section with content is core.
        if (!hasMarker && sections.Count > 0) TakeFirstAsCore(sections, core);

        int maxChars = (int)(maxTokens * 3.5), minChars = (int)(minTokens * 3.5);
        var merged = MergeSmall(Pieces(sections, maxChars), minChars, maxChars);
        var chunks = merged.Select((m, i) => new KnowledgeChunk(fileName, i, TextUtil.Truncate(m.Title, 120), m.Text)).ToList();
        return (core.ToString().Trim(), chunks);
    }

    private static void TakeFirstAsCore(List<Section> sections, StringBuilder core)
    {
        int first = sections.FindIndex(s => s.Body.ToString().Trim().Length > 0);
        if (first < 0) return;
        var s = sections[first];
        core.Append(s.Path.Count > 0 ? "## " + s.Path[^1] + "\n" : "").Append(s.Body.ToString().Trim());
        sections.RemoveAt(first);
    }

    /// <summary>Each section's text, a long one split into numbered parts.</summary>
    private static List<(string Title, string Text)> Pieces(List<Section> sections, int maxChars)
    {
        var pieces = new List<(string Title, string Text)>();
        foreach (var s in sections)
        {
            var body = s.Body.ToString().Trim();
            if (body.Length == 0) continue;
            if (body.Length <= maxChars) { pieces.Add((s.Title, body)); continue; }
            int part = 1;
            foreach (var p in SplitBody(body, maxChars)) pieces.Add(($"{s.Title} ({part++})", p));
        }
        return pieces;
    }

    /// <summary>Merges small consecutive pieces while they fit.</summary>
    private static List<(string Title, string Text)> MergeSmall(List<(string Title, string Text)> pieces, int minChars, int maxChars)
    {
        var merged = new List<(string Title, string Text)>();
        foreach (var p in pieces)
        {
            if (merged.Count > 0 && Mergeable(merged[^1], p, minChars, maxChars))
            {
                var last = merged[^1];
                merged[^1] = (last.Title + "; " + LastSegment(p.Title), last.Text + "\n" + p.Text);
                continue;
            }
            merged.Add(p);
        }
        return merged;
    }

    private static bool Mergeable((string Title, string Text) last, (string Title, string Text) p, int minChars, int maxChars) =>
        (last.Text.Length < minChars || p.Text.Length < minChars) && last.Text.Length + p.Text.Length + 2 <= maxChars;

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
        var expanded = new List<string>();
        foreach (var b in BodyBlocks(body)) AddBlock(expanded, b, maxChars);
        return PackBlocks(expanded, maxChars);
    }

    /// <summary>The body's blocks, split at blank lines, bullets and numbered items.</summary>
    private static List<string> BodyBlocks(string body)
    {
        var blocks = new List<string>();
        var current = new StringBuilder();
        foreach (var line in body.Split('\n'))
        {
            if (IsBlockBoundary(line) && current.Length > 0) { blocks.Add(current.ToString().Trim()); current.Clear(); }
            if (line.Trim().Length > 0) current.Append(line).Append('\n');
        }
        if (current.Length > 0) blocks.Add(current.ToString().Trim());
        return blocks;
    }

    private static bool IsBlockBoundary(string line) =>
        line.Trim().Length == 0 || line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* ") || Regex.IsMatch(line, @"^\s*\d+[.)]\s");

    /// <summary>A block that fits as it is, else packed by sentences (a sentence longer than the limit hard-split).</summary>
    private static void AddBlock(List<string> expanded, string b, int maxChars)
    {
        if (b.Length <= maxChars) { expanded.Add(b); return; }
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

    private static IEnumerable<string> PackBlocks(List<string> expanded, int maxChars)
    {
        var pack = new StringBuilder();
        foreach (var b in expanded)
        {
            if (pack.Length > 0 && pack.Length + b.Length + 1 > maxChars) { yield return pack.ToString().Trim(); pack.Clear(); }
            pack.Append(b).Append('\n');
        }
        if (pack.Length > 0) yield return pack.ToString().Trim();
    }
}

/// <summary>
/// Keeps the knowledge chunks in <see cref="RagIndex"/> in sync with <c>docs/knowledge/*.md</c> (re-index on file hash change).
/// A doc with a pack marker (<see cref="KnowledgeDocPack"/>) is only in when its pack is active in the db (meta
/// <c>knowledge_pack</c>: the version pack + mode packs): a skyblock doc in a plain world adds no core lines and no chunks (and
/// its chunks from before are dropped). Active packs whose files aren't on disk contribute the core stored at import.
/// </summary>
public static class KnowledgeIndexer
{
    public const string Kind = "knowledge";

    /// <summary>The chunk docs of one knowledge file, exactly as <see cref="Sync"/> indexes them (pack builders use this too).</summary>
    public static (string Core, List<RagDoc> Docs) Docs(string fileName, string markdown, RagOptions options, DateTimeOffset? at = null)
    {
        var (core, chunks) = KnowledgeChunker.Parse(fileName, markdown, options.ChunkMinTokens, options.ChunkMaxTokens);
        return (core, chunks.Select(c => new RagDoc(Ref(c), Kind, c.Title, c.Text) { Source = fileName, Weight = 1.0, At = at }).ToList());
    }

    /// <param name="gameVersion">The version the bot plays (null = trust the db's pack). When it differs from the db's knowledge pack, the
    /// docs of the game version are read (no mode docs) and nothing of the db's pack: a 1.12.2 bot never reads b1.8.1/skyblock docs.</param>
    public static KnowledgeLoad Sync(RagIndex index, string? directory, RagOptions options, string? gameVersion = null)
    {
        var db = index.Db;
        var core = new StringBuilder();
        int files = 0, chunks = 0, reindexed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excluded = new List<string>();
        var active = Admin.Shards.KnowledgePack.Active(db);
        string? mismatch = null;
        if (gameVersion is { Length: > 0 } gv && !gv.Equals(active.Version, StringComparison.OrdinalIgnoreCase))
        {
            mismatch = $"the db's knowledge pack is {active.Label} but this bot plays {gv}: only {gv} docs are read; " +
                       $"swap in the {gv} pack (or reset memory for a fresh world)";
            active = new Admin.Shards.KnowledgePackInfo(gv) { Modes = [] };
        }

        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            foreach (var file in Directory.GetFiles(directory, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                var text = File.ReadAllText(file);
                if (Admin.Shards.KnowledgeDocPack.Parse(text) is { } pack && !pack.ActiveIn(active))
                {
                    // Not in this world's packs: no core, no chunks; drop what an earlier start indexed.
                    excluded.Add(name);
                    index.DeleteByPrefix($"kb:{name}:");
                    db.Exec("DELETE FROM knowledge_files WHERE path = $p", ("$p", name));
                    continue;
                }
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

        foreach (var stored in Admin.Shards.KnowledgePack.StoredCores(active, seen))
            core.Append(core.Length > 0 ? "\n\n" : "").Append(stored);

        var coreText = core.ToString();
        bool truncated = coreText.Length > options.MaxCoreChars;
        if (truncated) coreText = coreText[..options.MaxCoreChars];
        return new KnowledgeLoad(coreText, files, chunks, reindexed, truncated) { Excluded = excluded, VersionMismatch = mismatch };
    }

    private static string Ref(KnowledgeChunk c) => $"kb:{c.File}:{c.Index}";
}
