using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Bot.Memory;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Bot.WorldIndex;
using MineClaw.Render;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// Vision v2 for one bot: resolves a focus, renders the contact sheet (same focus, compass and ticks in every panel, the role
/// layer), and either asks the perception model (text answer, voxel-verified) or returns the picture (mode direct). Used by the
/// <c>see</c> tool and by auto-vision (<see cref="AutoVision"/>). For main (the hive mind) a focus can be anywhere: a landmark in
/// any dimension, a clone's live view, or the hive overview; remote spots render from the shared world memory in the db
/// (every hive bot's remembered columns) with per-region staleness.
/// </summary>
public sealed partial class VisionHub
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
        string? dir = null;
        if (o.HistoryDir is { Length: > 0 } hd)
        {
            var root = Path.IsPathRooted(hd) ? hd : Path.Combine(rt.Options.DataDirectory, hd);
            var who = string.Concat((c.Username ?? "bot").Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));
            dir = Path.Combine(root, who);
        }
        History = new VisionHistory(Math.Max(1, o.HistorySize), dir, o.HistoryDiskKeep, log);
    }

    /// <summary>The weather a picture of <paramref name="dim"/> is drawn under: this body's (the server's) rain and thunder in its own
    /// dimension, clear elsewhere (a remote spot in another dimension; the Nether never rains).</summary>
    internal MineClaw.Render.Weather WeatherIn(int dim) =>
        dim == _c.World.Dimension && dim != -1 ? new MineClaw.Render.Weather(_c.World.Rain, _c.World.Thunder) : default;

    /// <summary>The last sheets a model received from this hub (the Live Hive's "what the AI saw"; the supervisor's vision routes).</summary>
    public VisionHistory History { get; }

    /// <summary>
    /// Stores a picture a model gets: the very PNG array of the request (no copy, no re-render) with what was asked. Never
    /// throws (vision must not fail because of its own debugging aid). Off when <see cref="VisionOptions.HistorySize"/> is 0.
    /// </summary>
    public VisionSheetInfo? Capture(byte[] png, int width, int height, string kind, string source, string mode, IEnumerable<string> views, BlockPos? focus,
        int radius, string? question, string? call, string legend, string? note = null)
    {
        if (_o.HistorySize <= 0) return null;
        try
        {
            return History.Add(png, width, height, new VisionSheetInfo(0, _time.GetUtcNow(), _c.Username ?? "bot", kind, source, mode, views.ToList(),
                focus is { } f ? [f.X, f.Y, f.Z] : null, radius, string.IsNullOrWhiteSpace(question) ? null : question, call, 0, 0, 0, 0, "", legend, Note: note));
        }
        catch (Exception ex) { _log?.LogDebug(ex, "vision history: capture failed"); return null; }
    }

    /// <summary>The perception tier's answer on a captured sheet (model, tokens; "cached: no image sent" when the cache answered).</summary>
    private void Answered(VisionSheetInfo? rec, VerifiedAnswer a)
    {
        if (rec == null) return;
        try
        {
            History.Update(rec.Id, i => i with
            {
                Answer = a.Text,
                Note = a.Cached ? "cached: the model was not called (same view + question)" : a.Error is { } e ? "not answered: " + e : i.Note,
                Model = a.Raw?.Model, ModelInputTokens = a.Raw?.Usage.InputTokens, ModelOutputTokens = a.Raw?.Usage.OutputTokens,
            });
        }
        catch (Exception ex) { _log?.LogDebug(ex, "vision history: answer not stored"); }
    }

    /// <summary>Views as the see tool takes them back ("iso yaw=200 pitch=50"), for the history.</summary>
    private static IEnumerable<string> ViewNames(IReadOnlyList<string>? views, IReadOnlyDictionary<string, CamParams>? ps)
    {
        var list = views is { Count: > 0 } ? views : ContactSheet.DefaultViews;
        foreach (var v in list)
        {
            var k = v.Trim().ToLowerInvariant();
            yield return ps != null && ps.TryGetValue(k, out var p) && !p.IsEmpty ? k + " " + p.Format() : k;
        }
    }

    public RoleClassifier Roles { get; }
    public PerceptionService? Perception { get; }
    public ContactSheet Sheets { get; } = new();
    /// <summary>The latest pointed block (a punch while looking at it).</summary>
    public Perception.Gesture? LastPoint { get; set; }
    /// <summary>The texture atlas when loaded (flat colours otherwise).</summary>
    public Func<TextureAtlas?> Atlas { get; set; } = () => null;
    /// <summary>The hive seen from main (clone snapshots, claims, "send me your view" IPC); null in a clone or without a hive.</summary>
    public IHiveVision? Hive { get; set; }

    private IWorldMemoryStore? _store;
    private bool _storeTried;

    /// <summary>The shared world memory (every hive bot's remembered columns, any dimension): remote foci render from it.</summary>
    public IWorldMemoryStore? WorldStore
    {
        get
        {
            if (_store != null || _storeTried) return _store;
            _storeTried = true;
            try { _store = new SqliteWorldMemoryStore(_rt.Db, _c.Username); }
            catch (Exception ex) { _log?.LogDebug(ex, "vision: world memory store unavailable"); }
            return _store;
        }
        set { _store = value; _storeTried = true; }
    }

    public const string DefaultQuestion =
        "What is at the focus (cyan box)? Its block and role, its neighbours' roles (floor, walls, edges, gen cells, portal), and which faces a block can be placed against.";

    /// <summary>Foci meaning the bot itself (case as typed).</summary>
    internal static readonly HashSet<string> SelfWords = new(StringComparer.Ordinal) { "you", "me", "self" };

    /// <summary>Foci meaning the block a player last pointed at (lower case).</summary>
    internal static readonly HashSet<string> PointedWords = new(StringComparer.Ordinal) { "pointed", "this", "that", "this one", "that one", "punched" };

    /// <summary>Foci meaning the hive overview (lower case).</summary>
    internal static readonly HashSet<string> HiveWords = new(StringComparer.Ordinal)
        { "hive", "the hive", "hive view", "overview", "hive overview", "everyone", "all bots", "bots" };

    /// <summary>The player who spoke this turn, if they're in view.</summary>
    private Entity? SpeakerEntity() =>
        TurnContext.Current?.Speaker is { } who ? _c.World.Entities.FindPlayer(who) : null;

    /// <summary>The focus of a look: coords, a player, a saved place, "pointed"/"this"; default the current job's target, else the bot.</summary>
    public (BlockPos Focus, Entity? Player, string? Error) ResolveFocus(string? focus)
    {
        var w = _c.World;
        var feet = w.Player.Position.ToBlockPos();
        var f = focus?.Trim();
        if (string.IsNullOrEmpty(f)) return (VisionTools.ActivityTarget(_rt, _places) ?? feet, null, null);
        if (SelfWords.Contains(f) || f.Equals(_c.Username, StringComparison.OrdinalIgnoreCase)) return (feet, null, null);
        var lower = f.ToLowerInvariant();
        if (PointedWords.Contains(lower))
            return RecentPoint() is { } g ? (g.Pos, null, null) : (feet, null, "nothing pointed at recently");
        if (lower == "player" && SpeakerEntity() is { } sp) return (sp.Position.ToBlockPos(), sp, null);
        return NamedFocus(f) ?? (feet, null, $"unknown focus '{f}' (x,y,z, player, clone, saved name, hive or pointed)");
    }

    /// <summary>Coords, a player in view, or a saved place.</summary>
    private (BlockPos Focus, Entity? Player, string? Error)? NamedFocus(string f)
    {
        if (Locations.ParseCoords(f) is { } p) return (p, null, null);
        if (_c.World.Entities.FindPlayer(f) is { } e) return (e.Position.ToBlockPos(), e, null);
        if (_places.Resolve(f) is { } q) return (q, null, null);
        return null;
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

    // ------------------------------------------------------------------ scenes (local world or shared memory)

    /// <summary>
    /// What a look renders and checks against: main's own live world (local), or a region of the shared world memory (another
    /// dimension, a landmark main isn't near, a clone's surroundings) with who saw each column when.
    /// </summary>
    public sealed record Scene(IBlockAccess Blocks, IVoxelWorld Light, RoleClassifier Roles, int Dim, RegionWorld? Region)
    {
        public bool Local => Region == null;
    }

    private Scene LocalScene() => new(_c.World.PlanningView, _voxels, Roles, _c.World.Dimension, null);

    /// <summary>The scene for <paramref name="focus"/> in <paramref name="dim"/>: local when main has that column live.</summary>
    public Scene SceneFor(BlockPos focus, int dim, int radius)
    {
        var w = _c.World;
        if (dim == w.Dimension && w.IsLoaded(focus.X, focus.Z)) return LocalScene();
        var region = RegionWorld.Build(w, _c.Username, WorldStore, dim, focus.X, focus.Z, Math.Max(radius, 16), _time.GetUtcNow());
        var view = region.World.PlanningView;
        bool here = dim == w.Dimension; // protection, dig cells and the index are per dimension: only main's own apply
        var roles = new RoleClassifier
        {
            Blocks = view,
            IsGenCell = here ? Roles.IsGenCell : null,
            IsGenGeometry = p => GenGeometry(view, p),
            Verdict = here ? Roles.Verdict : null,
            IndexerKind = here ? Roles.IndexerKind : null,
        };
        var vox = new WorldVoxels(region.World);
        vox.Prepare(focus.X, focus.Z, Math.Max(radius, 16) / 16 + 2);
        return new Scene(view, vox, roles, dim, region);
    }

    /// <summary>Cobble/stone/air with lava and water flowing in (BlockDescriber.IsGenCell over any block view).</summary>
    private static bool GenGeometry(IBlockAccess w, BlockPos p)
    {
        int id = w.GetBlock(p.X, p.Y, p.Z);
        if (id is not (MineClaw.Data.Blocks.Cobblestone or MineClaw.Data.Blocks.Stone or 0)) return false;
        bool lava = false, water = false;
        foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, 1, 0) })
        {
            int n = w.GetBlock(p.X + dx, p.Y + dy, p.Z + dz);
            lava |= MineClaw.Data.Blocks.IsLava(n);
            water |= MineClaw.Data.Blocks.IsWater(n);
        }
        return lava && water;
    }

    /// <summary>Hive bots in <paramref name="dim"/> near a remote focus, from their snapshots (main's own entity list is local).</summary>
    private List<VisionEntity> HiveEntities(int dim, BlockPos focus, int radius)
    {
        var list = new List<VisionEntity>();
        foreach (var b in HiveBots().Where(b => b.Dim == dim && !b.IsMain))
            if (Math.Abs(b.X - focus.X) <= radius + 4 && Math.Abs(b.Z - focus.Z) <= radius + 4)
                list.Add(new VisionEntity("hive", b.Short, b.X, b.Y, b.Z, b.Short[0]));
        return list;
    }

    /// <summary>Renders the contact sheet of <paramref name="focus"/> (main's world, or <paramref name="scene"/>; a clone's eye pose).</summary>
    public VisionImage Sheet(BlockPos focus, int radius, IReadOnlyList<string>? views, Entity? viewer = null, Scene? scene = null, HiveEyePose? pose = null,
        bool aids = true, IReadOnlyDictionary<string, CamParams>? viewParams = null) =>
        Sheets.Render(SheetRequest(focus, radius, views, viewer, scene, pose, aids, viewParams));

    /// <summary>
    /// The exact contact-sheet request a <c>see</c> of <paramref name="focus"/> renders (views, EYE pose, side axis, overlays,
    /// roles). The read-only supervisor viewer renders it with its own CPU-capped <see cref="ContactSheet"/>, so what it shows
    /// is what the bot sees.
    /// </summary>
    public ContactSheetRequest SheetRequest(BlockPos focus, int radius, IReadOnlyList<string>? views, Entity? viewer = null, Scene? scene = null, HiveEyePose? pose = null,
        bool aids = true, IReadOnlyDictionary<string, CamParams>? viewParams = null, XRay xray = XRay.Auto)
    {
        var w = _c.World;
        var me = w.Player.Position;
        var feet = me.ToBlockPos();
        radius = Math.Clamp(radius, 3, 12);
        scene ??= LocalScene();
        bool local = scene.Local;
        bool mainHere = scene.Dim == w.Dimension;
        var list = SheetViewList(views, pose);
        var pointed = local ? RecentPoint() : null;
        var ents = SheetEntities(scene, focus, radius);
        var ms = mainHere ? _marks(focus.X, focus.Y, focus.Z, radius) : [];
        var atlas = Atlas();
        if (local) _voxels.Prepare(focus.X, focus.Z, radius / 16 + 2);
        var eye = list.Contains("eye") ? SheetEyeScene(scene, focus, SheetEyePose(focus, viewer, pose, mainHere), atlas, ents, ms, pointed) : null;
        var axis = SheetAxis(focus, pose, mainHere);
        return new ContactSheetRequest
        {
            Blocks = scene.Blocks, Focus = (focus.X, focus.Y, focus.Z), Radius = radius, Views = list,
            Self = mainHere ? (feet.X, feet.Y, feet.Z) : null,
            Entities = ents, Marks = ms, Target = local && VisionTools.ActivityTarget(_rt, _places) is { } t ? (t.X, t.Y, t.Z) : null,
            Pointed = PointedCell(pointed), PointedBy = pointed?.Player, Nether = scene.Dim == -1,
            Atlas = atlas, Light = scene.Light, Time = w.Time, Weather = WeatherIn(scene.Dim), Roles = scene.Roles.Map, Axis = axis, Eye = eye,
            SelfPose = SheetSelfPose(pose, mainHere, me),
            Aids = aids,
            ViewParams = viewParams is { Count: > 0 } ? new Dictionary<string, CamParams>(viewParams, StringComparer.OrdinalIgnoreCase) : null,
            XRay = xray,
            Stamp = SheetStamp(scene, focus, radius),
        };
    }

    /// <summary>The sheet's view names (trimmed, lower case; none = the defaults), plus the EYE panel for a clone's pose.</summary>
    private static List<string> SheetViewList(IReadOnlyList<string>? views, HiveEyePose? pose)
    {
        var list = views is { Count: > 0 } ? views.Select(v => v.Trim().ToLowerInvariant()).ToList() : ContactSheet.DefaultViews.ToList();
        if (pose != null && !list.Contains("eye")) list.Add("eye");
        return list;
    }

    /// <summary>The last pointing gesture, if it's under ten minutes old.</summary>
    private Perception.Gesture? RecentPoint() =>
        LastPoint is { } g && _time.GetUtcNow() - g.At < TimeSpan.FromMinutes(10) ? LastPoint : null;

    /// <summary>Where the EYE panel looks from: a clone's pose, a viewer's eyes, else main (or a spot south of the focus) looking at it.</summary>
    private (double X, double Y, double Z, float Yaw, float Pitch, string? Who) SheetEyePose(BlockPos focus, Entity? viewer, HiveEyePose? pose, bool mainHere)
    {
        if (pose != null) return (pose.X, pose.Y + 1.62, pose.Z, pose.Yaw, pose.Pitch, HiveVision.ShortName(pose.Name));
        if (viewer is { } fp) return (fp.Position.X, fp.Position.Y + 1.62, fp.Position.Z, fp.Yaw, fp.Pitch, fp.Name);
        var w = _c.World;
        var me = w.Player.Position;
        var (ex, ey, ez) = mainHere ? (me.X, me.Y + 1.62, me.Z) : (focus.X + 0.5, focus.Y + 1.62, focus.Z + 3.5);
        double dx = focus.X + 0.5 - ex, dy = focus.Y + 0.5 - ey, dz = focus.Z + 0.5 - ez;
        bool self = Math.Abs(dx) < 1 && Math.Abs(dz) < 1;
        float yaw = self ? w.Player.Yaw : (float)(Math.Atan2(-dx, dz) * 180 / Math.PI);
        float pitch = self ? 60 : (float)(-Math.Atan2(dy, Math.Sqrt(dx * dx + dz * dz)) * 180 / Math.PI);
        return (ex, ey, ez, yaw, pitch, null);
    }

    /// <summary>Entities to draw: main's live list for a local scene, else the hive bots' snapshots.</summary>
    private List<VisionEntity> SheetEntities(Scene scene, BlockPos focus, int radius) =>
        scene.Local ? VisionTools.Entities(_c, focus, radius + 4, _isHive) : HiveEntities(scene.Dim, focus, radius);

    /// <summary>The first-person panel from <paramref name="at"/> (the eye pose).</summary>
    private EyeScene SheetEyeScene(Scene scene, BlockPos focus, (double X, double Y, double Z, float Yaw, float Pitch, string? Who) at, TextureAtlas? atlas,
        IReadOnlyList<VisionEntity> ents, IReadOnlyList<VisionMark> ms, Perception.Gesture? pointed) => new()
    {
        World = scene.Light, Atlas = atlas, EyeX = at.X, EyeY = at.Y, EyeZ = at.Z, Yaw = at.Yaw, Pitch = at.Pitch, Time = _c.World.Time, Nether = scene.Dim == -1,
        Weather = WeatherIn(scene.Dim), Smooth = _o.SmoothLighting, Entities = ents, Marks = ms, Target = (focus.X, focus.Y, focus.Z),
        Pointed = PointedCell(pointed), Viewer = at.Who,
    };

    private static (int X, int Y, int Z)? PointedCell(Perception.Gesture? pointed) => pointed is { } p ? (p.Pos.X, p.Pos.Y, p.Pos.Z) : null;

    /// <summary>The SIDE panel's axis: along a clone's facing, else the focus's own axis when main is in its dimension.</summary>
    private SideAxis SheetAxis(BlockPos focus, HiveEyePose? pose, bool mainHere)
    {
        if (pose != null)
            return Math.Abs(Math.Cos(pose.Yaw * Math.PI / 180)) >= Math.Abs(Math.Sin(pose.Yaw * Math.PI / 180)) ? SideAxis.LookEast : SideAxis.LookNorth;
        return mainHere ? AxisFor(focus) : SideAxis.LookNorth;
    }

    /// <summary>The body in the third-person panels: a clone's pose for its view, else main where it stands.</summary>
    private PerspectivePanel.Body? SheetSelfPose(HiveEyePose? pose, bool mainHere, Vec3d me)
    {
        if (pose != null) return new PerspectivePanel.Body(pose.X, pose.Y, pose.Z, pose.Yaw, pose.Pitch, HiveVision.ShortName(pose.Name));
        return mainHere ? new PerspectivePanel.Body(me.X, me.Y, me.Z, _c.World.Player.Yaw, _c.World.Player.Pitch, _c.Username) : null;
    }

    /// <summary>"seen 3m ago" for a remembered region (capped to 40 chars), null for live voxels.</summary>
    private string? SheetStamp(Scene scene, BlockPos focus, int radius) =>
        scene.Region?.Staleness((focus.X, focus.Y, focus.Z), radius, _time.GetUtcNow()) is { } st ? "seen " + (st.Length > 40 ? st[..39] + "…" : st) : null;

    /// <summary>Entities to draw around a spot: main's live entity list in its own dimension, else the hive bots' snapshots.</summary>
    public IReadOnlyList<VisionEntity> EntitiesNear(int dim, BlockPos centre, int radius) =>
        dim == _c.World.Dimension && _c.World.IsLoaded(centre.X, centre.Z) ? VisionTools.Entities(_c, centre, radius, _isHive) : HiveEntities(dim, centre, radius);

    /// <summary>The deterministic text facts a <c>see</c> question gets without any model call (VisionRouter), per intent.</summary>
    public IReadOnlyList<string> RouterFacts(BlockPos focus, int radius, Scene? scene = null)
    {
        scene ??= LocalScene();
        var f = (focus.X, focus.Y, focus.Z);
        int r = Math.Clamp(radius, 3, 12);
        var facts = new List<string>();
        foreach (var (intent, q) in new[]
        {
            (VisionIntent.FocusBlock, "what is at the focus"), (VisionIntent.GenCells, "which gen cells to mine"),
            (VisionIntent.Faces, "which faces can a block be placed against"), (VisionIntent.Void, "how far is the edge"),
            (VisionIntent.Things, "trees chests doors ores crops"), (VisionIntent.Portal, "portal"), (VisionIntent.Drops, "drops on the ground"),
        })
        {
            // Gen cells only where stone comes from a generator (skyblock); a normal world's stone is just terrain.
            if (intent == VisionIntent.GenCells && !MineClaw.Data.Versioning.WorldMode.Current.GeneratorStone) continue;
            try { facts.Add($"{intent}: {VisionRouter.Answer(intent, q, scene.Blocks, scene.Roles.Map, f, r, EntitiesNear(scene.Dim, focus, r + 8))}"); }
            catch (Exception ex) { _log?.LogDebug(ex, "router facts: {Intent} failed", intent); }
        }
        return facts;
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
        CancellationToken ct, Entity? viewer = null, Scene? scene = null, HiveEyePose? pose = null, bool aids = true,
        IReadOnlyDictionary<string, CamParams>? viewParams = null, string? call = null)
    {
        if (Perception is not { Available: true } p) return null;
        scene ??= LocalScene();
        // Deterministic first: gen cells/floor/walls, gen fluids, faces, void, focus block, portal, trees/chests/ores are
        // known exactly from the voxels and the role layer: no model call, no tokens, no hallucination.
        var w = scene.Blocks;
        var map = scene.Roles.Map;
        var f = (focus.X, focus.Y, focus.Z);
        int r = Math.Clamp(radius, 3, 12);
        if (VisionRouter.Answer(question, w, map, f, r, EntitiesNear(scene.Dim, focus, r)) is { } exact) return p.Exact(question, exact, source);
        // A question shape whose model answer had nothing checkable last time: voxels only.
        if (p.LowValue(question)) return p.Exact(question, VisionRouter.Fallback(w, map, f, r, "model gave nothing checkable for this before"), source);
        // Auto-vision while the rolling hallucination rate is too high: voxels only (an explicit see still asks the model).
        if (source.StartsWith("auto", StringComparison.Ordinal) && p.Telemetry.DeterministicOnly)
            return p.Exact(question, VisionRouter.Fallback(w, map, f, r, $"perception paused: halluc {p.Telemetry.RollingRate:P0}"), source);
        // A cached answer for an unchanged spot needs no render either.
        var sheet = Sheet(focus, radius, views, viewer, scene, pose, aids, viewParams);
        var rec = Capture(sheet.Png, sheet.Width, sheet.Height, "sheet", source, "perception", ViewNames(views, viewParams), focus, r, question, call, sheet.Legend);
        var answer = await p.AskAsync(new PerceptionLook(sheet, question, w, map, f, r, source), ct).ConfigureAwait(false);
        Answered(rec, answer);
        return answer;
    }

    // ------------------------------------------------------------------ remote foci: landmarks, clones, the hive

    /// <summary>Where a look goes: a block in a dimension; or a clone (its live view); or the hive overview.</summary>
    public sealed record VisionTarget(BlockPos Focus, int Dim, Entity? Player = null, HiveBotState? Bot = null, bool HiveView = false,
        string? Landmark = null, string? Error = null);

    [GeneratedRegex(@"^(?:at|near|around|by|from)\s+(.+)$|^what\s+(.+?)\s+(?:sees|can see|is looking at|looks at)$|^(.+?)'s\s+(?:view|eyes|pov|spot)$|^(?:view|eyes|pov)\s+of\s+(.+)$|^where\s+(.+?)\s+is$", RegexOptions.IgnoreCase)]
    private static partial Regex ViewPhraseRx();

    /// <summary>"at 67" / "what 1337 sees" / "67's view" → "67"; anything else unchanged.</summary>
    public static string StripViewPhrase(string s)
    {
        var m = ViewPhraseRx().Match(s.Trim());
        if (!m.Success) return s.Trim();
        for (int g = 1; g < m.Groups.Count; g++) if (m.Groups[g].Success) return m.Groups[g].Value.Trim();
        return s.Trim();
    }

    /// <summary>Main + every live clone (positions, jobs, vitals: data from the supervisor's snapshots).</summary>
    public List<HiveBotState> HiveBots()
    {
        var w = _c.World;
        var me = w.Player;
        string? job = null;
        try { job = _rt.Activities.Describe(); } catch (Exception) { }
        var list = new List<HiveBotState>
        {
            new(_c.Username, w.Dimension, me.Position.X, me.Position.Y, me.Position.Z, job, (int)Math.Round((double)me.Health), me.Food, _time.GetUtcNow(), IsMain: true),
        };
        try { if (Hive != null) list.AddRange(Hive.Clones().Where(b => !b.Name.Equals(_c.Username, StringComparison.OrdinalIgnoreCase))); }
        catch (Exception ex) { _log?.LogDebug(ex, "hive vision: clones unavailable"); }
        return list;
    }

    /// <summary>A saved place (waypoint / structure, any dimension) or an indexed landmark ("cobble gen", "portal").</summary>
    public (BlockPos Pos, int Dim, string Name)? Landmark(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (t.Length == 0) return null;
        return SavedLandmark(t) ?? (LandmarkKinds.TryGetValue(t, out var kind) ? IndexedLandmark(kind) : null);
    }

    /// <summary>Spoken landmark names → the world index kind they mean (lower case).</summary>
    internal static readonly Dictionary<string, string> LandmarkKinds = new(StringComparer.Ordinal)
    {
        ["gen"] = "cobble_generator", ["the gen"] = "cobble_generator", ["cobble gen"] = "cobble_generator", ["cobble generator"] = "cobble_generator",
        ["generator"] = "cobble_generator", ["the generator"] = "cobble_generator", ["cobblestone generator"] = "cobble_generator",
        ["portal"] = "portal", ["the portal"] = "portal", ["nether portal"] = "portal",
        ["farm"] = "farm", ["the farm"] = "farm", ["wheat farm"] = "farm",
    };

    /// <summary>A saved name matches exactly (underscores as spaces) or by all its meaningful words.</summary>
    private static bool LandmarkNameMatches(string name, string q)
    {
        var n = name.ToLowerInvariant().Replace('_', ' ');
        if (n == q) return true;
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x is not ("the" or "a" or "our" or "my")).ToList();
        return words.Count > 0 && words.All(n.Contains);
    }

    /// <summary>A saved structure (its box's centre, one above the floor), else a waypoint: exact name first, then by words.</summary>
    private (BlockPos Pos, int Dim, string Name)? SavedLandmark(string t)
    {
        try
        {
            var (wps, sts) = _rt.Memory.Read(d => (d.Waypoints.ToList(), d.Structures.ToList()));
            var withBox = sts.Where(x => x.Min != null).ToList();
            var s = withBox.FirstOrDefault(x => x.Name.Equals(t, StringComparison.OrdinalIgnoreCase)) ?? withBox.FirstOrDefault(x => LandmarkNameMatches(x.Name, t));
            if (s?.Min is { } mn)
            {
                var mx = s.Max ?? mn;
                return (new BlockPos((mn.X + mx.X) / 2, Math.Min(mn.Y, mx.Y) + 1, (mn.Z + mx.Z) / 2), s.Dimension, s.Name);
            }
            var wp = wps.FirstOrDefault(x => x.Name.Equals(t, StringComparison.OrdinalIgnoreCase)) ?? wps.FirstOrDefault(x => LandmarkNameMatches(x.Name, t));
            if (wp != null) return (new BlockPos(wp.X, wp.Y, wp.Z), wp.Dimension, wp.Name);
        }
        catch (Exception ex) { _log?.LogDebug(ex, "landmark lookup failed"); }
        return null;
    }

    /// <summary>The indexed landmark of a kind, main's dimension first.</summary>
    private (BlockPos Pos, int Dim, string Name)? IndexedLandmark(string kind)
    {
        try
        {
            var rows = _rt.Observations.OfKind(kind, null, 20);
            var row = rows.Where(r => r.Bounds != null).OrderBy(r => r.Dimension == _c.World.Dimension ? 0 : 1).FirstOrDefault();
            if (row?.Bounds is { } b)
                return (new BlockPos((int)Math.Floor(b.MinX), (int)Math.Floor(b.MinY), (int)Math.Floor(b.MinZ)), row.Dimension, kind.Replace('_', ' '));
        }
        catch (Exception ex) { _log?.LogDebug(ex, "landmark index lookup failed"); }
        return null;
    }

    internal static bool Deictic(string lower) => SelfWords.Contains(lower) || lower == "player" || PointedWords.Contains(lower);

    /// <summary>
    /// Resolves any focus: "hive" (the overview), a clone ("67", "at 67", "what 1337 sees"), coords, a player, pointed, a saved
    /// place or landmark in any dimension ("mob chamber", "cobble gen", "main house").
    /// </summary>
    public VisionTarget ResolveTarget(string? focus)
    {
        var w = _c.World;
        var t = focus?.Trim() ?? "";
        var lower = t.ToLowerInvariant();
        if (HiveWords.Contains(lower))
            return new VisionTarget(w.Player.Position.ToBlockPos(), w.Dimension, HiveView: true);
        if (t.Length > 0 && Locations.ParseCoords(t) == null && !Deictic(lower) && NamedTarget(StripViewPhrase(t)) is { } named) return named;
        var (f, player, err) = ResolveFocus(focus);
        return new VisionTarget(f, w.Dimension, player, Error: err);
    }

    /// <summary>A name that is a clone (its last snapshot) or, unless it's a player in view, a saved place / landmark.</summary>
    private VisionTarget? NamedTarget(string name)
    {
        if (Hive != null && !name.Equals(_c.Username, StringComparison.OrdinalIgnoreCase)
            && HiveVision.Find(HiveBots().Where(b => !b.IsMain), name) is { } bot)
            return new VisionTarget(new BlockPos((int)Math.Floor(bot.X), (int)Math.Floor(bot.Y), (int)Math.Floor(bot.Z)), bot.Dim, Bot: bot);
        if (_c.World.Entities.FindPlayer(name) == null && Landmark(name) is { } lm)
            return new VisionTarget(lm.Pos, lm.Dim, Landmark: lm.Name);
        return null;
    }

    /// <summary>
    /// The hive overview (deterministic, ≈150 tokens of text; the ≈300-token top-down picture only when asked or in direct mode):
    /// every bot's position, name and job, claims, dangers, and how fresh the map is.
    /// </summary>
    public ToolResult HiveOverview(bool image)
    {
        var now = _time.GetUtcNow();
        var bots = HiveBots();
        IReadOnlyList<HiveLease> leases = [];
        try { leases = Hive?.Leases() ?? []; } catch (Exception ex) { _log?.LogDebug(ex, "hive vision: leases unavailable"); }
        // The overview's area: main's dimension, around the bots there (the island), clamped.
        int dim = _c.World.Dimension;
        var here = bots.Where(b => b.Dim == dim).ToList();
        double cx = here.Average(b => b.X), cz = here.Average(b => b.Z);
        int cy = (int)Math.Floor(here.Min(b => b.Y));
        double far = here.Max(b => Math.Max(Math.Abs(b.X - cx), Math.Abs(b.Z - cz)));
        int radius = Math.Clamp((int)Math.Ceiling(far) + 4, 8, 24);
        var centre = ((int)Math.Floor(cx), cy, (int)Math.Floor(cz));
        var region = RegionWorld.Build(_c.World, _c.Username, WorldStore, dim, centre.Item1, centre.Item3, radius, now);
        var dangers = HiveVision.VitalDangers(bots);
        var dangerMarks = new List<(string, int, int, int)>();
        try
        {
            foreach (var (row, _) in _rt.Observations.Nearest("nether_hazard", cx, cy, cz, 4, radius + 2))
                if (row.Dimension == dim && row.Bounds is { } hb)
                {
                    int hx = (int)Math.Floor(hb.MinX), hy = (int)Math.Floor(hb.MinY), hz = (int)Math.Floor(hb.MinZ);
                    dangers.Add($"hazard @{hx},{hy},{hz}");
                    dangerMarks.Add(("hazard", hx, hy, hz));
                }
        }
        catch (Exception ex) { _log?.LogDebug(ex, "hive vision: hazards unavailable"); }
        foreach (var b in bots.Where(b => b.Dim == dim && b.Hp is > 0 and <= 6))
            dangerMarks.Add(($"{b.Short} hp {b.Hp}", (int)Math.Floor(b.X), (int)Math.Floor(b.Y), (int)Math.Floor(b.Z)));
        var text = HiveVision.Text(bots, leases, dangers, now, region.Staleness(centre, radius, now));
        if (!image) return ToolResult.Success(text);
        var roles = new RoleClassifier { Blocks = region.World.PlanningView, IsGenGeometry = p => GenGeometry(region.World.PlanningView, p) };
        var img = HiveVision.Image(region, bots, leases, dangerMarks, centre, radius, roles.Map, atlas: Atlas());
        var stub = string.Create(CultureInfo.InvariantCulture, $"[picture omitted: hive overview r{radius} at {centre.Item1},{centre.Item2},{centre.Item3}]");
        return ToolResult.Success(text) with { Image = new LlmImage(img.Png, img.Width, img.Height, stub) };
    }

    private static string Facing(float yaw)
    {
        double a = ((yaw % 360) + 360) % 360; // b1.8.1: 0 = south (+z), 90 = west, 180 = north, 270 = east
        return a < 45 || a >= 315 ? "S" : a < 135 ? "W" : a < 225 ? "N" : "E";
    }

    /// <summary>The <c>see</c> tool body for vision v2 (views[]/question/focus/radius).</summary>
    public async Task<ToolResult> SeeAsync(JsonElement i, Func<DateTimeOffset, bool> takeShot, CancellationToken ct)
    {
        var target = ResolveTarget(i.Str("focus"));
        if (target.Error != null) return ToolResult.Error(target.Error);
        bool direct = Perception is not { Available: true };
        if (target.HiveView) return SeeHive(i, direct, takeShot);
        int radius = Math.Clamp(i.Int("radius") ?? _o.SheetRadius, 3, 12);
        bool aids = i.Bool("aids") ?? true;
        // Panels naming their own spot, body or camera: one composite image of them all.
        var reqs = ParseViews(i, out var viewError);
        if (viewError != null) return ToolResult.Error(viewError);
        if (WantsComposite(reqs))
            return await SeeCompositeAsync(i, reqs!, target, radius, aids, direct, takeShot, ct).ConfigureAwait(false);
        var views = SeeViewNames(i, reqs);
        // Camera adjustments per named view ("iso yaw=200 pitch=50 zoom=1.3", {"top":{"zoom":2}}); none = the fixed defaults.
        var viewParams = CamParamsOf(reqs);
        var bad = views.Where(x => !ContactSheet.Known.Contains(x.Trim())).ToList();
        if (bad.Count > 0) return ToolResult.Error($"unknown view '{bad[0]}' (eye, shoulder, orbit, high, iso, top, side, layers; cam/follow/eye <body>)");
        var (focus, dim, pose, cloneLine) = await CloneViewAsync(target, ct).ConfigureAwait(false);
        var scene = SceneFor(focus, dim, radius);
        string? stale = scene.Region?.Staleness((focus.X, focus.Y, focus.Z), radius, _time.GetUtcNow(), target.Landmark);
        // Clone line and staleness first: the legend is long and the tool result is capped.
        string Tail(string text) => string.Join(" | ", new[] { cloneLine, stale != null ? "seen: " + stale : null, text }.Where(x => !string.IsNullOrEmpty(x)));
        var look = new SeeLook(focus, radius, views, target.Player, scene, pose, aids, viewParams, i.Str("question")?.Trim(), i.GetRawText());
        if (!direct) return await SeePerceivedAsync(look, Tail, ct).ConfigureAwait(false);
        return SeeDirect(look, Tail, takeShot);
    }

    /// <summary>One resolved <c>see</c>: what to render and the caller's question.</summary>
    private sealed record SeeLook(BlockPos Focus, int Radius, List<string> Views, Entity? Player, Scene Scene, HiveEyePose? Pose, bool Aids,
        IReadOnlyDictionary<string, CamParams>? ViewParams, string? Question, string Call);

    /// <summary>The overview is text (data); views (any) or direct mode add the top-down picture.</summary>
    private ToolResult SeeHive(JsonElement i, bool direct, Func<DateTimeOffset, bool> takeShot)
    {
        var hive = HiveOverview(image: i.StrList("views").Count > 0 || (direct && takeShot(_time.GetUtcNow())));
        if (hive.Image is { } hi) Capture(hi.Data, hi.Width, hi.Height, "hive", "see", "direct", ["hive"], null, 0, i.Str("question"), i.GetRawText(), hive.Text);
        return hive;
    }

    /// <summary>Panels naming their own spot or body, several orbits, or the same view twice at different angles
    /// ("iso yaw=0", "iso yaw=90"): one composite image with a panel each.</summary>
    private static bool WantsComposite(List<PanelRequest>? reqs) =>
        reqs != null && (reqs.Any(r => r.Composite || r.Kind == "eye" && r.At != null || r.Kind == "orbit" && r.N > 1)
            || reqs.Any(r => !r.Cam.IsEmpty) && reqs.GroupBy(r => r.Kind).Any(g => g.Count() > 1));

    /// <summary>The views a <c>see</c> names: parsed panels, else <c>views</c>, else the legacy single <c>view</c>.</summary>
    private static List<string> SeeViewNames(JsonElement i, List<PanelRequest>? reqs)
    {
        var views = reqs != null ? reqs.Select(r => r.Kind).ToList() : i.StrList("views");
        if (views.Count == 0 && i.Str("view") is { Length: > 0 } v) views.Add(v);
        return views;
    }

    /// <summary>Camera adjustments per named view; null when no view has any.</summary>
    private static IReadOnlyDictionary<string, CamParams>? CamParamsOf(List<PanelRequest>? reqs)
    {
        var ps = reqs?.Where(r => !r.Cam.IsEmpty).GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.First().Cam, StringComparer.OrdinalIgnoreCase);
        return ps is { Count: 0 } ? null : ps;
    }

    /// <summary>A clone's live eye pose over IPC (it flushes its world memory to the shared db, then answers); null if it didn't answer.</summary>
    private async Task<HiveEyePose?> ClonePoseAsync(HiveBotState bot, string failure, CancellationToken ct)
    {
        try { return Hive == null ? null : await Hive.ViewAsync(bot.Name, ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { _log?.LogDebug(ex, failure); return null; }
    }

    private static BlockPos PoseBlock(HiveEyePose pose) => new((int)Math.Floor(pose.X), (int)Math.Floor(pose.Y), (int)Math.Floor(pose.Z));

    /// <summary>A clone: "send me your view" — its pose moves the focus there; the clone line says what it's doing (or how old the snapshot is).</summary>
    private async Task<(BlockPos Focus, int Dim, HiveEyePose? Pose, string? CloneLine)> CloneViewAsync(VisionTarget target, CancellationToken ct)
    {
        var focus = target.Focus;
        if (target.Bot is not { } cb) return (focus, target.Dim, null, null);
        var pose = await ClonePoseAsync(cb, "hive view request failed", ct).ConfigureAwait(false);
        string job = string.IsNullOrWhiteSpace(cb.Job) ? "idle" : cb.Job!;
        if (pose == null)
            return (focus, target.Dim, null, $"{cb.Short} didn't answer: its last snapshot @{focus.X},{focus.Y},{focus.Z}, {RegionWorld.Age(_time.GetUtcNow() - cb.At)} ago, job {job}");
        focus = PoseBlock(pose);
        int dim = pose.Dim;
        return (focus, dim, pose, $"{cb.Short} live view @{focus.X},{focus.Y},{focus.Z}{(dim != _c.World.Dimension ? $" (dim {dim})" : "")} facing {Facing(pose.Yaw)}, job {job}");
    }

    /// <summary>Perception mode: the sheet goes to the perception model and comes back as verified text (a look map on failure).</summary>
    private async Task<ToolResult> SeePerceivedAsync(SeeLook l, Func<string, string> tail, CancellationToken ct)
    {
        var q = string.IsNullOrWhiteSpace(l.Question) ? DefaultQuestion : l.Question;
        var a = await LookAsync(l.Focus, q, l.Radius, l.Views, "see", ct, l.Player, l.Scene, l.Pose, l.Aids, l.ViewParams, l.Call).ConfigureAwait(false);
        if (a == null) return ToolResult.Error("perception unavailable");
        if (a.Error == null) return ToolResult.Success(tail(a.Text));
        var scene = l.Scene;
        return ToolResult.Success(tail(a.Text) + "\n" + AsciiMap.Render(scene.Blocks, l.Focus.X, l.Focus.Y, l.Focus.Z, 11,
            scene.Dim == _c.World.Dimension && _c.World.Player.Position.ToBlockPos() is var me ? (me.X, me.Y, me.Z) : null, null, null, scene.Roles.Map));
    }

    /// <summary>Direct mode (or no perception model): the sheet itself goes to the caller's model, under the hourly image cap.</summary>
    private ToolResult SeeDirect(SeeLook l, Func<string, string> tail, Func<DateTimeOffset, bool> takeShot)
    {
        if (!takeShot(_time.GetUtcNow()))
            return ToolResult.Success($"image budget used ({_o.ImagesPerHour}/h); text map instead:\n" + VisionTools.LookMap(_c, 15));
        var (focus, radius, question) = (l.Focus, l.Radius, l.Question);
        var sheet = Sheet(focus, radius, l.Views, l.Player, l.Scene, l.Pose, l.Aids, l.ViewParams);
        var text = sheet.Legend + (sheet.Labels.Count > 0 ? " Labels: " + string.Join("; ", sheet.Labels.Take(6)) : "")
            + (string.IsNullOrWhiteSpace(question) ? "" : " Q: " + question);
        var stub = string.Create(CultureInfo.InvariantCulture, $"[picture omitted: see sheet r{radius} at {focus.X},{focus.Y},{focus.Z}]");
        var image = new LlmImage(sheet.Png, sheet.Width, sheet.Height, stub);
        Capture(image.Data, image.Width, image.Height, "sheet", "see", "direct", ViewNames(l.Views, l.ViewParams), focus, radius, question, l.Call, tail(text));
        return ToolResult.Success(tail(text)) with { Image = image };
    }
}
