using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;
using MineClaw.World;

namespace MineClaw.Hive.Tests;

public sealed class StorySignWorldTests
{
    private static readonly BlockPos At = new(9, 64, 8);

    private static WorldState SignWorld(string? line = null)
    {
        var world = new WorldState();
        world.ApplyChunkRegion(0, 0, 0, 16, 128, 16, new byte[16 * 128 * 16], null, null, null);
        world.SetBlock(At.X, At.Y, At.Z, Blocks.WallSign, 2);
        if (line != null) world.BlockEntities.Set(At, BlockEntityInfo.Sign([line, "", "", ""]));
        return world;
    }

    [Fact]
    public void Sign_text_from_another_body_is_available_even_when_main_supplies_the_blocks()
    {
        var main = SignWorld();
        var pawn = SignWorld("wood");
        var world = new MergedStoryWorld(() => main, () => [pawn], Beta181GameData.Instance);
        var sign = Assert.Single(world.Signs());
        Assert.Equal(new P3(9, 64, 8), sign.At);
        Assert.Equal(new[] { "wood", "", "", "" }, sign.Lines);
    }

    [Fact]
    public void Most_recent_server_text_wins_across_body_caches()
    {
        var main = SignWorld("wood");
        var pawn = SignWorld("stone");
        var world = new MergedStoryWorld(() => main, () => [pawn], Beta181GameData.Instance);
        Assert.Equal("stone", Assert.Single(world.Signs()).Lines[0]);
        main.BlockEntities.Set(At, BlockEntityInfo.Sign(["food", "", "", ""]));
        Assert.Equal("food", Assert.Single(world.Signs()).Lines[0]);
    }

    [Fact]
    public void Removed_or_unloaded_signs_do_not_reappear_from_stale_text()
    {
        var main = SignWorld("wood");
        var pawn = SignWorld("stone");
        var world = new MergedStoryWorld(() => main, () => [pawn], Beta181GameData.Instance);
        main.SetBlock(At.X, At.Y, At.Z, Blocks.Air, 0);
        Assert.Empty(world.Signs());
        pawn.UnloadColumn(0, 0);
        Assert.Empty(pawn.BlockEntities.SignObservations());
        Assert.Empty(new WorldStateStoryWorld(() => pawn, Beta181GameData.Instance).Signs());
    }

    [Fact]
    public void Another_dimension_never_supplies_storage_sign_text()
    {
        var main = SignWorld("wood");
        var nether = SignWorld("lava");
        nether.Dimension = -1;
        var world = new MergedStoryWorld(() => main, () => [nether], Beta181GameData.Instance);
        Assert.Equal("wood", Assert.Single(world.Signs()).Lines[0]);
    }
}
