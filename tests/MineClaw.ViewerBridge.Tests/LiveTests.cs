using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MineClaw.Agent;
using MineClaw.ViewerBridge.Data;
using MineClaw.ViewerBridge.Live;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>SSE framing, resume and polling lifecycle of /v1/live/stream; memory heat propagation over a real RAPTOR tree.</summary>
public class LiveTests
{
    private static async Task<List<(string Event, long Id, string Data)>> ReadFrames(Stream s, int count, TimeSpan timeout, StringBuilder? raw = null)
    {
        var frames = new List<(string, long, string)>();
        using var cts = new CancellationTokenSource(timeout);
        var reader = new StreamReader(s, Encoding.UTF8);
        string? evt = null, data = null;
        long id = 0;
        while (frames.Count < count)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line == null) break;
            raw?.AppendLine(line);
            if (line.StartsWith("event: ", StringComparison.Ordinal)) evt = line[7..];
            else if (line.StartsWith("id: ", StringComparison.Ordinal)) id = long.Parse(line[4..], System.Globalization.CultureInfo.InvariantCulture);
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
            else if (line.Length == 0 && evt != null)
            {
                frames.Add((evt, id, data!));
                evt = data = null;
            }
        }
        return frames;
    }

    [Fact]
    public void Motion_topics_back_off_briefly_and_the_rest_as_before()
    {
        static List<double> Seq(LiveHub.BackoffPolicy p)
        {
            var list = new List<double>();
            var b = TimeSpan.Zero;
            for (int i = 0; i < 6; i++) list.Add((b = p.Next(b)).TotalMilliseconds);
            return list;
        }
        // Live 11-04: one busy answer on the entity poll froze every body for 1, 2, 4… s.
        Assert.Equal([100, 200, 400, 500, 500, 500], Seq(LiveHub.FastBackoff));
        Assert.Equal([1000, 2000, 4000, 8000, 15000, 15000], Seq(LiveHub.SlowBackoff));
    }

    [Fact]
    public void Frames_are_event_id_data_with_a_blank_line()
    {
        Assert.Equal("event: feed\nid: 7\ndata: {\"lines\":[],\"last\":0}\n\n", LiveHub.Format("feed", 7, """{"lines":[],"last":0}"""));
    }

    [Fact]
    public void Resume_replays_frames_after_the_last_event_id_and_a_stale_id_gets_state()
    {
        using var hub = new LiveHub(null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveHub>.Instance);
        // Publish without pollers: subscribe first would start them, so publish into the ring directly.
        var a = hub.Publish("bots", new JsonObject { ["main"] = "a" }, "bots");
        var b = hub.Publish("event", new JsonObject { ["events"] = new JsonArray(), ["last"] = 1 });
        var c = hub.Publish("bots", new JsonObject { ["main"] = "b" }, "bots");
        Assert.True(a.Id < b.Id && b.Id < c.Id);
        var (resumed, replay, sent) = Subscribe(hub, a.Id);
        Assert.Equal([b.Id, c.Id], replay.Select(f => f.Id));
        Assert.Equal(a.Id, sent);
        hub.Unsubscribe(resumed);
        var (fresh, state, sent2) = Subscribe(hub, 5);
        Assert.Equal([c.Id], state.Select(f => f.Id)); // only the latest state frame of each key
        Assert.Equal(0, sent2);
        hub.Unsubscribe(fresh);
    }

    private static (LiveHub.Client, IReadOnlyList<LiveHub.Frame>, long) Subscribe(LiveHub hub, long? last)
    {
        // Pollers need a supervisor; the hub under test has none, so stop them right after (Unsubscribe cancels).
        var r = hub.Subscribe(last);
        return r;
    }

    [Fact]
    public void A_slow_reader_is_disconnected_instead_of_silently_losing_deltas()
    {
        using var hub = new LiveHub(null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveHub>.Instance);
        var (client, _, _) = Subscribe(hub, null);
        try
        {
            for (int i = 0; i < 1100; i++) hub.Publish("entities", new JsonObject { ["rev"] = i });
            Assert.True(client.Lagging);
            var received = new List<long>();
            while (client.Reader.TryRead(out var f)) received.Add(f.Id);
            Assert.True(received.Count <= 64, "a stalled reader must not accumulate tens of seconds of old movement");
            Assert.True(client.Reader.Completion.IsCompletedSuccessfully);
        }
        finally { hub.Unsubscribe(client); }
    }

    [Fact]
    public void Large_frames_evict_replay_by_bytes_before_the_frame_count_limit()
    {
        using var hub = new LiveHub(null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveHub>.Instance);
        var first = hub.Publish("bots", new JsonObject { ["main"] = "a" }, "bots");
        for (int i = 0; i < 80; i++) hub.Publish("story", new JsonObject { ["data"] = new string('x', 64 * 1024) });
        var latest = hub.Publish("bots", new JsonObject { ["main"] = "b" }, "bots");
        var (client, replay, sent) = Subscribe(hub, first.Id);
        try
        {
            Assert.Equal(0, sent); // old cursor no longer fits: return current state, never a partial delta history
            Assert.Equal([latest.Id], replay.Select(f => f.Id));
        }
        finally { hub.Unsubscribe(client); }
    }

    [Fact]
    public async Task Chest_snapshot_removal_is_emitted_without_entity_revision_or_event_changes()
    {
        await using var h = await BridgeHost.StartAsync();
        int calls = 0;
        h.Sup.Routes["/api/world/entities"] = _ => FakeSupervisor.Json(Interlocked.Increment(ref calls) == 1
            ? """{"at":1,"rev":1,"full":true,"bots":[],"entities":[],"removed":[],"events":[],"chests":[{"dim":0,"x":1,"y":64,"z":2,"open_count":1}]}"""
            : """{"at":2,"rev":1,"full":false,"bots":[],"entities":[],"removed":[],"events":[],"chests":[]}""");
        var hub = h.Services.GetRequiredService<LiveHub>();
        var (client, _, _) = hub.Subscribe(null);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var frames = new List<LiveHub.Frame>();
            while (frames.Count < 2)
            {
                var frame = await client.Reader.ReadAsync(timeout.Token);
                if (frame.Event == "entities") frames.Add(frame);
            }
            Assert.Contains("\"open_count\":1", frames[0].Text);
            Assert.Contains("\"chests\":[]", frames[1].Text);
            for (int i = 0; i < 50 && Volatile.Read(ref calls) < 4; i++) await Task.Delay(20, timeout.Token);
            Assert.True(Volatile.Read(ref calls) >= 4);
            while (client.Reader.TryRead(out var frame)) Assert.NotEqual("entities", frame.Event);
        }
        finally { hub.Unsubscribe(client); }
    }

    [Fact]
    public async Task Entity_events_are_emitted_even_when_no_bot_or_entity_pose_changed()
    {
        await using var h = await BridgeHost.StartAsync();
        int calls = 0;
        h.Sup.Routes["/api/world/entities"] = req =>
        {
            Interlocked.Increment(ref calls);
            return FakeSupervisor.Json(req.Query["since"] == "0"
                ? """{"at":1,"rev":1,"full":true,"bots":[],"entities":[],"removed":[],"events":[]}"""
                : req.Query["since"] == "1"
                    ? """{"at":2,"rev":2,"full":false,"bots":[],"entities":[],"removed":[],"events":[{"at":2,"dim":0,"id":7,"kind":"hurt"}]}"""
                    : """{"at":3,"rev":2,"full":false,"bots":[],"entities":[],"removed":[],"events":[]}""");
        };
        var hub = h.Services.GetRequiredService<LiveHub>();
        var (client, _, _) = hub.Subscribe(null);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var entities = new List<LiveHub.Frame>();
            while (entities.Count < 2)
            {
                var frame = await client.Reader.ReadAsync(timeout.Token);
                if (frame.Event == "entities") entities.Add(frame);
            }
            Assert.Contains("\"kind\":\"hurt\"", entities[1].Text);
            for (int i = 0; i < 50 && Volatile.Read(ref calls) < 4; i++) await Task.Delay(20, timeout.Token);
            Assert.True(Volatile.Read(ref calls) >= 4);
            while (client.Reader.TryRead(out var frame)) Assert.NotEqual("entities", frame.Event);
            Assert.Contains(h.Sup.Seen, request => request.Target == "/api/world/entities?since=2");
        }
        finally { hub.Unsubscribe(client); }
    }

    [Fact]
    public async Task Stream_polls_only_while_connected_and_emits_typed_frames()
    {
        await using var h = await BridgeHost.StartAsync();
        var hub = h.Services.GetRequiredService<LiveHub>();
        Assert.False(hub.Polling);
        int blocksCalls = 0;
        h.Sup.Routes["/api/world/blocks"] = req =>
        {
            int n = Interlocked.Increment(ref blocksCalls);
            return FakeSupervisor.Json(n == 1
                ? """{"at":1,"rev":5,"full":true,"more":false,"blocks":{},"columns":[]}"""
                : n == 2 ? """{"at":2,"rev":6,"full":false,"more":false,"blocks":{"0":[[1,64,2,4,0]]},"columns":[{"rev":6,"at":2,"dim":0,"cx":0,"cz":0,"kind":"loaded"}]}"""
                : """{"at":3,"rev":6,"full":false,"more":false,"blocks":{},"columns":[]}""");
        };
        using var req = h.Signed("GET", "/v1/live/stream");
        using var res = await h.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        var stream = await res.Content.ReadAsStreamAsync();
        var raw = new StringBuilder();
        var frames = await ReadFrames(stream, 4, TimeSpan.FromSeconds(10), raw);
        Assert.StartsWith("retry: 3000", raw.ToString());
        var names = frames.Select(f => f.Event).ToHashSet();
        Assert.Contains("bots", names);
        Assert.Contains("hive", names);
        Assert.True(frames.Zip(frames.Skip(1)).All(p => p.First.Id < p.Second.Id), "ids increase");
        var now = frames.FirstOrDefault(f => f.Event == "now");
        if (now.Data != null) Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(now.Data).RootElement.GetProperty("body").GetProperty("pos").ValueKind);
        // The first blocks page (full: only the cursor) is not emitted; the second is.
        // Read until the blocks frame arrives rather than a fixed count: under load other topics can fill any fixed window first.
        var seen = frames.ToList();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!seen.Any(f => f.Event == "blocks") && DateTime.UtcNow < deadline)
            seen.AddRange(await ReadFrames(stream, 1, deadline - DateTime.UtcNow));
        var blocks = seen.Where(f => f.Event == "blocks").ToList();
        Assert.Single(blocks);
        Assert.Contains("[1,64,2,4,0]", blocks[0].Data);
        Assert.True(hub.Polling);
        res.Dispose();
        for (int i = 0; i < 50 && hub.Polling; i++) await Task.Delay(100);
        Assert.False(hub.Polling);
        Assert.Equal(0, hub.ClientCount);
    }

    [Fact]
    public async Task Story_colonies_are_polled_and_each_moved_revision_is_a_story_frame_with_its_id()
    {
        await using var h = await BridgeHost.StartAsync();
        int calls = 0;
        h.Sup.Routes["/api/story"] = _ => FakeSupervisor.Json("""{"colonies":[{"id":"story-1","name":"Hollow","pawns":["maid_s1_ash"]}]}""");
        h.Sup.Routes["/api/story/colonies/story-1"] = req =>
        {
            int n = Interlocked.Increment(ref calls);
            var since = req.Query["since"].ToString();
            // 1st: the whole state (only takes the cursor); 2nd: a delta at rev 8; afterwards nothing moves (rev stays 8).
            if (n == 1) return FakeSupervisor.Json("""{"rev":7,"at":"2026-11-03T10:00:00+00:00","full":true,"pawns":[],"zones":[],"designations":[],"orders":[],"alerts":[]}""");
            if (n == 2 && since == "7") return FakeSupervisor.Json("""{"rev":8,"at":"2026-11-03T10:00:00.5+00:00","full":false,"pawns":[{"name":"maid_s1_ash","display":"Ash","pos":[1,64,2],"state":"working"}]}""");
            return FakeSupervisor.Json("""{"rev":8,"at":"2026-11-03T10:00:01+00:00","full":false}""");
        };
        using var req = h.Signed("GET", "/v1/live/stream");
        using var res = await h.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        var stream = await res.Content.ReadAsStreamAsync();
        var seen = new List<(string Event, long Id, string Data)>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!seen.Any(f => f.Event == "story") && DateTime.UtcNow < deadline)
            seen.AddRange(await ReadFrames(stream, 1, deadline - DateTime.UtcNow));
        // A few more polls with nothing new emit nothing.
        for (int i = 0; i < 40 && Volatile.Read(ref calls) < 5; i++) await Task.Delay(100);
        var more = new List<(string Event, long Id, string Data)>();
        try
        {
            while (more.Count < 50) more.AddRange(await ReadFrames(stream, 1, TimeSpan.FromSeconds(1.5)));
        }
        catch (OperationCanceledException) { } // quiet: nothing more to read
        var story = seen.Concat(more).Where(f => f.Event == "story").ToList();
        Assert.Single(story);
        using var doc = JsonDocument.Parse(story[0].Data);
        Assert.Equal("story-1", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal(8, doc.RootElement.GetProperty("rev").GetInt64());
        Assert.Equal("Ash", doc.RootElement.GetProperty("pawns")[0].GetProperty("display").GetString());
        Assert.Contains(h.Sup.Seen, r => r.Target == "/api/story/colonies/story-1?since=8");
    }

    [Fact]
    public async Task Heat_propagates_to_every_ancestor_with_decay_staleness_and_eviction()
    {
        var fx = await FixtureDb.SharedAsync();
        using var bank = await MemoryBank.OpenAsync(fx.Path, new AgentOptions(), new HashEmbeddings());
        Assert.True(bank.HasTree);
        var (leaf, docId, parent, rootPath) = bank.ReadTree(t =>
        {
            var l = t.Leaves.First(n => bank.DocOf(n.Index) != null);
            return (l.Index, bank.DocOf(l.Index)!.Id, l.Parents.Min, MemoryBank.PathToRoot(t, l.Index));
        }, default);
        var at = DateTimeOffset.FromUnixTimeSeconds(1_762_077_600);
        var events = new List<MemoryEvent>
        {
            new(1, at.AddSeconds(-120), "main", "maid_chan", "episode", docId.ToString(), null, "rag", 0.9, 3),
            new(2, at.AddSeconds(-60), "main", "maid_chan", "episode", docId.ToString(), null, "recall", null, 4),
            new(3, at.AddSeconds(-30), "hive-2", null, "episode", docId.ToString(), null, "rag", null, null),
            new(4, at.AddHours(-2), "main", null, "episode", docId.ToString(), null, "rag", null, null), // outside the window
        };
        var inCtx = new List<InContext>
        {
            new("main", "episode", docId.ToString(), null, at.AddSeconds(-120), at.AddSeconds(-10), 3, 2),
            new("hive-2", "episode", docId.ToString(), null, at.AddSeconds(-30), at.AddSeconds(-20), 1, null),
        };
        var r = bank.ReadTree(t => MemoryHeat.Compute(events, inCtx, t, bank.NodeOfDoc, MemoryBank.StateKeyOf(t), at, 900, 60, null), null!);
        Assert.Equal(bank.StateKey, r.StateKey);
        Assert.Equal(["hive-2", "main"], r.Brains);
        var leafHeat = r.Nodes.Single(n => n.NodeId == leaf);
        Assert.Equal(3, leafHeat.AccessCount);
        Assert.Equal(at.AddSeconds(-30), leafHeat.LastAccessAt);
        Assert.Equal(Math.Pow(0.5, 30.0 / 60), leafHeat.Heat, 4);
        var main = leafHeat.Brains.Single(b => b.Brain == "main");
        Assert.True(main.InContext);
        Assert.Equal(10, main.StalenessS);
        Assert.False(main.Evicted);
        var hive = leafHeat.Brains.Single(b => b.Brain == "hive-2");
        Assert.False(hive.InContext);
        Assert.True(hive.Evicted);
        Assert.Null(hive.StalenessS);
        // Every ancestor is lit with the same max heat; the root of the path too.
        foreach (var anc in rootPath)
        {
            var n = r.Nodes.Single(x => x.NodeId == anc);
            Assert.Equal(leafHeat.Heat, n.Heat, 6);
            Assert.True(n.AccessCount >= 3);
        }
        Assert.Contains(r.Nodes, n => n.NodeId == parent);

        var onlyHive = bank.ReadTree(t => MemoryHeat.Compute(events, inCtx, t, bank.NodeOfDoc, MemoryBank.StateKeyOf(t), at, 900, 60, "hive-2"), null!);
        Assert.Equal(1, onlyHive.Nodes.Single(n => n.NodeId == leaf).AccessCount);
        Assert.Equal(["hive-2"], onlyHive.Brains);
    }

    [Fact]
    public async Task Heat_route_pulls_the_ring_and_maps_doc_ids_to_graph_nodes()
    {
        var fx = await FixtureDb.SharedAsync();
        await using var h = await BridgeHost.StartAsync(fx.Path, new HashEmbeddings());
        var bank = await h.Services.GetRequiredService<BankHost>().GetAsync(CancellationToken.None);
        var (leaf, docId) = bank.ReadTree(t => { var l = t.Leaves.First(n => bank.DocOf(n.Index) != null); return (l.Index, bank.DocOf(l.Index)!.Id); }, default);
        var now = DateTimeOffset.UtcNow.ToString("o");
        h.Sup.Routes["/api/viewer/memory-activity"] = req => FakeSupervisor.Json(req.Query["since"] == "0"
            ? $$"""{"events":[{"seq":1,"at":"{{now}}","brain":"main","body":null,"memory_kind":"episode","memory_id":{{docId}},"raptor_node_id":null,"source":"rag","score":null,"turn":null}],"in_context":[],"last":1}"""
            : """{"events":[],"in_context":[],"last":1}""");
        var (s, body) = await h.GetAsync("/v1/memory-activity/heat?window_s=600");
        Assert.Equal(200, s);
        var r = JsonDocument.Parse(body).RootElement;
        Assert.Equal(600, r.GetProperty("window_s").GetInt32());
        Assert.Contains(r.GetProperty("nodes").EnumerateArray(), n => n.GetProperty("node_id").GetInt32() == leaf && n.GetProperty("heat").GetDouble() > 0.9);
    }
}
