using MineClaw.App.Supervisor;
using MineClaw.Bot.Blueprints;
using MineClaw.Render;
using static MineClaw.App.Tests.DesignerTests;

namespace MineClaw.App.Tests;

[Collection(GameDataDefaultCollection.Name)]
public class DesignerPictureTests
{
    private static string Picture(uint color = 0x445566) => "data:image/png;base64," + Convert.ToBase64String(Png.Encode(new uint[] { color }, 1, 1));

    [Fact]
    public async Task Save_keeps_camera_png_and_picture_only_preserves_the_saved_design_and_revision()
    {
        var (server, _, svc) = Server(new ScriptedModel());
        long session = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { }))).GetProperty("id").GetInt64();
        J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{session}/canvas",
            new { rev = 0, ops = new[] { new { kind = "place", cells = new[] { new[] { 32, 0, 32, 4, 0 } } } } })));
        var saved = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{session}/save", new { name = "Storehouse", preview_png = Picture() })));
        long id = saved.GetProperty("design_id").GetInt64();
        int rev = saved.GetProperty("rev").GetInt32();
        var dsl = svc.Store.Get(id)!.Dsl;
        Assert.Equal(Png.Encode(new uint[] { 0x445566 }, 1, 1), svc.Store.Picture(id));
        var update = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{session}/save",
            new { name = "A name that must not replace the saved one", preview_only = true, preview_png = Picture(0x112233) })));
        Assert.False(update.GetProperty("created").GetBoolean());
        Assert.Equal(rev, svc.History!.Revisions.Head(id)!.Rev);
        Assert.Equal(dsl, svc.Store.Get(id)!.Dsl);
        Assert.Equal("Storehouse", svc.Store.Get(id)!.Name);
        Assert.Equal(Png.Encode(new uint[] { 0x112233 }, 1, 1), svc.Store.Picture(id));
    }

    [Theory]
    [InlineData("data:image/png;base64,invalid")]
    [InlineData("https://example.com/preview.png")]
    [InlineData("data:image/svg+xml;base64,PHN2Zz4=")]
    public async Task Invalid_picture_is_rejected_before_a_design_is_saved(string picture)
    {
        var (server, _, svc) = Server(new ScriptedModel());
        long session = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { }))).GetProperty("id").GetInt64();
        var response = await server.HandleAsync(Req("POST", $"/api/designer/sessions/{session}/save", new { name = "Nope", preview_png = picture }));
        Assert.Equal(400, response.Status);
        Assert.Contains("preview_png", response.Text);
        Assert.Null(svc.Store.Get(1));
    }

    [Fact]
    public async Task Huge_dimensions_are_rejected_and_picture_only_requires_an_existing_design()
    {
        var (server, _, _) = Server(new ScriptedModel());
        long session = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { }))).GetProperty("id").GetInt64();
        var png = Png.Encode(new uint[1281], 1281, 1);
        Assert.Equal(400, (await server.HandleAsync(Req("POST", $"/api/designer/sessions/{session}/save",
            new { name = "Nope", preview_png = "data:image/png;base64," + Convert.ToBase64String(png) }))).Status);
        Assert.Equal(400, (await server.HandleAsync(Req("POST", $"/api/designer/sessions/{session}/save",
            new { name = "Nope", preview_only = true, preview_png = Picture() }))).Status);
    }

    [Fact]
    public async Task Full_size_preview_is_saved_without_reducing_its_dimensions()
    {
        var (server, _, svc) = Server(new ScriptedModel());
        long session = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { }))).GetProperty("id").GetInt64();
        J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{session}/canvas",
            new { rev = 0, ops = new[] { new { kind = "place", cells = new[] { new[] { 32, 0, 32, 4, 0 } } } } })));
        var png = Png.Encode(new uint[1280 * 960], 1280, 960);
        var saved = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{session}/save",
            new { name = "Detailed", preview_png = "data:image/png;base64," + Convert.ToBase64String(png) })));
        Assert.Equal(png, svc.Store.Picture(saved.GetProperty("design_id").GetInt64()));
    }
}
