namespace MineClaw.Data;

/// <summary>
/// Block registry for Beta 1.8.1. STUB: only a few entries until the Data workstream fills it in.
/// The public API surface (Get / ByName / All / constants) is the contract other projects code against.
/// </summary>
public static class Blocks
{
    public const int Air = 0, Stone = 1, Grass = 2, Dirt = 3, Cobblestone = 4;

    private static readonly BlockInfo[] _byId = Build();
    private static readonly Dictionary<string, BlockInfo> _byName =
        _byId.Where(b => b is not null).ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<BlockInfo> All => _byId.Where(b => b is not null).ToArray();

    /// <summary>Returns the block for an id; unknown ids return a generic solid placeholder.</summary>
    public static BlockInfo Get(int id) =>
        id >= 0 && id < _byId.Length && _byId[id] is { } b
            ? b
            : new BlockInfo(id, $"unknown_{id}", $"Unknown ({id})", 1f, true, 1.0, false, false, false, false, ToolType.None, -1, false);

    public static BlockInfo? ByName(string name) => _byName.GetValueOrDefault(name);

    private static BlockInfo[] Build()
    {
        var a = new BlockInfo[256];
        a[0] = new(0, "air", "Air", 0f, false, 0, false, false, true, false, ToolType.None, -1, false);
        a[1] = new(1, "stone", "Stone", 1.5f, true, 1.0, false, false, false, false, ToolType.Pickaxe, 0, false);
        a[2] = new(2, "grass", "Grass", 0.6f, true, 1.0, false, false, false, false, ToolType.Shovel, -1, false);
        a[3] = new(3, "dirt", "Dirt", 0.5f, true, 1.0, false, false, false, false, ToolType.Shovel, -1, false);
        a[4] = new(4, "cobblestone", "Cobblestone", 2.0f, true, 1.0, false, false, false, false, ToolType.Pickaxe, 0, false);
        return a;
    }
}
