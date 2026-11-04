using MineClaw.World;

namespace MineClaw.App.Supervisor.Admin;

/// <summary>One body as the restart check sees it (block position of its feet).</summary>
public sealed record SafetyBody(string Name, bool Online, int Dim, double X, double Y, double Z, bool HasPos = true);

public sealed record RestartBodyCheck(string Name, int? Dim, double? Y, bool Exposed, bool HighBuild, int? Drop, string? Reason);

public sealed record RestartSafetyResult(bool Safe, DateTimeOffset CheckedAt, bool? Night, int? TimeOfDay, IReadOnlyList<string> Reasons, IReadOnlyList<RestartBodyCheck> Bodies);

/// <summary>What a restart check reads from the running bot (tests fake it).</summary>
public interface IRestartSafetyProbe
{
    RestartSafetyResult Check();
}

/// <summary>
/// Never restart while a body is exposed (in the overworld at night under open sky: it logs out where it stands and logs back in
/// to mobs) or on a high build (at a ledge over a long drop: a desync on rejoin can drop it). Judged from the world time and each
/// body's position in main's world: the column above its head and the drop beside its feet. A body in another dimension than
/// main's world, or over unloaded chunks, is not judged (it is listed without a reason).
/// </summary>
public static class RestartSafety
{
    /// <summary>A ledge counts as a high build from this many blocks of air below the body's feet level.</summary>
    public const int HighDrop = 8;
    private const int MaxScan = 64;

    public static RestartSafetyResult Evaluate(DateTimeOffset now, long? timeOfDay, int worldDim, IEnumerable<SafetyBody> bodies,
        Func<int, int, int, int> blockAt, Func<int, int, bool> isLoaded, int worldHeight)
    {
        bool? night = timeOfDay is { } t ? t is >= 13000 and < 23000 : null;
        var checks = new List<RestartBodyCheck>();
        var reasons = new List<string>();
        foreach (var b in bodies)
        {
            var check = Check(b, night, worldDim, blockAt, isLoaded, worldHeight);
            if (check.Reason != null) reasons.Add(check.Reason);
            checks.Add(check);
        }
        return new RestartSafetyResult(reasons.Count == 0, now, night, timeOfDay is { } tod ? (int)tod : null, reasons, checks);
    }

    /// <summary>One body: unknown (offline, no position, another dimension, an unloaded chunk), else exposed at night and/or at a high ledge.</summary>
    private static RestartBodyCheck Check(SafetyBody b, bool? night, int worldDim, Func<int, int, int, int> blockAt, Func<int, int, bool> isLoaded, int worldHeight)
    {
        if (!b.Online || !b.HasPos) return new RestartBodyCheck(b.Name, null, null, false, false, null, null);
        int fx = (int)Math.Floor(b.X), fy = (int)Math.Floor(b.Y), fz = (int)Math.Floor(b.Z);
        if (b.Dim != worldDim || !isLoaded(fx, fz)) return new RestartBodyCheck(b.Name, b.Dim, b.Y, false, false, null, null);
        bool exposed = night == true && b.Dim == 0 && OpenSky(fx, fy, fz, blockAt, worldHeight);
        int drop = DeepestDrop(fx, fy, fz, blockAt, isLoaded);
        bool high = drop >= HighDrop;
        return new RestartBodyCheck(b.Name, b.Dim, Math.Round(b.Y, 1), exposed, high, drop, Why(b.Name, exposed, high, drop));
    }

    private static string? Why(string name, bool exposed, bool high, int drop) =>
        exposed && high ? $"{name} is outside at night at a ledge over a {Drop(drop)} drop"
        : exposed ? $"{name} is outside at night under open sky"
        : high ? $"{name} is at a ledge over a {Drop(drop)} drop"
        : null;

    /// <summary>The most air below the feet level in the loaded 3×3 columns around the body.</summary>
    private static int DeepestDrop(int fx, int fy, int fz, Func<int, int, int, int> blockAt, Func<int, int, bool> isLoaded)
    {
        int drop = 0;
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (!isLoaded(fx + dx, fz + dz)) continue;
                drop = Math.Max(drop, AirBelow(fx + dx, fy, fz + dz, blockAt));
            }
        return drop;
    }

    private static string Drop(int d) => d >= MaxScan ? "long (void)" : $"{d}-block";

    /// <summary>Nothing but air from above the head to the top of the world.</summary>
    private static bool OpenSky(int x, int feetY, int z, Func<int, int, int, int> blockAt, int height)
    {
        for (int y = Math.Max(0, feetY + 2); y < height; y++)
            if (blockAt(x, y, z) != 0) return false;
        return true;
    }

    /// <summary>Air blocks straight down from just below the feet (capped at <see cref="MaxScan"/>; the void counts as air).</summary>
    private static int AirBelow(int x, int feetY, int z, Func<int, int, int, int> blockAt)
    {
        int n = 0;
        for (int y = feetY - 1; n < MaxScan; y--, n++)
        {
            if (y < 0) return MaxScan;
            if (blockAt(x, y, z) != 0) break;
        }
        return n;
    }
}

/// <summary>The live check: main's world (time, blocks) and every body this process controls.</summary>
public sealed class LiveRestartProbe(Func<WorldState?> world, Func<bool> inGame, Func<IEnumerable<SafetyBody>> bodies, TimeProvider? time = null) : IRestartSafetyProbe
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public RestartSafetyResult Check()
    {
        var w = inGame() ? world() : null;
        if (w == null)
            return new RestartSafetyResult(true, _time.GetUtcNow(), null, null, [], bodies().Select(b => new RestartBodyCheck(b.Name, null, null, false, false, null, null)).ToList());
        return RestartSafety.Evaluate(_time.GetUtcNow(), w.TimeOfDay, w.Dimension, bodies(), w.GetBlock, w.IsLoaded, w.WorldHeight);
    }
}
