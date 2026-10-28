using System.Globalization;
using System.Text;

namespace MineClaw.Render;

/// <summary>
/// Optional camera adjustments of one non-eye panel (a <c>see</c> view like <c>"iso yaw=200 pitch=50 zoom=1.3"</c>, the Explorer's
/// compose sliders). Every field null = the view's fixed default camera, rendered by exactly the old code path (the golden
/// sheets don't move). Angles use the game's convention: <see cref="Yaw"/> is the way the camera FACES (b1.8.1/F3 yaw: 0 = south,
/// 90 = west, 180 = north, 270 = east; the default iso faces NE = 225), <see cref="Pitch"/> degrees down. <see cref="Zoom"/> &gt; 1
/// moves 3D cameras closer and narrows the orthographic TOP/SIDE window; <see cref="Dist"/> sets a 3D camera's distance outright;
/// <see cref="Dx"/>/<see cref="Dy"/>/<see cref="Dz"/> pan (blocks; TOP/SIDE move their window and clip box with it);
/// <see cref="Axis"/> picks the SIDE cut ("N" looks north, "E" looks east).
/// </summary>
public readonly record struct CamParams(double? Yaw = null, double? Pitch = null, double? Zoom = null, double? Dist = null,
    double? Dx = null, double? Dy = null, double? Dz = null, string? Axis = null)
{
    public static readonly IReadOnlySet<string> Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "yaw", "pitch", "zoom", "dist", "dx", "dy", "dz", "axis" };

    public bool IsEmpty => Yaw == null && Pitch == null && Zoom == null && Dist == null && Dx == null && Dy == null && Dz == null && Axis == null;

    /// <summary>Facing yaw normalised to [0, 360).</summary>
    public static double NormYaw(double y) => ((y % 360) + 360) % 360;
    public double ZoomOr1 => Math.Clamp(Zoom ?? 1, 0.25, 4);
    public double PitchClamped(double fallback) => Math.Clamp(Pitch ?? fallback, 5, 89);
    public double DxOr0 => Math.Clamp(Dx ?? 0, -32, 32);
    public double DyOr0 => Math.Clamp(Dy ?? 0, -32, 32);
    public double DzOr0 => Math.Clamp(Dz ?? 0, -32, 32);

    /// <summary>One <c>key=value</c> token (yaw=200, axis=E); false when it isn't a camera key.</summary>
    public static bool TryToken(string token, ref CamParams p)
    {
        int eq = token.IndexOf('=');
        if (eq <= 0) return false;
        var k = token[..eq].Trim().ToLowerInvariant();
        var v = token[(eq + 1)..].Trim();
        if (!Keys.Contains(k)) return false;
        if (k == "axis")
        {
            var a = v.ToUpperInvariant();
            if (a is not ("N" or "E")) return false;
            p = p with { Axis = a };
            return true;
        }
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d)) return false;
        p = k switch
        {
            "yaw" => p with { Yaw = d }, "pitch" => p with { Pitch = d }, "zoom" => p with { Zoom = d }, "dist" => p with { Dist = d },
            "dx" => p with { Dx = d }, "dy" => p with { Dy = d }, _ => p with { Dz = d },
        };
        return true;
    }

    /// <summary>Pulls every camera token out of <paramref name="words"/> (what stays is the view's spot/body words).</summary>
    public static CamParams Extract(List<string> words)
    {
        var p = new CamParams();
        for (int i = words.Count - 1; i >= 0; i--)
            if (TryToken(words[i], ref p)) words.RemoveAt(i);
        return p;
    }

    /// <summary>"yaw=200 pitch=50 zoom=1.3" (set fields only, fixed order, invariant): the <c>see</c> view suffix.</summary>
    public string Format()
    {
        var sb = new StringBuilder();
        void Add(string k, double? v)
        {
            if (v is not { } x) return;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(k).Append('=').Append(Math.Round(x, 2).ToString("0.##", CultureInfo.InvariantCulture));
        }
        Add("yaw", Yaw); Add("pitch", Pitch); Add("zoom", Zoom); Add("dist", Dist); Add("dx", Dx); Add("dy", Dy); Add("dz", Dz);
        if (Axis != null) sb.Append(sb.Length > 0 ? " " : "").Append("axis=").Append(Axis);
        return sb.ToString();
    }

    public override string ToString() => Format();
}

public static partial class PerspectivePanel
{
    /// <summary>The default camera of each orbit-style view as (facing yaw, pitch down, fov): what the params adjust.</summary>
    public static (double Yaw, double Pitch, float Fov)? OrbitDefaults(string view, int rotation = 0) => view switch
    {
        "orbit" => (315, 40, 50), "orbit2" => (135, 40, 50), "orbit3" => (45, 40, 50), "orbit4" => (225, 40, 50),
        "high" => (180, 65, 50),
        "iso" => ((rotation & 3) switch { 1 => 135, 2 => 45, 3 => 315, _ => 225 }, 35, 45),
        _ => null,
    };

    /// <summary>The camera of a named view with adjustments; empty params = <see cref="For(string, ValueTuple{int,int,int}, int, int, Body?, EyeScene?, int)"/>.</summary>
    public static Rig? For(string view, (int X, int Y, int Z) focus, int feetY, int radius, Body? body, EyeScene? eye, CamParams p, int rotation = 0)
    {
        if (p.IsEmpty) return For(view, focus, feetY, radius, body, eye, rotation);
        if (OrbitDefaults(view, rotation) is { } d)
        {
            string name = view switch { "iso" => "ISO", "high" => "HIGH", _ => "ORBIT" };
            return Orbiting(name, focus, feetY, radius, p.Yaw ?? d.Yaw, p.PitchClamped(d.Pitch), d.Fov, p);
        }
        if (view == "shoulder" && body is { } b && p.Dist is { } behind)
        {
            // The shoulder camera at another distance behind the body (the default is 3.4 blocks).
            double back = Math.Clamp(behind, 1, 16);
            double fx = focus.X + 0.5, fy = feetY + 0.5, fz = focus.Z + 0.5;
            double ex = b.X, ey = b.Y + 1.62, ez = b.Z, tx = fx - ex, tz = fz - ez;
            bool self = tx * tx + tz * tz < 2.5;
            double yawR = self ? b.Yaw * Math.PI / 180 : Math.Atan2(-tx, tz);
            double fwx = -Math.Sin(yawR), fwz = Math.Cos(yawR), rx = -fwz, rz = fwx;
            double cx = ex - fwx * back - rx * 0.9, cy = ey + 1.1 * back / 3.4, cz = ez - fwz * back - rz * 0.9;
            var (yaw, pitch) = self ? EyeScene.LookAt(cx, cy, cz, ex + fwx * 5, ey - 1.6, ez + fwz * 5) : EyeScene.LookAt(cx, cy, cz, fx, fy, fz);
            string dir = EyeRenderer.Compass(yaw);
            return new Rig(cx, cy, cz, yaw, pitch, 62f, $"SHOULDER >{dir} {back:0.#}m", $"SHOULDER (behind {(b.Name ?? "you")} {back:0.#} m, facing {dir})", false, (int)(48 + back));
        }
        return For(view, focus, feetY, radius, body, eye, rotation);
    }

    /// <summary>A 3D camera around the focus facing <paramref name="yaw"/> (game convention), <paramref name="pitch"/> degrees down,
    /// at the default framing distance / zoom (or <see cref="CamParams.Dist"/>), panned by dx/dy/dz.</summary>
    public static Rig Orbiting(string name, (int X, int Y, int Z) focus, int feetY, int radius, double yaw, double pitch, float fov, CamParams p)
    {
        yaw = CamParams.NormYaw(yaw);
        double fx = focus.X + 0.5 + p.DxOr0, fy = feetY + 0.5 + p.DyOr0, fz = focus.Z + 0.5 + p.DzOr0;
        double d0 = (radius + 1.5) / Math.Tan(fov * Math.PI / 360) * 1.05;
        double d = Math.Clamp(p.Dist ?? d0 / p.ZoomOr1, 3, 96);
        double h = d * Math.Cos(pitch * Math.PI / 180), v = d * Math.Sin(pitch * Math.PI / 180);
        double yr = yaw * Math.PI / 180, fwx = -Math.Sin(yr), fwz = Math.Cos(yr);
        double cx = fx - fwx * h, cy = fy + v, cz = fz - fwz * h;
        var (cyaw, cpitch) = EyeScene.LookAt(cx, cy, cz, fx, fy, fz);
        string from = Dir8(-fwx, -fwz), to = Dir8(fwx, fwz);
        bool compass = Math.Abs(yaw / 45 - Math.Round(yaw / 45)) < 1e-6;
        string title = compass ? $"{name} {from}>{to} {pitch:0}°" : string.Create(CultureInfo.InvariantCulture, $"{name} yaw{yaw:0} {pitch:0}°");
        if (p.ZoomOr1 != 1 || p.Dist != null) title += string.Create(CultureInfo.InvariantCulture, $" {d:0}m");
        var legend = string.Create(CultureInfo.InvariantCulture, $"{name} from {from} looking {to} (yaw {yaw:0}), {pitch:0} deg down, {d:0} m");
        return new Rig(cx, cy, cz, cyaw, cpitch, fov, title, legend, true, (int)(d + 2 * radius + 8));
    }

    /// <summary>TOP with zoom (narrower window) and a pan; empty params = <see cref="Top(ValueTuple{int,int,int}, int, int)"/>.</summary>
    public static Rig Top((int X, int Y, int Z) focus, int feetY, int radius, CamParams p)
    {
        var r = Top(focus, feetY, radius);
        if (p.IsEmpty) return r;
        double dx = p.DxOr0, dz = p.DzOr0;
        var c = r.Clip!.Value;
        int ox = (int)Math.Round(dx), oz = (int)Math.Round(dz);
        string zoom = p.ZoomOr1 != 1 ? string.Create(CultureInfo.InvariantCulture, $" x{p.ZoomOr1:0.##}") : "";
        string pan = dx != 0 || dz != 0 ? string.Create(CultureInfo.InvariantCulture, $" pan {dx:0.#},{dz:0.#}") : "";
        return r with
        {
            X = r.X + dx, Z = r.Z + dz, Ortho = r.Ortho / p.ZoomOr1, Title = r.Title + zoom,
            Legend = r.Legend[..^1] + zoom + pan + ")",
            Clip = new ClipBox(c.X0 + ox, c.Y0, c.Z0 + oz, c.X1 + ox, c.Y1, c.Z1 + oz),
        };
    }

    /// <summary>SIDE with an axis override, zoom and a pan (the cut moves with dx/dz); empty params = the default SIDE.</summary>
    public static Rig Side((int X, int Y, int Z) focus, int feetY, int radius, SideAxis axis, CamParams p)
    {
        if (p.Axis is "E") axis = SideAxis.LookEast;
        else if (p.Axis is "N") axis = SideAxis.LookNorth;
        var r = Side(focus, feetY, radius, axis);
        if (p.IsEmpty || p with { Axis = null } == default) return r;
        double dx = p.DxOr0, dy = p.DyOr0, dz = p.DzOr0;
        var c = r.Clip!.Value;
        int ox = (int)Math.Round(dx), oy = (int)Math.Round(dy), oz = (int)Math.Round(dz);
        string zoom = p.ZoomOr1 != 1 ? string.Create(CultureInfo.InvariantCulture, $" x{p.ZoomOr1:0.##}") : "";
        string pan = dx != 0 || dy != 0 || dz != 0 ? string.Create(CultureInfo.InvariantCulture, $" pan {dx:0.#},{dy:0.#},{dz:0.#}") : "";
        return r with
        {
            X = r.X + dx, Y = r.Y + dy, Z = r.Z + dz, Ortho = r.Ortho / p.ZoomOr1, Title = r.Title + zoom,
            Legend = r.Legend[..^1] + zoom + pan + ")",
            Clip = new ClipBox(c.X0 + ox, c.Y0 + oy, c.Z0 + oz, c.X1 + ox, c.Y1 + oy, c.Z1 + oz),
        };
    }

    /// <summary>A free camera with a pitch override (degrees above the spot) and zoom; empty params = <see cref="Cam"/>.</summary>
    public static Rig Cam((int X, int Y, int Z) focus, int feetY, string? from, double? dist, string caption, int radius, CamParams p)
    {
        if (p.IsEmpty) return Cam(focus, feetY, from, dist, caption, radius);
        var f = (from ?? "S").Trim().ToUpperInvariant();
        bool above = f is "ABOVE" or "UP" or "TOP";
        var (sx, sz) = f switch
        {
            "N" => (0, -1), "NE" => (1, -1), "E" => (1, 0), "SE" => (1, 1), "S" => (0, 1), "SW" => (-1, 1), "W" => (-1, 0), "NW" => (-1, -1), _ => (0, 1),
        };
        double len = Math.Sqrt(sx * sx + sz * sz);
        // Facing yaw from the compass side (camera there, looking back at the spot), unless yaw is given.
        double yaw = p.Yaw ?? CamParams.NormYaw(Math.Atan2(sx / len, -sz / len) * 180 / Math.PI);
        double pitch = p.PitchClamped(above ? 80 : 35);
        double d = Math.Clamp((dist ?? p.Dist ?? radius * 1.8 + 4) / p.ZoomOr1, 3, 64);
        var rig = Orbiting("CAM", focus, feetY, radius, yaw, pitch, 55, p with { Dist = d, Zoom = null });
        return rig with { Title = caption + " " + rig.Title[4..], Legend = "CAM " + caption + " " + rig.Legend[4..], MaxDistance = (int)(d + 2 * radius + 10) };
    }
}
