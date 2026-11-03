using System.Text.Json;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Telemetry;
using MineClaw.App.Supervisor;

namespace MineClaw.App.Tests;

/// <summary>A fake bot for the viewer routes: two bots, Fox trusted, records what was sent.</summary>
internal sealed class FakeViewerData : IViewerData
{
    public readonly List<RemoteChat> Sent = [];
    public readonly Dictionary<string, BrainContextPage?> Pages = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<long, MemoryDocInfo> DocTable = [];

    public IReadOnlyCollection<string> TrustedPlayers => ["poofimafox"];

    public string? Resolve(string bot) => bot.ToLowerInvariant() switch
    {
        "main" or "maid_chan" => "maid_chan",
        "67" or "maid_chan_67" => "maid_chan_67",
        _ => null,
    };

    public Task<bool> SendAsync(RemoteChat chat, CancellationToken ct) { Sent.Add(chat); return Task.FromResult(true); }

    public Task<ViewerNow?> NowAsync(string bot, CancellationToken ct) =>
        Task.FromResult<ViewerNow?>(new ViewerNow(bot, DateTimeOffset.UnixEpoch, "awake", null, null, null, "idle", null, new ViewerBody(20, 18, "1,64,2", 0, "stone pickaxe")));

    public IReadOnlyList<string> Brains() => ["maid_chan"];
    public Task<BrainContextPage?> ContextAsync(string brain, CancellationToken ct) => Task.FromResult(Pages.GetValueOrDefault(brain));
    public IReadOnlyDictionary<long, MemoryDocInfo> Docs(IReadOnlyCollection<long> ids) => ids.Where(DocTable.ContainsKey).ToDictionary(i => i, i => DocTable[i]);
}

public class SupervisorViewerTests
{
    private const string Token = "s3cret-token";
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static (SupervisorServer Server, FakeViewerData Data, ManualClock Clock, SupervisorViewerRoutes Routes) Make(bool enabled = true,
        string? token = Token, bool allowCommands = false, int perMinute = 12)
    {
        var o = new SupervisorOptions { Token = token, Viewer = new SupervisorViewerOptions { Enabled = enabled, AllowCommands = allowCommands, PerMinute = perMinute } };
        var data = new FakeViewerData();
        var clock = new ManualClock(T0);
        var routes = new SupervisorViewerRoutes(o, data, new ViewerFeed(time: clock), new ViewerMemoryActivity(time: clock), clock);
        return (new SupervisorServer(o, new FakeSupervisorData()) { Viewer = routes }, data, clock, routes);
    }

    private static SupervisorRequest Req(string method, string path, string? token = Token, string? body = null, string? query = null)
    {
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (query != null)
            foreach (var kv in query.Split('&'))
            {
                var i = kv.IndexOf('=');
                q[kv[..i]] = kv[(i + 1)..];
            }
        return new SupervisorRequest(method, path, q, token, body);
    }

    private static string ChatBody(string text = "bring me cobble", string player = "poofimafox", string bot = "maid_chan", bool allow = false) =>
        JsonSerializer.Serialize(new { bot, text, player, allow_commands = allow, request_id = "req-1" });

    private static JsonElement J(SupervisorReply r) => JsonDocument.Parse(r.Text).RootElement;

    [Fact]
    public async Task Disabled_viewer_routes_are_404_and_need_a_configured_token()
    {
        await using var off = Make(enabled: false).Server;
        Assert.Equal(404, (await off.HandleAsync(Req("POST", "/api/viewer/chat", body: ChatBody()))).Status);
        Assert.Equal(404, (await off.HandleAsync(Req("GET", "/api/viewer/feed"))).Status);
        Assert.Equal(404, (await off.HandleAsync(Req("GET", "/api/viewer/memory-activity"))).Status);
        await using var noToken = Make(token: null).Server;
        Assert.Equal(403, (await noToken.HandleAsync(Req("GET", "/api/viewer/feed", token: null))).Status);
        // No routes object at all (Viewer:Enabled off in the host): 404 too, never the 405 of the read-only API.
        await using var bare = new SupervisorServer(new SupervisorOptions { Token = Token }, new FakeSupervisorData());
        Assert.Equal(404, (await bare.HandleAsync(Req("POST", "/api/viewer/chat", body: ChatBody()))).Status);
    }

    [Fact]
    public async Task The_token_header_is_required_and_query_tokens_are_refused()
    {
        var (s, data, _, _) = Make();
        await using var _s = s;
        Assert.Equal(403, (await s.HandleAsync(Req("POST", "/api/viewer/chat", token: null, body: ChatBody()))).Status);
        Assert.Equal(403, (await s.HandleAsync(Req("POST", "/api/viewer/chat", token: "wrong", body: ChatBody()))).Status);
        Assert.Equal(403, (await s.HandleAsync(Req("GET", "/api/viewer/feed", token: null, query: "token=" + Token))).Status);
        Assert.Equal(403, (await s.HandleAsync(Req("GET", "/api/viewer/now", token: Token + "x", query: "bot=main"))).Status);
        Assert.Empty(data.Sent);
        Assert.Equal(200, (await s.HandleAsync(Req("GET", "/api/viewer/feed"))).Status);
        Assert.Equal(405, (await s.HandleAsync(Req("GET", "/api/viewer/chat"))).Status);
    }

    [Fact]
    public async Task Untrusted_players_and_bad_lines_are_rejected()
    {
        var (s, data, _, _) = Make();
        await using var _s = s;
        Assert.Equal(403, (await s.HandleAsync(Req("POST", "/api/viewer/chat", body: ChatBody(player: "kiwi")))).Status);
        Assert.Equal(400, (await s.HandleAsync(Req("POST", "/api/viewer/chat", body: ChatBody(text: new string('a', 257))))).Status);
        Assert.Equal(400, (await s.HandleAsync(Req("POST", "/api/viewer/chat", body: ChatBody(text: "two\nlines")))).Status);
        Assert.Equal(400, (await s.HandleAsync(Req("POST", "/api/viewer/chat", body: ChatBody(text: "   ")))).Status);
        Assert.Equal(400, (await s.HandleAsync(Req("POST", "/api/viewer/chat", body: "not json"))).Status);
        Assert.Equal(404, (await s.HandleAsync(Req("POST", "/api/viewer/chat", body: ChatBody(bot: "nobody")))).Status);
        Assert.Empty(data.Sent);
    }

    [Fact]
    public async Task An_accepted_line_reaches_the_target_bot_as_a_viewer_line_and_shows_in_the_feed()
    {
        var (s, data, _, routes) = Make();
        await using var _s = s;
        var r = await s.HandleAsync(Req("POST", "/api/viewer/chat", body: ChatBody(bot: "67", player: "PoofImaFox")));
        Assert.Equal(200, r.Status);
        Assert.True(J(r).GetProperty("accepted").GetBoolean());
        var sent = Assert.Single(data.Sent);
        Assert.Equal(new RemoteChat("poofimafox", "bring me cobble", "viewer", "maid_chan_67", false, "req-1"), sent); // config casing, resolved login
        var line = Assert.Single(routes.Feed.Since(0, 10));
        Assert.Equal(J(r).GetProperty("id").GetInt64(), line.Seq);
        Assert.Equal(("viewer", "maid_chan_67", "poofimafox", "viewer", "req-1"), (line.Kind, line.Bot, line.From, line.Origin, line.RequestId));
    }

    [Fact]
    public async Task Commands_are_allowed_only_when_the_request_and_the_config_both_say_so()
    {
        var (off, offData, _, _) = Make(allowCommands: false);
        await using var _off = off;
        await off.HandleAsync(Req("POST", "/api/viewer/chat", body: ChatBody(allow: true)));
        Assert.False(Assert.Single(offData.Sent).AllowCommands);

        var (on, onData, _, _) = Make(allowCommands: true);
        await using var _on = on;
        await on.HandleAsync(Req("POST", "/api/viewer/chat", body: ChatBody(allow: false)));
        await on.HandleAsync(Req("POST", "/api/viewer/chat", body: ChatBody(text: "set it to day", allow: true)));
        Assert.Equal(new[] { false, true }, onData.Sent.Select(c => c.AllowCommands));
    }

    [Fact]
    public async Task Viewer_lines_are_rate_limited_per_player_per_minute()
    {
        var (s, data, clock, _) = Make(perMinute: 2);
        await using var _s = s;
        Assert.Equal(200, (await s.HandleAsync(Req("POST", "/api/viewer/chat", body: ChatBody(text: "one")))).Status);
        Assert.Equal(200, (await s.HandleAsync(Req("POST", "/api/viewer/chat", body: ChatBody(text: "two")))).Status);
        var limited = await s.HandleAsync(Req("POST", "/api/viewer/chat", body: ChatBody(text: "three")));
        Assert.Equal(429, limited.Status);
        Assert.Equal("60", limited.Headers!["Retry-After"]);
        Assert.Equal(2, data.Sent.Count);
        clock.Now += TimeSpan.FromSeconds(61);
        Assert.Equal(200, (await s.HandleAsync(Req("POST", "/api/viewer/chat", body: ChatBody(text: "four")))).Status);
    }

    [Fact]
    public async Task The_feed_pages_forward_by_seq()
    {
        var (s, _, _, routes) = Make();
        await using var _s = s;
        for (int i = 1; i <= 5; i++) routes.Feed.Add("maid_chan", "chat", "kiwi", "line " + i, "game");
        var page = J(await s.HandleAsync(Req("GET", "/api/viewer/feed", query: "since=2&max=2")));
        Assert.Equal(new[] { 3L, 4L }, page.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("seq").GetInt64()));
        Assert.Equal(4, page.GetProperty("last").GetInt64());
        var rest = J(await s.HandleAsync(Req("GET", "/api/viewer/feed", query: "since=4")));
        var l5 = Assert.Single(rest.GetProperty("lines").EnumerateArray());
        Assert.Equal("line 5", l5.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, l5.GetProperty("request_id").ValueKind); // nulls are written out
        Assert.Equal(5, J(await s.HandleAsync(Req("GET", "/api/viewer/feed", query: "since=5"))).GetProperty("last").GetInt64());
    }

    [Fact]
    public void The_feed_is_a_bounded_ring_that_folds_duplicates()
    {
        var feed = new ViewerFeed(capacity: 3);
        feed.Add("maid_chan_67", "said", "maid_chan_67", "on my way, grabbing the pickaxe first", "bot");
        Assert.Equal(0, feed.Add("maid_chan_67", "said", "maid_chan_67", "on my way, grabbing the pickaxe first", "game")); // the server's copy
        feed.Add("maid_chan", "muted", "maid_chan", "note to self", "bot");
        feed.Add("maid_chan", "internal", "maid_chan", "note to self", "bot"); // the same quiet-turn text: one internal line
        Assert.Equal(new[] { "said", "internal" }, feed.Since(0, 10).Select(l => l.Kind));
        for (int i = 0; i < 5; i++) feed.Add("maid_chan", "chat", "kiwi", "x" + i, "game");
        Assert.Equal(3, feed.Since(0, 10).Count);
        Assert.Equal(feed.Last, feed.Since(0, 10)[^1].Seq);
    }

    [Fact]
    public async Task Now_answers_for_a_known_bot_only()
    {
        var (s, _, _, _) = Make();
        await using var _s = s;
        var now = J(await s.HandleAsync(Req("GET", "/api/viewer/now", query: "bot=67")));
        Assert.Equal("maid_chan_67", now.GetProperty("bot").GetString());
        Assert.Equal(JsonValueKind.Null, now.GetProperty("in_flight").ValueKind);
        Assert.Equal("stone pickaxe", now.GetProperty("body").GetProperty("held").GetString());
        Assert.Equal(404, (await s.HandleAsync(Req("GET", "/api/viewer/now", query: "bot=nobody"))).Status);
    }

    // ------------------------------------------------------------------ memory activity

    private static CtxTurnSummary Turn(long id, DateTimeOffset start, DateTimeOffset? end) =>
        new(id, "brain", "chat", start, end, end == null, 1, "m", "opus", null, 0, 0, 0, 0, 0, 0, "", [], false, null, null, false, id);

    private static BrainContextPage Page(DateTimeOffset at, long turn, List<CtxTurnSummary> turns, params CtxMessage[] messages) =>
        new("maid_chan", turn, at, 30, true, null, turns,
            new CtxWindow(1, turn, at, "m", "opus", [], 0, 0, [], [.. messages], 0, 0, 0, 0, 0, null, false), []);

    private static CtxMessage User(int i, params CtxBlock[] blocks) => new(i, "user", [.. blocks], "turn");

    [Fact]
    public async Task Memory_reads_are_recorded_per_brain_with_their_raptor_node_and_page_by_seq()
    {
        var (s, data, clock, routes) = Make();
        await using var _s = s;
        data.DocTable[12] = new MemoryDocInfo("note", 340);
        data.DocTable[13] = new MemoryDocInfo("episode", null);
        data.Pages["maid_chan"] = Page(T0.AddSeconds(5), 2, [Turn(1, T0.AddSeconds(-30), T0.AddSeconds(-20)), Turn(2, T0.AddSeconds(1), T0.AddSeconds(5))]);
        routes.Memory.Add("maid_chan", null, "rag", [new MemoryAccessHit(12, "note", 0.81), new MemoryAccessHit(13, "episode", 0.5)]);
        clock.Now = T0.AddSeconds(3);
        routes.Memory.Add("maid_chan_67", "maid_chan_113", "recall", [new MemoryAccessHit(12, "note", 0.6)]);

        var all = J(await s.HandleAsync(Req("GET", "/api/viewer/memory-activity")));
        var events = all.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(3, events.Count);
        Assert.Equal(("maid_chan", 12L, 340, "rag"), (events[0].GetProperty("brain").GetString()!, events[0].GetProperty("memory_id").GetInt64(),
            events[0].GetProperty("raptor_node_id").GetInt32(), events[0].GetProperty("source").GetString()!));
        Assert.Equal(2, events[0].GetProperty("turn").GetInt64()); // the rag block was built for the turn that started right after it
        Assert.Equal(JsonValueKind.Null, events[1].GetProperty("raptor_node_id").ValueKind); // not in the tree
        Assert.Equal("maid_chan_113", events[2].GetProperty("body").GetString());
        Assert.Equal(3, all.GetProperty("last").GetInt64());

        var mine = J(await s.HandleAsync(Req("GET", "/api/viewer/memory-activity", query: "since=1&brain=maid_chan")));
        Assert.Equal(new[] { 2L }, mine.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("seq").GetInt64()));
    }

    [Fact]
    public async Task In_context_follows_the_brain_window_and_drops_evicted_memories()
    {
        var (s, data, clock, routes) = Make();
        await using var _s = s;
        data.DocTable[5] = new MemoryDocInfo("note", 77);
        data.DocTable[7] = new MemoryDocInfo("episode", 78);
        data.DocTable[9] = new MemoryDocInfo("waypoint", null);
        routes.Memory.Add("maid_chan", null, "rag", [new MemoryAccessHit(5, "note", 0.9), new MemoryAccessHit(7, "episode", 0.8)]);
        var turns = new List<CtxTurnSummary> { Turn(1, T0.AddSeconds(1), T0.AddSeconds(4)) };
        data.Pages["maid_chan"] = Page(T0.AddSeconds(4), 1, turns,
            User(0, new CtxBlock("text", "[memory]\n#5 note: the farm is east\n#7 ep fox asked for cobble", "memory")),
            new CtxMessage(1, "assistant", [new CtxBlock("tool_use", Name: "recall", Id: "tu1", Input: "{}")]),
            new CtxMessage(2, "user", [new CtxBlock("tool_result", "#9 home (overworld)", Id: "tu1")], "tool_results"));

        var first = J(await s.HandleAsync(Req("GET", "/api/viewer/memory-activity", query: "brain=maid_chan"))).GetProperty("in_context").EnumerateArray().ToList();
        Assert.Equal(new[] { 9L, 5L, 7L }, first.Select(m => m.GetProperty("memory_id").GetInt64())); // newest copy first
        var m5 = first.Single(m => m.GetProperty("memory_id").GetInt64() == 5);
        Assert.Equal((77, 2, 1, "note"), (m5.GetProperty("raptor_node_id").GetInt32(), m5.GetProperty("position_from_end").GetInt32(),
            m5.GetProperty("turns_in_context").GetInt32(), m5.GetProperty("memory_kind").GetString()!));
        Assert.Equal(T0, m5.GetProperty("added_at").GetDateTimeOffset());

        // Two turns later the compaction dropped the [memory] message: #5 and #7 are gone, #9 rode along for three turns.
        clock.Now = T0.AddSeconds(30);
        turns.AddRange([Turn(2, T0.AddSeconds(10), T0.AddSeconds(12)), Turn(3, T0.AddSeconds(20), T0.AddSeconds(30))]);
        data.Pages["maid_chan"] = Page(T0.AddSeconds(30), 3, turns,
            new CtxMessage(0, "assistant", [new CtxBlock("tool_use", Name: "recall", Id: "tu1", Input: "{}")]),
            new CtxMessage(1, "user", [new CtxBlock("tool_result", "#9 home (overworld)", Id: "tu1")], "tool_results"),
            new CtxMessage(2, "user", [new CtxBlock("text", "#5 is not a memory line outside [memory]")], "turn"));
        var later = J(await s.HandleAsync(Req("GET", "/api/viewer/memory-activity", query: "brain=maid_chan"))).GetProperty("in_context").EnumerateArray().ToList();
        var m9 = Assert.Single(later);
        Assert.Equal(9, m9.GetProperty("memory_id").GetInt64());
        Assert.Equal(3, m9.GetProperty("turns_in_context").GetInt32());
        Assert.Equal(T0.AddSeconds(30), m9.GetProperty("last_seen_at").GetDateTimeOffset());
    }

    [Fact]
    public void Memory_reads_from_a_registered_runtime_land_in_the_process_ring()
    {
        var feed = new ViewerFeed();
        var memory = new ViewerMemoryActivity();
        var owner = new object();
        ViewerTaps.Enable(feed, memory);
        ViewerTaps.Register(owner, "viewer_test_brain");
        MemoryAccess.Note(owner, "recall", [new RagHit(41, "note:41", "note", null, "t", "b", null, 0.5, 0, 0, null, DateTimeOffset.UnixEpoch, null)]);
        var e = Assert.Single(memory.Since(0, "viewer_test_brain", 10));
        Assert.Equal((41L, "recall", "note"), (e.MemoryId, e.Source, e.MemoryKind));
    }
}
