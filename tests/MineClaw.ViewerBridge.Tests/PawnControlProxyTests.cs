using MineClaw.ViewerBridge.Supervisor;

namespace MineClaw.ViewerBridge.Tests;

public class PawnControlProxyTests
{
    [Fact]
    public void Handover_has_time_for_planning_without_extending_movement_timeouts()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), SupervisorProxy.TimeoutFor("bodies/102/control/acquire"));
        Assert.Equal(TimeSpan.FromSeconds(4), SupervisorProxy.TimeoutFor("bodies/102/control/input"));
    }

    [Fact]
    public async Task Expired_control_preserves_the_conflict_and_recovery_message()
    {
        await using var h = await BridgeHost.StartAsync();
        h.Sup.Routes["/api/bodies/102/control/input"] = _ => FakeSupervisor.Json(
            """{"error":"Control session expired. Take control again."}""", 409);
        var (status, body) = await h.PostAsync("/v1/sup/bodies/102/control/input", """{"session":"test-only","seq":1}""", roles: "story");
        Assert.Equal(409, status);
        Assert.Contains("conflict", body);
        Assert.Contains("Take control again", body);
    }

    [Fact]
    public async Task Control_requires_story_and_forwards_only_the_signed_identity()
    {
        await using var h = await BridgeHost.StartAsync();
        const string target = "/api/bodies/102/control/acquire";
        h.Sup.Routes[target] = _ => FakeSupervisor.Json("""{"session":"test-only","body":"102"}""");
        const string path = "/v1/sup/bodies/102/control/acquire";
        Assert.Equal(403, (await h.PostAsync(path, """{"request_id":"abc"}""", roles: "viewer")).Status);
        Assert.DoesNotContain(h.Sup.Seen, r => r.Target == target);
        Assert.Equal(200, (await h.PostAsync(path, """{"request_id":"abc"}""", roles: "story,viewer")).Status);
        var seen = h.Sup.Seen.Last(r => r.Target == target);
        Assert.Equal("fox", seen.User);
        Assert.Equal("story,viewer", seen.Roles);
        Assert.Equal(400, (await h.PostAsync(path, """{"request_id":"abc","who":"mallory"}""", roles: "story")).Status);
    }

    [Theory]
    [InlineData("bodies/102/control/acquire", true)]
    [InlineData("bodies/102/control/input", true)]
    [InlineData("bodies/102/control/action", true)]
    [InlineData("bodies/102/control/release", true)]
    [InlineData("bodies/102/controller", false)]
    [InlineData("bodies/102/control/release/extra", false)]
    [InlineData("bodies/102/script", false)]
    public void Only_control_actions_are_allowed_as_body_writes(string path, bool expected) =>
        Assert.Equal(expected, SupervisorProxy.Allowed("POST", path));

    [Theory]
    [InlineData("api/bodies/102/control/input", true)]
    [InlineData("api/bodies/102/control/release", true)]
    [InlineData("api/bodies/102/control/action", false)]
    [InlineData("api/bodies/102/control/acquire", false)]
    public void Only_quick_control_calls_take_the_reserved_pool(string path, bool expected) =>
        Assert.Equal(expected, SupervisorClient.IsControlInput(HttpMethod.Post, path));
}
