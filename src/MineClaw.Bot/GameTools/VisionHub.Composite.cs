using System.Globalization;
using System.Text.Json;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Render;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// The composite <c>see</c>: one image per call made of panels the brain picks, each a camera anywhere the hive knows:
/// <c>eye 67</c> (a body's first person: main, a clone, a player), <c>cam &lt;at&gt; &lt;N..NW|above&gt; [dist]</c> (a free camera on a
/// coord / landmark / structure / bot, rendered from the merged world memory with its staleness), <c>orbit &lt;at&gt; [n]</c>
/// (n views evenly around a spot), <c>follow &lt;name&gt;</c> (over a player's or bot's shoulder), and the classic names around
/// the default focus (eye shoulder orbit orbit2..4 high iso top side layers). Objects work too:
/// <c>{"cam":{"at":"tower","from":"NE","dist":14}}</c>, <c>{"eye":"67"}</c>, <c>{"orbit":{"at":"gen","n":3}}</c>,
/// <c>{"follow":"robinchan"}</c>. At most <see cref="ContactSheet.MaxPanels"/> panels share the fixed pixel budget.
/// </summary>
public sealed partial class VisionHub
{
    /// <summary>One requested panel (or n orbit panels).</summary>
    /// <param name="Cam">Camera adjustments ("iso yaw=200 pitch=50 zoom=1.3", "top zoom=2 dx=3"): empty = the view's default camera.</param>
    public sealed record PanelRequest(string Kind, string? At = null, string? From = null, double? Dist = null, int N = 1, CamParams Cam = default)
    {
        /// <summary>The panel in the see tool's string form (what the history and "copy as see" show).</summary>
        public string Format() => new SeeView(Kind, Cam, Kind switch
        {
            "cam" => string.Join(' ', new[] { At, From, Dist?.ToString("0.#", CultureInfo.InvariantCulture) }.Where(x => !string.IsNullOrEmpty(x))),
            "orbit" when N > 1 => string.Join(' ', new[] { At, N.ToString(CultureInfo.InvariantCulture) }.Where(x => !string.IsNullOrEmpty(x))),
            _ => string.IsNullOrEmpty(At) ? null : At,
        }).Format();

        public int Count => Kind == "orbit" && At != null ? Math.Clamp(N, 1, ContactSheet.MaxPanels) : 1;
        /// <summary>A panel that needs the composite path (its own spot or body), not just a named view around the focus.</summary>
        public bool Composite => At != null || Kind is "cam" or "follow";
    }

    private static readonly HashSet<string> Compass = new(StringComparer.OrdinalIgnoreCase) { "N", "NE", "E", "SE", "S", "SW", "W", "NW", "above", "up", "top" };

    /// <summary>Parses <c>views</c> (strings and/or objects). Null without views; an error for unknown kinds or too many panels.</summary>
    public static List<PanelRequest>? ParseViews(JsonElement i, out string? error)
    {
        error = null;
        if (i.ValueKind != JsonValueKind.Object || !i.TryGetProperty("views", out var v)) return null;
        var items = v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : [v];
        var list = new List<PanelRequest>();
        foreach (var e in items)
        {
            if (ParseView(e) is not { } p)
            {
                error = $"unknown view '{(e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())}' (eye [body], shoulder, orbit [at] [n], high, iso, top, side, layers, cam <at> <N..NW|above> [dist], follow <name>)";
                return null;
            }
            list.Add(p);
        }
        int total = list.Sum(p => p.Count);
        if (total > ContactSheet.MaxPanels)
        {
            error = $"{total} panels asked: at most {ContactSheet.MaxPanels} per see (they share one image). Split it into two see calls.";
            return null;
        }
        return list.Count == 0 ? null : list;
    }

    private static PanelRequest? ParseView(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => ParseString(e.GetString() ?? ""),
        JsonValueKind.Object => ParseObject(e),
        _ => null,
    };

    /// <summary>"eye", "eye 67", "eye:67", "cam tower NE 14", "cam 4,64,6 above", "orbit gen 3", "follow robinchan", "high"...</summary>
    public static PanelRequest? ParseString(string s)
    {
        var t = s.Trim();
        if (t.Length == 0) return null;
        var (kind, rest) = KindAndRest(t);
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        // Camera adjustments ride as key=value words anywhere after the kind: "iso yaw=200 pitch=50", "cam tower NE pitch=60".
        var cam = CamParams.Extract(words);
        rest = string.Join(' ', words);
        return kind switch
        {
            "cam" => CamPanel(words, cam),
            "orbit" => OrbitPanel(words, cam),
            _ => NamedPanel(kind, rest, cam),
        };
    }

    /// <summary>"cam tower NE" → ("cam", "tower NE"); "eye:67" → ("eye", "67"); camera/freecam are cam.</summary>
    private static (string Kind, string Args) KindAndRest(string t)
    {
        int sp = t.IndexOfAny([' ', ':']);
        string kind = (sp < 0 ? t : t[..sp]).Trim().ToLowerInvariant(), rest = sp < 0 ? "" : t[(sp + 1)..].Trim();
        return (kind is "camera" or "freecam" ? "cam" : kind, rest);
    }

    /// <summary>"cam &lt;at&gt; [side] [dist]": the trailing number is the distance, then a compass side.</summary>
    private static PanelRequest? CamPanel(List<string> words, CamParams cam)
    {
        double? dist = null;
        string? from = null;
        if (words.Count > 1 && double.TryParse(words[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { dist = d; words.RemoveAt(words.Count - 1); }
        if (words.Count > 1 && Compass.Contains(words[^1])) { from = words[^1]; words.RemoveAt(words.Count - 1); }
        return words.Count == 0 ? null : new PanelRequest("cam", string.Join(' ', words), from, dist, Cam: cam);
    }

    /// <summary>"orbit [at] [n]".</summary>
    private static PanelRequest OrbitPanel(List<string> words, CamParams cam)
    {
        int n = 1;
        if (words.Count > 1 && int.TryParse(words[^1], out var k)) { n = k; words.RemoveAt(words.Count - 1); }
        else if (words.Count == 1 && int.TryParse(words[0], out var k1)) { n = k1; words.Clear(); }
        if (words.Count == 0) return n > 1 ? new PanelRequest("orbit", "", N: n, Cam: cam) : new PanelRequest("orbit", Cam: cam);
        return new PanelRequest("orbit", string.Join(' ', words), N: n, Cam: cam);
    }

    /// <summary>"eye [body]", "follow &lt;name&gt;", or a named view of the contact sheet (null = unknown).</summary>
    private static PanelRequest? NamedPanel(string kind, string rest, CamParams cam) => kind switch
    {
        "eye" => new PanelRequest("eye", rest.Length > 0 ? rest : null),
        "follow" => rest.Length > 0 ? new PanelRequest("follow", rest) : null,
        _ => ContactSheet.Known.Contains(kind) ? new PanelRequest(kind, rest.Length > 0 ? rest : null, Cam: cam) : null,
    };

    private static PanelRequest? ParseObject(JsonElement o)
    {
        foreach (var prop in o.EnumerateObject())
        {
            var kind = prop.Name.ToLowerInvariant();
            var val = prop.Value;
            if (val.ValueKind == JsonValueKind.String) return ParseString(kind + " " + val.GetString());
            if (val.ValueKind == JsonValueKind.Object) return ObjectPanel(kind, val);
            if (val.ValueKind == JsonValueKind.True && ContactSheet.Known.Contains(kind)) return new PanelRequest(kind);
        }
        return null;
    }

    /// <summary>{"cam":{"at":"tower","from":"NE","dist":14}}, {"orbit":{"at":"gen","n":3}}, {"eye":{"body":"67"}}...</summary>
    private static PanelRequest? ObjectPanel(string kind, JsonElement val)
    {
        string? at = val.Str("at"), from = val.Str("from");
        double? dist = NumberOf(val, "dist");
        int n = val.Int("n") ?? 1;
        var cam = ObjectCam(kind, val);
        return kind switch
        {
            "cam" or "camera" => at == null ? null : new PanelRequest("cam", at, from, dist, Cam: cam),
            "orbit" => new PanelRequest("orbit", at ?? "", N: n, Cam: cam),
            "eye" => new PanelRequest("eye", at ?? val.Str("body")),
            _ => FollowOrNamed(kind, at, cam),
        };
    }

    private static double? NumberOf(JsonElement val, string key) =>
        val.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static PanelRequest? FollowOrNamed(string kind, string? at, CamParams cam) =>
        kind == "follow" ? (at == null ? null : new PanelRequest("follow", at))
        : ContactSheet.Known.Contains(kind) ? new PanelRequest(kind, at, Cam: cam) : null;

    /// <summary>{"iso":{"yaw":200,"pitch":50,"zoom":1.3}}, {"top":{"zoom":2,"dx":3}}: the same keys as the string form (a cam's
    /// dist is its distance, not a camera key).</summary>
    private static CamParams ObjectCam(string kind, JsonElement val)
    {
        var cam = new CamParams();
        foreach (var k in val.EnumerateObject())
            if (CamParams.Keys.Contains(k.Name) && !(kind is "cam" or "camera" && k.Name == "dist"))
                CamParams.TryToken(k.Name + "=" + (k.Value.ValueKind == JsonValueKind.String ? k.Value.GetString() : k.Value.GetRawText()), ref cam);
        return cam;
    }

    /// <summary>A body by name: main ("me", its name), a clone (its live pose over the view IPC, else its snapshot), or a player.</summary>
    private async Task<(PerspectivePanel.Body Body, int Dim, string Label, string? Note)?> BodyAsync(string? name, CancellationToken ct)
    {
        var w = _c.World;
        var me = w.Player;
        var n = name?.Trim() ?? "";
        if (IsMainName(n))
            return (new PerspectivePanel.Body(me.Position.X, me.Position.Y, me.Position.Z, me.Yaw, me.Pitch, _c.Username), w.Dimension, "MAIN", null);
        var target = ResolveTarget(n);
        if (target.Bot is { } bot) return await CloneBodyAsync(bot, ct).ConfigureAwait(false);
        if (w.Entities.FindPlayer(StripViewPhrase(n)) is { } p)
            return (new PerspectivePanel.Body(p.Position.X, p.Position.Y, p.Position.Z, p.Yaw, p.Pitch, p.Name), w.Dimension, (p.Name ?? n).ToUpperInvariant(), null);
        return null;
    }

    private bool IsMainName(string n) =>
        n.Length == 0 || n is "me" or "self" or "you" or "main" || n.Equals(_c.Username, StringComparison.OrdinalIgnoreCase);

    /// <summary>A clone's live pose over the view IPC, else its snapshot (with how old it is).</summary>
    private async Task<(PerspectivePanel.Body Body, int Dim, string Label, string? Note)> CloneBodyAsync(HiveBotState bot, CancellationToken ct)
    {
        HiveEyePose? pose = null;
        try { pose = Hive == null ? null : await Hive.ViewAsync(bot.Name, ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { _log?.LogDebug(ex, "composite: view request to {Bot} failed", bot.Name); }
        if (pose != null) return (new PerspectivePanel.Body(pose.X, pose.Y, pose.Z, pose.Yaw, pose.Pitch, bot.Short), pose.Dim, bot.Short.ToUpperInvariant(), null);
        return (new PerspectivePanel.Body(bot.X, bot.Y, bot.Z, 0, 20, bot.Short), bot.Dim, bot.Short.ToUpperInvariant(),
            $"{bot.Short} didn't answer: its last snapshot, {RegionWorld.Age(_time.GetUtcNow() - bot.At)} old, facing S");
    }

    /// <summary>The context of one spot (its scene: live or the shared memory; entities, marks, roles, staleness).</summary>
    private PanelContext ContextFor(BlockPos focus, int dim, int radius, PerspectivePanel.Body? body, string? landmark, bool aids, IReadOnlyList<(int, int, int)>? spots = null)
    {
        var scene = SceneFor(focus, dim, radius);
        bool mainHere = scene.Dim == _c.World.Dimension;
        string? stale = scene.Region?.Staleness((focus.X, focus.Y, focus.Z), radius, _time.GetUtcNow(), landmark);
        if (stale is { Length: > 48 }) stale = stale[..47] + "…";
        return new PanelContext
        {
            Blocks = scene.Blocks, Light = scene.Light, Focus = (focus.X, focus.Y, focus.Z), FeetY = focus.Y, Radius = radius, Roles = scene.Roles.Map,
            Entities = EntitiesNear(scene.Dim, focus, radius + 4), Marks = mainHere ? _marks(focus.X, focus.Y, focus.Z, radius) : [], Atlas = Atlas(),
            Time = _c.World.Time, Weather = WeatherIn(scene.Dim), Nether = scene.Dim == -1, Body = body, Aids = aids, Spots = spots ?? [],
            Staleness = stale == null ? null : "seen " + stale.Replace(" seen by ", " by ", StringComparison.Ordinal),
        };
    }

    /// <summary>Resolves the panels into jobs (bodies' poses, remote spots from the world memory). Errors name the bad panel.</summary>
    public async Task<(List<PanelJob> Jobs, List<string> Notes, string? Error)> PanelsAsync(IReadOnlyList<PanelRequest> reqs, BlockPos focus, int dim, int radius,
        bool aids, CancellationToken ct)
    {
        var jobs = new List<PanelJob>();
        var notes = new List<string>();
        var mainBody = (await BodyAsync(null, ct).ConfigureAwait(false))!.Value.Body;
        var home = new HomePanels(this, focus, dim, radius, aids, mainBody);
        foreach (var r in reqs)
        {
            string? error = r.Kind switch
            {
                "eye" when r.At != null => await BodyPanelAsync(r, radius, aids, jobs, notes, ct).ConfigureAwait(false),
                "follow" => await BodyPanelAsync(r, radius, aids, jobs, notes, ct).ConfigureAwait(false),
                "cam" => SpotPanels(r, radius, aids, jobs),
                "orbit" when !string.IsNullOrEmpty(r.At) => SpotPanels(r, radius, aids, jobs),
                _ when r.At is { Length: > 0 } => SpotPanels(r, radius, aids, jobs),
                "orbit" when r.N > 1 => home.Orbits(r, jobs),
                _ => home.View(r, jobs),
            };
            if (error != null) return ([], notes, error);
        }
        return (jobs.Take(ContactSheet.MaxPanels).ToList(), notes, null);
    }

    /// <summary>"eye 67" / "follow Fox": a panel through (or behind) a body or player. Error when there's none by that name.</summary>
    private async Task<string?> BodyPanelAsync(PanelRequest r, int radius, bool aids, List<PanelJob> jobs, List<string> notes, CancellationToken ct)
    {
        if (await BodyAsync(r.At, ct).ConfigureAwait(false) is not { } b) return $"no body or player '{r.At}' to look through";
        if (b.Note != null) notes.Add(b.Note);
        var at = new BlockPos((int)Math.Floor(b.Body.X), (int)Math.Floor(b.Body.Y), (int)Math.Floor(b.Body.Z));
        var ctx = ContextFor(at, b.Dim, radius, b.Body, null, aids);
        var rig = r.Kind == "eye" ? PerspectivePanel.EyeOf(b.Body, "EYE " + b.Label) : PerspectivePanel.Follow(b.Body, b.Label);
        jobs.Add(new PanelJob { Ctx = ctx, Rig = rig });
        return null;
    }

    /// <summary>A panel at its own spot ("cam 5,65,7 NE", "orbit gen 3", "iso main house"). Error when the spot is unknown.</summary>
    private string? SpotPanels(PanelRequest r, int radius, bool aids, List<PanelJob> jobs)
    {
        var t = ResolveTarget(r.At);
        if (t.Error != null || t.HiveView) return $"cam: unknown spot '{r.At}' (x,y,z, a landmark/structure, a bot or a player)";
        var spot = t.Bot is { } bot ? new BlockPos((int)Math.Floor(bot.X), (int)Math.Floor(bot.Y), (int)Math.Floor(bot.Z)) : t.Focus;
        var ctx = ContextFor(spot, t.Bot?.Dim ?? t.Dim, radius, null, t.Landmark, aids);
        AddSpotPanels(r, ctx, "@" + Short(t.Landmark ?? t.Bot?.Short ?? r.At!), radius, jobs);
        return null;
    }

    /// <summary>The spot's panel(s) by kind: a camera, orbits, a titled 3D view, else a flat map.</summary>
    private static void AddSpotPanels(PanelRequest r, PanelContext ctx, string label, int radius, List<PanelJob> jobs)
    {
        if (r.Kind == "cam") jobs.Add(new PanelJob { Ctx = ctx, Rig = PerspectivePanel.Cam(ctx.Focus, ctx.FeetY, r.From, r.Dist, "CAM " + label, radius, r.Cam) });
        else if (r.Kind == "orbit" && (r.N > 1 || r.Cam.IsEmpty)) jobs.AddRange(PerspectivePanel.Orbits(ctx.Focus, ctx.FeetY, radius, r.N, "ORBIT " + label).Select(g => new PanelJob { Ctx = ctx, Rig = g }));
        else if (PerspectivePanel.Views.Contains(r.Kind) && PerspectivePanel.For(r.Kind, ctx.Focus, ctx.FeetY, radius, null, null, r.Cam) is { } g2) jobs.Add(new PanelJob { Ctx = ctx, Rig = g2 with { Title = g2.Title + " " + label } });
        else jobs.Add(new PanelJob { Ctx = ctx, Flat = r.Kind, Params = r.Cam });
    }

    /// <summary>The panels around the default focus: its context (made once, on first use) and main's eye looking at it.</summary>
    private sealed class HomePanels(VisionHub hub, BlockPos focus, int dim, int radius, bool aids, PerspectivePanel.Body mainBody)
    {
        private PanelContext? _ctx;
        private readonly EyeScene _eye = new()
        {
            World = new BlockAccessVoxels(hub._c.World.PlanningView), EyeX = mainBody.X, EyeY = mainBody.Y + 1.62, EyeZ = mainBody.Z,
            Yaw = EyeScene.LookAt(mainBody.X, mainBody.Y + 1.62, mainBody.Z, focus.X + 0.5, focus.Y + 0.5, focus.Z + 0.5).Yaw,
            Pitch = EyeScene.LookAt(mainBody.X, mainBody.Y + 1.62, mainBody.Z, focus.X + 0.5, focus.Y + 0.5, focus.Z + 0.5).Pitch, Viewer = hub._c.Username,
        };

        private PanelContext Ctx => _ctx ??= hub.ContextFor(focus, dim, radius, dim == hub._c.World.Dimension ? mainBody : null, null, aids);

        /// <summary>"orbit 3" around the default focus.</summary>
        public string? Orbits(PanelRequest r, List<PanelJob> jobs)
        {
            jobs.AddRange(PerspectivePanel.Orbits(Ctx.Focus, Ctx.FeetY, radius, r.N, "ORBIT").Select(g => new PanelJob { Ctx = Ctx, Rig = g }));
            return null;
        }

        /// <summary>A named view of the default focus: a 3D rig (main's eye for "eye"), else a flat map.</summary>
        public string? View(PanelRequest r, List<PanelJob> jobs)
        {
            var ctx = Ctx;
            if (PerspectivePanel.Views.Contains(r.Kind))
            {
                var rig = PerspectivePanel.For(r.Kind, ctx.Focus, ctx.FeetY, radius, ctx.Body, r.Kind == "eye" ? _eye : null, r.Cam)
                    ?? PerspectivePanel.For("orbit3", ctx.Focus, ctx.FeetY, radius, null, null);
                jobs.Add(new PanelJob { Ctx = ctx, Rig = rig });
            }
            else jobs.Add(new PanelJob { Ctx = ctx, Flat = r.Kind, Axis = hub.AxisFor(focus), Params = r.Cam });
            return null;
        }
    }

    private static string Short(string s) => s.Length <= 12 ? s.ToUpperInvariant() : s[..12].ToUpperInvariant();

    /// <summary>The composite image of resolved panels (the same renderer, budget and aids as the default sheet).</summary>
    public VisionImage Composite(IReadOnlyList<PanelJob> jobs, BlockPos focus, IReadOnlyList<string> notes, ContactSheet? sheets = null)
    {
        var head = string.Create(CultureInfo.InvariantCulture, $"see composite ({jobs.Count} panels) default focus {focus.X},{focus.Y},{focus.Z}");
        return (sheets ?? Sheets).Compose(jobs, ContactSheet.DefaultPixelBudget, head, notes, null);
    }

    /// <summary>The composite <c>see</c> (views naming spots, bodies or cameras): resolves, renders one image, then perception or
    /// the picture itself, like the classic sheet.</summary>
    private async Task<ToolResult> SeeCompositeAsync(JsonElement i, List<PanelRequest> reqs, VisionTarget target, int radius, bool aids, bool direct,
        Func<DateTimeOffset, bool> takeShot, CancellationToken ct)
    {
        var focus = target.Focus;
        var (jobs, notes, error) = await PanelsAsync(reqs, focus, target.Dim, radius, aids, ct).ConfigureAwait(false);
        if (error != null) return ToolResult.Error(error);
        if (jobs.Count == 0) return ToolResult.Error("no panels to render");
        string? question = i.Str("question")?.Trim();
        var scene = SceneFor(focus, target.Dim, radius);
        if (!direct && Perception is { Available: true } p)
        {
            var exact = VisionRouter.Answer(question, scene.Blocks, scene.Roles.Map, (focus.X, focus.Y, focus.Z), radius, EntitiesNear(scene.Dim, focus, radius));
            if (exact != null) return ToolResult.Success(string.Join(" | ", notes.Append(p.Exact(question ?? "", exact, "see").Text)));
            var img = Composite(jobs, focus, notes);
            var q = string.IsNullOrWhiteSpace(question) ? DefaultQuestion : question!;
            var rec = Capture(img.Png, img.Width, img.Height, "composite", "see", "perception", reqs.Select(r => r.Format()), focus, radius, q, i.GetRawText(), img.Legend);
            var a = await p.AskAsync(new PerceptionLook(img, q, scene.Blocks, scene.Roles.Map, (focus.X, focus.Y, focus.Z), radius, "see"), ct).ConfigureAwait(false);
            Answered(rec, a);
            return ToolResult.Success(string.Join(" | ", notes.Append(a.Text)));
        }
        if (!takeShot(_time.GetUtcNow()))
            return ToolResult.Success($"image budget used ({_o.ImagesPerHour}/h); text map instead:\n" + VisionTools.LookMap(_c, 15));
        var sheet = Composite(jobs, focus, notes);
        var text = sheet.Legend + (string.IsNullOrWhiteSpace(question) ? "" : " Q: " + question);
        var stub = string.Create(CultureInfo.InvariantCulture, $"[picture omitted: see composite of {jobs.Count} panels]");
        _log?.LogDebug("see composite: {Panels} panels, {W}x{H}, {Ms:0} ms", jobs.Count, sheet.Width, sheet.Height, Sheets.LastMs);
        var image = new LlmImage(sheet.Png, sheet.Width, sheet.Height, stub);
        Capture(image.Data, image.Width, image.Height, "composite", "see", "direct", reqs.Select(r => r.Format()), focus, radius, question, i.GetRawText(), text);
        return ToolResult.Success(text) with { Image = image };
    }
}
