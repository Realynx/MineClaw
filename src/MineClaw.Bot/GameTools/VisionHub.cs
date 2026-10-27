using System.Globalization;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Bot.Memory;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Render;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// Vision v2 for one bot: resolves a focus, renders the contact sheet (same focus, compass and ticks in every panel, the role
/// layer), and either asks the perception model (text answer, voxel-verified) or returns the picture (mode direct). Used by the
/// <c>see</c> tool and by auto-vision (<see cref="AutoVision"/>).
/// </summary>
public sealed class VisionHub
{
    private readonly AgentRuntime _rt;
    private readonly IGameClient _c;
    private readonly IPlaceResolver _places;
    private readonly VisionOptions _o;
    private readonly WorldVoxels _voxels;
    private readonly Func<int, int, int, int, IReadOnlyList<VisionMark>> _marks;
    private readonly Func<string, bool> _isHive;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;

    public VisionHub(AgentRuntime rt, IGameClient c, IPlaceResolver places, VisionOptions o, WorldVoxels voxels,
        Func<int, int, int, int, IReadOnlyList<VisionMark>> marks, Func<string, bool> isHive, RoleClassifier? roles, PerceptionService? perception,
        TimeProvider time, ILogger? log = null)
    {
        _rt = rt;
        _c = c;
        _places = places;
        _o = o;
        _voxels = voxels;
        _marks = marks;
        _isHive = isHive;
        _time = time;
        _log = log;
        Perception = perception;
        Roles = roles ?? RoleClassifier.For(c.World, null, null, null);
    }

    public RoleClassifier Roles { get; }
    public PerceptionService? Perception { get; }
    public ContactSheet Sheets { get; } = new();
    /// <summary>The latest pointed block (a punch while looking at it).</summary>
    public Perception.Gesture? LastPoint { get; set; }
    /// <summary>The texture atlas when loaded (flat colours otherwise).</summary>
    public Func<TextureAtlas?> Atlas { get; set; } = () => null;

    public const string DefaultQuestion =
        "What is at the focus (cyan box)? Its block and role, its neighbours' roles (gen cells vs floor, void edges, portal), and which faces a block can be placed against.";

    /// <summary>The focus of a look: coords, a player, a saved place, "pointed"/"this"; default the current job's target, else the bot.</summary>
    public (BlockPos Focus, Entity? Player, string? Error) ResolveFocus(string? focus)
    {
        var w = _c.World;
        var feet = w.Player.Position.ToBlockPos();
        var f = focus?.Trim();
        if (string.IsNullOrEmpty(f) || f is "you" or "me" or "self" || f.Equals(_c.Username, StringComparison.OrdinalIgnoreCase))
            return (string.IsNullOrEmpty(f) ? VisionTools.ActivityTarget(_rt, _places) ?? feet : feet, null, null);
        var lower = f.ToLowerInvariant();
        if (lower is "pointed" or "this" or "that" or "this one" or "that one" or "punched")
            return LastPoint is { } g && _time.GetUtcNow() - g.At < TimeSpan.FromMinutes(10) ? (g.Pos, null, null) : (feet, null, "nothing pointed at recently");
        if (lower == "player" && TurnContext.Current?.Speaker is { } who && w.Entities.FindPlayer(who) is { } sp) return (sp.Position.ToBlockPos(), sp, null);
        if (Locations.ParseCoords(f) is { } p) return (p, null, null);
        if (w.Entities.FindPlayer(f) is { } e) return (e.Position.ToBlockPos(), e, null);
        if (_places.Resolve(f) is { } q) return (q, null, null);
        return (feet, null, $"unknown focus '{f}' (x,y,z, player, saved name or pointed)");
    }

    /// <summary>The side panel's direction: the bot→focus line, else the way the bot faces (work axis).</summary>
    public SideAxis AxisFor(BlockPos focus)
    {
        var me = _c.World.Player;
        var feet = me.Position.ToBlockPos();
        int dx = focus.X - feet.X, dz = focus.Z - feet.Z;
        if (Math.Abs(dx) + Math.Abs(dz) >= 2) return Math.Abs(dx) >= Math.Abs(dz) ? SideAxis.LookNorth : SideAxis.LookEast;
        double rad = me.Yaw * Math.PI / 180;
        // b1.8.1 yaw: 0 = +z south, 90 = -x west. Facing along z: the cut must contain that line (looking east).
        return Math.Abs(Math.Cos(rad)) >= Math.Abs(Math.Sin(rad)) ? SideAxis.LookEast : SideAxis.LookNorth;
    }

    /// <summary>Renders the contact sheet of <paramref name="focus"/>.</summary>
    public VisionImage Sheet(BlockPos focus, int radius, IReadOnlyList<string>? views, Entity? viewer = null)
    {
        var w = _c.World;
        var me = w.Player.Position;
        var feet = me.ToBlockPos();
        radius = Math.Clamp(radius, 3, 12);
        var list = views is { Count: > 0 } ? views.Select(v => v.Trim().ToLowerInvariant()).ToList() : ContactSheet.DefaultViews.ToList();
        var pointed = LastPoint is { } g && _time.GetUtcNow() - g.At < TimeSpan.FromMinutes(10) ? LastPoint : null;
        var ents = VisionTools.Entities(_c, focus, radius + 4, _isHive);
        var ms = _marks(focus.X, focus.Y, focus.Z, radius);
        var atlas = Atlas();
        _voxels.Prepare(focus.X, focus.Z, radius / 16 + 2);
        EyeScene? eye = null;
        if (list.Contains("eye"))
        {
            double ex, ey, ez;
            float yaw, pitch;
            string? who = null;
            if (viewer is { } fp) (ex, ey, ez, yaw, pitch, who) = (fp.Position.X, fp.Position.Y + 1.62, fp.Position.Z, fp.Yaw, fp.Pitch, fp.Name);
            else
            {
                (ex, ey, ez) = (me.X, me.Y + 1.62, me.Z);
                double dx = focus.X + 0.5 - ex, dy = focus.Y + 0.5 - ey, dz = focus.Z + 0.5 - ez;
                bool self = Math.Abs(dx) < 1 && Math.Abs(dz) < 1;
                yaw = self ? w.Player.Yaw : (float)(Math.Atan2(-dx, dz) * 180 / Math.PI);
                pitch = self ? 60 : (float)(-Math.Atan2(dy, Math.Sqrt(dx * dx + dz * dz)) * 180 / Math.PI);
            }
            eye = new EyeScene
            {
                World = _voxels, Atlas = atlas, EyeX = ex, EyeY = ey, EyeZ = ez, Yaw = yaw, Pitch = pitch, Time = w.Time, Nether = w.Dimension == -1,
                Smooth = _o.SmoothLighting, Entities = ents, Marks = ms, Target = (focus.X, focus.Y, focus.Z),
                Pointed = pointed is { } pp ? (pp.Pos.X, pp.Pos.Y, pp.Pos.Z) : null, Viewer = who,
            };
        }
        return Sheets.Render(new ContactSheetRequest
        {
            Blocks = w.PlanningView, Focus = (focus.X, focus.Y, focus.Z), Radius = radius, Views = list, Self = (feet.X, feet.Y, feet.Z),
            Entities = ents, Marks = ms, Target = VisionTools.ActivityTarget(_rt, _places) is { } t ? (t.X, t.Y, t.Z) : null,
            Pointed = pointed is { } p2 ? (p2.Pos.X, p2.Pos.Y, p2.Pos.Z) : null, PointedBy = pointed?.Player, Nether = w.Dimension == -1,
            Atlas = atlas, Light = _voxels, Time = w.Time, Roles = Roles.Map, Axis = AxisFor(focus), Eye = eye,
        });
    }

    /// <summary>Change detection without drawing: the blocks + roles around the focus (feet layer ±3).</summary>
    public ulong ViewHash(BlockPos focus, int radius)
    {
        var w = _c.World.PlanningView;
        ulong h = 14695981039346656037UL;
        int r = Math.Clamp(radius, 2, 12);
        unchecked
        {
            for (int y = focus.Y - 3; y <= focus.Y + 3; y++)
                for (int z = focus.Z - r; z <= focus.Z + r; z++)
                    for (int x = focus.X - r; x <= focus.X + r; x++)
                    {
                        int id = w.GetBlock(x, y, z);
                        h = (h ^ (ulong)id) * 1099511628211UL;
                        if (id != 0) h = (h ^ (ulong)w.GetMeta(x, y, z)) * 1099511628211UL;
                    }
            h = (h ^ (ulong)(focus.X * 73856093 ^ focus.Y * 19349663 ^ focus.Z * 83492791)) * 1099511628211UL;
        }
        return h;
    }

    /// <summary>One perception look at <paramref name="focus"/> (auto-vision and the tool). Null when perception is off.</summary>
    public async Task<VerifiedAnswer?> LookAsync(BlockPos focus, string question, int radius, IReadOnlyList<string>? views, string source,
        CancellationToken ct, Entity? viewer = null)
    {
        if (Perception is not { Available: true } p) return null;
        // A cached answer for an unchanged spot needs no render either.
        var sheet = Sheet(focus, radius, views, viewer);
        return await p.AskAsync(new PerceptionLook(sheet, question, _c.World.PlanningView, Roles.Map, (focus.X, focus.Y, focus.Z), Math.Clamp(radius, 3, 12), source), ct)
            .ConfigureAwait(false);
    }

    /// <summary>The <c>see</c> tool body for vision v2 (views[]/question/focus/radius).</summary>
    public async Task<ToolResult> SeeAsync(JsonElement i, Func<DateTimeOffset, bool> takeShot, CancellationToken ct)
    {
        var (focus, player, err) = ResolveFocus(i.Str("focus"));
        if (err != null) return ToolResult.Error(err);
        int radius = Math.Clamp(i.Int("radius") ?? _o.SheetRadius, 3, 12);
        var views = i.StrList("views");
        if (views.Count == 0 && i.Str("view") is { Length: > 0 } v) views.Add(v);
        var bad = views.Where(x => !ContactSheet.Known.Contains(x.Trim())).ToList();
        if (bad.Count > 0) return ToolResult.Error($"unknown view '{bad[0]}' (top, side, iso, layers, eye)");
        string? question = i.Str("question")?.Trim();
        if (Perception is { Available: true })
        {
            var q = string.IsNullOrWhiteSpace(question) ? DefaultQuestion : question;
            var a = await LookAsync(focus, q, radius, views, "see", ct, player).ConfigureAwait(false);
            if (a == null) return ToolResult.Error("perception unavailable");
            if (a.Error != null)
                return ToolResult.Success(a.Text + "\n" + AsciiMap.Render(_c.World.PlanningView, focus.X, focus.Y, focus.Z, 11,
                    _c.World.Player.Position.ToBlockPos() is var me ? (me.X, me.Y, me.Z) : null, null, null, Roles.Map));
            return ToolResult.Success(a.Text);
        }
        // Direct mode (or no perception model): the sheet itself goes to the caller's model, under the hourly image cap.
        if (!takeShot(_time.GetUtcNow()))
            return ToolResult.Success($"image budget used ({_o.ImagesPerHour}/h); text map instead:\n" + VisionTools.LookMap(_c, 15));
        var sheet = Sheet(focus, radius, views, player);
        var text = sheet.Legend + (sheet.Labels.Count > 0 ? " Labels: " + string.Join("; ", sheet.Labels.Take(6)) : "")
            + (string.IsNullOrWhiteSpace(question) ? "" : " Q: " + question);
        var stub = string.Create(CultureInfo.InvariantCulture, $"[picture omitted: see sheet r{radius} at {focus.X},{focus.Y},{focus.Z}]");
        return ToolResult.Success(text) with { Image = new LlmImage(sheet.Png, sheet.Width, sheet.Height, stub) };
    }
}
