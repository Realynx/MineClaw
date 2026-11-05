using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 11-04 story-1 (Fox: "The quarry is not being dug any more because the pawns refuse to go into it. They get the idea to do it,
/// they go to do it, they get almost down the stairs and then cancel that idea."): "dig 181,57,366 failed: can't reach stone" ×14 from as
/// many pawns, the layer held for that one cell. The cell was under a stair step (docs/web/story-mode.md "Quarries"): its top is the step,
/// one side the next step, its open faces toward the pit floor. The pawn's walk "within 3" ended on the step over it, the dig's reach goal
/// (distance only) was met right there with the step in between, and the dig gave up without a face in view. Now the dig walks on to a
/// spot that sees a face: down the last step onto the floor of the layer being dug.
/// </summary>
public class QuarryDigTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private const int X1 = 3, Z1 = 3, Size = 5, Top = 63, Layer = 10;

    /// <summary>The 5×5 quarry's clockwise ring from its north-west corner (StoryQuarry.Ring).</summary>
    private static List<(int X, int Z)> Ring()
    {
        int x2 = X1 + Size - 1, z2 = Z1 + Size - 1;
        var ring = new List<(int, int)>();
        for (int x = X1; x <= x2; x++) ring.Add((x, Z1));
        for (int z = Z1 + 1; z <= z2; z++) ring.Add((x2, z));
        for (int x = x2 - 1; x >= X1; x--) ring.Add((x, z2));
        for (int z = z2 - 1; z > Z1; z--) ring.Add((X1, z));
        return ring;
    }

    /// <summary>The step of layer k: ring cell k mod n at y Top − k.</summary>
    private static BlockPos Step(int k)
    {
        var ring = Ring();
        var (x, z) = ring[k % ring.Count];
        return new BlockPos(x, Top - k, z);
    }

    /// <summary>The last cell of layer 10: the one under step 9 (step 9 on it, step 10 beside it, the pit floor on its two other sides).</summary>
    private static BlockPos Target => new(Step(Layer - 1).X, Top - Layer, Step(Layer - 1).Z);

    /// <summary>Stone y 40..63; the quarry dug down 10 layers (its steps standing) and layer 10 dug but for its step and the target.</summary>
    private static void QuarryWorld(FakeServer s, (double X, double Y, double Z) spawn)
    {
        s.Blocks.Clear();
        for (int x = -4; x <= 14; x++)
            for (int z = -4; z <= 14; z++)
                for (int y = 40; y <= Top; y++) s.Blocks[(x, y, z)] = Blocks.Stone;
        for (int k = 0; k <= Layer; k++)
            for (int x = X1; x < X1 + Size; x++)
                for (int z = Z1; z < Z1 + Size; z++)
                {
                    var at = new BlockPos(x, Top - k, z);
                    if (at != Step(k) && at != Target) s.Blocks.Remove((x, Top - k, z));
                }
        s.Inventory[36] = Harness.Stack(Items.StonePickaxe, 1);
        s.SpawnAt = spawn;
    }

    private Func<ClientOptions, ClientOptions> Logged => o => o with
    {
        Log = m => { try { log.WriteLine(m); } catch (InvalidOperationException) { } },
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_cell_under_a_stair_step_is_dug_from_the_pit_floor_not_given_up_on_from_the_step(bool release1122, bool fromSurface)
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var over = Step(Layer - 1);
        var spawn = fromSurface ? (0.5, Top + 1.0, 0.5) : (over.X + 0.5, over.Y + 1.0, over.Z + 0.5);
        var (c, s) = await Harness.StartAsync(srv => QuarryWorld(srv, spawn), Logged, release1122);
        await using var _ = c;
        using var __ = s;
        var t = Target;
        if (fromSurface)
        {
            // The pawn script's walk first (story_pawn reach(a, 3) → story.go): within 3, never from under the ground.
            var go = await c.GotoAsync(GoalFiltered.NearNotFromBelow(c.World, t, 3));
            Assert.True(go.Ok, go.Message);
        }
        var r = await c.DigBlockAsync(t.X, t.Y, t.Z);
        Assert.True(r.Ok, r.Message);
        Assert.False(s.Blocks.ContainsKey((t.X, t.Y, t.Z)), "the cell is dug");
        for (int k = 0; k <= Layer; k++) // the stair stands
            Assert.Equal(Blocks.Stone, s.Blocks.GetValueOrDefault((Step(k).X, Step(k).Y, Step(k).Z)));
        Assert.True(c.World.Player.Position.Y >= t.Y - 1e-6, $"fell below the layer: {c.World.Player.Position}");
    }

    /// <summary>The edge cell beside the pit, on the quarry's top layer (dirt under the grass line), west of its middle.</summary>
    private static BlockPos EdgeCell => new(X1 - 1, Top, Z1 + 2);

    /// <summary>Bedrock at y 50, stone 51..63; the 5×5 pit dug from the top to the bedrock (13 deep, no stair: only its edge matters).
    /// <paramref name="openToVoid"/>: no bedrock under the pit either (a skyblock island's cut edge: the void).</summary>
    private static void PitWorld(FakeServer s, bool openToVoid)
    {
        s.Blocks.Clear();
        for (int x = -4; x <= 14; x++)
            for (int z = -4; z <= 14; z++)
            {
                s.Blocks[(x, 50, z)] = Blocks.Bedrock;
                for (int y = 51; y <= Top; y++) s.Blocks[(x, y, z)] = Blocks.Stone;
            }
        for (int x = X1; x < X1 + Size; x++)
            for (int z = Z1; z < Z1 + Size; z++)
                for (int y = openToVoid ? 50 : 51; y <= Top; y++) s.Blocks.Remove((x, y, z));
        s.Blocks[(EdgeCell.X, EdgeCell.Y, EdgeCell.Z)] = Blocks.Dirt;
        s.Inventory[36] = Harness.Stack(Items.StonePickaxe, 1);
        s.SpawnAt = (EdgeCell.X - 2 + 0.5, Top + 1.0, EdgeCell.Z + 0.5);
    }

    /// <summary>
    /// Live 11-05 (bot 20261105-011140): "dig 195,63,367 failed: won't dig dirt …: it's the floor of a walkway with the void 1 away (dig only
    /// the taught cell)" ×3 for each of 9 top-layer cells beside the quarry's pit, dug to bedrock: the quarry stalled on that layer. The
    /// walkway veto counted any drop over 3 as "the void"; a pit with a floor is not one (Safety.FatalEdgeDistance).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_quarry_edge_cell_beside_its_pit_dug_to_bedrock_is_dug(bool release1122)
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv => PitWorld(srv, openToVoid: false), Logged, release1122);
        await using var _ = c;
        using var __ = s;
        var t = EdgeCell;
        var r = await c.DigBlockAsync(t.X, t.Y, t.Z);
        Assert.True(r.Ok, r.Message);
        Assert.False(s.Blocks.ContainsKey((t.X, t.Y, t.Z)), "the edge cell is dug");
        Assert.True(c.World.Player.Position.Y >= Top + 1 - 1e-6, $"fell into the pit: {c.World.Player.Position}");
    }

    /// <summary>The same edge over the real void (a skyblock island's cut edge): still the floor of a walkway, never dug.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_walkway_floor_beside_the_real_void_on_skyblock_is_still_refused(bool release1122)
    {
        using var mode = WorldMode.Override(WorldMode.SkyblockWorld);
        var (c, s) = await Harness.StartAsync(srv => PitWorld(srv, openToVoid: true), Logged, release1122);
        await using var _ = c;
        using var __ = s;
        var t = EdgeCell;
        var r = await c.DigBlockAsync(t.X, t.Y, t.Z);
        Assert.False(r.Ok);
        Assert.Contains("floor of a walkway with the void 1 away", r.Message);
        Assert.Equal(Blocks.Dirt, s.Blocks.GetValueOrDefault((t.X, t.Y, t.Z)));
    }
}
