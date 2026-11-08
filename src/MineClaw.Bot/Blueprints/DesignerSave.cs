using System.Globalization;
using System.Text.RegularExpressions;
using MineClaw.Bot.Blueprints.Mine;
using MineClaw.World;
using MineClaw.World.Designs;

namespace MineClaw.Bot.Blueprints;

/// <summary>What a Design Studio save did.</summary>
public sealed record DesignerSaveResult(bool Ok, long DesignId, int Rev, int Blocks, bool Created, string Text);

/// <summary>A design revision opened on a canvas: the canvas (null + <see cref="Why"/> when it can't be), the design → canvas offset,
/// and for a mine its entry on the canvas (the session's anchor) and its version preference.</summary>
public sealed record DesignerLoad(DesignerCanvas? Canvas, BlockPos Offset, BlueprintRecord? Design, string? Why, BlockPos? Anchor = null,
    string? PreferredFor = null, int Rev = 0);

/// <summary>
/// The Design Studio's bridge to the designs system: a canvas becomes an ordinary design (DSL drawn as it stands, exactly like a
/// capture or a commit: <see cref="DesignHistory.Draw"/>) and a revision on its Git-style history, authored by the signed-in
/// player; and a design's newest revision loads onto a canvas. Saving never locks anything (a trusted saver's touch opens the
/// design for requested edits, as a capture does). A studio design has no site: the bot builds a copy where it is asked
/// (<c>design build{id, at}</c>), so its record keeps no origin unless it was loaded from a design that stands in the world.
/// Below ground: the canvas's dug cells are drawn '.' (forced air: a build digs them out of the terrain) and the layers under the
/// canvas's y 0 are the design's <c>ground N</c> (a site puts layer N one above its ground block); undug ground is '_' (left as
/// it is). Loading puts that layer back on y 0 and the design's air under it back as dug cells.
/// </summary>
public static partial class DesignerSave
{
    [GeneratedRegex(@"^origin\s+[^\n]*\n", RegexOptions.Multiline)]
    private static partial Regex OriginLine();

    [GeneratedRegex(@"^# captured from the world at[^\n]*\n", RegexOptions.Multiline)]
    private static partial Regex CaptureComment();

    /// <summary>The newest revision of design <paramref name="id"/> on a fresh canvas (null + why when it can't be loaded).</summary>
    public static (DesignerCanvas? Canvas, BlockPos Offset, BlueprintRecord? Design, string? Why) Load(BlueprintService s, long id)
    {
        var l = LoadDesign(s, id);
        return (l.Canvas, l.Offset, l.Design, l.Why);
    }

    /// <summary>
    /// Revision <paramref name="rev"/> (default: the newest) of design <paramref name="id"/> on a fresh canvas: a build design centred
    /// on the plot (its <c>ground N</c> layer on y 0), a mine (<c>kind mine</c>) at its absolute y where it sat on the plot, on its
    /// seed's terrain, its entry as the anchor. Saving an older revision opened this way is a revert (the next revision is it again).
    /// </summary>
    public static DesignerLoad LoadDesign(BlueprintService s, long id, int? rev = null)
    {
        if (s.Store.Get(id) is not { } rec || rec.Status == DesignStatus.Deleted) return new(null, default, null, $"no design #{id}");
        var (pick, why) = PickRevision(s, rec, rev);
        if (pick == null) return new(null, default, rec, why);
        if (MineDesign.HeaderOf(pick.Dsl) is { } mine) return LoadMine(s, rec, pick.Dsl, pick.Snap, mine, pick.Version, pick.Rev);
        var snap = pick.Snap ?? s.History?.SnapshotOf(rec);
        if (snap == null && !TryPlanSnapshot(s, rec, out snap, out var err)) return new(null, default, rec, $"#{id} doesn't parse: {err}");
        // An older revision's ground level comes from its own DSL.
        int? grade = WithDsl(rec, pick.Dsl, () => GradeY(s, rec));
        var (canvas, off) = DesignerCanvas.FromSnapshot(snap!, grade);
        canvas.Version = pick.Version;
        if (canvas.Count < snap!.Count) return new(canvas, off, rec, $"#{id} is bigger than the plot: {snap.Count - canvas.Count} blocks left out", Rev: pick.Rev);
        return new(canvas, off, rec, null, Rev: pick.Rev);
    }

    /// <summary>The revision a load opens: its block set (null = draw it from the DSL), DSL, number (0 = no history) and version.</summary>
    private sealed record PickedRevision(DesignSnapshot? Snap, string Dsl, int Rev, string Version);

    /// <summary>Revision <paramref name="rev"/> (default: HEAD) of the design's history, else its record's DSL when it has no history
    /// (the design's own version: its DSL's line; a legacy design is the server's, never converted); null + why when asked for a
    /// revision it lacks.</summary>
    private static (PickedRevision? Pick, string? Why) PickRevision(BlueprintService s, BlueprintRecord rec, int? rev)
    {
        var version = DesignVersions.Of(rec);
        if (s.History is not { } h || h.Ensure(rec) is not { } head)
            return rev != null ? (null, $"#{rec.Id} has no revisions") : (new PickedRevision(null, rec.Dsl, 0, version), null);
        var r = rev is { } n ? h.Revisions.Get(rec.Id, n) : head;
        if (r == null) return (null, $"#{rec.Id} has no revision r{rev}");
        return (new PickedRevision(h.Revisions.Snapshot(rec.Id, r.Rev), r.Dsl, r.Rev, DesignVersions.OfDsl(r.Dsl) ?? version), null);
    }

    /// <summary>The block set the design's plan builds (a design with no history), or false + the parse error.</summary>
    private static bool TryPlanSnapshot(BlueprintService s, BlueprintRecord rec, out DesignSnapshot? snap, out string? err)
    {
        snap = null;
        if (s.PlanOf(rec, out err, forWorld: false) is not { } plan) return false;
        snap = new DesignSnapshot();
        foreach (var b in plan.Blocks.Values) snap.Blocks[b.Pos] = (b.Id, b.Meta);
        return true;
    }

    /// <summary>Reads the record as if its DSL were <paramref name="dsl"/> (an older revision's), then puts its own back.</summary>
    private static T WithDsl<T>(BlueprintRecord rec, string dsl, Func<T> read)
    {
        var keep = rec.Dsl;
        rec.Dsl = dsl;
        try { return read(); }
        finally { rec.Dsl = keep; }
    }

    /// <summary>A mine revision on a mine canvas: its cells where it sat on the plot (kept inside it), absolute y as saved.</summary>
    private static DesignerLoad LoadMine(BlueprintService s, BlueprintRecord rec, string dsl, DesignSnapshot? snap, MineHeader mine, string version, int rev)
    {
        snap ??= WithDsl(rec, dsl, () => s.History?.SnapshotOf(rec));
        var canvas = new DesignerCanvas { Version = version, Kind = DesignerCanvas.KindMine, Seed = MineTerrain.NormalizeSeed(mine.Seed) };
        if (snap == null || snap.Blocks.Count + snap.Air.Count == 0) return new(canvas, default, rec, null, PreferredFor: mine.PreferredFor, Rev: rev);
        var cells = snap.Blocks.Keys.Concat(snap.Air).ToList();
        var min = new BlockPos(cells.Min(p => p.X), cells.Min(p => p.Y), cells.Min(p => p.Z));
        int w = cells.Max(p => p.X) - min.X + 1, d = cells.Max(p => p.Z) - min.Z + 1;
        var target = new BlockPos(Math.Clamp(mine.Min.X, 0, Math.Max(0, DesignerCanvas.Size - w)), mine.Min.Y, Math.Clamp(mine.Min.Z, 0, Math.Max(0, DesignerCanvas.Size - d)));
        var off = new BlockPos(target.X - min.X, target.Y - min.Y, target.Z - min.Z);
        BlockPos Shift(BlockPos p) => new(p.X + off.X, p.Y + off.Y, p.Z + off.Z);
        canvas.Load(snap.Blocks.Select(kv => KeyValuePair.Create(Shift(kv.Key), kv.Value)), snap.Air.Where(p => !snap.Blocks.ContainsKey(p)).Select(Shift));
        BlockPos? anchor = mine.Entry is { } e ? new BlockPos(e.X - mine.Min.X + target.X, e.Y, e.Z - mine.Min.Z + target.Z) : null;
        var why = canvas.Count < snap.Count ? $"#{rec.Id} is bigger than the plot: {snap.Count - canvas.Count} blocks left out" : null;
        return new(canvas, off, rec, why, anchor, mine.PreferredFor, rev);
    }

    /// <summary>The y (in the design's revision coordinates) of its first layer above the ground, for a design that digs into it
    /// (<c>ground N</c>); null = none (its lowest block stands on the ground).</summary>
    internal static int? GradeY(BlueprintService s, BlueprintRecord rec)
    {
        try
        {
            if (s.PlanOf(rec, out _, forWorld: false) is not { } plan || plan.Blueprint.Ground <= 0) return null;
            return s.AsBuilt(rec, plan).Origin.Y + plan.Blueprint.Ground;
        }
        catch (Exception) { return null; }
    }

    public static DesignerSaveResult Save(BlueprintService s, DesignerSessionRecord session, string name, string? summary, string requester, bool asNew,
        string bot = "")
    {
        // Drawn in the session's version, with its version line (every revision records the version it was saved for).
        using var scope = DesignVersions.Use(session.Version);
        name = Clean(name);
        if (name.Length == 0) return Fail("a design needs a name");
        var req = new SaveRequest(name, summary, requester, bot);
        if (session.Canvas.Mine) return SaveMine(s, session, req, asNew);
        var canvas = session.Canvas;
        if (canvas.Count == 0) return Fail("the canvas is empty");
        var existing = Existing(s, session, asNew);
        // Canvas → design coordinates (a loaded design goes back where it stands).
        bool placed = existing?.Origin != null;
        var off = DesignOffset(session.Offset, existing != null, placed);
        var snap = new DesignSnapshot();
        foreach (var (p, st) in canvas.Blocks) snap.Blocks[new BlockPos(p.X - off.X, p.Y - off.Y, p.Z - off.Z)] = st;
        // The canvas's y 0 in design coordinates: below it is ground, air there only where it was dug.
        int grade = -off.Y;
        var dug = canvas.Dug.Select(p => new BlockPos(p.X - off.X, p.Y - off.Y, p.Z - off.Z)).ToHashSet();
        var box = snap.Box!.Value;
        AddGroundAir(snap, box, dug, grade);
        var air = DesignHistory.InteriorAir(snap);
        air.UnionWith(dug);
        bool underground = dug.Count > 0 || snap.Blocks.Keys.Any(p => p.Y < grade);
        var (dsl, min, bad, why) = DesignHistory.Draw(name, snap, air, ChunkColumn.SizeY, underground ? grade : null, session.Version);
        if (dsl == null) return Fail($"can't save it as a design: {why}");
        dsl = StudioDsl(dsl, placed, session.Anchor, off, min);
        var names = NamesOf(snap, session.Version);
        var line = BuildLine(name, box, snap, names, dug, grade);
        return Store(s, session, req, existing, names, new Drawing(dsl, line, bad, placed ? min : null, snap.Count), "", StudioAnchor, "");
    }

    private const string StudioAnchor = "designed in the Design Studio: no site yet (build a copy with at=…)";
    private const string MineAnchor = "a mine designed in the Design Studio: absolute y, no site yet (place it in Story Mode)";

    private static DesignerSaveResult Fail(string why) => new(false, 0, 0, 0, false, why);

    /// <summary>What was asked to be saved: the (cleaned) name, the summary (the revision's message), who saves it and for which bot.</summary>
    private sealed record SaveRequest(string Name, string? Summary, string Requester, string Bot)
    {
        public string Message => string.IsNullOrWhiteSpace(Summary) ? $"saved from the Design Studio by {Requester}" : Summary.Trim();

        /// <summary>A new record's description ("designed in the Design Studio by Fox", <paramref name="what"/> = "a mine " for a mine).</summary>
        public string Description(string what) => string.IsNullOrWhiteSpace(Summary) ? $"{what}designed in the Design Studio by {Requester}" : Summary.Trim();
    }

    /// <summary>A canvas drawn as a design: its DSL, its summary line, the cells drawn as stand-ins, its origin (a placed design's) and block count.</summary>
    private sealed record Drawing(string Dsl, string Line, int Bad, BlockPos? Origin, int Blocks);

    /// <summary>The design the session saves into (null = a new one: asked for, never loaded, or deleted since).</summary>
    private static BlueprintRecord? Existing(BlueprintService s, DesignerSessionRecord session, bool asNew)
    {
        var existing = !asNew && session.DesignId is { } did ? s.Store.Get(did) : null;
        return existing is { Status: DesignStatus.Deleted } ? null : existing;
    }

    /// <summary>Canvas → design offset: a loaded design's own; a studio design has no site and is drawn standing on a ground at y 63
    /// (any height in the world works; the copy's site decides): canvas y 0 = design y 64 (a basement fits under it).</summary>
    private static BlockPos DesignOffset(BlockPos loaded, bool existing, bool placed)
    {
        var off = existing ? loaded : default;
        return placed ? off : new BlockPos(off.X, -64, off.Z);
    }

    /// <summary>The box's air: above the ground every empty cell, below it only the dug ones (plus every dug cell).</summary>
    private static void AddGroundAir(DesignSnapshot snap, (BlockPos Min, BlockPos Max) box, HashSet<BlockPos> dug, int grade)
    {
        for (int y = box.Min.Y; y <= box.Max.Y; y++)
            for (int z = box.Min.Z; z <= box.Max.Z; z++)
                for (int x = box.Min.X; x <= box.Max.X; x++)
                {
                    var p = new BlockPos(x, y, z);
                    if (!snap.Blocks.ContainsKey(p) && (y >= grade || dug.Contains(p))) snap.Air.Add(p);
                }
        snap.Air.UnionWith(dug);
    }

    /// <summary>The drawn DSL as a studio design: no origin unless placed, "drawn in the Design Studio", the anchor when set.</summary>
    private static string StudioDsl(string dsl, bool placed, BlockPos? anchor, BlockPos off, BlockPos min)
    {
        if (!placed) dsl = OriginLine().Replace(dsl, "");
        dsl = CaptureComment().Replace(dsl, "# drawn in the Design Studio\n", 1);
        if (anchor is { } a)
            dsl = string.Create(CultureInfo.InvariantCulture, $"# anchor {a.X - off.X - min.X},{a.Y - off.Y - min.Y},{a.Z - off.Z - min.Z}\n") + dsl;
        return dsl;
    }

    /// <summary>The block names of a block set (second halves left out), most first.</summary>
    private static List<IGrouping<string, (int Id, int Meta)>> NamesOf(DesignSnapshot snap, string version)
    {
        var pal = DesignPalette.For(version);
        return snap.Blocks.Values.Where(c => !DesignFacing.SecondHalf(c.Id, c.Meta))
            .GroupBy(c => pal.Name(c.Id, c.Meta).ToLowerInvariant()).OrderByDescending(g => g.Count()).ToList();
    }

    /// <summary>"hut 5×5×9, 110 blocks (palette: planks, cobblestone…), 4 below ground".</summary>
    private static string BuildLine(string name, (BlockPos Min, BlockPos Max) box, DesignSnapshot snap, List<IGrouping<string, (int Id, int Meta)>> names,
        HashSet<BlockPos> dug, int grade)
    {
        int w = box.Max.X - box.Min.X + 1, hgt = box.Max.Y - box.Min.Y + 1, d = box.Max.Z - box.Min.Z + 1;
        var palette = string.Join(", ", names.Take(5).Select(g => g.Key)) + (names.Count > 5 ? "…" : "");
        var line = string.Create(CultureInfo.InvariantCulture, $"{name} {w}×{d}×{hgt}, {snap.Count} blocks (palette: {palette})");
        int depth = dug.Count == 0 && box.Min.Y >= grade ? 0 : grade - Math.Min(box.Min.Y, dug.Count > 0 ? dug.Min(p => p.Y) : grade);
        if (depth > 0) line += string.Create(CultureInfo.InvariantCulture, $", {depth} below ground");
        return line;
    }

    /// <summary>
    /// The drawn canvas becomes the design: a new record (accepted by the saver) or the loaded one updated, its bill and summary,
    /// the next revision on its history (a trusted saver's touch opens it for requested edits), the session pointed at it.
    /// </summary>
    private static DesignerSaveResult Store(BlueprintService s, DesignerSessionRecord session, SaveRequest req, BlueprintRecord? existing,
        List<IGrouping<string, (int Id, int Meta)>> names, Drawing drawn, string what, string anchor, string versionTail)
    {
        bool created = existing == null;
        var rec = existing ?? s.Store.Insert(new BlueprintRecord
        {
            Name = req.Name, Description = req.Description(what), Requester = req.Requester, Bot = req.Bot, Dsl = drawn.Dsl,
            Status = DesignStatus.Accepted, Rev = 1, Anchor = anchor,
        });
        rec.Name = req.Name;
        rec.Dsl = drawn.Dsl;
        rec.Origin = drawn.Origin;
        if (created) rec.AcceptedBy = req.Requester;
        rec.Summary = drawn.Line;
        rec.Bill = names.Select(g => new BillRow(g.Key, g.Count(), 0, 0, g.Count(), "")).ToList();
        rec.Eval = drawn.Bad > 0 ? $"{drawn.Bad} cells drawn as stand-ins" : "drawn exactly";
        rec.CostUsd += session.CostUsd;
        s.Store.Update(rec);
        int rev = s.History?.Sync(rec, req.Requester, RevisionAuthor.Player, req.Message)?.Rev ?? 0;
        if (s.Edits?.IsTrusted(req.Requester) == true) s.Edits.Touch(rec.Id, $"{req.Requester} saved it in the Design Studio");
        session.DesignId = rec.Id;
        session.Name = req.Name;
        var text = (created ? $"saved as design #{rec.Id}" : $"saved #{rec.Id}") + (rev > 0 ? $" r{rev}" : "") + $" ({session.Version}{versionTail}): {drawn.Line}"
                   + (drawn.Bad > 0 ? $" ({drawn.Bad} cells drawn as stand-ins)" : "");
        return new DesignerSaveResult(true, rec.Id, rev, drawn.Blocks, created, text);
    }

    /// <summary>
    /// A mine canvas saved as a mine design (<see cref="MineDesign"/>): its dug cells (the cells to mine out) and its blocks at
    /// absolute y, where they sit on the plot; the entry is the anchor ("move origin"), else the highest walkable feet cell.
    /// </summary>
    private static DesignerSaveResult SaveMine(BlueprintService s, DesignerSessionRecord session, SaveRequest req, bool asNew)
    {
        var canvas = session.Canvas;
        if (canvas.Count == 0 && canvas.Dug.Count == 0) return Fail("the canvas is empty: dig the mine first");
        var existing = Existing(s, session, asNew);
        var snap = canvas.ToSnapshot();
        BlockPos? entry = session.Anchor ?? MineEntry(canvas);
        var (dsl, min, bad, why) = MineDesign.Draw(req.Name, snap, entry, canvas.Seed, session.PreferredFor, session.Version);
        if (dsl == null) return Fail($"can't save it as a design: {why}");
        var line = req.Name + " " + MineDesign.Summary(snap) + (session.PreferredFor is { } pf ? $", preferred for {pf}" : "");
        var res = Store(s, session, req, existing, NamesOf(snap, session.Version), new Drawing(dsl, line, bad, null, snap.Count), "a mine ", MineAnchor,
            $", anchor absolute y {min.Y}");
        session.Offset = default;
        return res;
    }

    /// <summary>Headroom belongs to the design too, but a default entrance belongs at feet level above a solid floor.</summary>
    private static BlockPos? MineEntry(DesignerCanvas canvas)
    {
        var ordered = canvas.Dug.OrderByDescending(p => p.Y).ThenBy(p => p.Z).ThenBy(p => p.X).ToList();
        foreach (var p in ordered)
            if (canvas.Contains(p.X, p.Y + 1, p.Z) && canvas.Holds(p.X, p.Y - 1, p.Z)
                && !canvas.IsEarth(p.X, p.Y + 1, p.Z) && canvas.Get(p.X, p.Y + 1, p.Z).Id == 0)
                return p;
        // An unfinished shaft may not have a walkable entrance yet; preserve the existing fallback for that case.
        return ordered.Count > 0 ? ordered[0] : null;
    }

    private static string Clean(string? name)
    {
        var n = (name ?? "").Replace('#', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim().Trim('"', '\'').Trim();
        return n.Length <= 48 ? n : n[..48].TrimEnd();
    }
}
