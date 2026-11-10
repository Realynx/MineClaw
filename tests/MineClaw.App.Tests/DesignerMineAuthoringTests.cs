using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Bot.Blueprints;

namespace MineClaw.App.Tests;

public partial class DesignerMineTests
{
    private static string Authoring(bool visible) => JsonSerializer.Serialize(new
    {
        size = 256, modules = new[] { new { id = "shaft", at = new[] { 128, 70, 128 }, visible,
            spec = new { kind = "spiral", clockwise = false, depth = 59, torchEvery = 6, landingDepths = new[] { 59 } } } },
        baseDefault = -1, @base = Array.Empty<int[]>(), overrides = Array.Empty<int[]>(),
    });

    [Fact]
    public async Task Mine_authoring_is_saved_with_exact_revision_including_metadata_only_edits_and_save_as()
    {
        var server = Server(new ScriptedModel(), out _);
        var session = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { player = "poofimafox", kind = "mine", seed = "4242", version = "1.12.2" })));
        long id = session.GetProperty("id").GetInt64();
        J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/canvas", new
        {
            rev = 0, ops = new[] { new { kind = "dig", label = "mine", cells = new[] { new[] { 128, 70, 128, 0, 0 } } } },
        })));
        var first = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/save", new { name = "components", mine_authoring = Authoring(true) })));
        long design = first.GetProperty("design_id").GetInt64();
        int rev1 = first.GetProperty("rev").GetInt32();
        var second = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/save", new { name = "components", mine_authoring = Authoring(false) })));
        Assert.Equal(rev1 + 1, second.GetProperty("rev").GetInt32());
        var repeated = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/save", new { name = "components", mine_authoring = Authoring(false) })));
        Assert.Equal(rev1 + 1, repeated.GetProperty("rev").GetInt32());
        var fresh = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { design_id = design, player = "poofimafox" })));
        Assert.Equal(Authoring(false), fresh.GetProperty("mine_authoring").GetString());
        Assert.Equal(rev1 + 1, fresh.GetProperty("design_revision").GetInt32());
        Assert.Single(fresh.GetProperty("dug").EnumerateArray());
        var old = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { design_id = design, rev = rev1, player = "poofimafox" })));
        Assert.Equal(Authoring(true), old.GetProperty("mine_authoring").GetString());
        Assert.Equal(rev1, old.GetProperty("design_revision").GetInt32());
        long oldId = old.GetProperty("id").GetInt64();
        var copied = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{oldId}/save", new { name = "copy", as_new = true })));
        long copyId = copied.GetProperty("design_id").GetInt64();
        Assert.NotEqual(design, copyId);
        var copy = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { design_id = copyId, player = "poofimafox" })));
        Assert.Equal(Authoring(true), copy.GetProperty("mine_authoring").GetString());
        var resumed = J(await server.HandleAsync(Req("GET", $"/api/designer/sessions/{id}")));
        Assert.Equal(Authoring(false), resumed.GetProperty("mine_authoring").GetString());
        Assert.Equal(rev1 + 1, resumed.GetProperty("design_revision").GetInt32());
    }

    [Fact]
    public async Task Legacy_mines_without_authoring_still_open_and_invalid_metadata_never_saves_a_revision()
    {
        var server = Server(new ScriptedModel(), out _);
        var session = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { kind = "mine", seed = "4242", version = "1.12.2" })));
        long id = session.GetProperty("id").GetInt64();
        J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/canvas", new { rev = 0, ops = new[] { new { kind = "dig", cells = new[] { new[] { 128, 70, 128, 0, 0 } } } } })));
        var saved = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/save", new { name = "legacy" })));
        var opened = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { design_id = saved.GetProperty("design_id").GetInt64() })));
        Assert.Equal(JsonValueKind.Null, opened.GetProperty("mine_authoring").ValueKind);
        foreach (var bad in new[] { "not json", "{}", "[]", new string('x', DesignerMineAuthoring.MaxLength + 1) })
            Assert.Equal(400, (await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/save", new { name = "bad", mine_authoring = bad }))).Status);
        var unchanged = J(await server.HandleAsync(Req("GET", $"/api/designer/sessions/{id}")));
        Assert.Single(unchanged.GetProperty("revisions").EnumerateArray());
    }

    /// <summary>
    /// What the real studio produced for one scenario (a nested, turned, named and hidden group with a hand-placed torch): its
    /// canvas edits in order, the layout envelope it saved and the cells it held. The browser, contracts and API tests read the
    /// same file; web/apps/web/test/mineAuthoringRoundTrip.test.ts writes it.
    /// </summary>
    private static JsonElement NestedFixture()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MineClaw.sln"))) d = d.Parent;
        var root = d?.FullName ?? throw new InvalidOperationException("repo root not found");
        var file = Path.Combine(root, "web", "packages", "contracts", "test", "fixtures", "mine-authoring-nested.json");
        return JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
    }

    /// <summary>A JSON value as text without white space: two cell lists compare as they stand, order included.</summary>
    private static string Compact(JsonElement e) => JsonSerializer.Serialize(e);

    [Fact]
    public async Task Nested_groups_round_trip_through_save_reopen_and_resume()
    {
        var f = NestedFixture();
        string envelope = f.GetProperty("envelope").GetString()!;
        string blocks = Compact(f.GetProperty("blocks")), dug = Compact(f.GetProperty("dug"));
        var server = Server(new ScriptedModel(), out _);
        var session = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new
        {
            player = "poofimafox", kind = "mine", seed = f.GetProperty("seed").GetString(), version = f.GetProperty("version").GetString(),
        })));
        long id = session.GetProperty("id").GetInt64();

        // The studio's edits, one request each as it sent them. The bot changes every cell it is told and corrects none:
        // a torch it turned would be a cell the layout does not know, and the browser would drop the component around it.
        int rev = 0;
        foreach (var op in f.GetProperty("ops").EnumerateArray())
        {
            var reply = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/canvas", new { rev, ops = new[] { op } })));
            string label = op.GetProperty("label").GetString()!;
            Assert.True(reply.GetProperty("fixed").GetArrayLength() == 0, $"{label}: the bot corrected {Compact(reply.GetProperty("fixed"))}");
            Assert.True(op.GetProperty("cells").GetArrayLength() == reply.GetProperty("applied").GetInt32(), $"{label}: applied {reply.GetProperty("applied").GetInt32()}");
            rev = reply.GetProperty("rev").GetInt32();
        }
        var authored = J(await server.HandleAsync(Req("GET", $"/api/designer/sessions/{id}")));
        Assert.Equal(blocks, Compact(authored.GetProperty("blocks")));
        Assert.Equal(dug, Compact(authored.GetProperty("dug")));
        Assert.Equal(rev, authored.GetProperty("rev").GetInt32());

        // Saved, then opened from the design and resumed in the saving session: the same string, revision and cells, cell for cell.
        var first = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/save", new { name = "nested floor", mine_authoring = envelope, player = "poofimafox" })));
        long design = first.GetProperty("design_id").GetInt64();
        int rev1 = first.GetProperty("rev").GetInt32();
        var opened = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { design_id = design, player = "poofimafox" })));
        var resumed = J(await server.HandleAsync(Req("GET", $"/api/designer/sessions/{id}")));
        foreach (var s in new[] { opened, resumed })
        {
            Assert.Equal(envelope, s.GetProperty("mine_authoring").GetString());
            Assert.Equal(rev1, s.GetProperty("design_revision").GetInt32());
            Assert.Equal(blocks, Compact(s.GetProperty("blocks")));
            Assert.Equal(dug, Compact(s.GetProperty("dug")));
            Assert.Equal("mine", s.GetProperty("kind").GetString());
            Assert.Equal(f.GetProperty("seed").GetString(), s.GetProperty("seed").GetString());
        }
        Assert.NotEqual(id, opened.GetProperty("id").GetInt64());

        // Only the group's name changed: a revision of its own, and the first one keeps the layout it was saved with.
        var layout = JsonNode.Parse(envelope)!;
        var group = layout["modules"]!.AsArray().Single(m => m!["spec"]!["kind"]!.GetValue<string>() == "custom")!;
        Assert.Equal("Diamond floor", group["spec"]!["label"]!.GetValue<string>());
        group["spec"]!["label"] = "Diamond floor, east";
        string renamed = layout.ToJsonString();
        var second = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/save", new { name = "nested floor", mine_authoring = renamed, player = "poofimafox" })));
        Assert.Equal(design, second.GetProperty("design_id").GetInt64());
        Assert.Equal(rev1 + 1, second.GetProperty("rev").GetInt32());
        var newest = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { design_id = design, player = "poofimafox" })));
        Assert.Equal(renamed, newest.GetProperty("mine_authoring").GetString());
        Assert.Equal(rev1 + 1, newest.GetProperty("design_revision").GetInt32());
        Assert.Equal(dug, Compact(newest.GetProperty("dug")));
        var older = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { design_id = design, rev = rev1, player = "poofimafox" })));
        Assert.Equal(envelope, older.GetProperty("mine_authoring").GetString());
        Assert.Equal(blocks, Compact(older.GetProperty("blocks")));
        Assert.Equal(dug, Compact(older.GetProperty("dug")));
    }

    [Fact]
    public void Valid_accepts_nine_nested_levels_and_rejects_101_modules()
    {
        // The deepest layout the browser makes: nine component levels, the innermost a spiral with its landings.
        JsonNode spec = new JsonObject
        {
            ["kind"] = "spiral", ["depth"] = 12, ["landingDepths"] = new JsonArray(6, 12),
        };
        for (int level = 2; level <= 9; level++)
        {
            var part = new JsonObject { ["spec"] = spec, ["at"] = new JsonArray(0, 0, 0) };
            spec = new JsonObject { ["kind"] = "custom", ["label"] = $"level {level}", ["parts"] = new JsonArray(part) };
        }
        string Envelope(int modules) => new JsonObject
        {
            ["size"] = 256,
            ["modules"] = new JsonArray(Enumerable.Range(0, modules).Select(i => (JsonNode?)new JsonObject
            {
                ["id"] = $"module-{i}", ["spec"] = spec.DeepClone(), ["at"] = new JsonArray(128, 40, 128),
            }).ToArray()),
            ["baseDefault"] = -1, ["base"] = new JsonArray(), ["overrides"] = new JsonArray(),
        }.ToJsonString();

        using var nine = JsonDocument.Parse(Envelope(1), new JsonDocumentOptions { MaxDepth = 64 });
        Assert.Equal(29, Depth(nine.RootElement));
        Assert.True(DesignerMineAuthoring.Valid(Envelope(1)));
        Assert.True(DesignerMineAuthoring.Valid(Envelope(100)));
        Assert.False(DesignerMineAuthoring.Valid(Envelope(101)));
    }

    /// <summary>How deep a JSON value nests: 1 for an object or array of plain values, 0 for a plain value.</summary>
    private static int Depth(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => 1 + e.EnumerateObject().Select(p => Depth(p.Value)).DefaultIfEmpty(0).Max(),
        JsonValueKind.Array => 1 + e.EnumerateArray().Select(Depth).DefaultIfEmpty(0).Max(),
        _ => 0,
    };
}
