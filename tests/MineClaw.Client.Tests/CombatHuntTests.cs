using MineClaw.Client.Combat;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Hunting passive animals across one-block rises must not end the chase at the first safe uphill step.
/// </summary>
public class CombatHuntTests
{
    private static CombatOptions Opts() => new() { TelemetryDirectory = null, Crits = false, Block = false };

    [Fact]
    public void A_told_attack_on_a_sheep_up_a_one_block_rise_jumps_it_and_hits()
    {
        var r = new CombatRig(Opts());
        for (int x = 3; x <= 10; x++)
        for (int z = -10; z <= 10; z++)
            r.W.SetBlock(x, 64, z, Blocks.Grass, 0); // a hill step two blocks east
        var sheep = r.W.Entities.SpawnMob(9, MobTypes.Sheep, new Vec3d(6.5, 65, 0.5), 0, 0, null);
        var done = r.E.StartAttack(sheep.Id);
        int swings = 0;
        for (int i = 0; i < 500 && swings == 0; i++)
            if (r.Step(physics: true).AttackId == sheep.Id) swings++;
        Assert.True(swings > 0, $"never swung; at {r.P.Position}, " + string.Join(" | ", r.Events.Select(e => e.Message)));
        Assert.True(r.P.Position.Y >= 65 - 1e-6, $"still below the rise at {r.P.Position}");
        Assert.DoesNotContain(r.Events, e => e.Message.Contains("couldn't reach"));
        Assert.False(done.IsCompleted);
    }

    [Fact]
    public void A_fence_is_not_a_step_the_chase_never_hops_at_a_pen()
    {
        var r = new CombatRig(Opts());
        for (int z = -10; z <= 10; z++) r.W.SetBlock(3, 64, z, Blocks.Fence, 0); // 1.5 high: no jump clears it
        var sheep = r.W.Entities.SpawnMob(9, MobTypes.Sheep, new Vec3d(6.5, 64, 0.5), 0, 0, null);
        r.E.StartAttack(sheep.Id);
        for (int i = 0; i < 200; i++)
        {
            var step = r.Step(physics: true);
            Assert.NotEqual(sheep.Id, step.AttackId);
            Assert.False(step.Input.Jump, $"jumped at the fence at tick {i} from {r.P.Position}");
        }
        Assert.True(r.P.Position.X < 3, $"crossed the fence: {r.P.Position}");
    }

    [Fact]
    public void Chasing_a_passive_animal_sprints_a_hostile_approach_does_not()
    {
        var sheepRig = new CombatRig(Opts());
        var sheep = sheepRig.W.Entities.SpawnMob(9, MobTypes.Sheep, new Vec3d(8.5, 64, 0.5), 0, 0, null);
        sheepRig.E.StartAttack(sheep.Id);
        sheepRig.Step();
        Assert.True(sheepRig.Step().Input.Sprint);

        var zombieRig = new CombatRig(Opts());
        var zombie = zombieRig.W.Entities.SpawnMob(9, MobTypes.Zombie, new Vec3d(8.5, 64, 0.5), 0, 0, null);
        zombieRig.E.StartAttack(zombie.Id);
        zombieRig.Step();
        Assert.False(zombieRig.Step().Input.Sprint);
    }
}
