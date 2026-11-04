using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.World;
using MPos = MineClaw.Agent.Memory.BlockPos;
using BlockPos = MineClaw.World.BlockPos;

namespace MineClaw.Bot.Blueprints;

/// <summary>
/// "This is my house, memorize it": <see cref="StructureCapture"/> → a design record (status <see cref="DesignStatus.Captured"/>,
/// approved for reuse by whoever captured it), its preview and the bill to rebuild it, linked to a remembered structure (the
/// owner's soft-protected build, never dug for materials; "my house" resolves to the design). Rebuilding is the ordinary
/// <c>design build{id, at}</c> copy (<see cref="BlueprintService.Reuse"/>), never on top of the original unless asked
/// (<c>at:"in place"</c>). Capturing is read-only.
/// </summary>
public static partial class DesignCapture
{
    // ------------------------------------------------------------------ the design record

    /// <summary>Saves a verified capture as a design: "captured 'Fox's house' as design #11: 9×7×6, 214 blocks (palette: …); bill to rebuild: …".</summary>
    public static (BlueprintRecord? Rec, string Text) Save(BlueprintService s, CaptureResult r, string? by, Action<BlueprintRecord, CaptureResult>? link = null)
    {
        if (!r.Ok || r.Plan is not { } plan) return (null, "can't memorize it: " + (r.Refusal ?? "nothing captured"));
        var rec = InsertCaptured(s, r, plan, by);
        var site = OrDefault<SiteContext?>(() => s.Site(), null);
        var e = RebuildBill(plan, site);
        rec.Bill = e.Bill.Select(b => new BillRow(b.Name, b.Need, b.Have, b.Stored, b.Missing, b.Source)).ToList();
        rec.Eval = $"captured {r.Matched}/{r.Cells.Count} cells exact" + ReportTail(r, "; ");
        var palette = PaletteText(r);
        var bill = BillText(e);
        rec.Summary = $"{rec.Name} {plan.Blueprint.SizeText}, {r.Blocks} blocks (palette: {palette}); bill to rebuild: {bill}";
        s.Store.Update(rec, PreviewPng(s, plan, site));
        RecordRevision(s, rec, by);
        try { link?.Invoke(rec, r); } catch (Exception) { /* the design stands; the memory link is best effort */ }
        var text = $"captured '{rec.Name}' as design #{rec.Id}: {plan.Blueprint.SizeText}, {r.Blocks} blocks (palette: {palette}); bill to rebuild: {bill}{ReportTail(r, "; ")}"
                   + $"; rebuild elsewhere: design build{{id={rec.Id},at}}";
        return (rec, text);
    }

    /// <summary>"; " + the capture's report ("not captured: 2 bed; …"), or "" when there is nothing to report.</summary>
    private static string ReportTail(CaptureResult r, string sep) => r.Report() is { Length: > 0 } report ? sep + report : "";

    /// <summary><paramref name="get"/>'s value, or <paramref name="fallback"/> when it throws (a capture never fails on a side lookup).</summary>
    private static T OrDefault<T>(Func<T> get, T fallback)
    {
        try { return get(); }
        catch (Exception) { return fallback; }
    }

    /// <summary>The new design record: captured, accepted by its capturer, at revision 1, sited where the original stands.</summary>
    private static BlueprintRecord InsertCaptured(BlueprintService s, CaptureResult r, VoxelPlan plan, string? by)
    {
        var (fx, fz) = plan.Blueprint.Footprint;
        string bot = OrDefault(() => s.Crew().Bodies.FirstOrDefault()?.Name ?? "", "");
        int dim = OrDefault(() => s.Dimension(), 0);
        var rec = s.Store.Insert(new BlueprintRecord
        {
            Name = r.Name, Description = $"captured from the world ({r.Bounds}) for {by ?? "a player"}", Requester = by, Bot = bot, Dsl = r.Dsl,
            Status = DesignStatus.Captured, Dim = dim, Site = new BlockPos(r.Min.X + fx / 2, r.Min.Y - 1, r.Min.Z + fz / 2), Anchor = "where it stands",
        });
        rec.Origin = r.Min;
        rec.AcceptedBy = by;
        rec.Rev = 1;
        return rec;
    }

    /// <summary>The bill to build it again from nothing (at the original every block is "already there").</summary>
    private static DesignEvaluation RebuildBill(VoxelPlan plan, SiteContext? site)
    {
        var e = new DesignEvaluation();
        DesignChecks.Bill(plan, new SiteContext
        {
            World = new EmptySite(site?.World.WorldHeight ?? ChunkColumn.SizeY), Inventory = site?.Inventory, Stored = site?.Stored, GeneratorKnown = site?.GeneratorKnown ?? false,
            ChoppableTrees = site?.ChoppableTrees ?? 0,
        }, e);
        return e;
    }

    /// <summary>The five commonest blocks (a door's top half not counted): "cobblestone, planks, …".</summary>
    private static string PaletteText(CaptureResult r)
    {
        var names = r.Cells.Values.Where(c => !(BuildBlocks.KindOf(c.Id) == BuildKind.Door && (c.Meta & 8) != 0))
            .GroupBy(c => BuildBlocks.Name(c.Id, c.Meta)).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        return string.Join(", ", names.Take(5)) + (names.Count > 5 ? "…" : "");
    }

    private static string BillText(DesignEvaluation e) =>
        e.Bill.Count == 0 ? "nothing" : string.Join(", ", e.Bill.Take(4).Select(b => b.Text(source: false))) + (e.Bill.Count > 4 ? ", …" : "");

    /// <summary>The preview picture, or null.</summary>
    private static byte[]? PreviewPng(BlueprintService s, VoxelPlan plan, SiteContext? site)
    {
        try
        {
            if (site != null) return s.Preview.Render(plan, site.World, s.Roles(), s.Light(), s.Atlas(), s.WorldTime()).Png;
        }
        catch (Exception) { /* a capture without a picture is still a capture */ }
        return null;
    }

    /// <summary>
    /// r1: the build as it stands (its block set and the air around it), the owner's. Saving is a record, not a lock: a trusted
    /// capturer keeps working on it (live 10-30: "memorize these stairs", then every next column was refused).
    /// </summary>
    private static void RecordRevision(BlueprintService s, BlueprintRecord rec, string? by)
    {
        var who = by ?? "a player";
        s.History?.Sync(rec, who, RevisionAuthor.Player, $"captured by {who}", s.History.World());
        if (s.Edits?.IsTrusted(by) == true) s.Edits.Touch(rec.Id, $"{by} captured it");
    }

    /// <summary>All air, every column loaded, as high as the real site: the bill of a build from nothing.</summary>
    private sealed class EmptySite(int height) : IBlockAccess
    {
        public int GetBlock(int x, int y, int z) => 0;
        public int GetMeta(int x, int y, int z) => 0;
        public bool IsLoaded(int x, int z) => true;
        public Data.BlockInfo Info(int id) => Data.Blocks.Get(id);
        public int WorldHeight => height;
    }

    // ------------------------------------------------------------------ rebuild: never on top of the original

    private static readonly HashSet<string> InPlaceWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "in place", "original", "the original", "on the original", "over the original", "same place", "same spot", "where it is", "in-place",
    };

    /// <summary>A rebuild explicitly over the captured original ("in place": a repair): the original's own site, else null.</summary>
    public static SitePick? InPlace(BlueprintRecord source, string? at) =>
        source.Status == DesignStatus.Captured && at is { } a && InPlaceWords.Contains(a.Trim()) ? new SitePick(source.Site, "in place of the original") : null;

    /// <summary>
    /// Why a copy of a captured design can't go at <paramref name="ground"/> (the copy's box would overlap the original's), or
    /// null. The copy's origin is <see cref="BlueprintService.Reuse"/>'s: the footprint centred on the ground, one above it.
    /// </summary>
    public static string? OnTopOfOriginal(BlueprintRecord source, string? at, BlockPos ground)
    {
        if (source.Status != DesignStatus.Captured || source.Origin is not { } o || InPlace(source, at) != null) return null;
        var (bp, _) = BlueprintParser.Parse(source.Dsl);
        var (fx, fz) = bp.Footprint;
        if (fx < 1 || fz < 1 || bp.H < 1) return null;
        var n = bp.OriginOn(ground);
        bool overlap = Overlaps(n.X, o.X, fx) && Overlaps(n.Y, o.Y, bp.H) && Overlaps(n.Z, o.Z, fz);
        return overlap
            ? string.Create(CultureInfo.InvariantCulture,
                $"that spot is on top of the original {source.Name} (x {o.X}..{o.X + fx - 1}, z {o.Z}..{o.Z + fz - 1}); say where the new one goes (at=...), or at=\"in place\" to rebuild over the original")
            : null;
    }

    /// <summary>Two spans of <paramref name="len"/> cells starting at <paramref name="a"/> and <paramref name="b"/> share a cell.</summary>
    private static bool Overlaps(int a, int b, int len) => a <= b + len - 1 && b <= a + len - 1;

    // ------------------------------------------------------------------ memory link

    [GeneratedRegex(@"captured as design #(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRx();

    /// <summary>
    /// The captured build becomes (or links) a remembered structure: its box, kind "build", the owner, and "captured as design
    /// #11" in its note, so protection guards it as the owner's build ("part of the Fox's house build") and "my house" finds the
    /// design. Blocks a bot once placed there leave the placed-block log (they are the owner's now, never dug for materials).
    /// </summary>
    public static Structure Link(MemoryStore memory, WorldState? world, BlueprintRecord rec, CaptureResult r, string? structureName = null)
    {
        var owner = rec.Requester ?? "a player";
        var link = $"{owner}'s build, captured as design #{rec.Id}";
        var s = memory.UpsertStructure(structureName ?? rec.Name, st =>
        {
            if (string.IsNullOrWhiteSpace(st.Kind) || st.Kind == StructureKinds.Unknown) st.Kind = "build";
            st.Min = new MPos(r.Min.X, r.Min.Y, r.Min.Z);
            st.Max = new MPos(r.Max.X, r.Max.Y, r.Max.Z);
            st.Dimension = world?.Dimension ?? rec.Dim;
            st.TaughtBy ??= rec.Requester;
            st.Confidence = Math.Max(st.Confidence, 0.9);
            st.Note = string.IsNullOrWhiteSpace(st.Note) || LinkRx().IsMatch(st.Note) ? link : st.Note + "; " + link;
            st.History.Add($"captured as design #{rec.Id} by {owner} ({r.Bounds})");
        });
        if (world != null)
            foreach (var p in r.Cells.Keys) world.Placed.Forget(p, world.Dimension);
        return s;
    }

    /// <summary>The design a remembered structure links to ("captured as design #11"), or null.</summary>
    public static long? LinkedDesign(Structure s) =>
        s.Note is { } n && LinkRx().Match(n) is { Success: true } m && long.TryParse(m.Groups[1].Value, out var id) ? id : null;

    // ------------------------------------------------------------------ "my house" → design #11

    [GeneratedRegex(@"^(?:(?<my>my|our)\s+|(?<who>[a-z0-9_]{2,16})'s?\s+)?(?<what>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex OwnedRx();

    /// <summary>
    /// "my house" (said by Fox) / "Fox's house" / "robin's hut" → the captured design: by its remembered structure's link, else a
    /// captured design of that owner whose name holds the rest. Null for ids and anything not about a captured build (the
    /// store's own name search handles those).
    /// </summary>
    public static BlueprintRecord? Resolve(BlueprintStore store, MemoryStore? memory, string text, string? speaker)
    {
        if (OwnedQuery.Parse(text, speaker) is not { } q) return null;
        return (memory != null ? ByLinkedStructure(store, memory, q) : null) ?? ByCapturedName(store, q);
    }

    /// <summary>1. A remembered structure by its name ("Fox's house", "main house") that links to a live design.</summary>
    private static BlueprintRecord? ByLinkedStructure(BlueprintStore store, MemoryStore memory, OwnedQuery q)
    {
        var linked = memory.Read(d => d.Structures.Where(s => LinkedDesign(s) != null).ToList());
        foreach (var s in linked.OrderBy(s => s.Name.Equals(q.Text, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenByDescending(s => s.Updated))
            if (q.Names(s) && store.Get(LinkedDesign(s)!.Value) is { } r && r.Status != DesignStatus.Deleted) return r;
        return null;
    }

    /// <summary>2. A captured design by name and owner.</summary>
    private static BlueprintRecord? ByCapturedName(BlueprintStore store, OwnedQuery q)
    {
        var captured = store.List(50).Where(r => r.Status == DesignStatus.Captured).ToList();
        return captured.FirstOrDefault(r => r.Name.Equals(q.Text, StringComparison.OrdinalIgnoreCase))
               ?? (q.Possessive ? captured.FirstOrDefault(r => r.Name.Contains(q.What, StringComparison.OrdinalIgnoreCase) && q.Owns(r.Requester)) : null);
    }

    /// <summary>"my house" / "Fox's house" / "house": the whole text (no "the", no '#'), the thing, its owner (my = the speaker; our =
    /// anyone's), and whether an owner was said at all.</summary>
    private sealed record OwnedQuery(string Text, string What, string? Owner, bool Possessive)
    {
        /// <summary>Null for ids and empty text (the store's own search handles those).</summary>
        public static OwnedQuery? Parse(string text, string? speaker)
        {
            var t = (text ?? "").Trim().TrimStart('#');
            if (t.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) t = t[4..].Trim();
            if (t.Length == 0 || long.TryParse(t, out _)) return null;
            var m = OwnedRx().Match(t);
            if (!m.Success) return null;
            return new OwnedQuery(t, m.Groups["what"].Value.Trim(), OwnerOf(m, speaker), m.Groups["my"].Success || m.Groups["who"].Success);
        }

        private static string? OwnerOf(Match m, string? speaker)
        {
            if (m.Groups["my"].Success) return m.Groups["my"].Value.Equals("my", StringComparison.OrdinalIgnoreCase) ? speaker : null;
            return m.Groups["who"].Success ? m.Groups["who"].Value : null;
        }

        /// <summary>No owner said, or <paramref name="who"/> is (or contains, or is contained in) the owner's name.</summary>
        public bool Owns(string? who) => Owner == null || who != null && (who.Equals(Owner, StringComparison.OrdinalIgnoreCase)
                                                                          || who.Contains(Owner, StringComparison.OrdinalIgnoreCase) || Owner.Contains(who, StringComparison.OrdinalIgnoreCase));

        /// <summary>The structure is the one meant: its exact name, or (said with an owner) a name holding the thing, taught by
        /// the owner or naming them.</summary>
        public bool Names(Structure s) =>
            s.Name.Equals(Text, StringComparison.OrdinalIgnoreCase)
            || Possessive && s.Name.Contains(What, StringComparison.OrdinalIgnoreCase) && (Owns(s.TaughtBy) || Owner != null && s.Name.Contains(Owner, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ the design tool's op

    /// <summary>
    /// <c>design capture{name, at?|corners?}</c>: corners "x,y,z x,y,z" (two punched/named corners), or at = a remembered
    /// structure (its box), a pointed block / coords / a player (flood fill from there); neither = from where the speaker stands
    /// (or what they pointed at just now). The floor is the block under the speaker's feet when they stand at the build.
    /// </summary>
    public static ToolResult Op(JsonElement input, string? speaker, BlueprintService service, IGameClient client, MemoryStore? memory,
        Func<VisionHub?> hub, Func<BlockPos, bool>? exclude = null, Action? protectionChanged = null, TimeProvider? time = null)
    {
        var w = client.World;
        var name = CaptureName(input, speaker);
        if (Locate(input, speaker, w, memory, hub, time, out var where) is { } error) return ToolResult.Error(error);
        var result = StructureCapture.Capture(new CaptureRequest
        {
            Name = name, World = w.PlanningView, Live = w.IsLoaded, Corners = where.Corners, Seed = where.Corners == null ? where.Seed : null,
            FloorY = FloorAt(where.Seed, where.Requester), Exclude = exclude,
        });
        var structureName = where.StructureName;
        var (rec, text) = Save(service, result, speaker, memory == null ? null : (r, c) =>
        {
            Link(memory, w, r, c, structureName);
            protectionChanged?.Invoke();
        });
        return rec != null ? ToolResult.Success(text) : ToolResult.Error(text);
    }

    /// <summary>What a capture covers: two corners (said, or a remembered structure's box) or a seed to flood fill from, and who
    /// asked (seen in the world, or the player <c>at</c> named).</summary>
    private struct CaptureWhere
    {
        public (BlockPos A, BlockPos B)? Corners;
        public BlockPos? Seed;
        public string? StructureName;
        public Entity? Requester;
    }

    /// <summary>Corners, else <c>at</c>, else the speaker: what the capture covers; the error, or null.</summary>
    private static string? Locate(JsonElement input, string? speaker, WorldState w, MemoryStore? memory, Func<VisionHub?> hub, TimeProvider? time,
        out CaptureWhere where)
    {
        where = new CaptureWhere { Corners = Corners(input), Requester = speaker != null ? w.Entities.FindPlayer(speaker) : null };
        var at = input.Str("at")?.Trim();
        if (where.Corners == null && at is { Length: > 0 } && FromAt(at, memory, hub, ref where) is { } atError) return atError;
        if (where.Corners == null && where.Seed == null) return FromSpeaker(speaker, hub, time, ref where);
        return null;
    }

    /// <summary>name (or what), trimmed; "Fox's build" when neither is said.</summary>
    private static string CaptureName(JsonElement input, string? speaker)
    {
        var name = (input.Str("name") ?? input.Str("what"))?.Trim();
        return string.IsNullOrWhiteSpace(name) ? $"{speaker ?? "a player"}'s build" : name;
    }

    /// <summary><c>at</c>: a remembered structure (its box), else a pointed block / coords / a player (a seed); the error, or null.</summary>
    private static string? FromAt(string at, MemoryStore? memory, Func<VisionHub?> hub, ref CaptureWhere where)
    {
        if (memory?.GetStructure(at) is { Min: { } mn } st)
        {
            var mx = st.Max ?? mn;
            where.Corners = (new BlockPos(mn.X, mn.Y, mn.Z), new BlockPos(mx.X, mx.Y, mx.Z));
            where.StructureName = st.Name;
            return null;
        }
        if (hub() is { } h)
        {
            var (focus, player, error) = h.ResolveFocus(at);
            if (error != null) return error;
            where.Seed = focus;
            if (player != null) where.Requester ??= player;
            return null;
        }
        if (Locations.ParseCoords(at) is not { } p) return $"unknown spot '{at}' (two corners, pointed, x,y,z, a player or a remembered structure)";
        where.Seed = p;
        return null;
    }

    /// <summary>Neither corners nor <c>at</c>: where the speaker stands, else what they pointed at in the last ten minutes; the error, or null.</summary>
    private static string? FromSpeaker(string? speaker, Func<VisionHub?> hub, TimeProvider? time, ref CaptureWhere where)
    {
        var clock = time ?? TimeProvider.System;
        if (where.Requester != null) where.Seed = where.Requester.Position.ToBlockPos();
        else if (RecentPoint(hub, speaker, clock) is { } g) where.Seed = g;
        else return $"which build? I can't see {speaker ?? "you"}: stand in it, point at it, or give corners=\"x,y,z x,y,z\"";
        return null;
    }

    /// <summary>The block the speaker (anyone, when unknown) pointed at less than ten minutes ago, or null.</summary>
    private static BlockPos? RecentPoint(Func<VisionHub?> hub, string? speaker, TimeProvider clock) =>
        hub()?.LastPoint is { } g && (speaker == null || g.Player.Equals(speaker, StringComparison.OrdinalIgnoreCase)) && clock.GetUtcNow() - g.At < TimeSpan.FromMinutes(10)
            ? g.Pos
            : null;

    /// <summary>The floor is the block under the requester's feet when they stand at the build (within 12 sideways, not below the seed's floor).</summary>
    private static int? FloorAt(BlockPos? seed, Entity? requester)
    {
        if (seed is not { } sd || requester == null) return null;
        var feet = requester.Position.ToBlockPos();
        return Math.Abs(feet.X - sd.X) <= 12 && Math.Abs(feet.Z - sd.Z) <= 12 && feet.Y - 1 <= sd.Y ? feet.Y - 1 : null;
    }

    [GeneratedRegex(@"(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)")]
    private static partial Regex TripleRx();

    /// <summary>corners: "x,y,z x,y,z" (any separator), or ["x,y,z","x,y,z"].</summary>
    internal static (BlockPos A, BlockPos B)? Corners(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("corners", out var c)) return null;
        var text = c.ValueKind switch
        {
            JsonValueKind.String => c.GetString() ?? "",
            JsonValueKind.Array => string.Join(" ", c.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())),
            _ => "",
        };
        var ms = TripleRx().Matches(text);
        if (ms.Count < 2) return null;
        BlockPos P(Match m) => new(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        return (P(ms[0]), P(ms[1]));
    }
}
