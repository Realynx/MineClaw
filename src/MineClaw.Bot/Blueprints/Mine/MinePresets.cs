using System.Text.Json;

namespace MineClaw.Bot.Blueprints.Mine;

/// <summary>A version-preferred mine design: template steps on the plot (absolute y), with why it suits that version.</summary>
public sealed record MinePreset(string Id, string Version, string Name, string Ore, int Y, string Notes, IReadOnlyList<JsonElement> Steps);

/// <summary>
/// The version-preferred mine designs (docs/web/api.md "Mine designs"), the same data file the browser reads
/// (<c>web/apps/web/src/features/studio/mine/mine-presets.json</c>, embedded): "1.12.2 diamond strip mine at y 11", "b1.8.1
/// diamond strip mine at y 11", …. Applied to a mine canvas they are ordinary cells; a design saved from one is tagged
/// <c>preferred_for &lt;version&gt;</c>.
/// </summary>
public static class MinePresets
{
    private static readonly Lazy<IReadOnlyList<MinePreset>> Data = new(Load);

    public static IReadOnlyList<MinePreset> All => Data.Value;

    /// <summary>The presets preferred for a version.</summary>
    public static IEnumerable<MinePreset> For(string? version)
    {
        var v = DesignVersions.Normalize(version);
        return All.Where(p => v == null || p.Version == v);
    }

    public static MinePreset? Get(string? id) => All.FirstOrDefault(p => p.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<MinePreset> Load()
    {
        using var s = typeof(MinePresets).Assembly.GetManifestResourceStream("mine.mine-presets.json")
                      ?? throw new InvalidOperationException("mine.mine-presets.json is not embedded");
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.GetProperty("presets").EnumerateArray().Select(p => new MinePreset(
            p.GetProperty("id").GetString()!, p.GetProperty("version").GetString()!, p.GetProperty("name").GetString()!,
            p.GetProperty("ore").GetString()!, p.GetProperty("y").GetInt32(), p.GetProperty("notes").GetString()!,
            p.GetProperty("steps").EnumerateArray().Select(e => e.Clone()).ToList())).ToList();
    }

    /// <summary>The preset's cells on a terrain (its steps in order), with the first step's entry.</summary>
    public static MineCells Cells(MinePreset p, MineTerrain terrain)
    {
        var cells = new List<CanvasCell>();
        var path = new List<MineClaw.World.BlockPos>();
        MineClaw.World.BlockPos? entry = null;
        foreach (var step in p.Steps)
        {
            var c = MineTemplates.FromStep(step, terrain);
            cells.AddRange(c.Cells);
            path.AddRange(c.Path);
            entry ??= c.Entry;
        }
        return new MineCells(cells, entry ?? default, path, p.Name);
    }
}
