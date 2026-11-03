using MineClaw.Agent.Telemetry;
using MineClaw.Bot;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Hive.Supervisor;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Supervisor;

/// <summary>
/// The supervisor's view of the live hive (main process): positions/jobs/claims and views from the hub's hive vision (clone
/// poses over the existing <c>view</c> IPC, the shared world memory), the exact <c>see</c> sheet composition
/// (<see cref="VisionHub.SheetRequest"/>, rendered by this class's own CPU-capped renderer), and the TUI's telemetry sources
/// for the token/thought/chat panels. Reads only: nothing here moves a bot, chats, or writes memory.
/// </summary>
public sealed class LiveSupervisorData : ISupervisorData
{
    private readonly VisionHub _hub;
    private readonly Func<WorldState> _world;
    private readonly string _main;
    private readonly IReadOnlyList<IBotTelemetrySource> _sources;
    private readonly Func<HiveSupervisor?> _hive;
    private readonly SupervisorFeed _feed;
    private readonly SupervisorOptions _o;
    private readonly ILogger? _log;
    private readonly TimeProvider _time;
    private readonly ContactSheet _sheet = new(new VisionRenderer { CacheCapacity = 24 }) { EyeThreads = 1 };
    private readonly EyeRenderer _eye;
    private readonly CameraRenderer _camera;
    private readonly Dictionary<string, (DateTimeOffset At, Task<HiveEyePose?> Pose)> _poses = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public LiveSupervisorData(VisionHub hub, Func<WorldState> world, string main, IReadOnlyList<IBotTelemetrySource> sources, Func<HiveSupervisor?> hive,
        SupervisorFeed feed, SupervisorOptions o, ILogger? log = null, TimeProvider? time = null)
    {
        _hub = hub;
        _world = world;
        _main = main;
        _sources = sources;
        _hive = hive;
        _feed = feed;
        _o = o;
        _log = log;
        _time = time ?? TimeProvider.System;
        _eye = new EyeRenderer { Width = Math.Clamp(o.EyeWidth, 64, 960), Height = Math.Clamp(o.EyeHeight, 48, 720), MaxThreads = 1 };
        _camera = new CameraRenderer(world, main, () => hub.WorldStore, hub.Atlas, _time, o.CameraDistance, o.CameraMaxWidth, o.CameraMaxHeight);
    }

    /// <summary>Every body this process controls (main, hives, their subhive bodies, spawning nodes); null = the hub's hive bots
    /// only (main and the hive clones), as before.</summary>
    public HiveBodyList? Bodies { get; init; }

    private HiveBotState? Find(string bot)
    {
        var bots = _hub.HiveBots();
        return bots.FirstOrDefault(b => b.Name.Equals(bot, StringComparison.OrdinalIgnoreCase) || b.Short.Equals(bot, StringComparison.OrdinalIgnoreCase))
            ?? (bot.Equals("main", StringComparison.OrdinalIgnoreCase) ? bots.FirstOrDefault(b => b.IsMain) : null)
            // a hive's subhive body: its last snapshot position (no eye pose over IPC: it isn't a slot of main's)
            ?? (Bodies?.Find(bot) is { IsMain: false, HasPos: true } r
                ? new HiveBotState(r.Name, r.Dim, r.X, r.Y, r.Z, r.Job, r.Hp, r.Food, r.At, false, r.Brain) : null);
    }

    public IReadOnlyList<BotTile> Bots()
    {
        var now = _time.GetUtcNow();
        IReadOnlyList<HiveLease> leases = [];
        try { leases = _hub.Hive?.Leases() ?? []; } catch (Exception ex) { _log?.LogDebug(ex, "supervisor: leases unavailable"); }
        IReadOnlyList<string> Claims(string name) =>
            leases.Where(l => l.Owner.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(l => HiveVision.Lease(l.Key).Text).ToList();
        if (Bodies is { } all)
            return all.Rows().Select(r => new BotTile(r.Name, r.Short, r.IsMain, r.Dim, r.X, r.Y, r.Z, r.Hp, r.Food, r.Job, r.Brain,
                Math.Round(Math.Max(0, (now - r.At).TotalSeconds), 1), Claims(r.Name), r.Kind, r.Owner, r.Script, r.Online, r.State)).ToList();
        return _hub.HiveBots().Select(b => new BotTile(b.Name, b.Short, b.IsMain, b.Dim, Math.Round(b.X, 2), Math.Round(b.Y, 2), Math.Round(b.Z, 2), b.Hp, b.Food,
            b.Job, b.Brain, Math.Round(Math.Max(0, (now - b.At).TotalSeconds), 1),
            leases.Where(l => l.Owner.Equals(b.Name, StringComparison.OrdinalIgnoreCase)).Select(l => HiveVision.Lease(l.Key).Text).ToList())).ToList();
    }

    /// <summary>A clone's eye pose over IPC (it flushes its world memory first), at most once per PoseCacheMs per clone.</summary>
    private Task<HiveEyePose?> PoseAsync(string clone, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_poses.TryGetValue(clone, out var p) && now - p.At < TimeSpan.FromMilliseconds(_o.PoseCacheMs)) return p.Pose;
            var t = _hub.Hive is { } h ? Safe(h.ViewAsync(clone, ct)) : Task.FromResult<HiveEyePose?>(null);
            _poses[clone] = (now, t);
            return t;
        }
    }

    private async Task<HiveEyePose?> Safe(Task<HiveEyePose?> t)
    {
        try { return await t.ConfigureAwait(false); }
        catch (Exception ex) { _log?.LogDebug(ex, "supervisor: view request failed"); return null; }
    }

    public Task<ViewImage?> ViewAsync(string bot, string mode, int radius, RenderGate gate, CancellationToken ct) =>
        ViewAsync(bot, mode, radius, MineClaw.Render.XRay.Auto, gate, ct);

    public async Task<ViewImage?> ViewAsync(string bot, string mode, int radius, MineClaw.Render.XRay xray, RenderGate gate, CancellationToken ct)
    {
        if (Find(bot) is not { } b) return null;
        var w = _world();
        HiveEyePose? pose = null;
        BlockPos focus;
        string? note = null;
        if (b.IsMain)
        {
            var me = w.Player;
            if (mode == "eye") pose = new HiveEyePose(_main, w.Dimension, me.Position.X, me.Position.Y, me.Position.Z, me.Yaw, me.Pitch, _time.GetUtcNow());
            // A plain `see` (no focus): the current job's target, else the feet. Exactly what the brain's sheet shows.
            focus = mode is "sheet" or "brain" ? _hub.ResolveFocus(null).Focus : me.Position.ToBlockPos();
        }
        else
        {
            pose = await PoseAsync(b.Name, ct).ConfigureAwait(false);
            focus = pose is { } p ? new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z))
                : new BlockPos((int)Math.Floor(b.X), (int)Math.Floor(b.Y), (int)Math.Floor(b.Z));
            if (pose == null) note = $"{b.Short} didn't answer the view request: last snapshot position, eye from 3 blocks south";
        }
        int dim = pose?.Dim ?? b.Dim;
        return await gate.RunAsync(() =>
        {
            var scene = _hub.SceneFor(focus, dim, radius);
            IReadOnlyList<string>? views = mode switch { "sheet" or "brain" => null, _ => [mode] };
            var req = _hub.SheetRequest(focus, radius, views, null, scene, pose, xray: xray);
            VisionImage img = mode == "eye" && req.Eye is { } e ? _eye.Render(e) : _sheet.Render(req);
            var facts = mode is "sheet" or "brain" ? _hub.RouterFacts(focus, radius, scene) : [];
            var seen = scene.Region?.Staleness((focus.X, focus.Y, focus.Z), radius, _time.GetUtcNow());
            return new ViewImage(img.Png, img.Width, img.Height, img.Tokens, img.Legend, facts, seen, note);
        }, ct).ConfigureAwait(false);
    }

    public Task<ViewImage> HiveAsync(RenderGate gate, CancellationToken ct) => gate.RunAsync(() =>
    {
        var r = _hub.HiveOverview(image: true);
        var img = r.Image ?? throw new InvalidOperationException("hive overview rendered no picture");
        return new ViewImage(img.Data, img.Width, img.Height, img.Tokens, r.Text, [r.Text]);
    }, ct);

    public Task<ViewImage> CameraAsync(CameraQuery q, RenderGate gate, CancellationToken ct) => gate.RunAsync(() =>
    {
        var w = _world();
        var at = new BlockPos((int)Math.Floor(q.X), (int)Math.Floor(q.Y), (int)Math.Floor(q.Z));
        var ents = _hub.EntitiesNear(q.Dim, at, _camera.Distance).ToList();
        if (q.Dim == w.Dimension) ents.Add(new VisionEntity("hive", _main, w.Player.Position.X, w.Player.Position.Y, w.Player.Position.Z, 'M'));
        return _camera.Render(q, ents, w.Time);
    }, ct);

    public LocateResult? Locate(string query)
    {
        var t = query.Trim();
        if (Locations.ParseCoords(t) is { } c) return new LocateResult(t, _world().Dimension, c.X + 0.5, c.Y + 1.62, c.Z + 0.5);
        if (Find(VisionHub.StripViewPhrase(t)) is { } b) return new LocateResult(b.Name, b.Dim, b.X, b.Y + 1.62, b.Z);
        if (_hub.Landmark(t) is { } lm) return new LocateResult(lm.Name, lm.Dim, lm.Pos.X + 0.5, lm.Pos.Y + 2.5, lm.Pos.Z + 0.5);
        return null;
    }

    public IReadOnlyList<FeedEvent> Events(long since, int max) => _feed.Since(since, max);

    /// <summary>The blueprint designer (build from a description); null = none (designs listed empty).</summary>
    public Func<MineClaw.Bot.Blueprints.BlueprintService?> Blueprints { get; init; } = () => null;

    public IReadOnlyList<object> Designs() =>
        Blueprints()?.Store.List(30).Select(r => (object)new
        {
            id = r.Id, name = r.Name, status = r.Status, summary = r.Summary, requester = r.Requester, rev = r.Rev, cost_usd = Math.Round(r.CostUsd, 4),
            picture = r.HasPicture, updated = r.Updated,
        }).ToList() ?? [];

    public ViewImage? DesignPicture(long id)
    {
        if (Blueprints() is not { } s || s.Store.Get(id) is not { } rec || s.Store.Picture(id) is not { } png) return null;
        int w = 0, h = 0;
        try { var d = MineClaw.Render.Png.Decode(png); (w, h) = (d.Width, d.Height); } catch (Exception) { }
        return new ViewImage(png, w, h, (w * h + 749) / 750, rec.Summary, [rec.Eval], Note: rec.Critique.Length > 0 ? "look: " + rec.Critique : null);
    }

    public object? DesignBill(long id) => Blueprints() is { } s && s.Store.Get(id) is { } rec ? s.BillJson(rec) : null;

    public string? BuildPlanPage(long id)
    {
        if (Blueprints() is not { Plans: { } plans } s || s.Store.Get(id) is not { } rec) return null;
        var parts = plans.ForBlueprint(id);
        if (parts.Count == 0 && s.PlanSummary(rec) != null) parts = plans.ForBlueprint(id); // not built yet: slice it now
        return parts.Count == 0 ? null : MineClaw.Bot.Building.BuildPlanView.Html(parts, $"#{rec.Id} {rec.Name} ({rec.Status})");
    }

    public object? BuildPlanData(long id) =>
        Blueprints() is { Plans: { } plans } ? plans.ForBlueprint(id) is { Count: > 0 } parts ? parts : null : null;

    public object Status(string? selected)
    {
        var bots = new List<object>();
        IReadOnlyList<BodyRow> rows = [];
        try { rows = Bodies?.Rows() ?? []; } catch (Exception ex) { _log?.LogDebug(ex, "supervisor: body list unavailable"); }
        var sources = Bodies?.Sources(_sources) ?? _sources;
        var byName = new Dictionary<string, IBotTelemetrySource>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sources) byName.TryAdd(s.Name, s);
        bool Selected(string name) => selected != null && (name.Equals(selected, StringComparison.OrdinalIgnoreCase)
            || HiveVision.ShortName(name).Equals(selected, StringComparison.OrdinalIgnoreCase)
            || (selected.Equals("main", StringComparison.OrdinalIgnoreCase) && name.Equals(_main, StringComparison.OrdinalIgnoreCase)));
        object FromSource(IBotTelemetrySource s, BodyRow? row)
        {
            bool full = Selected(s.Name);
            BotSnapshot snap;
            try { snap = s.Snapshot(full); } catch (Exception) { snap = BotSnapshot.Offline(s.Name); }
            return BotStatusView(snap, full, row);
        }
        // every body in the body list's order (main first), from its telemetry source when it has one (main, hive clones)
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var name = r.IsMain ? _main : r.Name;
            if (!done.Add(name)) continue;
            bots.Add(byName.TryGetValue(name, out var s) ? FromSource(s, r) : RowStatusView(r));
        }
        foreach (var s in sources) // configured clones that aren't in game (the fixed roster)
            if (done.Add(s.Name)) bots.Add(FromSource(s, null));
        object? hive = null;
        if (_hive() is { } sup)
        {
            var (cd, cm) = sup.Ledger.ClonesSpend();
            hive = new
            {
                list = sup.List(),
                clones_today_usd = cd,
                clones_month_usd = cm,
                slots = sup.Slots.Select(sl => new
                {
                    name = sl.Name, number = sl.Number, state = sl.State.ToString().ToLowerInvariant(), connected = sl.Connected, task = sl.Task?.Text,
                    pending = sl.Pending, progress = sl.Progress, last_progress = sl.LastProgress, result = sl.ResultIn ? sl.ResultText : null,
                    deaths = sl.Deaths, last_exit = sl.LastExit,
                }).ToList(),
            };
        }
        IReadOnlyList<HiveLease> leases = [];
        try { leases = _hub.Hive?.Leases() ?? []; } catch (Exception) { }
        return new
        {
            at = _time.GetUtcNow(),
            main = _main,
            bots,
            hive,
            claims = leases.Select(l => new { owner = l.Owner, what = HiveVision.Lease(l.Key).Text }).ToList(),
        };
    }

    /// <summary>A body with no telemetry source of its own (a hive's subhive body, a node still spawning): what the body list knows.</summary>
    public static object RowStatusView(BodyRow r) => new
    {
        name = r.Name,
        status = r.Online ? "online" : r.State == "spawning" ? "connecting" : "offline",
        at = r.At,
        pos = r.HasPos ? new BotPos(r.X, r.Y, r.Z).ToString() : null,
        dim = r.Dim,
        hp = r.Hp,
        food = r.Food,
        task = r.Job,
        brain = r.Brain,
        budget_mode = "Normal",
        chat = Array.Empty<string>(),
        @short = r.Short,
        kind = r.Kind,
        owner = r.Owner,
        script = r.Script,
        state = r.State,
    };

    /// <summary>The TUI's per-bot data minus the heavy viewport/voxel volumes.</summary>
    public static object BotStatusView(BotSnapshot s, bool full) => BotStatusView(s, full, null);

    /// <summary><see cref="BotStatusView(BotSnapshot, bool)"/> plus the body list's short name, kind, owner and script.</summary>
    public static object BotStatusView(BotSnapshot s, bool full, BodyRow? row) => new
    {
        name = s.Name,
        status = s.Status.ToString().ToLowerInvariant(),
        at = s.At,
        pos = s.Pos?.ToString(),
        dim = s.Dimension,
        hp = s.Hp,
        food = s.Food,
        task = s.Task,
        brain = s.Brain,
        budget_mode = s.BudgetMode,
        header = full ? s.Header : null,
        usage = s.Usage is { } u ? new
        {
            today_usd = u.TodayUsd, month_usd = u.MonthUsd, daily_cap_usd = u.DailyCapUsd, monthly_cap_usd = u.MonthlyCapUsd, mode = u.Mode,
            cache_hit = u.TodayCacheHit, today = u.Today, month = u.Month, calls_last_hour = u.CallsLastHour, calls_per_hour = u.CallsPerHour,
            last = u.Last is { } l ? new { at = l.Ts, tier = l.Tier, model = l.Model, purpose = l.Purpose, cost_usd = l.CostUsd } : null,
            latency = u.Latency,
        } : null,
        thought = full ? s.Thought : null,
        chat = s.Chat.TakeLast(full ? 60 : 12).ToList(),
        @short = row?.Short ?? HiveVision.ShortName(s.Name),
        kind = row?.Kind,
        owner = row?.Owner,
        script = row?.Script,
        state = row?.State,
    };
}
