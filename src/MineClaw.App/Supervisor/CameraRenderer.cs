using System.Globalization;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Bot.WorldIndex;
using MineClaw.World;

namespace MineClaw.App.Supervisor;

/// <summary>
/// The supervisor's free camera: a first-person frame from any place in any dimension, rendered from the shared world memory
/// (main's live columns + every hive bot's remembered <c>world_chunks</c>, <see cref="RegionWorld"/>) with per-side staleness.
/// The region is rebuilt only when the camera leaves its inner area or it is older than <see cref="RegionMaxAge"/>, so flying
/// around at a few fps costs one raycast per frame, not a db read. Not thread-safe: the render gate runs it on one thread.
/// </summary>
public sealed class CameraRenderer(Func<WorldState?> live, string liveName, Func<IWorldMemoryStore?> store, Func<TextureAtlas?> atlas,
    TimeProvider? time = null, int distance = 48, int maxWidth = 800, int maxHeight = 600)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<(int, int), EyeRenderer> _eyes = new();
    private (int Dim, int X, int Z, DateTimeOffset At, RegionWorld Region, WorldVoxels Voxels)? _region;

    public TimeSpan RegionMaxAge { get; init; } = TimeSpan.FromSeconds(8);
    public int Distance => Math.Clamp(distance, 16, 96);
    /// <summary>Region rebuilds so far (tests: flying inside the region doesn't re-read the db).</summary>
    public int Builds { get; private set; }

    public ViewImage Render(CameraQuery q, IReadOnlyList<VisionEntity>? entities = null, long worldTime = 6000)
    {
        int w = Math.Clamp(q.W, 64, Math.Max(64, maxWidth)), h = Math.Clamp(q.H, 48, Math.Max(48, maxHeight));
        var now = _time.GetUtcNow();
        var r = RegionAt(q, now);
        var img = EyeFor(w, h).Render(new EyeScene
        {
            World = r.Voxels, Atlas = atlas(), EyeX = q.X, EyeY = q.Y, EyeZ = q.Z, Yaw = q.Yaw, Pitch = q.Pitch, Time = worldTime,
            Nether = q.Dim == -1, Entities = entities ?? [], Viewer = "camera",
        });
        var at = ((int)Math.Floor(q.X), (int)Math.Floor(q.Y), (int)Math.Floor(q.Z));
        var seen = r.Region.Staleness(at, Distance / 2, now);
        var legend = string.Create(CultureInfo.InvariantCulture,
            $"free camera @{at.Item1},{at.Item2},{at.Item3} {(q.Dim == -1 ? "nether" : "overworld")} yaw {q.Yaw:0} pitch {q.Pitch:0}, {w}x{h}px ~{img.Tokens}tok; {img.Legend}");
        return new ViewImage(img.Png, img.Width, img.Height, img.Tokens, legend, [], seen);
    }

    /// <summary>The cached region, rebuilt when the camera left its inner area, changed dimension, or it got too old.</summary>
    private (int Dim, int X, int Z, DateTimeOffset At, RegionWorld Region, WorldVoxels Voxels) RegionAt(CameraQuery q, DateTimeOffset now)
    {
        int bx = (int)Math.Floor(q.X), bz = (int)Math.Floor(q.Z);
        int slack = Distance / 3;
        if (_region is not { } r || r.Dim != q.Dim || Math.Abs(r.X - bx) > slack || Math.Abs(r.Z - bz) > slack || now - r.At > RegionMaxAge)
        {
            var liveWorld = live() ?? new WorldState { Dimension = int.MinValue };
            var region = RegionWorld.Build(liveWorld, liveName, store(), q.Dim, bx, bz, Distance + slack, now);
            _region = r = (q.Dim, bx, bz, now, region, new WorldVoxels(region.World));
            Builds++;
        }
        return r;
    }

    /// <summary>One eye renderer per frame size (a few sizes kept).</summary>
    private EyeRenderer EyeFor(int w, int h)
    {
        if (!_eyes.TryGetValue((w, h), out var eye))
        {
            if (_eyes.Count > 6) _eyes.Clear();
            _eyes[(w, h)] = eye = new EyeRenderer { Width = w, Height = h, MaxDistance = Distance, MaxThreads = 1 };
        }
        return eye;
    }
}
