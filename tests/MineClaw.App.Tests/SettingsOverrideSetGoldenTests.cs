using System.Text.Json.Nodes;
using MineClaw.App.Supervisor.Admin;

namespace MineClaw.App.Tests;

/// <summary>
/// Pins <see cref="SettingsOverrideStore.Set"/> (WP07): sets create parents (keeping an existing key's casing), a scalar parent is
/// replaced by an object, the leaf is re-added at the end, removals prune empty parents up to the first non-empty one and never
/// create anything.
/// </summary>
public class SettingsOverrideSetGoldenTests
{
    private static string Apply(string json, params (string Key, string? Value)[] ops)
    {
        var root = (JsonObject)JsonNode.Parse(json)!;
        foreach (var (k, v) in ops) SettingsOverrideStore.Set(root, k, v == null ? null : JsonNode.Parse(v));
        return root.ToJsonString();
    }

    [Theory]
    [InlineData("{}", "A:B:C", "1", """{"A":{"B":{"C":1}}}""")]
    [InlineData("""{"a":{"b":2,"x":0}}""", "A:B", "3", """{"a":{"x":0,"b":3}}""")]
    [InlineData("""{"a":{"B":2,"x":0}}""", "a:b", "\"s\"", """{"a":{"x":0,"B":"s"}}""")]
    [InlineData("""{"a":5}""", "A:B", "true", """{"a":{"B":true}}""")]
    [InlineData("""{"a":[1]}""", "a:0", "9", """{"a":{"0":9}}""")]
    [InlineData("""{"k":1}""", "K", "[1,2]", """{"k":[1,2]}""")]
    [InlineData("""{"a":{"b":{"c":1}},"z":0}""", "a:b:c", null, """{"z":0}""")]
    [InlineData("""{"a":{"b":{"c":1},"d":2}}""", "A:B:C", null, """{"a":{"d":2}}""")]
    [InlineData("""{"a":{"b":{"c":1,"e":3}}}""", "a:b:c", null, """{"a":{"b":{"e":3}}}""")]
    [InlineData("""{"a":{"b":{}}}""", "a:b:c", null, """{}""")]
    [InlineData("""{"a":{"b":1}}""", "a:x:y", null, """{"a":{"b":1}}""")]
    [InlineData("""{"a":5}""", "a:b", null, """{"a":5}""")]
    [InlineData("""{"a":1,"b":2}""", "A", null, """{"b":2}""")]
    [InlineData("""{}""", "missing", null, """{}""")]
    public void Set_and_remove(string json, string key, string? value, string expected)
    {
        Assert.Equal(expected, Apply(json, (key, value)));
    }

    [Fact]
    public void A_sequence_round_trips()
    {
        Assert.Equal("""{"Agent":{"Llm":{"Model":"m"}}}""",
            Apply("{}", ("Agent:Llm:Model", "\"m\""), ("Agent:Self:Name", "\"n\""), ("agent:self:name", null)));
    }
}
