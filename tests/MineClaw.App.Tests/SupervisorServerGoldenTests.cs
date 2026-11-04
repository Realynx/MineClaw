using System.Text;
using MineClaw.App.Supervisor;

namespace MineClaw.App.Tests;

/// <summary>
/// Pins <see cref="SupervisorServer.HandleAsync"/>'s routing (WP07): the sub-route dispatch with each route set off, the method
/// gate, the token, the page, the JSON routes, the blueprint/build-plan/view/camera routes (PNG hashes and their headers) and the
/// error mapping (busy, missing, cancelled, a crash). A refactor must leave <see cref="SupervisorServerGoldenData.Expected"/> unchanged.
/// </summary>
public class SupervisorServerGoldenTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class Data : ISupervisorData
    {
        public readonly List<string> Calls = [];

        public IReadOnlyList<BotTile> Bots() =>
        [
            new("maid_chan", "main", true, 0, 1.5, 64, 1.5, 20, 18, "idle", "awake", 0.2, []),
            new("maid_chan_67", "67", false, 0, 6.5, 64, 8.5, 17, 12, "mining", "asleep", 0.8, ["block 6,64,8"]),
            new("busy", "b", false, 0, 0, 64, 0, 20, 20, null, null, 0, []),
            new("gone", "g", false, 0, 0, 64, 0, 20, 20, null, null, 0, []),
            new("cancel", "c", false, 0, 0, 64, 0, 20, 20, null, null, 0, []),
            new("boom", "x", false, 0, 0, 64, 0, 20, 20, null, null, 0, []),
        ];

        private static ViewImage Img(string legend, int w = 4, int h = 3, string? seen = null) =>
            new(Encoding.UTF8.GetBytes("PNG:" + legend + $":{w}x{h}"), w, h, 1, legend, ["fact: " + legend], seen, "note");

        public Task<ViewImage?> ViewAsync(string bot, string mode, int radius, MineClaw.Render.XRay xray, RenderGate gate, CancellationToken ct)
        {
            Calls.Add($"view {bot} {mode} {radius} {xray}");
            return bot switch
            {
                "busy" or "b" => throw new RenderBusyException(),
                "gone" or "g" => Task.FromResult<ViewImage?>(null),
                "cancel" or "c" => throw new OperationCanceledException(),
                "boom" or "x" => throw new InvalidOperationException("kaboom"),
                _ => Task.FromResult<ViewImage?>(Img($"{bot} {mode} r{radius} {xray}", seen: "séen\nline")),
            };
        }

        public Task<ViewImage?> ViewAsync(string bot, string mode, int radius, RenderGate gate, CancellationToken ct) =>
            ViewAsync(bot, mode, radius, MineClaw.Render.XRay.Auto, gate, ct);

        public Task<ViewImage> HiveAsync(RenderGate gate, CancellationToken ct) { Calls.Add("hive"); return Task.FromResult(Img("hive")); }

        public Task<ViewImage> CameraAsync(CameraQuery q, RenderGate gate, CancellationToken ct)
        {
            Calls.Add($"camera {q}");
            return Task.FromResult(Img($"camera {q.X},{q.Y},{q.Z}", q.W, q.H));
        }

        public object Status(string? selected) => new { main = "maid_chan", selected };
        public IReadOnlyList<FeedEvent> Events(long since, int max) =>
            since >= 5 ? [] : [new FeedEvent(since + 1, T0, "chat", "info", "a"), new FeedEvent(since + 2, T0, "danger", "warn", "b")];
        public LocateResult? Locate(string query) => query == "mob chamber" ? new LocateResult("mob chamber", 0, 20.5, 80, -4.5) : null;
        public IReadOnlyList<object> Designs() => [new { id = 7, name = "hut" }];
        public ViewImage? DesignPicture(long id) => id == 7 ? Img("design 7") : null;
        public object? DesignBill(long id) => id == 7 ? new { id, blocks = 12 } : null;
        public string? BuildPlanPage(long id) => id == 7 ? "<html>plan 7</html>" : null;
        public object? BuildPlanData(long id) => id == 7 ? new { id, steps = 3 } : null;
    }

    private static SupervisorServer Make(string? token, Data d) =>
        new(new SupervisorOptions { Token = token, MinRenderIntervalMs = 0 }, d, time: new ManualClock(T0));

    private static SupervisorRequest Req(string method, string path, string? query = null, string? token = null) =>
        new(method, path, RouteGolden.Query(query), token);

    private static readonly (string Method, string Path, string? Query, string? Token)[] Cases =
    [
        ("GET", "/api/admin/settings", null, null), ("GET", "/api/admin/x", null, null), ("POST", "/api/admin/x", null, "t"),
        ("GET", "/api/viewer/now", null, null), ("GET", "/api/designer/sessions", null, null), ("GET", "/api/story", null, null),
        ("POST", "/api/bots", null, null), ("DELETE", "/", null, null), ("HEAD", "/", null, null), ("GET", "/", null, null), ("GET", "/index.html", null, null),
        ("GET", "/favicon.ico", null, null), ("GET", "/api/bots", null, null), ("GET", "/api/bots", null, "t"), ("GET", "/api/bots/", "token=t", null),
        ("GET", "/api/bots", "token=wrong", null), ("GET", "/api/bots", null, "tt"), ("GET", "/api/hive/tree", null, "t"), ("GET", "/api/status", "bot=67", "t"),
        ("GET", "/api/events", null, "t"), ("GET", "/api/events", "since=3", "t"), ("GET", "/api/events", "since=9", "t"), ("GET", "/api/events", "since=x", "t"),
        ("GET", "/api/locate", "q=mob chamber", "t"), ("GET", "/api/locate", "q=", "t"), ("GET", "/api/locate", "q=nowhere", "t"), ("GET", "/api/locate", null, "t"),
        ("GET", "/view/hive", null, "t"), ("GET", "/view/hive", "meta=1", "t"),
        ("GET", "/camera", null, "t"), ("GET", "/camera", "x=1&y=2", "t"), ("GET", "/camera", "x=1&y=500&z=3", "t"), ("GET", "/camera", "x=1&y=64&z=3&meta=true", "t"),
        ("GET", "/camera", "x=1.25&y=64&z=-3&yaw=90&pitch=-120&dim=-1&w=200&h=100", "t"), ("GET", "/camera", "x=NaN&y=1&z=1", "t"), ("GET", "/camera", "x=1&y=1&z=1&dim=7", "t"),
        ("GET", "/api/blueprints", null, "t"), ("GET", "/api/blueprints/7", null, "t"), ("GET", "/api/blueprints/8", null, "t"), ("GET", "/api/blueprints/x", null, "t"),
        ("GET", "/view/buildplan/7", null, "t"), ("GET", "/view/buildplan/7", "meta=1", "t"), ("GET", "/view/buildplan/8", null, "t"), ("GET", "/view/buildplan/x", null, "t"),
        ("GET", "/api/buildplans/7", null, "t"), ("GET", "/api/buildplans/8", null, "t"), ("GET", "/api/buildplans/", null, "t"),
        ("GET", "/view/blueprint/7", null, "t"), ("GET", "/view/blueprint/7", "meta=true", "t"), ("GET", "/view/blueprint/8", "meta=1", "t"), ("GET", "/view/blueprint/8", null, "t"),
        ("GET", "/view/blueprint/x", null, "t"),
        ("GET", "/view/maid_chan", null, "t"), ("GET", "/view/67", "mode=EYE&radius=20", "t"), ("GET", "/view/67", "mode=brain&radius=1", "t"),
        ("GET", "/view/67", "mode=iso&xray=on", "t"), ("GET", "/view/67", "mode=iso&xray=0", "t"), ("GET", "/view/67", "mode=iso&xray=maybe&radius=x", "t"),
        ("GET", "/view/67", "mode=sheet&meta=1", "t"), ("GET", "/view/67", "mode=dance", "t"), ("GET", "/view/nobody", null, "t"), ("GET", "/view/maid%5Fchan", null, "t"),
        ("GET", "/view/busy", null, "t"), ("GET", "/view/gone", null, "t"), ("GET", "/view/cancel", null, "t"), ("GET", "/view/boom", null, "t"),
        ("GET", "/api/nothing", null, "t"), ("GET", "/view", null, "t"),
    ];

    internal static async Task<string> Dump()
    {
        var sb = new StringBuilder();
        foreach (var token in new[] { null, "t" })
        {
            var d = new Data();
            await using var s = Make(token, d);
            sb.Append("== token ").Append(token ?? "-").Append('\n');
            foreach (var (m, p, q, t) in Cases)
            {
                var label = $"{m} {p}{(q != null ? "?" + q : "")}{(t != null ? " [" + t + "]" : "")}";
                var reply = await s.HandleAsync(Req(m, p, q, t));
                RouteGolden.Append(sb, label, reply, maxBody: 600);
            }
            sb.Append("calls: ").Append(string.Join(" | ", d.Calls)).Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public async Task Server_routes_match_the_golden_dump() =>
        RouteGolden.Check("server-routes", SupervisorServerGoldenData.Expected, await Dump());
}
