using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Bot.Memory;
using MineClaw.Render;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.GameTools;

/// <summary>Settings of the <c>see</c> tool and the <c>look</c> map ("Bot:Vision").</summary>
public sealed class VisionOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Hard cap on pictures per rolling hour (per bot); over it, <c>see</c> answers with the text map instead.</summary>
    public int ImagesPerHour { get; set; } = 30;
    public int DefaultRadius { get; set; } = 12;
    public int EyeWidth { get; set; } = 320;
    public int EyeHeight { get; set; } = 240;
    /// <summary>Use the b1.8.1 textures when a client jar / extracted atlas is available (flat colours otherwise).</summary>
    public bool Textures { get; set; } = true;
    /// <summary>Eye view: smooth lighting (per-vertex light) instead of vanilla "Fast" flat per-face light.</summary>
    public bool SmoothLighting { get; set; }
    /// <summary>Explicit b1.8.1 client jar (the "Render:ClientJar" setting).</summary>
    public string? ClientJar { get; set; }
    /// <summary>Extracted textures + cached jar; default "&lt;Agent:DataDirectory&gt;/assets" (gitignored).</summary>
    public string? AssetsDir { get; set; }
    /// <summary>Download Mojang's official client jar (SHA-1 verified) when nothing local is found.</summary>
    public bool AllowDownload { get; set; } = true;
}

/// <summary>
/// The <c>see</c> tool (a PNG for the model: top map / iso / side slice / first-person eye view, with grid, coordinates,
/// compass, labels from memory) and the data behind <c>look {map:true}</c>. Pictures ride in the tool_result of the newest
/// turn only: compaction swaps each for a one-line stub, and a rolling hourly cap keeps the bots from spamming screenshots.
/// </summary>
public static class VisionTools
{
    public const string ToolName = "see";

    public const string Description =
        "PNG picture: top=map, 1-block grid+coords (default); iso=3D; side=slice facing N; eye=first-person. For building, " +
        "edges/rows/cells, which-cell disputes, unfamiliar builds, checking work. Quick check: look map. 30/h.";

    /// <summary>Per-world context for the look map (marks from memory), set by <see cref="Register"/>.</summary>
    private static readonly ConditionalWeakTable<WorldState, Context> Contexts = new();

    private sealed class Context
    {
        public Func<int, int, int, int, IReadOnlyList<VisionMark>>? Marks;
        public Func<string, bool>? IsHive;
    }

    /// <summary>The text map for <c>look {map:true}</c> (size 5..25, default 15).</summary>
    public static string LookMap(IGameClient c, int? size)
    {
        if (!c.IsPlaying) return "not in game";
        var w = c.World;
        var feet = w.Player.Position.ToBlockPos();
        int n = Math.Clamp(size ?? 15, 5, 25) | 1;
        Contexts.TryGetValue(w, out var ctx);
        var ents = Entities(c, feet, n / 2 + 1, ctx?.IsHive);
        var marks = ctx?.Marks?.Invoke(feet.X, feet.Y, feet.Z, n / 2) ?? [];
        return AsciiMap.Render(w.PlanningView, feet.X, feet.Y, feet.Z, n, (feet.X, feet.Y, feet.Z), ents, marks);
    }

    internal static List<VisionEntity> Entities(IGameClient c, BlockPos centre, int radius, Func<string, bool>? isHive)
    {
        var list = new List<VisionEntity>();
        var w = c.World;
        foreach (var e in w.Entities.Nearby(new Vec3d(centre.X + 0.5, centre.Y, centre.Z + 0.5), radius * 1.5 + 2))
        {
            switch (e.Kind)
            {
                case EntityKind.Player:
                    if (e.Name == null || e.Name.Equals(c.Username, StringComparison.OrdinalIgnoreCase)) continue;
                    bool hive = isHive?.Invoke(e.Name) == true;
                    list.Add(new VisionEntity(hive ? "hive" : "player", e.Name, e.Position.X, e.Position.Y, e.Position.Z, hive ? HiveGlyph(e.Name) : null));
                    break;
                case EntityKind.Mob:
                    list.Add(new VisionEntity("mob", e.DisplayName, e.Position.X, e.Position.Y, e.Position.Z));
                    break;
                case EntityKind.Item:
                    list.Add(new VisionEntity("item", null, e.Position.X, e.Position.Y, e.Position.Z));
                    break;
            }
        }
        return list;
    }

    /// <summary>Hive bot glyph like the TUI roster: first digit of the numeric suffix (maid_chan_67 → 6), else 'M'.</summary>
    public static char HiveGlyph(string name)
    {
        var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return digits.Length > 0 ? digits[0] : 'M';
    }

    /// <summary>Short label of an indexed object kind (null = not labelled).</summary>
    private static string? ObjectLabel(string kind) => kind switch
    {
        "chest" => "chest",
        "furnace" => "furnace",
        "crafting_table" => "bench",
        "cobble_generator" => "gen",
        "farm" => "farm",
        "portal" => "portal",
        _ => null,
    };

    /// <summary>Registers <c>see</c> (brain + worker) after the brain split; also enables marks in the look map.</summary>
    public static void Register(AgentRuntime rt, IGameClient c, IPlaceResolver places, VisionOptions? options = null,
        Perception.GestureTracker? gestures = null, Func<IEnumerable<string>>? hiveBots = null, TimeProvider? time = null, ILogger? log = null)
    {
        var o = options ?? new VisionOptions();
        time ??= TimeProvider.System;
        bool IsHive(string n) => hiveBots?.Invoke().Contains(n, StringComparer.OrdinalIgnoreCase) == true;

        IReadOnlyList<VisionMark> Marks(int cx, int cy, int cz, int r)
        {
            var list = new List<VisionMark>();
            try
            {
                int dim = c.World.Dimension;
                bool Near(double x, double z) => Math.Abs(x - cx) <= r + 2 && Math.Abs(z - cz) <= r + 2;
                var (wps, sts) = rt.Memory.Read(d => (d.Waypoints.Where(w => w.Dimension == dim).ToList(), d.Structures.Where(s => s.Dimension == dim).ToList()));
                foreach (var s in sts)
                {
                    if (s.Min == null) continue;
                    var mx = s.Max ?? s.Min;
                    var m = new VisionMark(s.Name, Math.Min(s.Min.X, mx.X), Math.Min(s.Min.Y, mx.Y), Math.Min(s.Min.Z, mx.Z),
                        Math.Max(s.Min.X, mx.X), Math.Max(s.Min.Y, mx.Y), Math.Max(s.Min.Z, mx.Z), Box: true);
                    if (Near(m.CentreX, m.CentreZ) || (m.MaxX >= cx - r && m.MinX <= cx + r && m.MaxZ >= cz - r && m.MinZ <= cz + r)) list.Add(m);
                }
                foreach (var w in wps) if (Near(w.X, w.Z)) list.Add(VisionMark.At(w.Name, w.X, w.Y, w.Z));
                foreach (var (row, _) in rt.Observations.Nearest(null, cx, cy, cz, 40, r * 1.5 + 2))
                {
                    if (ObjectLabel(row.Kind) is not { } label || row.Bounds is not { } b) continue;
                    int x0 = (int)Math.Floor(b.MinX), y0 = (int)Math.Floor(b.MinY), z0 = (int)Math.Floor(b.MinZ);
                    int x1 = (int)Math.Floor(b.MaxX), y1 = (int)Math.Floor(b.MaxY), z1 = (int)Math.Floor(b.MaxZ);
                    if (list.Any(m => Math.Abs(m.CentreX - (x0 + x1) / 2.0) <= 2 && Math.Abs(m.CentreZ - (z0 + z1) / 2.0) <= 2
                        && (m.Box || VisionRenderer.ShortLabel(m.Text).Equals(label, StringComparison.OrdinalIgnoreCase)))) continue;
                    list.Add(new VisionMark(label, x0, y0, z0, x1, y1, z1, Box: x1 > x0 || z1 > z0));
                }
            }
            catch (Exception ex) { log?.LogDebug(ex, "see: marks unavailable"); }
            return list.Take(14).ToList();
        }
        Contexts.AddOrUpdate(c.World, new Context { Marks = Marks, IsHive = IsHive });
        if (!o.Enabled) return;

        // Textures: found once in the background (local jar, extracted atlas, or a verified download); flat colours meanwhile.
        var texOptions = new TextureOptions
        {
            ClientJar = o.ClientJar,
            AssetsDir = o.AssetsDir ?? Path.Combine(rt.Options.DataDirectory, "assets"),
            AllowDownload = o.AllowDownload,
        };
        // Lazy: nothing is searched (or downloaded) until the first picture is asked for.
        var atlasTask = new Lazy<Task<TextureAtlas?>>(() => o.Textures
            ? Task.Run(() => TextureLocator.Load(texOptions, m => log?.LogInformation("see: {Line}", m)))
            : Task.FromResult<TextureAtlas?>(null));
        var renderer = new VisionRenderer();
        var eye = new EyeRenderer { Width = Math.Clamp(o.EyeWidth, 64, 512), Height = Math.Clamp(o.EyeHeight, 48, 384) };
        var voxels = new WorldVoxels(c.World);
        var gate = new object();
        var shots = new Queue<DateTimeOffset>();
        Perception.Gesture? lastPoint = null;
        if (gestures != null) gestures.Pointed += g => lastPoint = g;

        rt.Tools.Register(ToolName, Description,
            ToolSchema.Create().Str("view", required: false, enumValues: ["top", "iso", "side", "eye"])
                .Int("radius", "8-24", required: false)
                .Str("focus", "player, place or x,y,z", required: false).Build(),
            (i, ct) =>
            {
                if (!c.IsPlaying) return Task.FromResult(ToolResult.Error("not in game"));
                var now = time.GetUtcNow();
                lock (gate)
                {
                    while (shots.Count > 0 && now - shots.Peek() > TimeSpan.FromHours(1)) shots.Dequeue();
                    if (shots.Count >= Math.Max(0, o.ImagesPerHour))
                        return Task.FromResult(ToolResult.Success($"image budget used ({o.ImagesPerHour}/h); text map instead:\n" + LookMap(c, 15)));
                }
                var at = atlasTask.Value;
                var atlas = at.Wait(TimeSpan.FromSeconds(3)) ? at.Result : null; // still loading: flat colours this once
                var result = Render(i, c, places, rt, o, renderer, eye, voxels, atlas, Marks, IsHive, lastPoint, now);
                if (result.Image != null) lock (gate) shots.Enqueue(now);
                return Task.FromResult(result);
            }, tiers: ToolTiers.Main | ToolTiers.Worker, maxResultChars: 700);
    }

    private static readonly string[] TargetKeys = ["to", "at", "target", "pos"];

    /// <summary>The block the current activity works on (its first coordinate argument), if any.</summary>
    private static BlockPos? ActivityTarget(AgentRuntime rt, IPlaceResolver places)
    {
        if (rt.Activities.Current is not { } a) return null;
        foreach (var (_, input) in a.Steps)
        {
            if (input.ValueKind != JsonValueKind.Object) continue;
            foreach (var k in TargetKeys)
                if (input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s)
                    if ((Locations.ParseCoords(s) ?? places.Resolve(s)) is { } p) return p;
        }
        return null;
    }

    internal static ToolResult Render(JsonElement i, IGameClient c, IPlaceResolver places, AgentRuntime rt, VisionOptions o,
        VisionRenderer renderer, EyeRenderer eye, WorldVoxels voxels, TextureAtlas? atlas,
        Func<int, int, int, int, IReadOnlyList<VisionMark>> marks, Func<string, bool> isHive, Perception.Gesture? lastPoint, DateTimeOffset now)
    {
        var w = c.World;
        var me = w.Player.Position;
        var feet = me.ToBlockPos();
        string view = (i.Str("view") ?? "top").ToLowerInvariant();
        int radius = Math.Clamp(i.Int("radius") ?? o.DefaultRadius, 8, 24);
        string? focus = i.Str("focus")?.Trim();
        BlockPos centre = feet;
        Entity? focusPlayer = null;
        BlockPos? asked = null;
        if (!string.IsNullOrEmpty(focus) && !focus.Equals("you", StringComparison.OrdinalIgnoreCase) && !focus.Equals("me", StringComparison.OrdinalIgnoreCase)
            && !focus.Equals("player", StringComparison.OrdinalIgnoreCase) && !focus.Equals(c.Username, StringComparison.OrdinalIgnoreCase))
        {
            if (Locations.ParseCoords(focus) is { } p) { centre = p; asked = p; }
            else if (w.Entities.FindPlayer(focus) is { } e) { focusPlayer = e; centre = e.Position.ToBlockPos(); }
            else if (places.Resolve(focus) is { } q) { centre = q; asked = q; }
            else return ToolResult.Error($"unknown focus '{focus}' (player, saved name or x,y,z)");
        }
        else if (focus is not null && focus.Equals("player", StringComparison.OrdinalIgnoreCase)
            && Agent.TurnContext.Current?.Speaker is { } who && w.Entities.FindPlayer(who) is { } sp)
        {
            focusPlayer = sp;
            centre = sp.Position.ToBlockPos();
        }

        var target = asked ?? ActivityTarget(rt, places);
        var pointed = lastPoint is { } g && now - g.At < TimeSpan.FromMinutes(10) ? lastPoint : null;
        var ents = Entities(c, centre, radius + 4, isHive);
        var ms = marks(centre.X, centre.Y, centre.Z, radius);
        bool nether = w.Dimension == -1;
        VisionImage img;
        string kind;
        if (view == "eye")
        {
            double ex, ey, ez;
            float yaw, pitch;
            string? viewer = null;
            if (focusPlayer is { } fp)
            {
                (ex, ey, ez, yaw, pitch, viewer) = (fp.Position.X, fp.Position.Y + 1.62, fp.Position.Z, fp.Yaw, fp.Pitch, fp.Name);
                ents.RemoveAll(e => e.Name != null && e.Name.Equals(fp.Name, StringComparison.OrdinalIgnoreCase));
                ents.Add(new VisionEntity("hive", c.Username, me.X, me.Y, me.Z, '@'));
            }
            else
            {
                (ex, ey, ez) = (me.X, me.Y + 1.62, me.Z);
                (yaw, pitch) = (w.Player.Yaw, w.Player.Pitch);
                if (asked is { } t)
                {
                    double dx = t.X + 0.5 - ex, dy = t.Y + 0.5 - ey, dz = t.Z + 0.5 - ez;
                    yaw = (float)(Math.Atan2(-dx, dz) * 180 / Math.PI);
                    pitch = (float)(-Math.Atan2(dy, Math.Sqrt(dx * dx + dz * dz)) * 180 / Math.PI);
                }
            }
            img = eye.Render(new EyeScene
            {
                World = voxels, Atlas = atlas, EyeX = ex, EyeY = ey, EyeZ = ez, Yaw = yaw, Pitch = pitch, Time = w.Time, Nether = nether,
                Smooth = o.SmoothLighting, Entities = ents, Marks = ms, Target = target is { } tt ? (tt.X, tt.Y, tt.Z) : null,
                Pointed = pointed is { } pp ? (pp.Pos.X, pp.Pos.Y, pp.Pos.Z) : null, Viewer = viewer,
            });
            kind = "eye";
        }
        else
        {
            var v = view switch { "iso" => VisionView.Iso, "side" => VisionView.Side, _ => VisionView.Top };
            voxels.Prepare(centre.X, centre.Z, radius / 16 + 2);
            img = renderer.Render(new VisionScene
            {
                View = v, Radius = radius, Cx = centre.X, Cy = centre.Y, Cz = centre.Z, Blocks = w.PlanningView,
                Self = (feet.X, feet.Y, feet.Z), Entities = ents, Marks = ms, Target = target is { } tt ? (tt.X, tt.Y, tt.Z) : null,
                Pointed = pointed is { } pp ? (pp.Pos.X, pp.Pos.Y, pp.Pos.Z) : null, PointedBy = pointed?.Player, Nether = nether,
                Atlas = atlas, Light = voxels, Time = w.Time,
            });
            kind = v.ToString().ToLowerInvariant();
        }
        var text = img.Legend + (img.Labels.Count > 0 ? " Labels: " + string.Join("; ", img.Labels.Take(8)) : "");
        var stub = string.Create(CultureInfo.InvariantCulture, $"[picture omitted: see {kind} r{radius} at {centre.X},{centre.Y},{centre.Z}]");
        return ToolResult.Success(text) with { Image = new LlmImage(img.Png, img.Width, img.Height, stub) };
    }
}
