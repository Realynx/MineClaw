using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.App.Supervisor;
using MineClaw.Bot.Blueprints;
using MineClaw.World;

namespace MineClaw.App.Tests;

/// <summary>The supervisor serves designs: the list, /view/blueprint/{id} (PNG) and the JSON bill.</summary>
public class SupervisorBlueprintTests
{
    private sealed class DesignData(BlueprintService s) : ISupervisorData
    {
        private readonly FakeSupervisorData _base = new();
        public IReadOnlyList<BotTile> Bots() => _base.Bots();
        public Task<ViewImage?> ViewAsync(string bot, string mode, int radius, RenderGate gate, CancellationToken ct) => _base.ViewAsync(bot, mode, radius, gate, ct);
        public Task<ViewImage> HiveAsync(RenderGate gate, CancellationToken ct) => _base.HiveAsync(gate, ct);
        public Task<ViewImage> CameraAsync(CameraQuery q, RenderGate gate, CancellationToken ct) => _base.CameraAsync(q, gate, ct);
        public object Status(string? selected) => _base.Status(selected);
        public IReadOnlyList<FeedEvent> Events(long since, int max) => _base.Events(since, max);
        public LocateResult? Locate(string query) => _base.Locate(query);
        public IReadOnlyList<object> Designs() => s.Store.List().Select(r => (object)new { id = r.Id, name = r.Name, status = r.Status, summary = r.Summary }).ToList();
        public ViewImage? DesignPicture(long id) => s.Store.Picture(id) is { } png ? new ViewImage(png, 4, 4, 1, "design", []) : null;
        public object? DesignBill(long id) => s.Store.Get(id) is { } r ? s.BillJson(r) : null;
    }

    [Fact]
    public async Task Designs_list_preview_png_and_bill_are_served_read_only()
    {
        var store = new BlueprintStore(AgentDb.Open(null));
        var s = new BlueprintService(new BlueprintOptions(), store, new BlueprintPreview())
        {
            Site = () => throw new InvalidOperationException(), Crew = () => throw new InvalidOperationException(),
            ResolveSite = (_, _) => (null, "n/a"),
        };
        var rec = store.Insert(new BlueprintRecord { Name = "hut", Description = "small cobble hut", Bot = "maid_chan", Dsl = "size 1x1x1", Status = DesignStatus.Review });
        rec.Summary = "hut 5×5×4, door south: 44 cobblestone";
        rec.Bill = [new BillRow("cobblestone", 44, 20, 10, 14, "gen ~23s")];
        store.Update(rec, png: Png.Encode(new uint[16], 4, 4));
        var server = new SupervisorServer(new SupervisorOptions(), new DesignData(s));
        static SupervisorRequest Get(string p, string? meta = null) =>
            new("GET", p, meta == null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["meta"] = meta });

        var list = await server.HandleAsync(Get("/api/blueprints"));
        Assert.Equal(200, list.Status);
        Assert.Contains("door south", list.Text);
        var png = await server.HandleAsync(Get($"/view/blueprint/{rec.Id}"));
        Assert.Equal(200, png.Status);
        Assert.Equal("image/png", png.ContentType);
        var bill = await server.HandleAsync(Get($"/api/blueprints/{rec.Id}"));
        using var doc = JsonDocument.Parse(bill.Text);
        Assert.Equal(14, doc.RootElement.GetProperty("bill")[0].GetProperty("missing").GetInt32());
        Assert.Contains("gen", (await server.HandleAsync(Get($"/view/blueprint/{rec.Id}", "1"))).Text);
        Assert.Equal(404, (await server.HandleAsync(Get("/view/blueprint/99"))).Status);
        // "blueprint" is never mistaken for a bot name, and writes stay refused.
        Assert.Equal(405, (await server.HandleAsync(new SupervisorRequest("POST", "/api/blueprints", new Dictionary<string, string>()))).Status);
        Assert.Contains("Designs", System.Text.Encoding.UTF8.GetString(SupervisorServer.Page()));
    }
}
