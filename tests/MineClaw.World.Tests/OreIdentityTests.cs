using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.World.Tests;

public class OreIdentityTests
{
    private sealed class Grid : IBlockAccess
    {
        public readonly Dictionary<BlockPos, int> Blocks = new();
        public readonly HashSet<(int, int)> Unknown = new();
        public int GetBlock(int x, int y, int z) => Blocks.GetValueOrDefault(new(x, y, z), 1);
        public int GetMeta(int x, int y, int z) => 0;
        public bool IsLoaded(int x, int z) => !Unknown.Contains((x, z));
        public BlockInfo Info(int id) => throw new NotSupportedException();
        public int WorldHeight => 256;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Only_version_supported_ores_have_an_identity_and_redstone_states_match(bool modern)
    {
        foreach (int id in new[] { 14, 15, 16, 21, 56, 73 }) Assert.Equal(id, OreIdentity.Canonical(id, modern));
        Assert.Equal(73, OreIdentity.Canonical(74, modern));
        foreach (int id in new[] { 129, 153 }) Assert.Equal(modern ? id : 0, OreIdentity.Canonical(id, modern));
        foreach (int id in new[] { 0, 1, 4, 7, 17, 41, 42, 54, 57, 152 }) Assert.Equal(0, OreIdentity.Canonical(id, modern));
    }

    [Fact]
    public void Connectivity_includes_exactly_six_faces_and_no_diagonals()
    {
        Assert.Equal(6, OreIdentity.Neighbors.Distinct().Count());
        Assert.All(OreIdentity.Neighbors, d => Assert.Equal(1, Math.Abs(d.Dx) + Math.Abs(d.Dy) + Math.Abs(d.Dz)));
        foreach (var d in OreIdentity.Neighbors) Assert.Contains((-d.Dx, -d.Dy, -d.Dz), OreIdentity.Neighbors);
    }

    [Fact]
    public void Exposure_requires_loaded_neighbors_and_never_opens_adjacent_liquid()
    {
        var at = new BlockPos(10, 30, 10);
        var world = new Grid();
        Assert.True(OreIdentity.ExposureSafe(world, at));
        foreach (var (dx, dy, dz) in OreIdentity.Neighbors)
        {
            var neighbor = new BlockPos(at.X + dx, at.Y + dy, at.Z + dz);
            foreach (int id in new[] { 8, 9, 10, 11 })
            {
                world.Blocks[neighbor] = id;
                Assert.False(OreIdentity.ExposureSafe(world, at));
            }
            world.Blocks.Clear();
            world.Unknown.Add((neighbor.X, neighbor.Z));
            Assert.False(OreIdentity.ExposureSafe(world, at));
            world.Unknown.Clear();
        }
        Assert.False(OreIdentity.ExposureSafe(world, new BlockPos(10, 0, 10)));
        Assert.False(OreIdentity.ExposureSafe(world, new BlockPos(10, 255, 10)));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(145)]
    public void Removing_ore_does_not_drop_sand_gravel_or_an_anvil_onto_the_worker(int falling)
    {
        var world = new Grid();
        var at = new BlockPos(10, 30, 10);
        world.Blocks[at.Up()] = falling;
        Assert.False(OreIdentity.ExposureSafe(world, at));
        world.Blocks.Clear();
        world.Blocks[at.Down()] = falling;
        Assert.True(OreIdentity.ExposureSafe(world, at));
    }
}
