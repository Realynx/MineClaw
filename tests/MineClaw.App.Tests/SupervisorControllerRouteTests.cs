using System.Text.Json;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;
using MineClaw.App.Supervisor;

namespace MineClaw.App.Tests;

/// <summary>The Controller routes: what drives a body, a brain's context page and one turn; token-gated (403), read-only.</summary>
public class SupervisorControllerRouteTests
{
    private const string Token = "s3cret-controller";

    /// <summary>Main's brain = a real capture; "67" = a subhive of main; "1337" = a script-only clone.</summary>
    private sealed class Data : IControllerSupervisorData
    {
        public readonly BrainContextCapture Capture = new() { Brain = "maid_chan" };

        public Task<BodyControllerInfo?> ControllerAsync(string body, CancellationToken ct) => Task.FromResult<BodyControllerInfo?>(body switch
        {
            "main" => new BodyControllerInfo("main", "main", null, "main", "opus", "claude-opus-5-5", "anthropic", false, null, ["brain"], null,
                new ControllerLinks("api/brains/main/context", "api/bodies/main/script", null)),
            "67" => new BodyControllerInfo("67", "subhive", "main", "main", "opus", null, null, false, null, ["brain"], "67",
                new ControllerLinks("api/brains/main/context", "api/bodies/67/script", "main")),
            "1337" => new BodyControllerInfo("1337", "script", "main", null, null, null, null, true, "cobble_loop", ["script"], null,
                new ControllerLinks(null, "api/bodies/1337/script", "main")),
            _ => null,
        });

        public Task<BrainContextPage?> ContextAsync(string brain, long since, string? body, CancellationToken ct) =>
            Task.FromResult(brain == "main" ? Capture.Page(since, body) : null);

        public Task<(bool Found, BrainTurnDetail? Turn)> TurnAsync(string brain, long turn, string? body, CancellationToken ct) =>
            Task.FromResult(brain == "main" ? (true, Capture.Turn(turn, body)) : (false, (BrainTurnDetail?)null));
    }

    private static (SupervisorServer Server, Data Data) Server()
    {
        var data = new Data();
        var c = data.Capture.Begin(new LlmRequest
        {
            Tier = ModelTier.Main, Purpose = "task", Messages = [LlmMessage.User("[chat] Fox: 67 mine the gen")],
            System = [new LlmSystemBlock("you are maid_chan", Cache: true, Ttl: "1h")],
        }, null);
        data.Capture.End(c, new LlmResponse([new LlmThinking("67 is free", "sig"),
            new LlmToolUse("t1", "do", JsonSerializer.SerializeToElement(new { action = "mine", body = "67" }))], StopReasons.ToolUse,
            new LlmUsage(100, 20, 3000, 0), "claude-opus-5-5"), null, null);
        var server = new SupervisorServer(new SupervisorOptions { Token = Token }, new FakeSupervisorData())
        {
            Controller = new SupervisorControllerRoutes(data, Token),
        };
        return (server, data);
    }

    private static SupervisorRequest Get(string path, string? token = Token, Dictionary<string, string>? q = null) =>
        new("GET", path, q ?? new Dictionary<string, string>(), token);

    [Theory]
    [InlineData("/api/bodies/main/controller")]
    [InlineData("/api/brains/main/context")]
    [InlineData("/api/brains/main/turn/1")]
    public async Task Routes_answer_403_without_the_token(string path)
    {
        var (s, _) = Server();
        await using var _s = s;
        var r = await s.HandleAsync(Get(path, token: null));
        Assert.Equal(403, r.Status);
        Assert.Equal(403, (await s.HandleAsync(Get(path, token: "wrong"))).Status);
        Assert.Equal(200, (await s.HandleAsync(Get(path))).Status);
        Assert.Equal(405, (await s.HandleAsync(new SupervisorRequest("POST", path, new Dictionary<string, string>(), Token))).Status);
        // ?token= works as for the other routes
        Assert.Equal(200, (await s.HandleAsync(Get(path, token: null, q: new() { ["token"] = Token }))).Status);
    }

    [Fact]
    public async Task Controller_context_and_turn_round_trip()
    {
        var (s, _) = Server();
        await using var _s = s;
        using (var info = JsonDocument.Parse((await s.HandleAsync(Get("/api/bodies/67/controller"))).Text))
        {
            Assert.Equal("subhive", info.RootElement.GetProperty("kind").GetString());
            Assert.Equal("67", info.RootElement.GetProperty("filter").GetString());
            Assert.Equal("main", info.RootElement.GetProperty("links").GetProperty("owner").GetString());
        }
        Assert.Equal(404, (await s.HandleAsync(Get("/api/bodies/nobody/controller"))).Status);

        var page = await s.HandleAsync(Get("/api/brains/main/context", q: new() { ["since"] = "0", ["body"] = "67" }));
        Assert.Equal(200, page.Status);
        using (var doc = JsonDocument.Parse(page.Text))
        {
            var root = doc.RootElement;
            Assert.Equal("maid_chan", root.GetProperty("brain").GetString());
            var turn = root.GetProperty("turns")[0];
            Assert.True(turn.GetProperty("targets").GetBoolean());
            Assert.True(turn.GetProperty("has_thinking").GetBoolean());
            var window = root.GetProperty("window");
            Assert.True(window.GetProperty("system")[0].GetProperty("in_cached_prefix").GetBoolean());
            long version = root.GetProperty("version").GetInt64();
            var again = await s.HandleAsync(Get("/api/brains/main/context", q: new() { ["since"] = version.ToString() }));
            using var d2 = JsonDocument.Parse(again.Text);
            Assert.Equal(0, d2.RootElement.GetProperty("turns").GetArrayLength());
            Assert.False(d2.RootElement.TryGetProperty("window", out _)); // unchanged: no window
        }
        var detail = await s.HandleAsync(Get("/api/brains/main/turn/1", q: new() { ["body"] = "67" }));
        Assert.Equal(200, detail.Status);
        Assert.Contains("\"thinking\"", detail.Text);
        Assert.Equal(404, (await s.HandleAsync(Get("/api/brains/main/turn/99"))).Status);
        Assert.Equal(404, (await s.HandleAsync(Get("/api/brains/maid_chan_101/context"))).Status);
        Assert.Equal(400, (await s.HandleAsync(Get("/api/brains/main/turn/x"))).Status);
    }

    [Fact]
    public void A_clone_reply_survives_the_ipc_json()
    {
        var (_, data) = Server();
        var reply = data.Capture.Query(new BrainContextQuery(0, null, "67"));
        var el = MineClaw.Hive.Ipc.HiveJson.ToElement(reply)!.Value;
        var back = el.Deserialize<BrainContextReply>(MineClaw.Hive.Ipc.HiveJson.Options)!;
        Assert.NotNull(back.Page);
        Assert.Equal(reply.Page!.Version, back.Page!.Version);
        Assert.Equal(reply.Page.Window!.Messages.Count, back.Page.Window!.Messages.Count);
        Assert.True(back.Page.Turns[0].Targets);
        var turn = data.Capture.Query(new BrainContextQuery(Turn: 1));
        var tb = MineClaw.Hive.Ipc.HiveJson.ToElement(turn)!.Value.Deserialize<BrainContextReply>(MineClaw.Hive.Ipc.HiveJson.Options)!;
        Assert.Equal("thinking", tb.Turn!.Calls[0].Response[0].Kind);
    }
}
