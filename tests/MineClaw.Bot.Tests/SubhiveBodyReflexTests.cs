using MineClaw.Agent.Events;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-29: a DeepSeek hive's subhive bodies (in the hive's process, built by <see cref="BodyFactory.CreateClone"/>) were shot by
/// skeletons in daylight shade. Their safety reflexes are the client's own (combat with the 1.12.2 estimate and cover, Shelter's
/// box-in, the breath reflex), so a body built by the factory has them exactly like main or a clone process, plus the hive's
/// target leases and its dangers reaching the one brain tagged with the body.
/// </summary>
public class SubhiveBodyReflexTests
{
    private static async Task<(MinecraftClient C, FakeServer S)> Start(string name, Action<FakeServer>? setup)
    {
        var s = new FakeServer(true);
        setup?.Invoke(s);
        var c = new MinecraftClient(new ClientOptions
        {
            Username = name, TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero, RespawnDelay = TimeSpan.FromMilliseconds(10),
            ConnectionFactory = s.CreateConnection, HostileAlertRadius = 0, AutoReconnect = false,
            Profile = MineClaw.Client.Versioning.GameVersionProfiles.Java1122,
        });
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await c.ConnectAsync(cts.Token);
        await Until(() => c.World.Player.OnGround, 3000);
        return (c, s);
    }

    private static async Task<bool> Until(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(5); }
        return cond();
    }

    [Fact]
    public async Task A_factory_built_subhive_body_has_every_safety_reflex_and_takes_cover_from_a_skeleton_it_cant_beat()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (mainC, mainS) = await Start("maid_chan", null);
        var (bodyC, bodyS) = await Start("maid_chan_113", srv =>
        {
            for (int x = 0; x <= 24; x++) for (int z = 0; z <= 16; z++) srv.Blocks[(x, 63, z)] = Blocks.Grass;
            for (int z = 5; z <= 11; z++) { srv.Blocks[(8, 64, z)] = Blocks.Stone; srv.Blocks[(8, 65, z)] = Blocks.Stone; }
            srv.Blocks[(8, 64, 8)] = 0; srv.Blocks[(8, 65, 8)] = 0;
            srv.SpawnAt = (6.5, 64, 8.5);
            srv.Difficulty = 2;
        });
        await using var _m = mainC;
        await using var _b = bodyC;
        using var __m = mainS;
        using var __b = bodyS;
        using var h = BotHost.Create(new MinecraftGameClient(mainC), TestBot.Options(), null, new NoLlm(), persist: false);
        var leases = new TestLeases();
        var body = BodyFactory.CreateClone(h.Brain!, bodyC, leases.ViewFor("maid_chan_113"));

        // The wiring a subhive body shares with main and the clone processes.
        Assert.NotNull(bodyC.TargetClaims);                 // one body per tree across the hive
        Assert.NotNull(bodyC.Combat.CanBoxIn);              // Shelter's box-in from the combat engine
        Assert.NotNull(bodyC.Combat.ShouldShelter);
        Assert.True(bodyC.Combat.DefendSelf);               // night defence / defending itself
        Assert.True(bodyC.Options.Survival.SurfaceAtAir > 0); // the breath reflex surfaces before drowning
        Assert.NotNull(GameClients.Claimed(body.Game));     // its tools take leases (chests, blocks)

        // Bare hands vs a skeleton 10 m off in the shade: cover, not a losing melee, and the brain hears it as this body's.
        bodyS.StartMobAi();
        var skeleton = bodyS.SpawnMob(MobTypes.Skeleton, 16.5, 64, 8.5, health: 20, attackDamage: 3);
        skeleton.Archer = true;
        Assert.True(await Until(() => bodyS.ArrowHits > 0, 10000), "never shot");
        Assert.True(await Until(() => !MineClaw.Client.Combat.CombatGeometry.HasLineOfSight(bodyC.World,
            new MineClaw.World.Vec3d(skeleton.X, skeleton.Y + 1.6, skeleton.Z), bodyC.World.Player.Physics.EyePosition), 8000),
            $"still in the skeleton's sight: {bodyC.Combat.Status}");
        Assert.True(bodyS.LowestHealth >= 10, $"lowest hp {bodyS.LowestHealth}");
        Assert.True(await Until(() => h.Runtime.Events.Snapshot().Any(e => e.Body == "113"), 3000),
            "no event from the body reached the brain");
        foreach (var d in body.Owned) d.Dispose();
    }
}
