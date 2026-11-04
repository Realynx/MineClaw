using System.Text.Json;
using MineClaw.App.Hosting;
using Microsoft.Extensions.Configuration;

namespace MineClaw.App.Tests;

/// <summary>
/// Pins <see cref="SettingsOverrideFile.ResolvePath"/> and <see cref="SettingsOverrideProvider.Flatten"/> (WP07): every branch of the
/// override path (env, test seam, configured key, data directory) and the flat keys and list paths of a mixed JSON object.
/// </summary>
public class SettingsOverrideGoldenTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "mc-override-root");

    private static IConfiguration Partial(string? configured = null, string? dataDir = null)
    {
        var d = new Dictionary<string, string?>();
        if (configured != null) d[SettingsOverrideFile.ConfigKey] = configured;
        if (dataDir != null) d["Agent:DataDirectory"] = dataDir;
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    private static string Under(string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(Root, p));

    [Theory]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("off", null)]
    [InlineData(" none ", null)]
    [InlineData("OFF", "OFF")]
    [InlineData(" over/ride.json ", "over/ride.json")]
    public void Env_value_names_the_path_or_turns_it_off(string env, string? expected)
    {
        foreach (var seam in new[] { false, true })
            Assert.Equal(expected == null ? null : Under(expected),
                SettingsOverrideFile.ResolvePath(env, "cli-data", Partial("conf.json", "cfg-data"), Root, seam));
    }

    [Fact]
    public void Test_seam_without_env_is_off()
    {
        Assert.Null(SettingsOverrideFile.ResolvePath(null, "cli-data", Partial("conf.json", "cfg-data"), Root, testSeam: true));
    }

    [Theory]
    [InlineData("off", null)]
    [InlineData(" none ", null)]
    [InlineData(" conf.json ", "conf.json")]
    [InlineData("Off", "Off")]
    public void Configured_key_names_the_path_or_turns_it_off(string configured, string? expected)
    {
        Assert.Equal(expected == null ? null : Under(expected), SettingsOverrideFile.ResolvePath(null, "cli-data", Partial(configured, "cfg-data"), Root));
    }

    [Theory]
    [InlineData(null, null, "data")]
    [InlineData(null, " ", "data")]
    [InlineData("  ", "cfg-data", "data")]
    [InlineData(null, "cfg-data", "cfg-data")]
    [InlineData("cli-data", "cfg-data", "cli-data")]
    public void Default_lives_in_the_data_directory(string? cliDataDir, string? configDataDir, string expectedDir)
    {
        foreach (var configured in new string?[] { null, "", "   " })
            Assert.Equal(Path.Combine(Under(expectedDir), SettingsOverrideFile.FileName),
                SettingsOverrideFile.ResolvePath(null, cliDataDir, Partial(configured, configDataDir), Root));
    }

    [Fact]
    public void Flatten_keys_values_and_list_paths()
    {
        const string json = """
            {"A":{"B":"x","C":3,"D":true,"E":null,"F":[1,"two",null,{"G":false},[4.50]],"H":{}},"I":[],"J":"","K":-1e3}
            """;
        using var doc = JsonDocument.Parse(json);
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var lists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SettingsOverrideProvider.Flatten(doc.RootElement, null, data, lists);
        var flat = string.Join("|", data.Select(kv => kv.Key + "=" + (kv.Value ?? "<null>")));
        Assert.Equal("A:B=x|A:C=3|A:D=true|A:E=<null>|A:F:0=1|A:F:1=two|A:F:2=<null>|A:F:3:G=false|A:F:4:0=4.50|J=|K=-1e3", flat);
        Assert.Equal("A:F|A:F:4|I", string.Join("|", lists.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void Flatten_scalars_without_a_prefix_write_nothing()
    {
        foreach (var raw in new[] { "1", "\"s\"", "null", "true" })
        {
            using var doc = JsonDocument.Parse(raw);
            var data = new Dictionary<string, string?>();
            var lists = new HashSet<string>();
            SettingsOverrideProvider.Flatten(doc.RootElement, null, data, lists);
            Assert.Empty(data);
            Assert.Empty(lists);
        }
        using var arr = JsonDocument.Parse("[1,[2]]");
        var d2 = new Dictionary<string, string?>();
        var l2 = new HashSet<string>();
        SettingsOverrideProvider.Flatten(arr.RootElement, null, d2, l2);
        Assert.Equal(":0=1|:1:0=2", string.Join("|", d2.Select(kv => kv.Key + "=" + kv.Value)));
        Assert.Equal(":1", string.Join("|", l2));
    }
}
