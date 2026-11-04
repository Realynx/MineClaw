using System.Security.Cryptography;
using System.Text;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins the pure block-state functions of the blueprint code over every id × meta (× direction / quarter turns / axis), one hash
/// per function: <see cref="DesignConvert.Map"/>, <see cref="BuildBlocks"/>, <see cref="DesignFacing"/> and
/// <see cref="PlanTransform.Mirror"/>. Set WP02_GOLDEN_OUT to a directory to write the dumps out (to diff after a change).
/// </summary>
public class BlockStateGoldenTests
{
    private const int MaxId = 260;

    private static IEnumerable<(int Id, int Meta)> States()
    {
        for (int id = 0; id <= MaxId; id++)
            for (int meta = -1; meta <= 16; meta++)
                yield return (id, meta);
    }

    private static string Try(Func<object?> f)
    {
        try { return f()?.ToString() ?? "null"; }
        catch (Exception ex) { return "throws " + ex.GetType().Name; }
    }

    private static readonly Dir[] AllDirs = [Dir.North, Dir.East, Dir.South, Dir.West];

    private static readonly Dictionary<string, Func<int, int, string>> Functions = new()
    {
        ["convert b->r"] = (id, m) => Try(() => DesignConvert.Map(DesignVersions.Beta, DesignVersions.Release, id, m)),
        ["convert r->b"] = (id, m) => Try(() => DesignConvert.Map(DesignVersions.Release, DesignVersions.Beta, id, m)),
        ["BuildBlocks.MetaProblem"] = (id, m) => Try(() => BuildBlocks.MetaProblem(id, m)),
        ["BuildBlocks.Refusal"] = (id, _) => Try(() => BuildBlocks.Refusal(id)),
        ["BuildBlocks.Rotate"] = (id, m) => string.Join(",", Enumerable.Range(-1, 6).Select(q => Try(() => BuildBlocks.Rotate(id, m, q)))),
        ["BuildBlocks.MetaFor"] = (id, _) => string.Join(",", AllDirs.Select(d => Try(() => BuildBlocks.MetaFor(id, d)))),
        ["DesignFacing.FacingOf"] = (id, m) => Try(() => DesignFacing.FacingOf(id, m)),
        ["DesignFacing.WithFacing"] = (id, m) => string.Join(",", AllDirs.Select(d => Try(() => DesignFacing.WithFacing(id, m, d)))),
        ["DesignFacing.Rotate"] = (id, m) => string.Join(",", Enumerable.Range(-1, 6).Select(q => Try(() => DesignFacing.Rotate(id, m, q)))),
        ["DesignFacing.Mirror"] = (id, m) => string.Join(",", "xyz".Select(a => Try(() => DesignFacing.Mirror(id, m, a)))),
        ["DesignFacing.Partner"] = (id, m) => Try(() => DesignFacing.Partner(id, m)),
        ["DesignFacing.FirstHalf"] = (id, m) => Try(() => DesignFacing.FirstHalf(id, m)),
        ["PlanTransform.Mirror"] = (id, m) => string.Join(",", "xyz".Select(a => Try(() => PlanTransform.Mirror(id, m, a)))),
    };

    [Fact]
    public void Block_state_functions_match_the_golden_hashes()
    {
        var outDir = Environment.GetEnvironmentVariable("WP02_GOLDEN_OUT");
        var actual = new List<string>();
        foreach (var (name, f) in Functions)
        {
            var sb = new StringBuilder();
            foreach (var (id, meta) in States()) sb.Append(id).Append(':').Append(meta).Append(' ').Append(f(id, meta)).Append('\n');
            var dump = sb.ToString();
            if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, $"state-{name.Replace(' ', '_').Replace('>', '_')}.txt"), dump);
            actual.Add($"{name} {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)))[..24]}");
        }
        if (outDir is { Length: > 0 }) File.WriteAllLines(Path.Combine(outDir, "state-hashes.txt"), actual);
        else Assert.Equal(Expected, actual);
    }

    /// <summary>Generated from the pre-refactor code (WP02).</summary>
    private static readonly string[] Expected =
    [
        "convert b->r EE1D4A4EB81EE72B4C70F594",
        "convert r->b 0B70F854A6DD9DD758DE4BDD",
        "BuildBlocks.MetaProblem 894604F897E3929636DE6C82",
        "BuildBlocks.Refusal 2B721EFA45C41B0B9D87994A",
        "BuildBlocks.Rotate AB6F3B74B2DD56150C46A46D",
        "BuildBlocks.MetaFor 5DB7AC110D5F79CCFD1F80FA",
        "DesignFacing.FacingOf 8B5ADFAE598650BC9EF16BD7",
        "DesignFacing.WithFacing 28BF2123A9F5E0F201BAE813",
        "DesignFacing.Rotate 63C25A3BD64E81BC2FB6EC60",
        "DesignFacing.Mirror 2D27EF69B052F4E5A5040CA3",
        "DesignFacing.Partner 39A0D30E87D24AF7D112F85A",
        "DesignFacing.FirstHalf 92C70C73B0E118C372E892ED",
        "PlanTransform.Mirror 2A3543E899A18A804BECC19F",
    ];
}
