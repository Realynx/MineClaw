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
}
