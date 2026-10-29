using MineClaw.Client.Combat;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 10-29, daytime, a DeepSeek hive's 20 bodies deforesting: two were shot dead by skeletons standing in tree shade (102, 117).
/// 117: "fight started: skeleton 6m away", 14 hits that never killed it (bare hands), took 20, "took cover" for 0.6 s, shot again,
/// dead. On 1.12.2 an archer fight is estimated too (Outmatched): a body that can win (a stone axe) closes in sprinting the zig-zag
/// and kills it; one that can't breaks line of sight and stays hidden until the skeleton has lost it.
/// </summary>
public class Release1122SkeletonTests
{
    private static Task<(MinecraftClient C, FakeServer S)> Start(Action<FakeServer>? setup = null) =>
        Harness.StartAsync(srv =>
        {
            for (int x = 0; x <= 24; x++)
            for (int z = 0; z <= 16; z++)
                srv.Blocks[(x, 63, z)] = Blocks.Grass;
            srv.Difficulty = 2;
            setup?.Invoke(srv);
        }, release1122: true);

    /// <summary>A leafy canopy over the skeleton's spot (the daylight shade it lives in).</summary>
    private static void Shade(FakeServer srv, int cx, int cz)
    {
        for (int x = cx - 2; x <= cx + 2; x++)
        for (int z = cz - 2; z <= cz + 2; z++)
            srv.Blocks[(x, 68, z)] = Blocks.Leaves;
        for (int y = 64; y <= 67; y++) srv.Blocks[(cx + 2, y, cz + 2)] = Blocks.Log;
    }

    [Fact]
    public void The_estimate_says_bare_hands_lose_to_an_archer_and_a_stone_axe_wins()
    {
        // Archer dps at normal ≈ 1.3 (an arrow of ~3.5 every 2 s, 3 in 4 land); one landed swing a second at most.
        var rules = Release1122CombatRules.Instance;
        double die = 19 / CombatEngine.ArrowDps(2) * 0.75;
        double walk = (10 - 3) / CombatEngine.RangedApproachSpeed;
        double hand = Math.Min(WeaponChoice.Dps(0, rules)!.Value, rules.ChargedDamage(0, 1f) * CombatEngine.RangedHitsPerSecond);
        double axe = Math.Min(WeaponChoice.Dps(Items.StoneAxe, rules)!.Value, rules.ChargedDamage(Items.StoneAxe, 1f) * CombatEngine.RangedHitsPerSecond);
        Assert.True(20 / hand + walk > die, $"bare hands: {20 / hand + walk:0.#} s to kill vs {die:0.#}");
        Assert.True(20 / axe + walk <= die, $"stone axe: {20 / axe + walk:0.#} s to kill vs {die:0.#}");
    }

    [Fact]
    public async Task A_skeleton_10_m_away_in_daylight_shade_is_killed_by_a_body_with_a_stone_axe_and_the_body_lives()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Start(srv =>
        {
            srv.SpawnAt = (4.5, 64, 8.5);
            srv.Inventory[36] = Harness.Stack(Items.StoneAxe, 1);
            Shade(srv, 14, 8);
        });
        await using var _ = c;
        using var __ = s;
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.HeldItem(0).Id == Items.StoneAxe));
        s.Send(new MineClaw.Protocol.Packets.TimeUpdatePacket { Time = 6000 });
        s.StartMobAi();
        var skeleton = s.SpawnMob(MobTypes.Skeleton, 14.5, 64, 8.5, health: 20, attackDamage: 3);
        skeleton.Archer = true;
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        c.CombatEventRaised += e => events.Enqueue(e.Message);

        bool ok = await Harness.WaitAsync(() => skeleton.Dead || HiddenFrom(c, skeleton) && !c.Combat.Engaged && s.ArrowHits > 0, 20000);
        Assert.True(ok, $"skeleton {skeleton.Health} hp, arrows {s.ArrowHits}, hp {c.World.Player.Health}, combat {c.Combat.Status}; {string.Join(" | ", events)}");
        Assert.True(skeleton.Dead, $"a stone axe wins this fight; events: {string.Join(" | ", events)}");
        Assert.True(s.LowestHealth >= 8, $"lowest hp {s.LowestHealth} after {s.ArrowHits} arrows");
    }

    [Fact]
    public async Task A_bare_handed_body_shot_by_a_skeleton_takes_cover_behind_a_wall_and_stays_there_and_lives()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Start(srv =>
        {
            srv.SpawnAt = (6.5, 64, 8.5);
            Shade(srv, 16, 8);
            // A wall west of the bot: cover two steps away.
            for (int z = 5; z <= 11; z++) { srv.Blocks[(8, 64, z)] = Blocks.Stone; srv.Blocks[(8, 65, z)] = Blocks.Stone; }
            srv.Blocks[(8, 64, 8)] = 0; srv.Blocks[(8, 65, 8)] = 0; // a doorway the bot stands in front of
        });
        await using var _ = c;
        using var __ = s;
        s.StartMobAi();
        var skeleton = s.SpawnMob(MobTypes.Skeleton, 16.5, 64, 8.5, health: 20, attackDamage: 3);
        skeleton.Archer = true;
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        c.CombatEventRaised += e => events.Enqueue(e.Message);

        Assert.True(await Harness.WaitAsync(() => s.ArrowHits > 0, 10000), "the skeleton never shot");
        Assert.True(await Harness.WaitAsync(() => events.Any(e => e.Contains("cover", StringComparison.OrdinalIgnoreCase)), 8000),
            $"no cover; combat {c.Combat.Status}; {string.Join(" | ", events)}");
        // Hidden from it, and held: 4 s later it still can't see the body (no peeking out to be shot again).
        Assert.True(await Harness.WaitAsync(() => HiddenFrom(c, skeleton), 8000), $"still in sight at {c.World.Player.Position}; {string.Join(" | ", events)}");
        int arrows = s.ArrowHits;
        await Task.Delay(800); // ≈ 160 ticks at the fake server's 5 ms: 4 shots if it could see the body
        Assert.True(s.ArrowHits <= arrows + 1, $"{s.ArrowHits - arrows} more arrows after taking cover");
        Assert.True(s.LowestHealth >= 10, $"lowest hp {s.LowestHealth}");
        Assert.DoesNotContain(events, e => e.StartsWith("fight started: skeleton", StringComparison.Ordinal) && !e.Contains("cover"));
    }

    private static bool HiddenFrom(MinecraftClient c, FakeServer.FakeMob m) =>
        !CombatGeometry.HasLineOfSight(c.World, new Vec3d(m.X, m.Y + 1.6, m.Z), c.World.Player.Physics.EyePosition);
}
