using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 2026-10-28, torching the main island at night for Robin: the worker guessed torch cells over air and failed twice
/// ("-12,64,-3 has nothing solid on any side to place against (all air)", "-9,64,-18 ... all air"). An attachable whose cell
/// has nothing to hang on snaps to the nearest cell within 2 with ground under it or a wall beside it, never over the void.
/// </summary>
public class TorchSnapTests
{
    /// <summary>A 5×5 cobble floor at y63 (x -14..-10, z -2..2): z -3 is already past the rim, over the void.</summary>
    private static Dictionary<(int, int, int), int> Island()
    {
        var b = new Dictionary<(int, int, int), int>();
        for (int x = -14; x <= -10; x++)
        for (int z = -2; z <= 2; z++)
            b[(x, 63, z)] = Blocks.Cobblestone;
        return b;
    }

    [Fact]
    public void A_torch_over_the_air_past_the_rim_snaps_to_the_adjacent_ground()
    {
        var w = new TestBlocks(Island());
        var req = new BlockPos(-12, 64, -3);
        Assert.Empty(PlaceRules.AttachFaces(w, req, Blocks.Torch));
        var s = PlaceRules.SnapAttachable(w, req, Blocks.Torch);
        Assert.NotNull(s);
        Assert.Equal(new BlockPos(-12, 64, -2), s!.Value.Pos);
        Assert.Equal(BlockFace.Top, s.Value.Face); // a floor torch (meta 5), not one hung on the island's outer side over the void
        Assert.Equal(5, PlaceRules.TorchMeta(s.Value.Face));
        Assert.Equal(1, s.Value.Moved);
    }

    [Fact]
    public void Nothing_within_2_over_the_void_is_one_actionable_line()
    {
        var w = new TestBlocks(Island());
        var req = new BlockPos(-9, 64, -18);
        Assert.Null(PlaceRules.SnapAttachable(w, req, Blocks.Torch));
        Assert.Equal("no ground or wall within 2 of -9,64,-18 (it's over the void): pick a spot on the island", PlaceRules.NoAttachProblem(w, req));
    }

    [Fact]
    public void Attach_rules_follow_b181_canPlace()
    {
        var b = Island();
        b[(-12, 64, 0)] = Blocks.Cobblestone;
        b[(-12, 66, 1)] = Blocks.Cobblestone; // a ceiling over -12,65,1
        var w = new TestBlocks(b);
        Assert.Equal([BlockFace.Top, BlockFace.South], PlaceRules.AttachFaces(w, new BlockPos(-12, 64, 1), Blocks.Torch));
        Assert.Equal([BlockFace.South], PlaceRules.AttachFaces(w, new BlockPos(-12, 64, 1), Blocks.Ladder)); // ladders: walls only
        Assert.Empty(PlaceRules.AttachFaces(w, new BlockPos(-12, 65, 1), Blocks.Torch));                   // never a ceiling
        Assert.True(PlaceRules.IsAttachable(Blocks.Torch) && PlaceRules.IsAttachable(Items.Sign) && PlaceRules.IsAttachable(Blocks.Lever));
        Assert.False(PlaceRules.IsAttachable(Blocks.Cobblestone));
        Assert.Equal([1, 2, 3, 4], new[] { BlockFace.East, BlockFace.West, BlockFace.South, BlockFace.North }.Select(PlaceRules.TorchMeta));
    }

    /// <summary>The fake server's stone floor (6..10 × 6..10 at y63) with a 5-high cobble pillar at 8,64..68,8; the bot at
    /// 9.5,64,7.5. The request 9,66,9 hangs in the air two above the floor.</summary>
    private static void Pillar(FakeServer srv)
    {
        for (int y = 64; y <= 68; y++) srv.Blocks[(8, y, 8)] = Blocks.Cobblestone;
        srv.SpawnAt = (9.5, 64, 7.5);
        srv.Inventory[36] = Harness.Stack(Blocks.Torch, 8);
    }

    [Fact]
    public async Task A_wall_nearby_makes_a_wall_torch_with_the_right_meta()
    {
        var (c, s) = await Harness.StartAsync(Pillar);
        await using var _ = c;
        using var __ = s;
        Assert.True(await Harness.WaitAsync(() => c.World.Player.OnGround && c.World.GetBlock(8, 66, 8) == Blocks.Cobblestone));
        var req = new BlockPos(9, 66, 9);
        Assert.Empty(PlaceRules.AttachFaces(c.World, req, Blocks.Torch));
        var spot = PlaceRules.SnapAttachable(c.World, req, Blocks.Torch, c.World.Player.Position);
        Assert.NotNull(spot);
        Assert.Equal(new BlockPos(9, 66, 8), spot!.Value.Pos); // on the pillar's east side, nearer the bot than its south side
        Assert.Equal(BlockFace.East, spot.Value.Face);
        var r = await c.PlaceBlockAsync(spot.Value.Pos.X, spot.Value.Pos.Y, spot.Value.Pos.Z, Blocks.Torch, spot.Value.Face);
        Assert.True(r.Ok, r.Message);
        var click = s.Got<PlayerBlockPlacementPacket>().Last();
        Assert.Equal((8, 66, 8), (click.X, (int)click.Y, click.Z));
        Assert.Equal(Blocks.Torch, s.Blocks.GetValueOrDefault((9, 66, 8)));
        Assert.Equal(1, s.Metas.GetValueOrDefault((9, 66, 8))); // wall torch leaning off the block to its west
    }

    /// <summary>Minimal block access over a FakeServer-style dictionary (everything loaded).</summary>
    private sealed class TestBlocks(Dictionary<(int, int, int), int> blocks) : IBlockAccess
    {
        public int GetBlock(int x, int y, int z) => blocks.GetValueOrDefault((x, y, z));
        public int GetMeta(int x, int y, int z) => 0;
        public bool IsLoaded(int x, int z) => true;
        public BlockInfo Info(int id) => Blocks.Get(id);
    }
}
