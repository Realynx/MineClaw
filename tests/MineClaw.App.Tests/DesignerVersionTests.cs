using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using MineClaw.App.Supervisor;
using MineClaw.Bot.Blueprints;

namespace MineClaw.App.Tests;

/// <summary>
/// The Design Studio's versions (docs/web/api.md "Design Studio"): a session's version (default the server's, b1.8.1 here), its
/// whole palette with categories and specs, switching an empty canvas, the compatibility check and the conversion of a full one,
/// a design opened in its own version and converted for a world of the other one, and the AI's vocabulary (find_blocks, any
/// block by name, "did you mean") at a prompt that never lists every block.
/// </summary>
public class DesignerVersionTests
{
    private const string Token = "s3cret-token";

    private sealed class ScriptedModel(params Func<LlmRequest, LlmResponse>[] steps) : ILlmClient
    {
        private int _i;
        public readonly ConcurrentQueue<LlmRequest> Requests = new();

        public Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
        {
            Requests.Enqueue(r);
            return Task.FromResult(steps[Math.Min(_i++, steps.Length - 1)](r));
        }
    }

    private static readonly LlmUsage Use = new(1000, 200, 3000, 0);

    private static LlmResponse Tools(LlmRequest r, params (string Name, object Input)[] calls) =>
        new(calls.Select((c, i) => (LlmBlock)new LlmToolUse($"t{i}", c.Name, JsonSerializer.SerializeToElement(c.Input))).ToList(), StopReasons.ToolUse, Use, r.Model ?? "claude-opus-5-5");

    private static LlmResponse Say(LlmRequest r, string text) => new([new LlmText(text)], StopReasons.EndTurn, Use, r.Model ?? "claude-opus-5-5");

    private static SupervisorServer Server(ILlmClient llm, out BlueprintService designs)
    {
        var db = AgentDb.Open(null);
        var s = new BlueprintService(new BlueprintOptions(), new BlueprintStore(db), new BlueprintPreview())
        {
            Site = () => throw new InvalidOperationException("no site in tests"), Crew = () => throw new InvalidOperationException(),
            ResolveSite = (_, _) => (null, "n/a"),
        };
        s.History = new DesignHistory(s, new DesignRevisions(db));
        designs = s;
        var o = new SupervisorOptions { Token = Token, Viewer = new SupervisorViewerOptions { Enabled = true, Designer = new DesignerOptions { Enabled = true } } };
        var routes = new DesignerRoutes(o, () => s, new DesignerStore(db), llm, new LlmOptions { ApiKey = "sk-test" }, null, () => "maid_chan");
        return new SupervisorServer(o, new FakeSupervisorData()) { Designer = routes };
    }

    private static SupervisorRequest Req(string method, string path, object? body = null) =>
        new(method, path, new Dictionary<string, string>(), Token, body == null ? null : JsonSerializer.Serialize(body));

    private static JsonElement J(SupervisorReply r)
    {
        Assert.True(r.Status == 200, $"{r.Status}: {r.Text}");
        return JsonDocument.Parse(r.Text).RootElement.Clone();
    }

    private static int[] Cell(int x, int y, int z, int id, int meta) => [x, y, z, id, meta];

    [Fact]
    public async Task A_session_has_a_version_its_whole_palette_and_switches_while_empty()
    {
        var server = Server(new ScriptedModel(), out _);
        var models = J(await server.HandleAsync(Req("GET", "/api/designer/models")));
        Assert.Equal(["b1.8.1", "1.12.2"], models.GetProperty("versions").EnumerateArray().Select(v => v.GetString()));
        // Default: the server's version (b1.8.1 here) and its palette.
        var s = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { player = "poofimafox" })));
        long id = s.GetProperty("id").GetInt64();
        Assert.Equal("b1.8.1", s.GetProperty("version").GetString());
        Assert.Equal("b1.8.1", s.GetProperty("server_version").GetString());
        Assert.InRange(s.GetProperty("palette").GetArrayLength(), 100, 180);
        var wool = s.GetProperty("palette").EnumerateArray().First(p => p.GetProperty("spec").GetString() == "wool:gray");
        Assert.Equal(7, wool.GetProperty("meta").GetInt32());
        Assert.Equal("Colored", wool.GetProperty("cat").GetString());
        Assert.Equal(7, s.GetProperty("categories").GetArrayLength());
        // An empty canvas switches at once; its palette follows.
        var v = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/version", new { version = "1.12.2", player = "poofimafox" })));
        Assert.True(v.GetProperty("changed").GetBoolean());
        var s2 = v.GetProperty("session");
        Assert.Equal("1.12.2", s2.GetProperty("version").GetString());
        Assert.True(s2.GetProperty("palette").GetArrayLength() >= 390);
        var door = s2.GetProperty("palette").EnumerateArray().First(p => p.GetProperty("spec").GetString() == "spruce_door");
        Assert.Equal("door", door.GetProperty("family").GetString());
        Assert.True(door.GetProperty("facing").GetBoolean());
        Assert.Equal("1.12.2", J(await server.HandleAsync(Req("GET", $"/api/designer/sessions/{id}"))).GetProperty("version").GetString()); // persisted
        // A new session can start in either version; a bad one is refused.
        var modern = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { version = "1.12.2" })));
        Assert.Equal("1.12.2", modern.GetProperty("version").GetString());
        Assert.Equal(400, (await server.HandleAsync(Req("POST", "/api/designer/sessions", new { version = "1.7" }))).Status);
    }

    [Fact]
    public async Task Changing_the_version_of_a_full_canvas_checks_first_then_converts_with_a_report()
    {
        var server = Server(new ScriptedModel(), out _);
        var s = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { version = "1.12.2" })));
        long id = s.GetProperty("id").GetInt64();
        J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/canvas", new
        {
            rev = 0, ops = new[] { new { kind = "place", label = "mix", cells = new[] { Cell(30, 0, 30, 1, 0), Cell(31, 0, 30, 5, 1), Cell(32, 0, 30, 251, 0) } } },
        })));
        // The check changes nothing.
        var check = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/version", new { version = "b1.8.1" })));
        Assert.False(check.GetProperty("changed").GetBoolean());
        var report = check.GetProperty("report");
        Assert.False(report.GetProperty("compatible").GetBoolean());
        Assert.Equal(1, report.GetProperty("same").GetInt32());
        Assert.Equal("Spruce Planks → Wooden Planks", report.GetProperty("substituted")[0].GetProperty("name").GetString());
        Assert.Equal("White Concrete", report.GetProperty("missing")[0].GetProperty("name").GetString());
        Assert.Equal("1.12.2", J(await server.HandleAsync(Req("GET", $"/api/designer/sessions/{id}"))).GetProperty("version").GetString());
        // Applied: the concrete is left out, the planks become b1.8.1's, the model hears about it.
        var conv = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/version", new { version = "b1.8.1", apply = true })));
        var session = conv.GetProperty("session");
        Assert.Equal("b1.8.1", session.GetProperty("version").GetString());
        var blocks = session.GetProperty("blocks").EnumerateArray().Select(b => (b[3].GetInt32(), b[4].GetInt32())).OrderBy(b => b).ToList();
        Assert.Equal([(1, 0), (5, 0)], blocks);
        Assert.Contains(session.GetProperty("messages").EnumerateArray(), m => m.GetProperty("text").GetString()!.Contains("Converted the design from 1.12.2 to b1.8.1"));
    }

    [Fact]
    public async Task A_design_opens_in_its_own_version_and_converts_for_another_world_with_the_report()
    {
        var server = Server(new ScriptedModel(), out var designs);
        var s = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { version = "1.12.2" })));
        long id = s.GetProperty("id").GetInt64();
        J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/canvas", new
        {
            rev = 0, ops = new[] { new { kind = "place", label = "hut", cells = new[] { Cell(30, 0, 30, 4, 0), Cell(31, 0, 30, 95, 14), Cell(32, 0, 30, 159, 3) } } },
        })));
        var saved = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/save", new { name = "glass hut", player = "poofimafox" })));
        long did = saved.GetProperty("design_id").GetInt64();
        Assert.Equal("1.12.2", DesignVersions.Of(designs.Store.Get(did)!));
        // Opened again: 1.12.2 (not the server's), with how it would build here.
        var open = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { design_id = did })));
        Assert.Equal("1.12.2", open.GetProperty("version").GetString());
        Assert.Equal("1.12.2", open.GetProperty("revisions")[0].GetProperty("version").GetString());
        var here = open.GetProperty("server_compat");
        Assert.False(here.GetProperty("compatible").GetBoolean());
        Assert.Equal("Light Blue Terracotta", here.GetProperty("missing")[0].GetProperty("name").GetString());
        // Story asks for it converted to this world: the cells come in b1.8.1's ids, the report says what didn't make it.
        var story = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { design_id = did, convert_to = "b1.8.1" })));
        Assert.Equal("b1.8.1", story.GetProperty("version").GetString());
        var compat = story.GetProperty("compat");
        Assert.False(compat.GetProperty("compatible").GetBoolean());
        Assert.Contains("Red Stained Glass → Glass", compat.GetProperty("text").GetString());
        Assert.DoesNotContain(story.GetProperty("blocks").EnumerateArray(), b => b[3].GetInt32() is 95 or 159);
        // The bot's own build refuses it, naming the block.
        Assert.Contains("1 Light Blue Terracotta", designs.WorldRefusal(designs.Store.Get(did)!));
    }

    [Fact]
    public async Task The_model_finds_blocks_by_name_uses_any_of_them_and_hears_what_was_meant()
    {
        var model = new ScriptedModel(
            r => Tools(r, ("find_blocks", new { q = "magenta concrete" })),
            r => Tools(r, ("place", new { b = "concrete:magenta", at = new[] { new[] { 30, 0, 30 } } }),
                ("place", new { b = "redwol", at = new[] { new[] { 31, 0, 30 } } }),
                ("place", new { b = "bed:north", at = new[] { new[] { 33, 0, 33 } } })),
            r => Say(r, "Placed a magenta concrete block and a bed."));
        var server = Server(model, out _);
        var s = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { version = "1.12.2" })));
        long id = s.GetProperty("id").GetInt64();
        var m = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/messages", new { text = "a magenta block and a bed", canvas = new { rev = 0 } })));
        Assert.Equal(2, m.GetProperty("ops").GetArrayLength());
        var reqs = model.Requests.ToList();
        // The prompt names the version and its categories, never the whole list.
        var system = reqs[0].System[0].Text;
        Assert.Contains("Minecraft 1.12.2", system);
        Assert.Contains("find_blocks", system);
        Assert.DoesNotContain("light_blue_glazed_terracotta", system);
        Assert.True(system.Length < 4200, $"{system.Length} chars");
        Assert.Contains(reqs[0].Tools, t => t.Name == "find_blocks");
        // find_blocks answered with the spec; the misspelling got "did you mean".
        string Result(LlmRequest r, int i) => ((LlmToolResult)r.Messages[^1].Content[i]).Content;
        Assert.StartsWith("concrete:magenta = Magenta Concrete", Result(reqs[1], 0));
        Assert.Contains("did you mean", Result(reqs[2], 1));
        Assert.Contains("wool:red", Result(reqs[2], 1));
        var after = J(await server.HandleAsync(Req("GET", $"/api/designer/sessions/{id}")));
        var cells = after.GetProperty("blocks").EnumerateArray().Select(b => (b[0].GetInt32(), b[2].GetInt32(), b[3].GetInt32(), b[4].GetInt32())).ToList();
        Assert.Contains((30, 30, 251, 2), cells);
        Assert.Contains((33, 33, 26, 2), cells);   // the bed's foot, facing north…
        Assert.Contains((33, 32, 26, 10), cells);  // …its head added to the north
    }
}
