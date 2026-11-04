using System.Security.Cryptography;
using System.Text;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Scripting.Sdk;
using Xunit.Abstractions;

namespace MineClaw.Scripting.Tests;

/// <summary>
/// Pins helpers the WP09 refactor reshaped without other coverage: the patch-then words (a table now; checked against a copy of
/// the old switch) and <c>gather()</c>'s item source routes for every item and block name of both versions (hashed).
/// WP09_PURE_OUT=&lt;dir&gt; writes the routes text (item-sources-&lt;version&gt;.txt).
/// </summary>
public class ScriptHelperGoldenTests(ITestOutputHelper output)
{
    private static PatchThen OldParse(string? s) => (s ?? "").Trim().ToLowerInvariant().Replace('-', '_') switch
    {
        "" or "resume" or "continue" or "ok" => PatchThen.Resume,
        "restart_step" or "restart" or "retry" or "redo" => PatchThen.RestartStep,
        "stop" or "cancel" or "end" => PatchThen.Stop,
        _ => PatchThen.Escalate,
    };

    [Fact]
    public void Patch_then_words_match_the_old_switch()
    {
        string?[] words =
        [
            null, "", "  ", "resume", "continue", "ok", "restart_step", "restart-step", "restart", "retry", "redo", "stop", "cancel",
            "end", "escalate", "brain", "pass", " Resume ", "RESTART_STEP", "Restart-Step", "STOP", "ok!", "resume_step", "re do", "x",
        ];
        foreach (var w in words) Assert.Equal(OldParse(w), PatchThens.Parse(w));
    }

    [Theory]
    [InlineData(false, "BB06500E4AB21EDEBBCC8947AE5388C7")]
    [InlineData(true, "5A6BDA12963261A53B41D1B5873B9900")]
    public void Item_source_routes_are_unchanged(bool release1122, string pinned)
    {
        IGameData data = release1122 ? Release1122GameData.Instance : GameData.Current;
        var names = new SortedSet<string>(StringComparer.Ordinal);
        for (int id = 0; id < 2400; id++)
            if (Items.Exists(id)) names.Add(Items.Get(id).Name);
        foreach (var b in data.BlockIds) names.Add(data.Block(b).Name);
        foreach (var n in new[] { "log", "planks", "oak_log", "rotten_flesh", "mutton", "rabbit_hide", "beetroot", "carrot", "potato", "bogus", "" })
            names.Add(n);
        var sb = new StringBuilder();
        foreach (var n in names) sb.Append(n).Append(" => ").Append(ItemSources.For(data, n).ToJsonString()).Append('\n');
        var text = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
        if (Environment.GetEnvironmentVariable("WP09_PURE_OUT") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"item-sources-{(release1122 ? "1122" : "b181")}.txt"), text);
        }
        output.WriteLine($"{(release1122 ? "1122" : "b181")} {hash} ({names.Count} names)");
        Assert.Equal(pinned, hash);
    }
}
