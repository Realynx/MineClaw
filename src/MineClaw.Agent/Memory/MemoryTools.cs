using System.Globalization;
using System.Text.RegularExpressions;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Beliefs;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Memory;

/// <summary>Model-facing memory tools over <see cref="MemoryStore"/>. All results are short.</summary>
public static class MemoryTools
{
    /// <param name="position">Current block position (used when coordinates are omitted); provided by the client layer.</param>
    public static void RegisterAll(ToolRegistry registry, MemoryStore memory, Func<BlockPos?>? position = null)
    {
        registry.Register("remember_waypoint", "Save a named location (defaults to where you stand).",
            ToolSchema.Create().Str("name").Int("x", required: false).Int("y", required: false).Int("z", required: false)
                .Str("note", required: false).Build(),
            (input, _) =>
            {
                var name = input.ReqStr("name");
                var (x, y, z) = Coords(input, "x", "y", "z", position);
                memory.SetWaypoint(name, x, y, z, input.Str("note"));
                return Task.FromResult(ToolResult.Success($"waypoint {name} = {x},{y},{z}"));
            }, cheapTier: true);

        registry.Register("mark_structure",
            "Record a structure: kind (cobble_generator|chest_storage|furnace_bank|farm|tree_farm|unknown|...), corner(s), key blocks.",
            ToolSchema.Create().Str("name").Str("kind")
                .Int("x1", required: false).Int("y1", required: false).Int("z1", required: false)
                .Int("x2", required: false).Int("y2", required: false).Int("z2", required: false)
                .StrArray("keys", "e.g. \"mine_here@4,65,-2\"", required: false)
                .Str("note", required: false).Str("taught_by", required: false).Num("confidence", "0..1", required: false).Build(),
            (input, _) =>
            {
                var name = input.ReqStr("name");
                var hasMin = input.Int("x1") != null;
                BlockPos? min = null, max = null;
                if (hasMin)
                {
                    var (x, y, z) = Coords(input, "x1", "y1", "z1", position);
                    min = new BlockPos(x, y, z);
                }
                else if (position?.Invoke() is { } here) min = here;
                if (input.Int("x2") is { } x2 && min != null)
                {
                    var p2 = new BlockPos(x2, input.Int("y2") ?? min.Y, input.Int("z2") ?? min.Z);
                    max = new BlockPos(Math.Max(min.X, p2.X), Math.Max(min.Y, p2.Y), Math.Max(min.Z, p2.Z));
                    min = new BlockPos(Math.Min(min.X, p2.X), Math.Min(min.Y, p2.Y), Math.Min(min.Z, p2.Z));
                }
                var keys = input.StrList("keys").Select(ParseKey).Where(k => k != null).Cast<KeyBlock>().ToList();
                var s = memory.UpsertStructure(name, s =>
                {
                    s.Kind = input.Str("kind") ?? s.Kind;
                    // Coordinates given here are in the dimension the bot stands in.
                    if (min != null || keys.Count > 0) s.Dimension = memory.CurrentDimension;
                    if (min != null) { s.Min = min; s.Max = max ?? s.Max; }
                    foreach (var k in keys)
                    {
                        s.KeyBlocks.RemoveAll(o => o.Label.Equals(k.Label, StringComparison.OrdinalIgnoreCase));
                        s.KeyBlocks.Add(k);
                    }
                    if (input.Str("note") is { } n) s.Note = n;
                    if (input.Str("taught_by") is { } t) s.TaughtBy = t;
                    if (input.Num("confidence") is { } c) s.Confidence = Math.Clamp(c, 0, 1);
                });
                return Task.FromResult(ToolResult.Success("saved " + MemoryStore.FormatStructure(s)));
            });

        registry.Register("describe_structure", "Add how-to-use steps to a known structure (short imperative steps).",
            ToolSchema.Create().Str("name").StrArray("steps").Str("taught_by", required: false)
                .Bool("replace", "replace existing steps", required: false).Num("confidence", "0..1", required: false).Build(),
            (input, _) =>
            {
                var name = input.ReqStr("name");
                var steps = input.StrList("steps");
                if (steps.Count == 0) return Task.FromResult(ToolResult.Error("no steps"));
                var s = memory.UpsertStructure(name, s =>
                {
                    if (input.Bool("replace") == true) s.Steps.Clear();
                    s.Steps.AddRange(steps.Select(x => TextUtil.Truncate(TextUtil.OneLine(x), 120)));
                    if (s.Steps.Count > 15) s.Steps.RemoveRange(0, s.Steps.Count - 15);
                    if (input.Str("taught_by") is { } t) s.TaughtBy = t;
                    if (input.Num("confidence") is { } c) s.Confidence = Math.Clamp(c, 0, 1);
                });
                return Task.FromResult(ToolResult.Success($"{s.Name}: {s.Steps.Count} steps"));
            });

        var beliefs = BeliefStore.For(memory);
        // Token budget: no documented "by" param. The claimant is this turn's speaker; an undocumented "by" (worker/tests) or a
        // "seen:" prefix marks the bot's own observation.
        registry.Register("remember_note", "Save a fact. What players say becomes a claim to verify.",
            ToolSchema.Create().Str("subject", "player name, base, world...").Str("text").Build(),
            (input, _) => Task.FromResult(RememberNote(memory, beliefs, input.ReqStr("subject"), input.ReqStr("text"), input.Str("by"))),
            cheapTier: true);

        RegisterRecall(registry, memory, position);

        registry.Register("forget", "Delete a memory by name (note: id like #3).",
            ToolSchema.Create().Str("name").Str("kind", required: false, enumValues: ["waypoint", "structure", "note", "routine"]).Build(),
            (input, _) =>
            {
                int n = memory.Forget(input.Str("kind"), input.ReqStr("name"));
                return Task.FromResult(n > 0 ? ToolResult.Success($"forgot {n}") : ToolResult.Error("nothing matched"));
            });

        registry.Register("list_memory", "List saved names (optionally one kind).",
            ToolSchema.Create().Str("kind", required: false, enumValues: ["waypoint", "structure", "note", "routine"]).Build(),
            (input, _) => Task.FromResult(ToolResult.Success(memory.List(input.Str("kind")))), cheapTier: true);
    }

    private static readonly Regex SeenPrefix = new(@"^\s*(seen|saw|i saw|i see|observed|checked)\s*:\s*", RegexOptions.IgnoreCase);
    private static readonly HashSet<string> OwnWords = new(StringComparer.OrdinalIgnoreCase) { "me", "self", "you", "bot", "i", "myself", "seen", "observed" };
    private static readonly HashSet<string> GenericSubjects = new(StringComparer.OrdinalIgnoreCase)
        { "world", "base", "island", "server", "spawn", "me", "self", "bot", "everyone", "team", "game", "farm", "home", "general", "us" };

    /// <summary>
    /// Belief routing: a fact told by a player (explicit <paramref name="by"/>, else this turn's speaker) becomes a claim with a
    /// status, never a note; the bot's own observations stay notes. Instructions dressed as facts are never saved as either.
    /// </summary>
    internal static ToolResult RememberNote(MemoryStore memory, BeliefStore beliefs, string subject, string text, string? by)
    {
        text = TextUtil.Truncate(TextUtil.OneLine(text), 200);
        var seen = SeenPrefix.Match(text);
        if (seen.Success) by ??= "me"; // the bot's own look, kept verbatim as a note
        var claimant = string.IsNullOrWhiteSpace(by) ? TurnContext.Current?.Speaker : by.Trim().TrimStart('@');
        bool own = claimant == null || OwnWords.Contains(claimant) || beliefs.IsBot?.Invoke(claimant) == true;
        if (!own && beliefs.Options.Enabled)
        {
            var s = subject.Trim();
            var claimText = !GenericSubjects.Contains(s) && !s.Equals(claimant, StringComparison.OrdinalIgnoreCase)
                && !text.Contains(s, StringComparison.OrdinalIgnoreCase) ? $"{s} {text}" : text;
            var r = beliefs.Ingest(claimant!, claimText);
            if (r.Stored || TurnContext.IsTest) return ToolResult.Success(r.Text);
            return ToolResult.Error(r.Text);
        }
        if (ClaimParser.IsDirective(text)) return ToolResult.Error("not saved: that's an instruction, not a fact");
        var n = memory.AddNote(subject, text);
        return ToolResult.Success($"note#{n.Id} saved");
    }

    public static readonly string[] RecallKinds =
        ["waypoint", "structure", "note", "player", "routine", "episode", "knowledge", "world", "chat"];

    /// <summary><c>recall</c> (hybrid search, optional kind/near) and <c>recall_detail</c> (full record by id, paged).</summary>
    public static void RegisterRecall(ToolRegistry registry, MemoryStore memory, Func<BlockPos?>? position)
    {
        var index = memory.Index;
        var o = index.Options;
        registry.Register("recall",
            "Search memory: places, structures, players, notes, routines, past episodes, chat log, Skyblock knowledge, world objects. " +
            "near=\"here\" or x,y,z lists what's known around a spot.",
            ToolSchema.Create().Str("query", required: false).Str("kind", required: false, enumValues: RecallKinds)
                .Str("near", "\"here\" or x,y,z", required: false).Int("radius", "blocks, default 16", required: false)
                .Int("limit", required: false).Str("dim", "overworld|nether|any; default yours", required: false).Build(),
            async (input, ct) =>
            {
                var query = input.Str("query")?.Trim();
                var kind = input.Str("kind")?.Trim().ToLowerInvariant();
                int limit = Math.Clamp(input.Int("limit") ?? o.RecallLimit, 1, 12);
                if (!WorldDim.TryParse(input.Str("dim"), memory.CurrentDimension, out var dim)) return ToolResult.Error("dim: overworld|nether|any");
                (double, double, double)? near = null;
                if (input.Str("near") is { Length: > 0 } ns)
                {
                    var p = ParseNear(ns, position);
                    if (p == null) return ToolResult.Error("near must be \"here\" or x,y,z");
                    near = p;
                }
                if (string.IsNullOrEmpty(query) && near == null) return ToolResult.Error("give a query or near");

                if (kind == "chat")
                {
                    var rows = index.SearchChat(query, null, limit);
                    if (rows.Count == 0) return ToolResult.Success("no chat matches");
                    return ToolResult.Success(Bounded(rows.Select(r => TextUtil.Truncate(r.Format(), 160)), o.RecallMaxChars));
                }

                IReadOnlyCollection<string>? kinds = kind switch
                {
                    null or "" => null,
                    "note" => ["note", "player", BeliefStore.DocKind],
                    _ => [kind],
                };
                var hits = await index.SearchAsync(new RagQuery
                {
                    Text = query, Kinds = kinds, Near = near, Radius = Math.Clamp(input.Int("radius") ?? 16, 1, 128), Limit = limit,
                    Dimension = dim,
                }, ct).ConfigureAwait(false);
                return ToolResult.Success(MemoryStore.FormatHits(index, hits, o.RecallMaxChars, 200));
            }, cheapTier: true);

        registry.Register("recall_detail", "Full text of a recall/[memory] entry by id (#12, or c34 for chat). Long ones are paged.",
            ToolSchema.Create().Str("id").Int("page", required: false).Build(),
            (input, _) =>
            {
                var raw = input.ReqStr("id").Trim().TrimStart('#');
                int page = Math.Max(1, input.Int("page") ?? 1);
                string? text = null;
                if (raw.StartsWith('c') && long.TryParse(raw[1..], out var cid))
                {
                    var rows = index.ChatAround(cid);
                    if (rows.Count > 0) text = string.Join("\n", rows.Select(r => r.Format()));
                }
                else if (long.TryParse(raw, out var id) && index.Get(id) is { } h)
                {
                    text = h.Kind switch
                    {
                        "knowledge" => $"{h.Title}\n{h.Body}",
                        "episode" => h.Body,
                        _ => h.Body,
                    };
                    if (h.Bounds is { } b && h.Kind is "world" or "episode") text += $" @{b.Format()}";
                    text = $"#{h.Id} [{h.Tag ?? h.Kind}] " + text;
                    if (index.Corrections?.Superseding(h) is { } fix) text = $"[{fix.Correction}] " + text; // stale lesson, corrected
                }
                if (text == null) return Task.FromResult(ToolResult.Error("no such id"));
                int size = Math.Max(100, o.RecallMaxChars - 20);
                int pages = (text.Length + size - 1) / size;
                if (page > pages) return Task.FromResult(ToolResult.Error($"only {pages} page(s)"));
                var slice = text.Substring((page - 1) * size, Math.Min(size, text.Length - (page - 1) * size));
                return Task.FromResult(ToolResult.Success(pages > 1 ? $"{slice} (page {page}/{pages})" : slice));
            }, cheapTier: true);
    }

    internal static (double, double, double)? ParseNear(string s, Func<BlockPos?>? position)
    {
        s = s.Trim();
        if (s.Equals("here", StringComparison.OrdinalIgnoreCase) || s.Equals("me", StringComparison.OrdinalIgnoreCase))
            return position?.Invoke() is { } p ? (p.X, p.Y, p.Z) : null;
        var parts = s.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && parts.All(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            return (double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture), double.Parse(parts[2], CultureInfo.InvariantCulture));
        if (parts.Length == 2 && parts.All(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            return (double.Parse(parts[0], CultureInfo.InvariantCulture), position?.Invoke()?.Y ?? 64, double.Parse(parts[1], CultureInfo.InvariantCulture));
        return null;
    }

    internal static string Bounded(IEnumerable<string> lines, int maxChars)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var l in lines)
        {
            if (sb.Length + l.Length + 1 > maxChars) break;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(l);
        }
        return sb.ToString();
    }

    private static (int, int, int) Coords(System.Text.Json.JsonElement input, string xs, string ys, string zs, Func<BlockPos?>? position)
    {
        var x = input.Int(xs);
        var y = input.Int(ys);
        var z = input.Int(zs);
        if (x != null && y != null && z != null) return (x.Value, y.Value, z.Value);
        var here = position?.Invoke() ?? throw new ToolInputException("coordinates required");
        return (x ?? here.X, y ?? here.Y, z ?? here.Z);
    }

    private static KeyBlock? ParseKey(string s)
    {
        var m = Regex.Match(s, @"^\s*(?<label>[^@]+?)\s*@\s*(?<x>-?\d+)\s*[, ]\s*(?<y>-?\d+)\s*[, ]\s*(?<z>-?\d+)\s*$");
        if (!m.Success) return null;
        return new KeyBlock
        {
            Label = m.Groups["label"].Value,
            X = int.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture),
            Y = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
            Z = int.Parse(m.Groups["z"].Value, CultureInfo.InvariantCulture),
        };
    }
}
