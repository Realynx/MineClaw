using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    // Accessed under the client's action lock. A changed target/support is new evidence; mere retries aren't.
    private readonly Dictionary<(int Dim, BlockPos Pos, int Item, int? Damage), (int Geometry, int Count)> _placementRefusals = new();

    private int PlacementGeometry(BlockPos pos)
    {
        var hash = new HashCode();
        foreach (var p in BlockFaceExtensions.All.Select(pos.Offset).Prepend(pos))
        {
            hash.Add(World.GetBlock(p));
            hash.Add(World.GetMeta(p));
        }
        return hash.ToHashCode();
    }

    private bool PlacementRefusedTooOften(BlockPos pos, int itemId, int? damage) =>
        _placementRefusals.TryGetValue((World.Dimension, pos, itemId, damage), out var r)
        && r.Geometry == PlacementGeometry(pos) && r.Count >= 3;

    private void NotePlacementRefusal(BlockPos pos, int itemId, int? damage)
    {
        var key = (World.Dimension, pos, itemId, damage);
        int geometry = PlacementGeometry(pos);
        int count = _placementRefusals.TryGetValue(key, out var old) && old.Geometry == geometry ? old.Count + 1 : 1;
        if (_placementRefusals.Count >= 128 && !_placementRefusals.ContainsKey(key))
            _placementRefusals.Remove(_placementRefusals.Keys.First());
        _placementRefusals[key] = (geometry, count);
    }
}
