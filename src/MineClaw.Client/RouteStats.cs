using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// The shape of a planned route, process-wide counters for the supervisor's once-a-minute perf line. Live 11-04 story-1 (Fox: "it makes
/// the pawn go way out of the way underground"): a route is a <b>detour</b> when it is more than <see cref="DetourFactor"/> times the
/// straight line from start to end (and at least <see cref="MinDetourExtra"/> blocks longer), or when it dips more than
/// <see cref="MaxDip"/> blocks below both its start and its end (a walk round through a cave).
/// </summary>
public static class RouteStats
{
    public const double DetourFactor = 3;
    public const double MinDetourExtra = 10;
    public const int MaxDip = 4;

    /// <summary>One route: its walked length (blocks, step to step), the straight line start → end, the lowest and the start/end feet y.</summary>
    public readonly record struct Shape(double Length, double Straight, int MinY, int StartY, int EndY, int Steps)
    {
        public int Dip => Math.Min(StartY, EndY) - MinY;

        public bool IsDetour =>
            Straight >= 1 && Length > DetourFactor * Straight && Length - Straight >= MinDetourExtra || Dip > MaxDip;

        public override string ToString() =>
            $"route {Length:0} blocks for {Straight:0} straight, {Steps} steps, lowest y {MinY} (start {StartY}, end {EndY}"
            + (Dip > 0 ? $", dips {Dip}" : "") + ")";
    }

    /// <summary>The shape of <paramref name="steps"/> walked from <paramref name="start"/>.</summary>
    public static Shape Of(Vec3d start, IReadOnlyList<PathStep> steps)
    {
        double len = 0;
        double px = start.X, py = start.Y, pz = start.Z;
        int startY = (int)Math.Floor(start.Y + 1e-6), minY = startY;
        foreach (var s in steps)
        {
            double x = s.X + 0.5, y = s.Y, z = s.Z + 0.5;
            len += Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py) + (z - pz) * (z - pz));
            (px, py, pz) = (x, y, z);
            minY = Math.Min(minY, s.Y);
        }
        int endY = steps.Count > 0 ? steps[^1].Y : startY;
        double straight = Math.Sqrt((px - start.X) * (px - start.X) + (py - start.Y) * (py - start.Y) + (pz - start.Z) * (pz - start.Z));
        return new Shape(len, straight, minY, startY, endY, steps.Count);
    }

    private static long _routes, _detours, _dugShort;
    private static string? _worst;
    private static double _worstRatio;
    private static readonly object WorstLock = new();

    /// <summary>Counts a planned route (and remembers the worst detour since the last <see cref="Take"/>).</summary>
    public static void Note(Shape s, string? label)
    {
        Interlocked.Increment(ref _routes);
        if (!s.IsDetour) return;
        Interlocked.Increment(ref _detours);
        double ratio = s.Length / Math.Max(1, s.Straight) + s.Dip;
        lock (WorstLock)
            if (ratio > _worstRatio) { _worstRatio = ratio; _worst = (label is null ? "" : label + ": ") + s; }
    }

    /// <summary>A detour replaced by a shorter route dug through the ground.</summary>
    public static void NoteDugShort() => Interlocked.Increment(ref _dugShort);

    /// <summary>"routes 120, detours 3 (1 dug short; worst chop: route 64 blocks for 12 straight …)" since the last call, or null when no
    /// route was planned; starts over.</summary>
    public static string? Take()
    {
        long routes = Interlocked.Exchange(ref _routes, 0), detours = Interlocked.Exchange(ref _detours, 0), dug = Interlocked.Exchange(ref _dugShort, 0);
        string? worst;
        lock (WorstLock) { worst = _worst; _worst = null; _worstRatio = 0; }
        if (routes == 0) return null;
        return $"routes {routes}, detours {detours}" + (detours > 0 ? $" ({dug} dug short; worst {worst})" : "");
    }
}
