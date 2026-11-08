using MineClaw.Bot.Blueprints;
using static MineClaw.App.Tests.DesignerTests;

namespace MineClaw.App.Tests;

[Collection(GameDataDefaultCollection.Name)]
public class DesignerLibraryTests
{
    [Fact]
    public async Task Library_delete_preserves_world_design_and_history_but_cannot_be_reopened_or_overwritten()
    {
        var (server, _, svc) = Server(new ScriptedModel());
        long session = J(await server.HandleAsync(Req("POST", "/api/designer/sessions", new { }))).GetProperty("id").GetInt64();
        J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{session}/canvas",
            new { rev = 0, ops = new[] { new { kind = "place", cells = new[] { new[] { 32, 0, 32, 4, 0 } } } } })));
        long id = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{session}/save", new { name = "Storehouse" }))).GetProperty("design_id").GetInt64();
        var before = svc.Store.Get(id)!;
        var head = svc.History!.Revisions.Head(id)!;
        Assert.Equal(400, (await server.HandleAsync(Req("POST", $"/api/designer/designs/{id}/delete", new { name = "Different design" }))).Status);
        Assert.Equal(403, (await server.HandleAsync(Req("POST", $"/api/designer/designs/{id}/delete", new { name = "Storehouse" }, token: null))).Status);
        var reply = J(await server.HandleAsync(Req("POST", $"/api/designer/designs/{id}/delete", new { name = "Storehouse", who = "tester" })));
        Assert.Contains("Existing builds are unchanged", reply.GetProperty("text").GetString());
        Assert.Equivalent(before, svc.Store.Get(id)); // No demolition, cancellation, status or DSL mutation.
        Assert.Equivalent(head, svc.History.Revisions.Head(id));
        Assert.NotEqual(DesignStatus.Deleted, svc.Store.Get(id)!.Status);
        Assert.Equal(404, (await server.HandleAsync(Req("POST", "/api/designer/sessions", new { design_id = id }))).Status);
        Assert.Equal(400, (await server.HandleAsync(Req("POST", $"/api/designer/sessions/{session}/save", new { name = "Storehouse" }))).Status);
        long copy = J(await server.HandleAsync(Req("POST", $"/api/designer/sessions/{session}/save", new { name = "Copy", as_new = true }))).GetProperty("design_id").GetInt64();
        Assert.NotEqual(id, copy);
    }
}
