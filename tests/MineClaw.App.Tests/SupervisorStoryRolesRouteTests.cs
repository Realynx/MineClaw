using System.Text.Json.Nodes;
using MineClaw.App.Supervisor;
using MineClaw.Hive.Story;

namespace MineClaw.App.Tests;

/// <summary>Story Mode's work roles over the wire: <c>POST …/roles</c> and <c>POST …/priorities {role}</c> (docs/web/story-mode.md "Roles").</summary>
public sealed class SupervisorStoryRolesRouteTests
{
    private static async Task<(int Status, JsonNode? Json)> Call(SupervisorStoryRoutes r, string method, string path, object? body = null)
    {
        var json = body == null ? null : System.Text.Json.JsonSerializer.Serialize(body);
        var reply = await r.TryHandleAsync(new SupervisorRequest(method, path, new Dictionary<string, string>(), "s3cret", json));
        Assert.NotNull(reply);
        return (reply!.Status, reply.Body.Length > 0 ? JsonNode.Parse(reply.Text) : null);
    }

    [Fact]
    public async Task Story_roles_are_assigned_edited_and_removed_over_the_routes()
    {
        var o = new SupervisorOptions { Token = "s3cret", Viewer = { Enabled = true } };
        var w = new SupervisorStoryRouteTests.World();
        var r = new SupervisorStoryRoutes(o, new StoryColonies(new SupervisorStoryRouteTests.Host(w)), null, null, null);
        var (cs, cj) = await Call(r, "POST", "/api/story/colonies", new { name = "Roles", pawns = 3, home = new { x = 0, y = 64, z = 0 } });
        Assert.Equal(200, cs);
        var b = $"/api/story/colonies/{cj!["id"]!.GetValue<string>()}/";
        var (_, st) = await Call(r, "GET", b.TrimEnd('/'));
        Assert.Equal(10, st!["roles"]!.AsArray().Count);
        Assert.True(st["pawns"]![0]!.AsObject().ContainsKey("role"));
        var pawns = st["pawns"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToArray();

        // Two Farmers.
        Assert.Equal(200, (await Call(r, "POST", b + "priorities", new { pawns = pawns[..2], role = "farmer" })).Status);
        Assert.Equal(404, (await Call(r, "POST", b + "priorities", new { pawns = pawns[..1], role = "astronaut" })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "priorities", new { pawns = pawns[..1], role = "farmer", work = new { chop = 1 } })).Status);
        (_, st) = await Call(r, "GET", b.TrimEnd('/'));
        Assert.Equal("farmer", st!["pawns"]![0]!["role"]!.GetValue<string>());
        Assert.Equal(1, st["pawns"]![1]!["priorities"]!["farm"]!.GetValue<int>());

        // Edit the role: both rows follow.
        var (us, uj) = await Call(r, "POST", b + "roles", new { op = "update", role = new { id = "farmer", priorities = new { farm = 2 } } });
        Assert.Equal(200, us);
        Assert.Equal("farmer", uj!["id"]!.GetValue<string>());
        (_, st) = await Call(r, "GET", b.TrimEnd('/'));
        Assert.All(st!["pawns"]!.AsArray().Take(2), p => Assert.Equal(2, p!["priorities"]!["farm"]!.GetValue<int>()));

        // Validation.
        Assert.Equal(400, (await Call(r, "POST", b + "roles", new { op = "dance" })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "roles", new { op = "add", role = new { name = "" } })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "roles", new { op = "update", role = new { id = "nope", name = "X" } })).Status);
        Assert.Equal(400, (await Call(r, "POST", b + "roles", new { op = "add", role = new { name = "Bad", icon = "NOT OK!" } })).Status);

        // Add, clear, remove, reset.
        var (a, aj) = await Call(r, "POST", b + "roles", new { op = "add", role = new { name = "Sentry", icon = "shield", priorities = new { guard = 1 } } });
        Assert.Equal(200, a);
        Assert.Equal("sentry", aj!["id"]!.GetValue<string>());
        Assert.Equal(200, (await Call(r, "POST", b + "priorities", new { pawns = pawns[2..], role = "sentry" })).Status);
        Assert.Equal(200, (await Call(r, "POST", b + "priorities", new Dictionary<string, object?> { ["pawns"] = pawns[..1], ["role"] = null })).Status);
        var (rs, rj) = await Call(r, "POST", b + "roles", new { op = "remove", id = "sentry" });
        Assert.Equal(200, rs);
        Assert.True(rj!["removed"]!.GetValue<bool>());
        (_, st) = await Call(r, "GET", b.TrimEnd('/'));
        Assert.Null(st!["pawns"]![0]!["role"]);
        Assert.Null(st["pawns"]![2]!["role"]);
        Assert.Equal(1, st["pawns"]![2]!["priorities"]!["guard"]!.GetValue<int>()); // Custom, same values
        Assert.Equal(200, (await Call(r, "POST", b + "roles", new { op = "reset_defaults" })).Status);
        (_, st) = await Call(r, "GET", b.TrimEnd('/'));
        Assert.Equal(1, st!["pawns"]![1]!["priorities"]!["farm"]!.GetValue<int>()); // the remaining Farmer is reset with its role
    }
}
