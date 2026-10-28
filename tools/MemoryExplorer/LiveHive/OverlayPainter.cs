using MineClaw.Render;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>Which overlays the free camera draws.</summary>
[Flags]
public enum Overlay
{
    None = 0,
    Bots = 1,
    Claims = 2,
    Places = 4,
    Chests = 8,
    Plans = 16,
    Dangers = 32,
    Staleness = 64,
    Default = Bots | Claims | Places | Chests | Plans | Dangers | Staleness,
}

/// <summary>A text label for the control to draw crisply over the frame (render-pixel coords).</summary>
public readonly record struct OverlayLabel(string Text, float X, float Y, uint Colour, bool Occluded, float Dist);

/// <summary>
/// Draws the free camera's overlays into a rasterized frame, depth-tested against the raycaster's depth buffer: claim boxes,
/// structure/place boxes, chests and gens, build-plan ghost blocks, danger boxes, and the staleness tint (columns not seen
/// recently shift toward blue-grey by age). Text comes back as <see cref="OverlayLabel"/>s for the UI to draw on top.
/// </summary>
public static class OverlayPainter
{
    public const uint ClaimColour = 0xffd84a, PlaceColour = 0xe8ecf4, WaypointColour = 0x6ad8ff, ChestColour = 0xffa040, GenColour = 0xc58aff,
        FarmColour = 0x8ad86a, PortalColour = 0xd070ff, PlanColour = 0x5ee6e6, PlanDoneColour = 0x5ee68a, DangerColour = 0xff5050, BotColour = 0xff9f43,
        PlayerColour = 0x9ad0ff, StaleColour = 0x6b7a99;

    public static List<OverlayLabel> Paint(EyeRenderer.Frame fr, ViewProjector cam, Overlay flags, OverlaysDto? ov, IReadOnlyList<BotDto> bots,
        IReadOnlyList<EntityDto> ents, HiveVoxels vox, DateTimeOffset now, double maxDist)
    {
        var labels = new List<OverlayLabel>();
        var p = new Painter(fr, cam, maxDist);
        int dim = vox.Dim;
        if (flags.HasFlag(Overlay.Staleness)) StaleTint(fr, cam, vox, now);
        if (ov != null && ov.Dim == dim)
        {
            if (flags.HasFlag(Overlay.Plans))
            {
                int drawn = 0;
                foreach (var plan in ov.Plans)
                    foreach (var b in plan.Blocks)
                    {
                        if (b.Length < 6 || drawn > 4000) continue;
                        if (Math.Abs(b[0] + 0.5 - cam.Ox) + Math.Abs(b[2] + 0.5 - cam.Oz) > maxDist * 1.5) continue;
                        bool done = b[5] != 0;
                        if (done && vox.Block(b[0], b[1], b[2]) == b[3]) continue; // built as planned: nothing to show
                        p.Box(b[0] + 0.03, b[1] + 0.03, b[2] + 0.03, b[0] + 0.97, b[1] + 0.97, b[2] + 0.97, done ? PlanDoneColour : PlanColour, 0.25f);
                        drawn++;
                    }
                foreach (var plan in ov.Plans.Where(pl => pl.Blocks.Count > 0))
                {
                    var first = plan.Blocks[0];
                    Label($"plan #{plan.Id} {plan.Name} ({plan.Status})", first[0] + 0.5, first[1] + 1.6, first[2] + 0.5, PlanColour, false);
                }
            }
            if (flags.HasFlag(Overlay.Claims))
                foreach (var c in ov.Claims) MarkBox(c, ClaimColour, $"{Short(c.Owner)}: {c.Text}", 0.0f, true);
            if (flags.HasFlag(Overlay.Places))
                foreach (var m in ov.Places) MarkBox(m, m.Kind == "waypoint" ? WaypointColour : PlaceColour, m.Text, 0.0f, true);
            if (flags.HasFlag(Overlay.Chests))
                foreach (var m in ov.Points)
                {
                    var (col, label) = m.Kind switch
                    {
                        "chest" => (ChestColour, false),
                        "cobble_generator" => (GenColour, true),
                        "farm" => (FarmColour, true),
                        "portal" => (PortalColour, true),
                        "key" => (GenColour, true),
                        "island" => (0u, true),
                        _ => (PlaceColour, false),
                    };
                    if (m.Kind == "island") { Label(m.Text, Mid(m, 0), m.Max[1] + 2.5, Mid(m, 2), PlaceColour, false); continue; }
                    MarkBox(m, col, m.Kind == "key" ? m.Text : m.Kind.Replace('_', ' '), 0.02f, label);
                }
            if (flags.HasFlag(Overlay.Dangers))
                foreach (var d in ov.Dangers) MarkBox(d, DangerColour, "! " + d.Text, -0.05f, true, xray: true);
        }
        if (flags.HasFlag(Overlay.Bots))
        {
            foreach (var b in bots.Where(b => b.Dim == dim))
                Label(b.Main ? "main" : b.Short, b.X, b.Y + 2.2, b.Z, BotColour, true);
            foreach (var e in ents.Where(e => e.Dim == dim && e.Kind == "player" && e.Name != null))
                Label(e.Name!, e.X, e.Y + 2.2, e.Z, PlayerColour, true);
        }
        return labels;

        void MarkBox(MarkDto m, uint colour, string text, float inset, bool label, bool xray = false)
        {
            if (m.Min.Length < 3 || m.Max.Length < 3) return;
            double x0 = Math.Min(m.Min[0], m.Max[0]) + inset, y0 = Math.Min(m.Min[1], m.Max[1]) + inset, z0 = Math.Min(m.Min[2], m.Max[2]) + inset;
            double x1 = Math.Max(m.Min[0], m.Max[0]) + 1 - inset, y1 = Math.Max(m.Min[1], m.Max[1]) + 1 - inset, z1 = Math.Max(m.Min[2], m.Max[2]) + 1 - inset;
            if (Math.Max(Math.Abs((x0 + x1) / 2 - cam.Ox), Math.Abs((z0 + z1) / 2 - cam.Oz)) > maxDist + Math.Max(x1 - x0, z1 - z0)) return;
            p.Box(x0, y0, z0, x1, y1, z1, colour, xray ? 0.45f : 0f);
            if (label && text.Length > 0) Label(text, (x0 + x1) / 2, y1 + 0.6, (z0 + z1) / 2, colour, xray);
        }

        void Label(string text, double x, double y, double z, uint colour, bool always)
        {
            if (!cam.Project(x, y, z, out var sx, out var sy, out var dist) || dist > maxDist * 1.25) return;
            if (sx < -40 || sy < -20 || sx > cam.W + 40 || sy > cam.H + 20) return;
            int ix = Math.Clamp((int)sx, 0, cam.W - 1), iy = Math.Clamp((int)sy, 0, cam.H - 1);
            bool occluded = fr.Depth[iy * fr.Width + ix] < dist - 1.0;
            if (occluded && !always) return;
            labels.Add(new OverlayLabel(text.Length > 40 ? text[..39] + "…" : text, (float)sx, (float)sy, colour, occluded, (float)dist));
        }
    }

    private static double Mid(MarkDto m, int a) => (m.Min[a] + m.Max[a]) / 2.0 + 0.5;

    private static string Short(string? owner)
    {
        if (string.IsNullOrEmpty(owner)) return "?";
        int i = owner.LastIndexOf('_');
        return i >= 0 && i < owner.Length - 1 && owner[(i + 1)..].All(char.IsDigit) ? owner[(i + 1)..] : owner;
    }

    /// <summary>Tints each pixel whose hit column was last seen long ago (1 min → faint, 1 h+ → strong).</summary>
    public static void StaleTint(EyeRenderer.Frame fr, ViewProjector cam, HiveVoxels vox, DateTimeOffset now)
    {
        long nowMs = now.ToUnixTimeMilliseconds();
        int W = fr.Width, H = fr.Height;
        // One worker per ~64 rows; each reads the voxel view's prepared array only (no writes), so sharing it is safe.
        Parallel.For(0, (H + 63) / 64, band =>
        {
            int ccx = int.MinValue, ccz = int.MinValue;
            float a = 0;
            for (int y = band * 64; y < Math.Min(H, band * 64 + 64); y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    float d = fr.Depth[i];
                    if (float.IsInfinity(d)) continue;
                    var (dx, dy, dz) = cam.Ray(x, y);
                    int hx = (int)Math.Floor(cam.Ox + dx * (d + 0.01)), hz = (int)Math.Floor(cam.Oz + dz * (d + 0.01));
                    if (hx >> 4 != ccx || hz >> 4 != ccz)
                    {
                        ccx = hx >> 4; ccz = hz >> 4;
                        a = vox.Column(hx, hz) is { } c ? AgeAlpha(nowMs - c.SeenMs) : 0;
                    }
                    if (a > 0) fr.Px[i] = IsoPalette.Mix(fr.Px[i], StaleColour, a);
                }
        });
    }

    /// <summary>0 under a minute, then a log ramp to 0.42 at a day.</summary>
    public static float AgeAlpha(long ageMs)
    {
        if (ageMs < 60_000) return 0;
        double minutes = ageMs / 60_000.0;
        return (float)Math.Clamp(0.08 + 0.34 * Math.Log10(minutes) / Math.Log10(1440), 0.08, 0.42);
    }

    /// <summary>Depth-tested line drawing into the frame (Liang–Barsky screen clip, near-plane clip in 3D).</summary>
    private readonly struct Painter(EyeRenderer.Frame fr, ViewProjector cam, double maxDist)
    {
        public void Box(double x0, double y0, double z0, double x1, double y1, double z1, uint c, float hiddenAlpha)
        {
            Line(x0, y0, z0, x1, y0, z0, c, hiddenAlpha); Line(x0, y1, z0, x1, y1, z0, c, hiddenAlpha);
            Line(x0, y0, z1, x1, y0, z1, c, hiddenAlpha); Line(x0, y1, z1, x1, y1, z1, c, hiddenAlpha);
            Line(x0, y0, z0, x0, y1, z0, c, hiddenAlpha); Line(x1, y0, z0, x1, y1, z0, c, hiddenAlpha);
            Line(x0, y0, z1, x0, y1, z1, c, hiddenAlpha); Line(x1, y0, z1, x1, y1, z1, c, hiddenAlpha);
            Line(x0, y0, z0, x0, y0, z1, c, hiddenAlpha); Line(x1, y0, z0, x1, y0, z1, c, hiddenAlpha);
            Line(x0, y1, z0, x0, y1, z1, c, hiddenAlpha); Line(x1, y1, z0, x1, y1, z1, c, hiddenAlpha);
        }

        public void Line(double ax, double ay, double az, double bx, double by, double bz, uint c, float hiddenAlpha)
        {
            const double near = 0.1;
            double fa = cam.Forward(ax, ay, az), fb = cam.Forward(bx, by, bz);
            if (fa < near && fb < near) return;
            if (fa < near) { double t = (near - fa) / (fb - fa); ax += (bx - ax) * t; ay += (by - ay) * t; az += (bz - az) * t; }
            else if (fb < near) { double t = (near - fb) / (fa - fb); bx += (ax - bx) * t; by += (ay - by) * t; bz += (az - bz) * t; }
            if (!cam.Project(ax, ay, az, out var sx0, out var sy0, out var d0) || !cam.Project(bx, by, bz, out var sx1, out var sy1, out var d1)) return;
            if (Math.Min(d0, d1) > maxDist) return;
            // Clip to the screen.
            double t0 = 0, t1 = 1, ddx = sx1 - sx0, ddy = sy1 - sy0;
            if (!Clip(-ddx, sx0, ref t0, ref t1) || !Clip(ddx, cam.W - 1 - sx0, ref t0, ref t1)
                || !Clip(-ddy, sy0, ref t0, ref t1) || !Clip(ddy, cam.H - 1 - sy0, ref t0, ref t1)) return;
            double px0 = sx0 + ddx * t0, py0 = sy0 + ddy * t0, px1 = sx0 + ddx * t1, py1 = sy0 + ddy * t1;
            double e0 = d0 + (d1 - d0) * t0, e1 = d0 + (d1 - d0) * t1;
            int steps = (int)Math.Ceiling(Math.Max(Math.Abs(px1 - px0), Math.Abs(py1 - py0)));
            if (steps > 4096) return;
            int W = fr.Width;
            for (int s = 0; s <= steps; s++)
            {
                double k = steps == 0 ? 0 : (double)s / steps;
                int x = (int)(px0 + (px1 - px0) * k), y = (int)(py0 + (py1 - py0) * k);
                if ((uint)x >= (uint)fr.Width || (uint)y >= (uint)fr.Height) continue;
                double d = e0 + (e1 - e0) * k;
                int i = y * W + x;
                if (fr.Depth[i] >= d - 0.3) fr.Px[i] = c;
                else if (hiddenAlpha > 0) fr.Px[i] = IsoPalette.Mix(fr.Px[i], c, hiddenAlpha);
            }
        }

        private static bool Clip(double p, double q, ref double t0, ref double t1)
        {
            if (Math.Abs(p) < 1e-12) return q >= 0;
            double r = q / p;
            if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
            return true;
        }
    }
}
