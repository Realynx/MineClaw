using System.Globalization;
using MineClaw.Data;

namespace MineClaw.Render;

/// <summary>
/// The "reading aids" drawn over every 3D panel after the raster (screen space, depth-aware), so a model reads coordinates
/// instead of guessing: a faint ground grid on the focus floor (one line per block, every 5th stronger and labelled with its
/// absolute x/z at the grid's edge, like a ruler), a y ruler beside the focus, the focus/spots marked with a crosshair and
/// their "x,y,z", distance rings around the body (2/4/8 blocks), an axes gizmo (N/E/S/W, +x/+z, up), and prioritised
/// floating labels (players, mobs + burning, drops × count, containers, stations, gen cells G1.., structures, the build site)
/// that dodge each other with leader lines, capped at ~12 per panel. Labels behind walls are dropped unless they are players
/// (drawn dim, "behind"). Every label is also returned as text ("chest 14,64,-5") for the deterministic facts.
/// </summary>
public static class ReadingAids
{
    public const int MaxLabels = 12;
    private const uint GridC = 0xffffff, Gold = 0xffd84a, FocusC = 0x5fe8ff, Dim = 0x9aa3b5;

    public sealed class Input
    {
        public required (int X, int Y, int Z) Focus { get; init; }
        public int FeetY { get; init; }
        public int Radius { get; init; } = 6;
        public IReadOnlyList<(int X, int Y, int Z)> Spots { get; init; } = [];
        public PerspectivePanel.Body? Body { get; init; }
        internal VisionRenderer.Slab? Slab { get; init; }
        public bool Rings { get; init; } = true;
        public bool Grid { get; init; } = true;
    }

    private sealed record Cand(int Priority, double X, double Y, double Z, string Text, uint Colour, bool KeepOccluded, string? Fact);

    /// <summary>Draws the aids and returns the label facts ("chest 14,64,-5").</summary>
    internal static List<string> Draw(EyeRenderer r, Canvas c, EyeRenderer.Frame fr, EyeScene s, Input a, VisionRenderer.Placer placer)
    {
        var facts = new List<string>();
        if (a.Grid) Grid(r, c, fr, s, a, placer);
        if (a.Rings && a.Body is { } b && Math.Abs(b.X - a.Focus.X) <= 2 * a.Radius && Math.Abs(b.Z - a.Focus.Z) <= 2 * a.Radius) Rings(r, c, fr, s, b);
        YRuler(r, c, fr, s, a, placer);
        var cands = Candidates(s, a);
        Place(r, c, fr, s, cands, placer, facts);
        Gizmo(c, s, fr.Width - 14, fr.Height - 14);
        return facts;
    }

    // ------------------------------------------------------------------ grid + rulers

    private static void Grid(EyeRenderer r, Canvas c, EyeRenderer.Frame fr, EyeScene s, Input a, VisionRenderer.Placer placer)
    {
        int x0 = a.Focus.X - a.Radius, x1 = a.Focus.X + a.Radius + 1, z0 = a.Focus.Z - a.Radius, z1 = a.Focus.Z + a.Radius + 1;
        double y = a.FeetY + 0.01;
        for (int x = x0; x <= x1; x++)
        {
            bool major = Mod5(x) == 0;
            r.Line3D(c, fr, s, (x, y, z0), (x, y, z1), GridC, hiddenDots: false, alpha: major ? 0.5f : 0.22f);
            if (major) EdgeLabel(r, c, fr, s, placer, (x, y, z0), (x, y, z1), "X" + x.ToString(CultureInfo.InvariantCulture));
        }
        for (int z = z0; z <= z1; z++)
        {
            bool major = Mod5(z) == 0;
            r.Line3D(c, fr, s, (x0, y, z), (x1, y, z), GridC, hiddenDots: false, alpha: major ? 0.5f : 0.22f);
            if (major) EdgeLabel(r, c, fr, s, placer, (x0, y, z), (x1, y, z), "Z" + z.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static int Mod5(int v) => ((v % 5) + 5) % 5;

    /// <summary>The label of a major grid line at whichever end lies nearer the panel border (a ruler reading).</summary>
    private static void EdgeLabel(EyeRenderer r, Canvas c, EyeRenderer.Frame fr, EyeScene s, VisionRenderer.Placer placer,
        (double X, double Y, double Z) a, (double X, double Y, double Z) b, string text)
    {
        var pa = r.ProjectF(s, a.X, a.Y, a.Z);
        var pb = r.ProjectF(s, b.X, b.Y, b.Z);
        (double X, double Y)? best = null;
        double bestEdge = double.MaxValue;
        foreach (var p in new[] { pa, pb })
        {
            if (p is not { } q || q.X < 2 || q.Y < 2 || q.X > fr.Width - 3 || q.Y > fr.Height - 3) continue;
            double edge = Math.Min(Math.Min(q.X, fr.Width - q.X), Math.Min(q.Y, fr.Height - q.Y));
            if (edge < bestEdge) { bestEdge = edge; best = (q.X, q.Y); }
        }
        if (best is not { } at) return;
        int w = PixelFont.Width(text) + 2;
        int lx = Math.Clamp((int)at.X - w / 2, 0, fr.Width - w), ly = Math.Clamp((int)at.Y - 4, 0, fr.Height - 9);
        if (placer.FindNear(lx, ly, w, 9) is { } p2) Outlined(c, p2.X + 1, p2.Y + 1, text, 0xd8dee9);
    }

    /// <summary>Text with a 1 px dark outline (legible on any background).</summary>
    public static void Outlined(Canvas c, int x, int y, string text, uint fg)
    {
        foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) }) c.Text(x + dx, y + dy, text, 0x0b0d12);
        c.Text(x, y, text, fg);
    }

    /// <summary>A vertical y ruler on the focus column's near corner: a tick per block, every other one labelled.</summary>
    private static void YRuler(EyeRenderer r, Canvas c, EyeRenderer.Frame fr, EyeScene s, Input a, VisionRenderer.Placer placer)
    {
        // The corner of the focus block nearest the camera (so the ruler stands in front).
        double cx = s.EyeX < a.Focus.X + 0.5 ? a.Focus.X : a.Focus.X + 1, cz = s.EyeZ < a.Focus.Z + 0.5 ? a.Focus.Z : a.Focus.Z + 1;
        int y0 = a.FeetY - 2, y1 = a.FeetY + 3;
        r.Line3D(c, fr, s, (cx, y0, cz), (cx, y1, cz), Gold, hiddenDots: true, alpha: 0.85f);
        for (int y = y0; y <= y1; y++) YRulerTick(r, c, fr, s, placer, cx, y, cz, (y - a.FeetY) % 2 == 0);
    }

    /// <summary>One ruler tick at height y (labelled "Y.." when asked and there's room), skipped off-picture.</summary>
    private static void YRulerTick(EyeRenderer r, Canvas c, EyeRenderer.Frame fr, EyeScene s, VisionRenderer.Placer placer, double cx, int y, double cz, bool labelled)
    {
        if (r.ProjectF(s, cx, y, cz) is not { } p || p.X < 0 || p.Y < 0 || p.X >= fr.Width || p.Y >= fr.Height) return;
        int px = (int)p.X, py = (int)p.Y;
        c.Line(px - 2, py, px + 2, py, Gold);
        if (!labelled) return;
        string t = "Y" + y.ToString(CultureInfo.InvariantCulture);
        int w = PixelFont.Width(t) + 2;
        if (placer.FindNear(Math.Min(fr.Width - w, px + 4), py - 4, w, 9) is { } at) Outlined(c, at.X + 1, at.Y + 1, t, Gold);
    }

    /// <summary>Dashed rings on the body's floor at 2, 4 and 8 blocks (reach, danger range).</summary>
    private static void Rings(EyeRenderer r, Canvas c, EyeRenderer.Frame fr, EyeScene s, PerspectivePanel.Body b)
    {
        foreach (double rad in new[] { 2.0, 4.0, 8.0 })
        {
            int n = (int)(rad * 10);
            for (int i = 0; i < n; i += 2)
            {
                double a0 = i * Math.PI * 2 / n, a1 = (i + 1) * Math.PI * 2 / n;
                r.Line3D(c, fr, s, (b.X + Math.Cos(a0) * rad, b.Y + 0.02, b.Z + Math.Sin(a0) * rad), (b.X + Math.Cos(a1) * rad, b.Y + 0.02, b.Z + Math.Sin(a1) * rad),
                    0xff9f43, hiddenDots: false, alpha: 0.55f);
            }
        }
    }

    // ------------------------------------------------------------------ labels

    private static string Pos(double x, double y, double z) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Floor(x)},{(int)Math.Floor(y)},{(int)Math.Floor(z)}");

    private static List<Cand> Candidates(EyeScene s, Input a)
    {
        var list = new List<Cand>();
        var f = a.Focus;
        list.Add(new Cand(0, f.X + 0.5, f.Y + 1.0, f.Z + 0.5, Pos(f.X, f.Y, f.Z), FocusC, true, $"focus {Pos(f.X, f.Y, f.Z)} {Blocks.Get(s.World.Block(f.X, f.Y, f.Z)).Name}"));
        foreach (var sp in a.Spots)
            list.Add(new Cand(0, sp.X + 0.5, sp.Y + 1.0, sp.Z + 0.5, Pos(sp.X, sp.Y, sp.Z), Gold, true, $"spot {Pos(sp.X, sp.Y, sp.Z)}"));
        foreach (var e in s.Entities)
        {
            if (e.Kind != "item" && s.IsViewerBody(e)) continue; // the camera's own body
            if (EntityCandidate(e) is { } cand) list.Add(cand);
        }
        if (a.Slab is { Roles: not null } slab) GenCellCandidates(slab, list);
        foreach (var m in s.Marks)
            if (MarkCandidate(s, m) is { } cand) list.Add(cand);
        return list;
    }

    /// <summary>A name tag (players, bots), a mob's name, or a drop's label; null for other kinds.</summary>
    private static Cand? EntityCandidate(VisionEntity e)
    {
        switch (e.Kind)
        {
            case "player" or "hive" or "self": return NameCandidate(e);
            case "mob": return MobCandidate(e);
            case "item": return new Cand(2, e.X, e.Y + 0.45, e.Z, EyeRenderer.DropLabel(e), 0xf2e46a, false, $"drop {EyeRenderer.DropLabel(e)} {Pos(e.X, e.Y, e.Z)}");
            default: return null;
        }
    }

    private static Cand NameCandidate(VisionEntity e)
    {
        var name = string.IsNullOrEmpty(e.Name) ? e.Kind : e.Name!.Length > 14 ? e.Name[..14] : e.Name;
        return new Cand(1, e.X, e.Y + (e.Sneaking ? 2.0 : 2.15), e.Z, name, e.Kind is "hive" or "self" ? 0xff9f43 : VisionRenderer.PlayerColour(e.Name), true,
            $"{(e.Kind == "player" ? "player" : "bot")} {e.Name} {Pos(e.X, e.Y, e.Z)}");
    }

    private static Cand MobCandidate(VisionEntity e)
    {
        var name = (e.Mob ?? e.Name ?? "mob").Replace('_', ' ');
        var model = EntityGeometry.Create(EntityGeometry.ModelKind(e));
        return new Cand(e.Burning ? 2 : 3, e.X, e.Y + model.Height + 0.3, e.Z, e.Burning ? name + " BURNING" : name, e.Burning ? 0xff7a1au : 0xff5a5au, false,
            $"{name}{(e.Burning ? " burning" : "")} {Pos(e.X, e.Y, e.Z)}");
    }

    /// <summary>The gen cells in the slab, numbered G1, G2... in scan order.</summary>
    private static void GenCellCandidates(VisionRenderer.Slab slab, List<Cand> list)
    {
        int g = 0;
        for (int iy = 0; iy < slab.Sy; iy++)
            for (int iz = 0; iz < slab.N; iz++)
                for (int ix = 0; ix < slab.N; ix++)
                {
                    int x = slab.X0 + ix, y = slab.Y0 + iy, z = slab.Z0 + iz;
                    if (slab.RoleAt(x, y, z) != BlockRole.GenCell) continue;
                    g++;
                    list.Add(new Cand(2, x + 0.5, y + 1.0, z + 0.5, "G" + g, BlockRoles.Colour(BlockRole.GenCell), false, $"gen cell G{g} {Pos(x, y, z)}"));
                }
    }

    private static string FurnaceState(int id, string label) =>
        id == Blocks.LitFurnace ? " LIT" : id == Blocks.Furnace && label.Contains("FURNACE", StringComparison.OrdinalIgnoreCase) ? " OFF" : "";

    /// <summary>A mark's short label (furnaces say LIT / OFF; stations rank above other marks), null when it has no text.</summary>
    private static Cand? MarkCandidate(EyeScene s, VisionMark m)
    {
        var t = VisionRenderer.ShortLabel(m.Text);
        if (t.Length == 0) return null;
        int id = s.World.Block(m.MinX, m.MinY, m.MinZ);
        t += FurnaceState(id, t);
        bool station = id is Blocks.Chest or Blocks.Furnace or Blocks.LitFurnace or Blocks.Workbench or Blocks.Dispenser;
        return new Cand(station ? 4 : 5, m.CentreX + 0.5, m.MaxY + (m.Box ? 1.2 : 1.1), m.CentreZ + 0.5, t, m.Box ? Gold : 0xe8ecf4, false,
            $"{t.ToLowerInvariant()} {m.MinX},{m.MinY},{m.MinZ}");
    }
    /// <summary>Places labels by priority near their anchors (stacked when crowded, a leader line when moved off the anchor),
    /// skipping anchors behind walls (players are kept, dimmed), at most <see cref="MaxLabels"/>.</summary>
    private static void Place(EyeRenderer r, Canvas c, EyeRenderer.Frame fr, EyeScene s, List<Cand> cands, VisionRenderer.Placer placer, List<string> facts)
    {
        int placed = 0;
        var crosshairs = new List<(int X, int Y, uint C)>();
        foreach (var k in cands.OrderBy(k => k.Priority).ThenBy(k => (k.X - s.EyeX) * (k.X - s.EyeX) + (k.Z - s.EyeZ) * (k.Z - s.EyeZ)))
        {
            if (placed >= MaxLabels) break;
            if (!PlaceLabel(r, c, fr, s, k, placer, crosshairs)) continue;
            placed++;
            if (k.Fact != null) facts.Add(k.Fact);
        }
        // Crosshairs last: no chip may cover the exact pixel of a key block.
        foreach (var (x, y, col) in crosshairs) Crosshair(c, x, y, col);
    }

    /// <summary>One label: skipped when off-picture or hidden (unless kept, then marked BEHIND); a key block's anchor queues a
    /// crosshair; the chip goes near the anchor with a leader line when it had to move. False when nothing was drawn.</summary>
    private static bool PlaceLabel(EyeRenderer r, Canvas c, EyeRenderer.Frame fr, EyeScene s, Cand k, VisionRenderer.Placer placer, List<(int X, int Y, uint C)> crosshairs)
    {
        if (r.Project(s, k.X, k.Y, k.Z) is not { } p) return false;
        bool visible = fr.Depth[p.Y * fr.Width + p.X] >= p.Dist - 0.7;
        if (!visible && !k.KeepOccluded) return false;
        string text = visible ? k.Text : k.Text + " (BEHIND)";
        int w = PixelFont.Width(text) + 4, h = 11;
        if (k.Priority == 0) crosshairs.Add((p.X, p.Y, k.Colour));
        var at = placer.FindNear(p.X - w / 2, p.Y - h - 3, w, h) ?? placer.Find(p.X - 2, p.Y - 2, 4, 4, w, h);
        if (at is not { } q) return false;
        LabelChip(c, p.X, p.Y, q.X, q.Y, w, h, text, visible ? k.Colour : Dim);
        return true;
    }

    /// <summary>The chip at (qx, qy), with a leader line from the anchor when the chip sits more than 6 px away.</summary>
    private static void LabelChip(Canvas c, int px, int py, int qx, int qy, int w, int h, string text, uint colour)
    {
        int cxl = Math.Clamp(px, qx, qx + w - 1), cyl = py < qy ? qy : qy + h - 1;
        if (Math.Abs(cxl - px) + Math.Abs(cyl - py) > 6) c.Line(px, py, cxl, cyl, colour);
        c.Chip(qx, qy, text, colour);
    }

    /// <summary>A target crosshair (a ring with ticks) at a pixel.</summary>
    public static void Crosshair(Canvas c, int x, int y, uint col)
    {
        c.Ring(x, y, 4, 0x0b0d12);
        c.Ring(x, y, 3, col);
        c.Line(x - 7, y, x - 5, y, col); c.Line(x + 5, y, x + 7, y, col);
        c.Line(x, y - 7, x, y - 5, col); c.Line(x, y + 5, x, y + 7, col);
        c.Set(x, y, col);
    }

    // ------------------------------------------------------------------ gizmo

    /// <summary>Axes gizmo for the camera: N/E/S/W on the projected horizontal directions (+x = E, +z = S) and an up tick.</summary>
    public static void Gizmo(Canvas c, EyeScene s, int cx, int cy, int r = 11)
    {
        c.Disc(cx, cy, r + 2, 0x101318);
        double yaw = s.Yaw * Math.PI / 180, pitch = Math.Clamp(s.Pitch, -89f, 89f) * Math.PI / 180;
        double fx = -Math.Sin(yaw), fz = Math.Cos(yaw), rx = -fz, rz = fx;
        (int X, int Y) Dir(double wx, double wz, double len)
        {
            double sx = wx * rx + wz * rz, sy = (wx * fx + wz * fz) * Math.Sin(pitch); // forward foreshortened by the pitch
            double l = Math.Sqrt(sx * sx + sy * sy);
            if (l < 1e-6) return (cx, cy);
            double k = len * Math.Max(0.35, l) / l;
            return (cx + (int)Math.Round(sx * k), cy - (int)Math.Round(sy * k));
        }
        foreach (var (wx, wz, label, col) in new[] { (0.0, -1.0, "N", 0xff5a5aU), (1.0, 0.0, "E", 0xe8ecf4U), (0.0, 1.0, "S", 0xe8ecf4U), (-1.0, 0.0, "W", 0xe8ecf4U) })
        {
            var (tx, ty) = Dir(wx, wz, r - 3);
            c.Line(cx, cy, tx, ty, label == "N" ? 0xff5a5au : label == "E" ? 0xff9a9au : label == "S" ? 0x7fb8ffu : 0x707888u);
            var (lx, ly) = Dir(wx, wz, r + 1);
            c.Text(lx - 2, ly - 3, label, col);
        }
        // Up: straight up on screen, shorter when looking down.
        int up = (int)Math.Round((r - 4) * Math.Cos(pitch));
        c.Line(cx, cy, cx, cy - up, 0x6bff8f);
    }
}
