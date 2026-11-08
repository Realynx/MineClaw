using System.Text.Json;
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
}
