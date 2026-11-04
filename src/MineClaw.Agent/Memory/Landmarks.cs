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
        if (memory != null && NearestNamed(memory, x, y, z, excludeName) is { Name: { } best } named && named.D <= maxNamed)
        {
            var off = Offset(named.X, named.Z, x, z);
            return off == "here" ? $"at {best}" : $"{off} of {best}";
        }
        return world != null ? NearObject(world, x, y, z, maxObject) : null;
    }

    /// <summary>The nearest waypoint or structure (centre) of this dimension, within 12 blocks up/down, by horizontal distance.</summary>
    private static NamedBest NearestNamed(MemoryStore memory, double x, double y, double z, string? excludeName)
    {
        var best = new NamedBest(x, y, z, excludeName);
        int dim = memory.CurrentDimension; // a landmark in the other dimension is never "near"
        var (wps, sts) = memory.Read(d => (d.Waypoints.Where(w => w.Dimension == dim).ToList(), d.Structures.Where(s => s.Dimension == dim).ToList()));
        foreach (var w in wps)
            best.Consider(w.Name, w.X + 0.5, w.Y, w.Z + 0.5);
        foreach (var s in sts)
        {
            if (s.Min == null) continue;
            var mx = s.Max ?? s.Min;
            best.Consider(s.Name, (s.Min.X + mx.X) / 2.0 + 0.5, (s.Min.Y + mx.Y) / 2.0, (s.Min.Z + mx.Z) / 2.0 + 0.5);
        }
        return best;
    }

    private sealed class NamedBest(double x, double y, double z, string? excludeName)
    {
        public string? Name;
        public double D = double.MaxValue, X, Z;

        public void Consider(string name, double lx, double ly, double lz)
        {
            if (excludeName != null && name.Equals(excludeName, StringComparison.OrdinalIgnoreCase)) return;
            if (Math.Abs(ly - y) > 12) return;
            double dx = lx - x, dz = lz - z, d = Math.Sqrt(dx * dx + dz * dz);
            if (d < D) { D = d; Name = name; X = lx; Z = lz; }
        }
    }

    /// <summary>"by a furnace": the nearest known object from the world index (not an island/area/region); null when none or busy.</summary>
    private static string? NearObject(ObservationStore world, double x, double y, double z, double maxObject)
    {
        try
        {
            var hit = world.Nearest(null, x, y, z, 4, maxObject).FirstOrDefault(n => n.Row.Kind is not ("island" or "area" or "region"));
            if (hit.Row == null) return null;
            // An unknown structure is somebody's build; "a"/"an" by the sound ("by a unknown structure", live 10-28).
            var kind = hit.Row.Kind == "unknown_structure" ? "player build" : hit.Row.Kind.Replace('_', ' ');
            return $"by {Article(kind)} {kind}";
        }
        catch (Exception) { return null; /* index busy or not ready: no hint */ }

        static string Article(string noun) => noun.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(noun[0])) ? "an" : "a";
    }
}
