using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Agent.Tools;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Hierarchy.Bus;

namespace MineClaw.Hive.Story;

/// <summary>Main and hive brains share one storage vocabulary and execute every edit in the owning colony on main.</summary>
public static class StoryStorageTool
{
    public const string Name = "colony_storage";

    private static JsonElement Schema() => ToolSchema.Create()
        .Str("op", enumValues: ["list", "catalog", "set", "add", "remove"])
        .Str("colony", "Colony id; optional when only one exists.", required: false)
        .Raw("chests", """{"type":"array","items":{"type":"array","items":{"type":"integer"},"minItems":3,"maxItems":3}}""", required: false)
        .Raw("area", """{"type":"array","items":{"type":"integer"},"minItems":6,"maxItems":6,"description":"Inclusive x1,y1,z1,x2,y2,z2."}""", required: false)
        .Str("build_id", "Building footprint from list; use y_min/y_max to select its floor.", required: false)
        .Int("y_min", required: false).Int("y_max", required: false)
        .StrArray("allow", "Category ids, item names or id:meta.", required: false)
        .StrArray("deny", required: false)
        .Str("priority", enumValues: ["low", "normal", "preferred", "important", "critical"], required: false)
        .Str("label", required: false)
        .Int("offset", required: false).Int("limit", required: false).Build();

    private static void Register(ToolRegistry tools, Func<JsonElement, CancellationToken, Task<ToolResult>> run)
    {
        tools.Register(Name,
            "Inspect colony chests/build bounds or edit storage filters. List first to locate a building floor; choose chests, area, or build_id with Y bounds. " +
            "set replaces filters; add/remove patch entries (remove allow excludes it). Signs enforce filters and highest priority; pawns remove signs before edits apply.",
            Schema(), run, tiers: ToolTiers.Main);
        tools.SetCap(Name, 20_000);
    }

    public static void RegisterMain(ToolRegistry tools, StoryColonies colonies) =>
        Register(tools, (input, _) => Task.FromResult(Handle(colonies, input)));

    public static void RegisterRemote(ToolRegistry tools, CloneHost host) =>
        RegisterRemote(tools, (input, ct) => host.SendBusAsync(
            BusEnvelope.Make(BusTypes.StoryStorage, host.Name, HiveTree.Root, input, host.BusToken), TimeSpan.FromSeconds(15), ct));

    internal static void RegisterRemote(ToolRegistry tools, Func<JsonElement, CancellationToken, Task<BusReply?>> send) =>
        Register(tools, async (input, ct) =>
        {
            var reply = await send(input, ct).ConfigureAwait(false);
            return reply == null ? ToolResult.Error("main did not answer the storage request")
                : !reply.Ok ? ToolResult.Error(reply.Error ?? "storage request failed")
                : ToolResult.Success(reply.As<string>() ?? "{}");
        });

    /// <summary>Chain only this request onto the existing authenticated bus; spawning and shared memory keep their handlers.</summary>
    public static void AttachBus(HiveBus bus, StoryColonies colonies)
    {
        var previous = bus.MainHandler;
        bus.MainHandler = (envelope, ct) =>
        {
            if (envelope.Type != BusTypes.StoryStorage)
                return previous?.Invoke(envelope, ct) ?? Task.FromResult(BusReply.Fail("main has no handler for this request"));
            if (envelope.Body is not { ValueKind: JsonValueKind.Object } input)
                return Task.FromResult(BusReply.Fail("storage request must be an object"));
            var result = Handle(colonies, input);
            return Task.FromResult(result.Ok ? BusReply.Of(result.Text) : BusReply.Fail(result.Text));
        };
    }

    internal static ToolResult Handle(StoryColonies colonies, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return ToolResult.Error("storage request must be an object");
        var request = JsonNode.Parse(input.GetRawText())!.AsObject();
        var id = input.Str("colony");
        var all = colonies.All;
        var colony = id == null && all.Count == 1 ? all[0] : id == null ? null : colonies.Get(id);
        if (colony == null)
            return ToolResult.Error(all.Count == 0 ? "no Story colony exists"
                : $"{(id == null ? "colony is required" : "unknown colony " + id)}; available: {string.Join(", ", all.Select(c => c.Id + " (" + c.Meta.Name + ")"))}");
        var (result, error) = colony.StorageManage(request);
        return error == null ? ToolResult.Success(result?.ToJsonString() ?? "{}") : ToolResult.Error(error);
    }
}
