using System.Text.RegularExpressions;
using MineClaw.Data;
using MineClaw.Skills;
using MineClaw.World;

namespace MineClaw.Bot;

/// <summary>What a chat line says about protection (pure parse, see <see cref="ProtectionCommands.Parse"/>).</summary>
public enum ProtectionIntentKind
{
    /// <summary>"you can break it", "it's ok to mine that", "go ahead and break the house": permit + unmark never-break.</summary>
    Allow,
    /// <summary>"break that", "mine it down", "remove those": permit only (never-break marks still hold).</summary>
    Command,
    /// <summary>"never break the gen", "don't mine the middle one", "leave that alone": a hard never-break mark.</summary>
    Never,
    /// <summary>"yes" / "confirm" after a large-area confirmation question.</summary>
    Confirm,
}

/// <param name="Target">The named thing, or null for a deictic "it/that/this" (a punch, or the last refused block).</param>
public sealed record ProtectionIntent(ProtectionIntentKind Kind, string? Target, BlockPos? Coords = null);

/// <summary>
/// Protection rules 3-4 from chat (zero tokens, C# only): a trusted player's explicit "break that" (with a punch), "you can
/// break it" (after a refusal) or "you can break the main house" grants a permit (<see cref="ProtectionMarks"/>) that soft
/// protection yields to; more than <see cref="ProtectionOptions.ConfirmAboveBlocks"/> blocks asks one confirmation line
/// first. Any player's "never break X" marks X hard; a trusted "you can break X" removes the mark. Untrusted players'
/// commands never unlock anything. Hard safety (void support, lava, other players' footing) is in the client and never yields.
/// </summary>
public sealed partial class ProtectionCommands
{
    private readonly ProtectionMarks _marks;
    private readonly Protection _policy;
    private readonly WorldState _world;
    private readonly ProtectionOptions _o;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (ProtectionMark Draft, bool Unmark, DateTimeOffset Until, int Count)> _pending = new(StringComparer.OrdinalIgnoreCase);

    public ProtectionCommands(ProtectionMarks marks, Protection policy, WorldState world, ProtectionOptions? options = null, TimeProvider? time = null)
    {
        _marks = marks;
        _policy = policy;
        _world = world;
        _o = options ?? new ProtectionOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Trusted players (poofimafox, kiwi): only they unlock protected blocks.</summary>
    public Func<IEnumerable<string>> Trusted { get; set; } = () => [];
    /// <summary>The block a player punch-pointed at just now and what it was then (a plant broken by the punch is air now), or null.</summary>
    public Func<string, (BlockPos Pos, int Id)?>? PointedBy { get; set; }
    /// <summary>A remembered structure/place by name → its box, or null.</summary>
    public Func<string, (BlockPos Min, BlockPos Max)?>? NamedArea { get; set; }
    /// <summary>Generator dig cells near the bot ("the middle one").</summary>
    public Func<IReadOnlyList<BlockPos>>? GenCells { get; set; }

    /// <summary>A trusted player allowed mining a generator cell (cell, player, said "the middle one"): memory notes that
    /// forbade it are cleared (<see cref="Perception.DigCellCorrections.UnmarkGenCell"/>).</summary>
    public Action<BlockPos, string, bool>? GenCellAllowed { get; set; }
    /// <summary>One chat line from the bot (the large-area confirmation).</summary>
    public Action<string>? Say { get; set; }
    /// <summary>A quiet note for the brain ("[permit] kiwi allowed ...").</summary>
    public Action<string>? Notice { get; set; }

    private int Dim => _world.Dimension;

    // ------------------------------------------------------------------ parse

    private const string Verb = @"(?:break|mine|dig|remove|destroy|take\s+down|clear|get\s+rid\s+of|chop)";

    [GeneratedRegex(@"^\s*(?:[\w_]+[,:]?\s+)?(?:yes|yeah|yep|yup|ya|confirm(?:ed)?|do\s+it|go\s+ahead|sure|ok(?:ay)?)\s*[.!]*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ConfirmRx();

    [GeneratedRegex(@"\b(?:never|don'?t|dont|do\s+not|you\s+can'?t|you\s+cannot|you\s+can\s+not|stop)\s+(?:ever\s+)?" + Verb + @"\b(?<t>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NeverRx();

    [GeneratedRegex(@"\bleave\s+(?<t>.+?)\s+alone\b", RegexOptions.IgnoreCase)]
    private static partial Regex LeaveAloneRx();

    [GeneratedRegex(@"\b(?:you\s+(?:can|may|could)(?!'?t\b|\s*not\b)|you'?re\s+allowed\s+to|feel\s+free\s+to|go\s+ahead\s+and|it'?s\s+(?:ok|okay|fine|alright)\s+to|its\s+(?:ok|okay|fine)\s+to)\s+" + Verb + @"\b(?<t>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex AllowRx();

    [GeneratedRegex(@"^\s*(?:(?:hey\s+)?[\w_]+[,:]?\s+)?(?:hey\s+)?(?:please\s+|pls\s+|can\s+you\s+|could\s+you\s+|now\s+|just\s+)*" + Verb + @"\b(?<t>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex CommandRx();

    [GeneratedRegex(@"(-?\d+)\s*[, ]\s*(-?\d+)\s*[, ]\s*(-?\d+)")]
    private static partial Regex CoordsRx();

    private static readonly HashSet<string> Deictic = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "it", "that", "this", "those", "these", "them", "there", "one", "that one", "this one", "the one", "that block", "this block",
        "those blocks", "these blocks", "the block", "the blocks", "that thing", "this thing", "those too", "it too", "that too",
        // The bot's own scaffolding (rule 1 already allows it; when the log predates tracking, this means "the one I refused").
        "pillar", "pillars", "the pillar", "your pillar", "your pillars", "scaffolding", "your scaffolding", "the scaffolding", "bridge",
        "bridges", "your bridge", "your bridges", "your blocks", "your stuff", "the logs", "logs", "the log", "log",
    };

    /// <summary>Parses one chat line (null = says nothing about protection).</summary>
    public static ProtectionIntent? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (ConfirmRx().IsMatch(text)) return new(ProtectionIntentKind.Confirm, null);
        if (NeverRx().Match(text) is { Success: true } n) return Make(ProtectionIntentKind.Never, n.Groups["t"].Value);
        if (LeaveAloneRx().Match(text) is { Success: true } l) return Make(ProtectionIntentKind.Never, l.Groups["t"].Value);
        if (AllowRx().Match(text) is { Success: true } a) return Make(ProtectionIntentKind.Allow, a.Groups["t"].Value);
        if (CommandRx().Match(text) is { Success: true } c) return Make(ProtectionIntentKind.Command, c.Groups["t"].Value);
        return null;
    }

    private static ProtectionIntent Make(ProtectionIntentKind kind, string raw)
    {
        BlockPos? coords = CoordsRx().Match(raw) is { Success: true } m
            ? new BlockPos(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
        var t = Regex.Replace(raw.ToLowerInvariant(), @"[!?.~,;:]+", " ");
        t = Regex.Replace(t, @"\b(?:down|up|on|off|out|for\s+me|please|pls|now|anyway|anyways|too|again|safely|fr|uwu|lol|all|at|from\s+the\s+top)\b", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        if (coords != null) t = "";
        return new(kind, Deictic.Contains(t) ? null : t, coords);
    }

    // ------------------------------------------------------------------ handle

    private bool IsTrusted(string player) => Trusted().Any(t => t.Equals(player, StringComparison.OrdinalIgnoreCase));

    /// <summary>Handles one addressed chat line. Returns what it did (null = nothing about protection).</summary>
    public string? OnChat(string? player, string text)
    {
        if (string.IsNullOrWhiteSpace(player) || Parse(text) is not { } intent) return null;
        var now = _time.GetUtcNow();
        if (intent.Kind == ProtectionIntentKind.Confirm)
        {
            lock (_pending)
            {
                if (!_pending.Remove(player, out var p) || p.Until < now) return null;
                return Grant(p.Draft, p.Unmark, p.Count);
            }
        }
        if (intent.Kind == ProtectionIntentKind.Never)
        {
            if (Resolve(player, intent) is not { } nt || nt.Boxes.Count == 0) return null;
            // Live 10-27: a vague "don't mine the middle one" took one of the gen's three regen cells away for good. A gen cell
            // takes a never-break mark only when the player names that exact cell (coordinates or a punch).
            if (nt.GenCell && intent.Coords is null)
            {
                var no = $"[protect] not marking {nt.Label} never-break from '{intent.Target}': generator cells are what I mine; name the exact cell (x,y,z) to keep me off one";
                Notice?.Invoke(no);
                return "ignored: " + no;
            }
            var mark = _marks.Add(MarkKind.Never, Dim, nt.Boxes, nt.Label, player, null, nt.Exact);
            var line = $"[protect] {player} marked {nt.Label} never-break (hard: no command overrides it; a trusted 'you can break it' unmarks it)";
            Notice?.Invoke(line);
            return line;
        }
        if (!IsTrusted(player))
        {
            var line = $"[protect] {player} asked to break protected blocks, but only {string.Join("/", Trusted())} can unlock them";
            Notice?.Invoke(line);
            return "ignored: " + line;
        }
        if (Resolve(player, intent) is not { } t)
        {
            if (intent.Target is not null)
            {
                var note = $"[protect] couldn't resolve '{intent.Target}': name a block kind near me, a place, or x,y,z";
                Notice?.Invoke(note);
                return "ask: " + note;
            }
            // A stale refusal may refer to a block that is already gone. Ask for a current target.
            return AskWhich(player);
        }
        if (t.Already is { } already)
        {
            Say?.Invoke(already);
            return "nothing to unlock: " + already;
        }
        if (intent.Kind == ProtectionIntentKind.Allow && GenCellAllowed is { } allowed)
        {
            var cells = GenCells?.Invoke() ?? [];
            foreach (var (a, b) in t.Boxes)
                if (a == b && (t.GenCell || cells.Contains(a)))
                    try { allowed(a, player, t.GenCell && intent.Coords is null); } catch (Exception) { /* memory busy */ }
        }
        var draft = new ProtectionMark(0, MarkKind.Permit, Dim, t.Boxes, t.Label, player, now, null, t.Exact);
        int count = CountBlocks(t.Boxes);
        if (count == 0) return AskWhich(player);
        bool unmark = intent.Kind == ProtectionIntentKind.Allow;
        if (count > _o.ConfirmAboveBlocks)
        {
            lock (_pending) _pending[player] = (draft, unmark, now + TimeSpan.FromMinutes(2), count);
            var ask = $"that's {count} blocks ({t.Label}): say 'yes' to confirm I may break all of it";
            Say?.Invoke(ask);
            return "confirm: " + ask;
        }
        return Grant(draft, unmark, count);
    }

    private string Grant(ProtectionMark draft, bool unmark, int count)
    {
        var unmarked = unmark ? _marks.Unmark(draft.Dimension, draft.Boxes) : [];
        _marks.Add(MarkKind.Permit, draft.Dimension, draft.Boxes, draft.Label, draft.By, TimeSpan.FromMinutes(Math.Max(1, _o.PermitMinutes)), draft.Exact);
        _policy.Invalidate();
        var line = $"[permit] {draft.By} allowed breaking {draft.Label} ({count} block{(count == 1 ? "" : "s")}, {_o.PermitMinutes} min)"
                   + (unmarked.Count > 0 ? $"; unmarked {unmarked.Count} never-break mark(s)" : "");
        Notice?.Invoke(line);
        return line;
    }

    private string AskWhich(string player)
    {
        var ask = $"{player}, which block? punch it (or give x,y,z) and say it again";
        Say?.Invoke(ask);
        return "ask: " + ask;
    }

    /// <param name="GenCell">Resolved as a generator cell by a vague phrase ("the middle one").</param>
    /// <param name="Already">Nothing there is protected: say this instead of granting a permit.</param>
    private sealed record Target(IReadOnlyList<(BlockPos Min, BlockPos Max)> Boxes, string Label, bool Exact, bool GenCell = false,
        string? Already = null);

    private Target? Resolve(string player, ProtectionIntent intent)
    {
        if (intent.Coords is { } c) return new([Column(c)], $"the {BlockName(c)} at {Locations.Fmt(c)}", true);
        if (intent.Target is null)
        {
            if (PointedBy?.Invoke(player) is { } pointed)
            {
                if (_world.GetBlock(pointed.Pos) != 0)
                    return new([Column(pointed.Pos)], $"the {BlockName(pointed.Pos)} column at {Locations.Fmt(pointed.Pos)} (pointed)", true);
                // The punch broke it (tall grass, a flower): they mean that kind of block, around the spot.
                if (pointed.Id != 0 && SameKindAround(pointed.Pos, pointed.Id) is { } kind) return kind;
            }
            // Only use a refusal whose block is still present.
            if (_policy.LastRefusal is { } r && _time.GetUtcNow() - r.At <= TimeSpan.FromMinutes(Math.Max(1, _o.RefusalContextMinutes))
                && _world.GetBlock(r.Pos) == r.Id)
                return new([Column(r.Pos)], $"the {BlockName(r.Pos)} column at {Locations.Fmt(r.Pos)} (the one I refused)", true);
            return null;
        }
        var name = Regex.Replace(intent.Target, @"^(?:the|my|our|your|that|this|those|these)\s+", "").Trim();
        if (name.Length == 0) return null;
        if (Regex.IsMatch(name, @"^(?:middle|center|centre)(?:\s+(?:one|cell|block))?$") && GenCells?.Invoke() is { Count: > 0 } cells)
        {
            var mid = Middle(cells);
            return new([(mid, mid)], $"the middle generator cell {Locations.Fmt(mid)}", true, GenCell: true);
        }
        if (NamedArea?.Invoke(name) is { } box) return new([box], "the " + name, false);
        // A block type near me ("you can break the planks"): the protected ones within 6 (Allow only: "mine cobble" is a job).
        if (intent.Kind != ProtectionIntentKind.Command && NamedBlocks(name) is { } blocks) return blocks;
        return null;
    }

    private Target? NamedBlocks(string name)
    {
        // Keep the named kind separate from its qualifiers. Previously the entire sentence (including "next to the
        // water holes" / "replant it right after") was looked up as one block name and silently discarded.
        var parts = Regex.Split(name, @"\s+(?:next\s+to|near|around|beside|by|for|then|and|replant)\s+", RegexOptions.IgnoreCase);
        var kind = Regex.Replace(parts[0], @"^(?:(?:it|that|this)\s+)?(?:(?:one|the|a|an|some)\s+)?(?:(?:unripe|ripe|growing)\s+)?", "").Trim();
        if (ResolveBlock(kind) is not { } id) return null;
        var age = Regex.Match(parts[0], @"\b(?:unripe|ripe|growing)\b", RegexOptions.IgnoreCase).Value;
        if (age.Length > 0 && !CropKinds.IsCrop(id)) return null;
        bool water = Regex.IsMatch(name, @"\bwater\s+holes?\b", RegexOptions.IgnoreCase);
        var origin = _world.Player.Position;
        if (!water && parts.Length > 1)
        {
            var landmark = Regex.Replace(parts[1], @"^(?:the|each)\s+", "").Trim();
            if (NamedArea?.Invoke(landmark) is { } area)
                origin = new Vec3d((area.Min.X + area.Max.X + 1) / 2.0, (area.Min.Y + area.Max.Y) / 2.0, (area.Min.Z + area.Max.Z + 1) / 2.0);
        }
        var found = _world.FindBlocks(origin, 6, (b, meta) => b == id
            && (age.Length == 0 || CropKinds.Ripe(b, meta) == age.Equals("ripe", StringComparison.OrdinalIgnoreCase)), 256)
            .Select(f => f.Pos).Where(p => _policy.IsProtected(p, id)).ToList();
        bool one = Regex.IsMatch(parts[0], @"\bone\b", RegexOptions.IgnoreCase);
        var faces = new[] { BlockFace.North, BlockFace.South, BlockFace.East, BlockFace.West };
        var holes = new Dictionary<BlockPos, BlockPos?>();
        BlockPos? WaterHole(BlockPos start)
        {
            if (holes.TryGetValue(start, out var known)) return known;
            // One hole can contain several horizontally connected water cells. Bound the scan and decline large bodies
            // of water rather than treating an arbitrary truncated piece as a separate hole.
            var cells = new HashSet<BlockPos> { start };
            var queue = new Queue<BlockPos>();
            queue.Enqueue(start);
            while (queue.TryDequeue(out var p) && cells.Count <= 256)
                foreach (var face in faces)
                {
                    var q = p.Offset(face);
                    if (BlockShapes.IsWater(_world.GetBlock(q)) && cells.Add(q)) queue.Enqueue(q);
                }
            BlockPos? key = cells.Count > 256 ? null : cells.OrderBy(p => p.X).ThenBy(p => p.Z).First();
            foreach (var p in cells) holes[p] = key;
            return key;
        }
        BlockPos? WaterAt(BlockPos p)
        {
            foreach (var face in faces)
                for (int dy = -1; dy <= 0; dy++)
                {
                    var q = p.Offset(face) with { Y = p.Y + dy };
                    if (BlockShapes.IsWater(_world.GetBlock(q))) return one ? WaterHole(q) : q;
                }
            return null;
        }
        if (water) found = found.Where(p => WaterAt(p) != null).ToList();
        if (one)
            found = water ? found.GroupBy(p => WaterAt(p)).Select(g => g.First()).ToList() : found.Take(1).ToList();
        return found.Count == 0 ? null : new(found.Select(p => (p, p)).ToList(), $"the {Blocks.Get(id).Name.Replace('_', ' ')} near { (water ? "the water holes" : "me") }", false);
    }

    private static int? ResolveBlock(string name)
    {
        var n = name.Replace(' ', '_');
        if (Blocks.ByName(n) is { } b) return b.Id;
        if (n.EndsWith('s') && Blocks.ByName(n[..^1]) is { } b2) return b2.Id;
        return n switch { "wheat" => Blocks.Crops, "cobble" => Blocks.Cobblestone, "wood" or "logs" => Blocks.Log, "plank" or "planks" => Blocks.Planks, _ => null };
    }

    /// <summary>Blocks of <paramref name="id"/> within 6 of <paramref name="at"/>: the protected ones (one box each), or an
    /// "already" line when none of them is protected; null when there are none.</summary>
    private Target? SameKindAround(BlockPos at, int id)
    {
        string name = Blocks.Get(id).Name.Replace('_', ' ');
        var found = _world.FindBlocks(at.Center, 6, (b, _) => b == id, 256).Select(f => f.Pos).ToList();
        if (found.Count == 0) return null;
        var locked = found.Where(p => _policy.IsProtected(p, id)).ToList();
        string label = $"the {name} around {Locations.Fmt(at)} (pointed)";
        return locked.Count > 0
            ? new(locked.Select(p => (p, p)).ToList(), label, false)
            : new([], label, false, Already: $"the {name} around {Locations.Fmt(at)} isn't protected: I can already break it");
    }

    /// <summary>The cell between the others (a dual gen's three cells in a line: the middle one).</summary>
    private static BlockPos Middle(IReadOnlyList<BlockPos> cells)
    {
        double cx = cells.Average(c => c.X), cy = cells.Average(c => c.Y), cz = cells.Average(c => c.Z);
        return cells.OrderBy(c => (c.X - cx) * (c.X - cx) + (c.Y - cy) * (c.Y - cy) + (c.Z - cz) * (c.Z - cz)).First();
    }

    /// <summary>The same-id vertical run through <paramref name="p"/> (a pillar, a column of a wall), bounded to 16 each way.</summary>
    private (BlockPos Min, BlockPos Max) Column(BlockPos p)
    {
        int id = _world.GetBlock(p);
        if (id == 0) return (p, p);
        int lo = p.Y, hi = p.Y;
        while (lo > 0 && p.Y - lo < 16 && _world.GetBlock(p.X, lo - 1, p.Z) == id) lo--;
        while (hi < _world.WorldHeight - 1 && hi - p.Y < 16 && _world.GetBlock(p.X, hi + 1, p.Z) == id) hi++;
        return (new BlockPos(p.X, lo, p.Z), new BlockPos(p.X, hi, p.Z));
    }

    private string BlockName(BlockPos p) => Blocks.Get(_world.GetBlock(p)).Name.Replace('_', ' ');

    private int CountBlocks(IReadOnlyList<(BlockPos Min, BlockPos Max)> boxes)
    {
        long n = 0;
        foreach (var (a, b) in boxes)
        {
            long vol = (long)(b.X - a.X + 1) * (b.Y - a.Y + 1) * (b.Z - a.Z + 1);
            if (vol > 65536) { n += vol; continue; }
            for (int x = a.X; x <= b.X; x++)
            for (int y = Math.Max(0, a.Y), y1 = Math.Min(_world.WorldHeight - 1, b.Y); y <= y1; y++)
            for (int z = a.Z; z <= b.Z; z++)
                if (_world.GetBlock(x, y, z) != 0) n++;
        }
        return (int)Math.Min(n, int.MaxValue);
    }
}
