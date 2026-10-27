using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Memory;

/// <summary>
/// Cheap, zero-token relational hints ("2 E of cobble_gen", "by a furnace", "kiwi 3 W of you") that ride next to precise
/// coordinates in tool results, so the model can talk like a player instead of reading coordinates out.
/// Minecraft axes: -Z is north, +Z south, +X east, -X west.
/// </summary>
public static class Landmarks
{
    private static readonly string[] Dirs = ["S", "SW", "W", "NW", "N", "NE", "E", "SE"];

    /// <summary>8-way compass direction of a horizontal offset (dx east, dz south); "" when (almost) zero.</summary>
    public static string Compass(double dx, double dz)
    {
        if (Math.Abs(dx) < 0.5 && Math.Abs(dz) < 0.5) return "";
        // atan2 with south = 0°, west = 90° (Minecraft yaw convention).
        double deg = Math.Atan2(-dx, dz) * 180 / Math.PI;
        int i = (int)Math.Round(((deg % 360) + 360) % 360 / 45.0) % 8;
        return Dirs[i];
    }

    /// <summary>"3 NE" for the offset from (fromX,fromZ) to (toX,toZ); "here" when within a block.</summary>
    public static string Offset(double fromX, double fromZ, double toX, double toZ)
    {
        double dx = toX - fromX, dz = toZ - fromZ;
        double d = Math.Sqrt(dx * dx + dz * dz);
        if (d < 1.5) return "here";
        return $"{Math.Round(d):0} {Compass(dx, dz)}";
    }

    /// <summary>
    /// Nearest named landmark (waypoint or structure from memory; else a known chest/furnace/... from the world index)
    /// phrased relative to the point: "at cobble_gen", "2 E of spawn", "by a furnace". Null when nothing is close.
    /// </summary>
    public static string? Near(MemoryStore? memory, ObservationStore? world, double x, double y, double z,
        double maxNamed = 24, double maxObject = 8, string? excludeName = null)
    {
        string? best = null;
        double bestD = double.MaxValue, bx = 0, bz = 0;
        if (memory != null)
        {
            var (wps, sts) = memory.Read(d => (d.Waypoints.ToList(), d.Structures.ToList()));
            foreach (var w in wps)
                Consider(w.Name, w.X + 0.5, w.Y, w.Z + 0.5);
            foreach (var s in sts)
            {
                if (s.Min == null) continue;
                var mx = s.Max ?? s.Min;
                Consider(s.Name, (s.Min.X + mx.X) / 2.0 + 0.5, (s.Min.Y + mx.Y) / 2.0, (s.Min.Z + mx.Z) / 2.0 + 0.5);
            }
        }
        if (best != null && bestD <= maxNamed)
        {
            var off = Offset(bx, bz, x, z);
            return off == "here" ? $"at {best}" : $"{off} of {best}";
        }

        if (world != null)
        {
            try
            {
                var hit = world.Nearest(null, x, y, z, 4, maxObject).FirstOrDefault(n => n.Row.Kind is not ("island" or "area" or "region"));
                if (hit.Row != null) return $"by a {hit.Row.Kind.Replace('_', ' ')}";
            }
            catch (Exception) { /* index busy or not ready: no hint */ }
        }
        return null;

        void Consider(string name, double lx, double ly, double lz)
        {
            if (excludeName != null && name.Equals(excludeName, StringComparison.OrdinalIgnoreCase)) return;
            if (Math.Abs(ly - y) > 12) return;
            double dx = lx - x, dz = lz - z, d = Math.Sqrt(dx * dx + dz * dz);
            if (d < bestD) { bestD = d; best = name; bx = lx; bz = lz; }
        }
    }
}
