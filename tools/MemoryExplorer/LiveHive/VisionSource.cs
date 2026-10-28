using System.Diagnostics;
using System.Globalization;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.MemoryExplorer.LiveHive;

// Wire shapes of the Supervisor's vision routes (src/MineClaw.App/Supervisor/SupervisorVisionRoutes.cs; the record is
// MineClaw.Bot.Perception.Vision.VisionSheetInfo, snake_case JSON).

/// <summary>One picture a body's model received: when, what was asked, its cost, the perception answer.</summary>
public sealed record VisionSheetDto(long Id, DateTimeOffset At, string Body, string Kind, string Source, string Mode, List<string>? Views,
    int[]? Focus, int Radius, string? Question, string? Call, int Width, int Height, int Tokens, int Bytes, string Sha256, string Legend,
    string? Answer = null, string? Note = null, string? Model = null, long? ModelInputTokens = null, long? ModelOutputTokens = null);

/// <summary><c>/api/bodies/{name}/vision</c>.</summary>
public sealed record VisionListDto(DateTimeOffset At, string Body, int Capacity, string? Note, List<VisionSheetDto> Sheets);

/// <summary>A rendered sheet (the AI's, a local compose, or a server render).</summary>
public sealed record SheetPicture(byte[] Png, int Width, int Height, string Legend, double Ms, string Where);

/// <summary>Where the vision panel reads sheets and server renders (the Supervisor; tests use a fake).</summary>
public interface IVisionSource
{
    Task<VisionListDto> ListAsync(string body, long since, CancellationToken ct);
    Task<byte[]> PngAsync(string body, long id, CancellationToken ct);
    /// <summary>The server's render of a see input (roles and marks included: exactly what the AI would get).</summary>
    Task<SheetPicture> RenderAsync(string body, string seeJson, CancellationToken ct);
}

/// <summary>The live source: GETs through the Live Hive's <see cref="HiveApi"/> (timeouts, per-route back-off).</summary>
public sealed class HiveVisionSource(HiveApi api) : IVisionSource
{
    public Task<VisionListDto> ListAsync(string body, long since, CancellationToken ct) =>
        api.GetJsonAsync<VisionListDto>("vision", $"api/bodies/{Uri.EscapeDataString(body)}/vision{(since > 0 ? "?since=" + since.ToString(CultureInfo.InvariantCulture) : "")}", ct, track: true);

    public Task<byte[]> PngAsync(string body, long id, CancellationToken ct) =>
        api.GetBytesAsync("vision_png", $"api/bodies/{Uri.EscapeDataString(body)}/vision/{id.ToString(CultureInfo.InvariantCulture)}.png", ct);

    public async Task<SheetPicture> RenderAsync(string body, string seeJson, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var png = await api.GetBytesAsync("vision_render", $"api/bodies/{Uri.EscapeDataString(body)}/vision/render?see={Uri.EscapeDataString(seeJson)}", ct).ConfigureAwait(false);
        var (w, h) = PngSize(png);
        return new SheetPicture(png, w, h, "", sw.Elapsed.TotalMilliseconds, "server");
    }

    public static (int W, int H) PngSize(byte[] png) =>
        png.Length >= 24 ? (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20))) : (0, 0);
}

/// <summary>What compose mode renders: a body, a centre, a radius and the panels (each with its camera adjustments).</summary>
public sealed record ComposeSpec(BotDto Body, (int X, int Y, int Z) Focus, int Radius, IReadOnlyList<SeeView> Views, bool Aids = true, string? FocusText = null)
{
    /// <summary>The exact <c>see</c> call that renders this sheet ("Copy as see{...}").</summary>
    public string SeeCall() => MineClaw.Render.SeeCall.Format(Views, FocusText ?? MineClaw.Render.SeeCall.Coords(Focus.X, Focus.Y, Focus.Z), Radius, Aids);
    public string SeeJson() => MineClaw.Render.SeeCall.Json(Views, FocusText ?? MineClaw.Render.SeeCall.Coords(Focus.X, Focus.Y, Focus.Z), Radius, Aids);
}

/// <summary>Renders a compose spec (locally from the synced world, or a fake in tests).</summary>
public interface IComposeRenderer
{
    SheetPicture Render(ComposeSpec spec);
}

/// <summary><see cref="IBlockAccess"/> over a prepared <see cref="HiveVoxels"/> (the sheet's slab and role-free panels need one).</summary>
public sealed class HiveBlocks(HiveVoxels vox) : IBlockAccess
{
    public int GetBlock(int x, int y, int z) => vox.Block(x, y, z);
    public int GetMeta(int x, int y, int z) => vox.Meta(x, y, z);
    public bool IsLoaded(int x, int z) => vox.Column(x, z) != null;
    public BlockInfo Info(int id) => Blocks.Get(id);
}

/// <summary>
/// Compose mode's local renderer: the bot's <see cref="ContactSheet"/> (the same raycaster, panels, captions, reading aids and
/// pixel budget as the AI's sheets) over the Explorer's synced copy of the hive's world memory, so slider changes need no round
/// trip. It builds the request the way <c>VisionHub.SheetRequest</c> does for the body's own <c>see</c> (eye toward the focus,
/// or the body's yaw when the focus is its feet; the SIDE cut along the bot→focus line or its facing). What only the server knows
/// is absent here: the role layer (gen cells, protection), memory labels and the world time (rendered at noon); "Render exact"
/// asks the server for those. One render at a time (the panel's debounce serialises calls).
/// </summary>
public sealed class LocalSheetComposer(HiveWorldCache cache, Func<TextureAtlas?> atlas, Func<IReadOnlyList<BotDto>> bots, Func<IReadOnlyList<EntityDto>> entities)
    : IComposeRenderer
{
    private readonly HiveVoxels _vox = new(cache) { Fresh = TimeSpan.FromHours(24) };
    private readonly ContactSheet _sheet = new(new VisionRenderer { CacheCapacity = 4 }) { EyeThreads = Math.Clamp(Environment.ProcessorCount - 2, 1, 8) };
    private readonly object _lock = new();

    public SheetPicture Render(ComposeSpec spec)
    {
        lock (_lock)
        {
            var sw = Stopwatch.StartNew();
            var b = spec.Body;
            var f = spec.Focus;
            int radius = Math.Clamp(spec.Radius, 3, 12);
            _vox.Prepare(b.Dim, f.X, f.Z, 4, DateTimeOffset.UtcNow);
            var blocks = new HiveBlocks(_vox);
            var names = spec.Views.Select(v => v.Kind.ToLowerInvariant()).ToList();
            var ps = spec.Views.Where(v => !v.Params.IsEmpty).GroupBy(v => v.Kind.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First().Params, StringComparer.OrdinalIgnoreCase);
            float yaw = b.Yaw ?? 0, pitch = b.Pitch ?? 20;
            EyeScene? eye = null;
            var ents = Entities(spec, radius);
            if (names.Count == 0 || names.Contains("eye"))
            {
                // VisionHub.SheetRequest for a body's own see: from its eyes toward the focus (its own yaw, 60 down, at its feet).
                double ex = b.X, ey = b.Y + 1.62, ez = b.Z;
                double dx = f.X + 0.5 - ex, dy = f.Y + 0.5 - ey, dz = f.Z + 0.5 - ez;
                bool self = Math.Abs(dx) < 1 && Math.Abs(dz) < 1;
                float eyaw = self ? yaw : (float)(Math.Atan2(-dx, dz) * 180 / Math.PI);
                float epitch = self ? 60 : (float)(-Math.Atan2(dy, Math.Sqrt(dx * dx + dz * dz)) * 180 / Math.PI);
                eye = new EyeScene
                {
                    World = _vox, Atlas = atlas(), EyeX = ex, EyeY = ey, EyeZ = ez, Yaw = eyaw, Pitch = epitch, Time = 6000, Nether = b.Dim == -1,
                    Entities = ents, Target = f, Viewer = b.Key,
                };
            }
            var feet = ((int)Math.Floor(b.X), (int)Math.Floor(b.Y), (int)Math.Floor(b.Z));
            var img = _sheet.Render(new ContactSheetRequest
            {
                Blocks = blocks, Light = _vox, Focus = f, Radius = radius, Views = names, Self = feet, Entities = ents, Nether = b.Dim == -1, Atlas = atlas(),
                Time = 6000, Axis = AxisFor(feet, f, yaw), Eye = eye, Aids = spec.Aids,
                SelfPose = new PerspectivePanel.Body(b.X, b.Y, b.Z, yaw, pitch, b.Name),
                ViewParams = ps.Count > 0 ? ps : null,
            });
            return new SheetPicture(img.Png, img.Width, img.Height, img.Legend, sw.Elapsed.TotalMilliseconds, "local");
        }
    }

    /// <summary>VisionHub.AxisFor: the bot→focus line, else the way the body faces.</summary>
    public static SideAxis AxisFor((int X, int Y, int Z) feet, (int X, int Y, int Z) focus, float yaw)
    {
        int dx = focus.X - feet.X, dz = focus.Z - feet.Z;
        if (Math.Abs(dx) + Math.Abs(dz) >= 2) return Math.Abs(dx) >= Math.Abs(dz) ? SideAxis.LookNorth : SideAxis.LookEast;
        double rad = yaw * Math.PI / 180;
        return Math.Abs(Math.Cos(rad)) >= Math.Abs(Math.Sin(rad)) ? SideAxis.LookEast : SideAxis.LookNorth;
    }

    private List<VisionEntity> Entities(ComposeSpec spec, int radius)
    {
        var list = new List<VisionEntity>();
        var b = spec.Body;
        double reach = radius + 6;
        bool Near(double x, double z) => Math.Abs(x - spec.Focus.X) <= reach && Math.Abs(z - spec.Focus.Z) <= reach;
        foreach (var o in bots())
            if (o.Dim == b.Dim && o.Name != b.Name && Near(o.X, o.Z)) list.Add(new VisionEntity("hive", o.Key, o.X, o.Y, o.Z) { Yaw = o.Yaw });
        foreach (var e in entities())
            if (e.Dim == b.Dim && e.Kind is "player" or "mob" or "item" && Near(e.X, e.Z) && !string.Equals(e.Name, b.Name, StringComparison.OrdinalIgnoreCase))
                list.Add(Gpu.FrameScene.Vision(e));
        return list;
    }
}
