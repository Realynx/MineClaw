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
        // The design's own version (its DSL's line; a legacy design is the server's): the canvas opens in it, never converted.
        var version = DesignVersions.Of(rec);
        DesignSnapshot? snap = null;
        string dsl = rec.Dsl;
        int at = 0;
        if (s.History is { } h && h.Ensure(rec) is { } head)
        {
            var r = rev is { } n ? h.Revisions.Get(rec.Id, n) : head;
            if (r == null) return new(null, default, rec, $"#{id} has no revision r{rev}");
            snap = h.Revisions.Snapshot(rec.Id, r.Rev);
            dsl = r.Dsl;
            at = r.Rev;
            version = DesignVersions.OfDsl(r.Dsl) ?? version;
        }
        else if (rev != null) return new(null, default, rec, $"#{id} has no revisions");
        if (MineDesign.HeaderOf(dsl) is { } mine) return LoadMine(s, rec, dsl, snap, mine, version, at);
        snap ??= s.History?.SnapshotOf(rec);
        if (snap == null)
        {
            if (s.PlanOf(rec, out var err, forWorld: false) is not { } plan) return new(null, default, rec, $"#{id} doesn't parse: {err}");
            snap = new DesignSnapshot();
            foreach (var b in plan.Blocks.Values) snap.Blocks[b.Pos] = (b.Id, b.Meta);
        }
        // An older revision's ground level comes from its own DSL.
        var keep = rec.Dsl;
        rec.Dsl = dsl;
        int? grade;
        try { grade = GradeY(s, rec); }
        finally { rec.Dsl = keep; }
        var (canvas, off) = DesignerCanvas.FromSnapshot(snap, grade);
        canvas.Version = version;
        if (canvas.Count < snap.Count) return new(canvas, off, rec, $"#{id} is bigger than the plot: {snap.Count - canvas.Count} blocks left out", Rev: at);
        return new(canvas, off, rec, null, Rev: at);
    }

    /// <summary>A mine revision on a mine canvas: its cells where it sat on the plot (kept inside it), absolute y as saved.</summary>
    private static DesignerLoad LoadMine(BlueprintService s, BlueprintRecord rec, string dsl, DesignSnapshot? snap, MineHeader mine, string version, int rev)
    {
        if (snap == null)
        {
            var keep = rec.Dsl;
            rec.Dsl = dsl;
            try { snap = s.History?.SnapshotOf(rec); }
            finally { rec.Dsl = keep; }
        }
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
        static DesignerSaveResult Fail(string why) => new(false, 0, 0, 0, false, why);
        // Drawn in the session's version, with its version line (every revision records the version it was saved for).
        using var scope = DesignVersions.Use(session.Version);
        name = Clean(name);
        if (name.Length == 0) return Fail("a design needs a name");
        if (session.Canvas.Mine) return SaveMine(s, session, name, summary, requester, asNew, bot);
        var canvas = session.Canvas;
        if (canvas.Count == 0) return Fail("the canvas is empty");
        var existing = !asNew && session.DesignId is { } did ? s.Store.Get(did) : null;
        if (existing is { Status: DesignStatus.Deleted }) existing = null;
        // Canvas → design coordinates (a loaded design goes back where it stands).
        bool placed = existing?.Origin != null;
        // A studio design has no site: it is drawn standing on a ground at y 63 (any height in the world works; the copy's site decides).
        var off = existing != null ? session.Offset : default;
        if (!placed) off = new BlockPos(off.X, -64, off.Z); // canvas y 0 = design y 64 (a basement fits under it)
        var snap = new DesignSnapshot();
        foreach (var (p, st) in canvas.Blocks) snap.Blocks[new BlockPos(p.X - off.X, p.Y - off.Y, p.Z - off.Z)] = st;
        // The canvas's y 0 in design coordinates: below it is ground, air there only where it was dug.
        int grade = -off.Y;
        var dug = canvas.Dug.Select(p => new BlockPos(p.X - off.X, p.Y - off.Y, p.Z - off.Z)).ToHashSet();
        var box = snap.Box!.Value;
        for (int y = box.Min.Y; y <= box.Max.Y; y++)
            for (int z = box.Min.Z; z <= box.Max.Z; z++)
                for (int x = box.Min.X; x <= box.Max.X; x++)
                {
                    var p = new BlockPos(x, y, z);
                    if (!snap.Blocks.ContainsKey(p) && (y >= grade || dug.Contains(p))) snap.Air.Add(p);
                }
        snap.Air.UnionWith(dug);
        var air = DesignHistory.InteriorAir(snap);
        air.UnionWith(dug);
        var (dsl, min, bad, why) = DesignHistory.Draw(name, snap, air, ChunkColumn.SizeY, dug.Count > 0 || snap.Blocks.Keys.Any(p => p.Y < grade) ? grade : null,
            session.Version);
        if (dsl == null) return Fail($"can't save it as a design: {why}");
        if (!placed) dsl = OriginLine().Replace(dsl, "");
        dsl = CaptureComment().Replace(dsl, "# drawn in the Design Studio\n", 1);
        if (session.Anchor is { } a)
            dsl = string.Create(CultureInfo.InvariantCulture, $"# anchor {a.X - off.X - min.X},{a.Y - off.Y - min.Y},{a.Z - off.Z - min.Z}\n") + dsl;
        int w = box.Max.X - box.Min.X + 1, hgt = box.Max.Y - box.Min.Y + 1, d = box.Max.Z - box.Min.Z + 1;
        var pal = DesignPalette.For(session.Version);
        var names = snap.Blocks.Values.Where(c => !DesignFacing.SecondHalf(c.Id, c.Meta))
            .GroupBy(c => pal.Name(c.Id, c.Meta).ToLowerInvariant()).OrderByDescending(g => g.Count()).ToList();
        var palette = string.Join(", ", names.Take(5).Select(g => g.Key)) + (names.Count > 5 ? "…" : "");
        var line = string.Create(CultureInfo.InvariantCulture, $"{name} {w}×{d}×{hgt}, {snap.Count} blocks (palette: {palette})");
        var msg = string.IsNullOrWhiteSpace(summary) ? $"saved from the Design Studio by {requester}" : summary.Trim();
        bool created = existing == null;
        int depth = dug.Count == 0 && box.Min.Y >= grade ? 0 : grade - Math.Min(box.Min.Y, dug.Count > 0 ? dug.Min(p => p.Y) : grade);
        if (depth > 0) line += string.Create(CultureInfo.InvariantCulture, $", {depth} below ground");
        var rec = existing ?? s.Store.Insert(new BlueprintRecord
        {
            Name = name, Description = string.IsNullOrWhiteSpace(summary) ? $"designed in the Design Studio by {requester}" : summary.Trim(),
            Requester = requester, Bot = bot, Dsl = dsl, Status = DesignStatus.Accepted, Rev = 1,
            Anchor = "designed in the Design Studio: no site yet (build a copy with at=…)",
        });
        rec.Name = name;
        rec.Dsl = dsl;
        rec.Origin = placed ? min : null;
        if (created) rec.AcceptedBy = requester;
        rec.Summary = line;
        rec.Bill = names.Select(g => new BillRow(g.Key, g.Count(), 0, 0, g.Count(), "")).ToList();
        rec.Eval = bad > 0 ? $"{bad} cells drawn as stand-ins" : "drawn exactly";
        rec.CostUsd += session.CostUsd;
        s.Store.Update(rec);
        int rev = s.History?.Sync(rec, requester, RevisionAuthor.Player, msg)?.Rev ?? 0;
        if (s.Edits?.IsTrusted(requester) == true) s.Edits.Touch(rec.Id, $"{requester} saved it in the Design Studio");
        session.DesignId = rec.Id;
        session.Name = name;
        var text = (created ? $"saved as design #{rec.Id}" : $"saved #{rec.Id}") + (rev > 0 ? $" r{rev}" : "") + $" ({session.Version}): {line}"
                   + (bad > 0 ? $" ({bad} cells drawn as stand-ins)" : "");
        return new DesignerSaveResult(true, rec.Id, rev, snap.Count, created, text);
    }

    /// <summary>
    /// A mine canvas saved as a mine design (<see cref="MineDesign"/>): its dug cells (the cells to mine out) and its blocks at
    /// absolute y, where they sit on the plot; the entry is the anchor ("move origin"), else the highest dug cell.
    /// </summary>
    private static DesignerSaveResult SaveMine(BlueprintService s, DesignerSessionRecord session, string name, string? summary, string requester, bool asNew,
        string bot)
    {
        static DesignerSaveResult Fail(string why) => new(false, 0, 0, 0, false, why);
        var canvas = session.Canvas;
        if (canvas.Count == 0 && canvas.Dug.Count == 0) return Fail("the canvas is empty: dig the mine first");
        var existing = !asNew && session.DesignId is { } did ? s.Store.Get(did) : null;
        if (existing is { Status: DesignStatus.Deleted }) existing = null;
        var snap = new DesignSnapshot();
        foreach (var (p, st) in canvas.Blocks) snap.Blocks[p] = st;
        snap.Air.UnionWith(canvas.Dug);
        BlockPos? entry = session.Anchor ?? (canvas.Dug.Count > 0 ? canvas.Dug.OrderByDescending(p => p.Y).ThenBy(p => p.Z).ThenBy(p => p.X).First() : null);
        var (dsl, min, bad, why) = MineDesign.Draw(name, snap, entry, canvas.Seed, session.PreferredFor, session.Version);
        if (dsl == null) return Fail($"can't save it as a design: {why}");
        var pal = DesignPalette.For(session.Version);
        var names = snap.Blocks.Values.Where(c => !DesignFacing.SecondHalf(c.Id, c.Meta))
            .GroupBy(c => pal.Name(c.Id, c.Meta).ToLowerInvariant()).OrderByDescending(g => g.Count()).ToList();
        var line = name + " " + MineDesign.Summary(snap) + (session.PreferredFor is { } pf ? $", preferred for {pf}" : "");
        var msg = string.IsNullOrWhiteSpace(summary) ? $"saved from the Design Studio by {requester}" : summary.Trim();
        bool created = existing == null;
        var rec = existing ?? s.Store.Insert(new BlueprintRecord
        {
            Name = name, Description = string.IsNullOrWhiteSpace(summary) ? $"a mine designed in the Design Studio by {requester}" : summary.Trim(),
            Requester = requester, Bot = bot, Dsl = dsl, Status = DesignStatus.Accepted, Rev = 1,
            Anchor = "a mine designed in the Design Studio: absolute y, no site yet (place it in Story Mode)",
        });
        rec.Name = name;
        rec.Dsl = dsl;
        rec.Origin = null;
        if (created) rec.AcceptedBy = requester;
        rec.Summary = line;
        rec.Bill = names.Select(g => new BillRow(g.Key, g.Count(), 0, 0, g.Count(), "")).ToList();
        rec.Eval = bad > 0 ? $"{bad} cells drawn as stand-ins" : "drawn exactly";
        rec.CostUsd += session.CostUsd;
        s.Store.Update(rec);
        int rev = s.History?.Sync(rec, requester, RevisionAuthor.Player, msg)?.Rev ?? 0;
        if (s.Edits?.IsTrusted(requester) == true) s.Edits.Touch(rec.Id, $"{requester} saved it in the Design Studio");
        session.DesignId = rec.Id;
        session.Name = name;
        session.Offset = default;
        var text = (created ? $"saved as design #{rec.Id}" : $"saved #{rec.Id}") + (rev > 0 ? $" r{rev}" : "") + $" ({session.Version}, anchor absolute y {min.Y}): {line}"
                   + (bad > 0 ? $" ({bad} cells drawn as stand-ins)" : "");
        return new DesignerSaveResult(true, rec.Id, rev, snap.Count, created, text);
    }

    private static string Clean(string? name)
    {
        var n = (name ?? "").Replace('#', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim().Trim('"', '\'').Trim();
        return n.Length <= 48 ? n : n[..48].TrimEnd();
    }
}
