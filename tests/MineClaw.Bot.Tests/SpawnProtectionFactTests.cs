using MineClaw.Agent.Rag;
using MineClaw.Client;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Inferred spawn protection is shared, but a later successful edit disproves it for all bodies (including accounts
/// whose previous failures were caused by invalid placement geometry). Explicit op exemptions still work at startup.
/// </summary>
public class SpawnProtectionFactTests
{
    private static readonly BlockPos Spawn = new(0, 66, -3);

    private static LearnedProtectedRegion Region(int radius = 16) =>
        new(Spawn, radius, 0, 2, "server undid dig at 5,66,2, 5 from spawn (2 undone edits)", DateTime.UtcNow);

    [Fact]
    public void Learned_regions_are_shared_until_a_success_inside_disproves_them()
    {
        var (h, _) = TestBot.Create();
        using var _h = h;
        var obs = h.Runtime.Observations;
        // 420's client learned the region from two independent undone digs: one fact in the shared memory, binding 420.
        SpawnProtectionFact.Learned(null, Region(), "maid_chan_420").Write(obs, 256);
        var fact = SpawnProtectionFact.Read(obs, 0)!;
        Assert.Equal(16, fact.Radius);
        Assert.Equal(["maid_chan_420"], fact.Affects);
        Assert.Contains("within 16 of 0,66,-3 for accounts without op; undone for 420", fact.Text());

        // A configured op is exempt before any attempt; a successful edit is evidence about the whole inferred region.
        fact.WithExempt("maid_chan").Write(obs, 256);
        Assert.False(SpawnProtectionFact.Read(obs, 0)!.AppliesTo("maid_chan"));
        Assert.Contains("maid_chan can edit there (op)", SpawnProtectionFact.Read(obs, 0)!.Text());
        Assert.True(fact.AppliesTo("maid_chan_67") && fact.AppliesTo("maid_chan_102"));
        // An edit outside the radius says nothing about it.
        BotHost.NoteEditWorked(h.Runtime, "maid_chan_67", new BlockPos(30, 66, 1), 0, 256, null);
        Assert.True(SpawnProtectionFact.Read(obs, 0)!.AppliesTo("maid_chan_67"));

        // Every other body (in any hive process) adopts it and skips edits there; main never does.
        var body = new MinecraftClient(new ClientOptions { Username = "maid_chan_102" });
        BotHost.SyncProtectedRegion(body, h.Runtime, null);
        Assert.NotNull(body.ProtectedRegion);
        Assert.True(body.EditBlocked(new BlockPos(5, 66, 2)));
        Assert.False(body.EditBlocked(new BlockPos(40, 66, 2)));
        var main = new MinecraftClient(new ClientOptions { Username = "maid_chan" });
        BotHost.SyncProtectedRegion(main, h.Runtime, null);
        Assert.Null(main.ProtectedRegion);

        // Another account it bound widens it (the larger radius wins) and is added.
        var wider = SpawnProtectionFact.Learned(SpawnProtectionFact.Read(obs, 0), Region(20), "maid_chan_67");
        wider.Write(obs, 256);
        Assert.Equal((20, 2), (SpawnProtectionFact.Read(obs, 0)!.Radius, SpawnProtectionFact.Read(obs, 0)!.Affects.Count));

        // Even the exempt main's success disproves the inferred region for all accounts, not just itself.
        BotHost.NoteEditWorked(h.Runtime, "maid_chan", new BlockPos(1, 66, 0), 0, 256, null);
        Assert.Null(SpawnProtectionFact.Read(obs, 0));
    }

    [Fact]
    public void Any_hive_accounts_success_disproves_the_region_for_every_account()
    {
        var (h, _) = TestBot.Create();
        using var _h = h;
        var fact = SpawnProtectionFact.Learned(null, Region(), "maid_chan_101");
        SpawnProtectionFact.Learned(fact, Region(), "maid_chan_102").Write(h.Runtime.Observations, 256);
        BotHost.NoteEditWorked(h.Runtime, "maid_chan_103", Spawn, 0, 256, null);
        Assert.Null(SpawnProtectionFact.Read(h.Runtime.Observations, 0));
    }

    [Fact]
    public async Task Spawn_protection_off_prevents_runtime_adoption_and_learning()
    {
        using var s = new MineClaw.Client.Tests.FakeServer();
        s.Inventory[36] = new MineClaw.Data.ItemStack(MineClaw.Data.Blocks.Cobblestone, 8, 0);
        s.Hook = p => p is MineClaw.Protocol.Packets.PlayerBlockPlacementPacket;
        await using var c = new MinecraftClient(new ClientOptions
        {
            Username = "maid_chan", ConnectionFactory = s.CreateConnection, TickInterval = TimeSpan.FromMilliseconds(5), HostileAlertRadius = 0,
        });
        using (var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await c.ConnectAsync(ct.Token);
        using var h = BotHost.Create(new MineClaw.Bot.Game.MinecraftGameClient(c), TestBot.Options(),
            new BotOptions { Protection = new ProtectionOptions { ServerOp = false, SpawnProtectionOff = true } }, new NoLlm(), persist: false);
        SpawnProtectionFact.Learned(null, Region(), "maid_chan_102").Write(h.Runtime.Observations, 256);
        BotHost.SyncProtectedRegion(c, h.Runtime, null);
        Assert.Null(c.ProtectedRegion);
        for (int i = 0; i < 2; i++) Assert.False((await c.PlaceBlockAsync(9, 64, 8, MineClaw.Data.Blocks.Cobblestone)).Ok);
        Assert.Null(c.ProtectedRegion); // even two valid visible-face refusals cannot re-learn it
        SpawnProtectionFact.Learned(null, Region(), "maid_chan_102").Write(h.Runtime.Observations, 256);
        await using var clone = new MinecraftClient(new ClientOptions { Username = "maid_chan_103" });
        var body = MineClaw.Bot.Bodies.BodyFactory.CreateClone(h.Brain!, clone);
        try { BotHost.SyncProtectedRegion(clone, h.Runtime, null); Assert.Null(clone.ProtectedRegion); }
        finally { MineClaw.Bot.Bodies.BodyFactory.DisposeClone(body); }
    }

    [Fact]
    public void Op_mode_exempts_main_but_keeps_a_fact_that_binds_other_accounts()
    {
        var (h, _) = TestBot.Create();
        using var _h = h;
        var obs = h.Runtime.Observations;
        SpawnProtectionFact.Learned(null, Region(), "maid_chan_420").Write(obs, 256);
        BotHost.ForgetSpawnProtection(h.Runtime, null, "maid_chan");
        var fact = SpawnProtectionFact.Read(obs, 0)!;
        Assert.NotNull(fact);
        Assert.False(fact.AppliesTo("maid_chan"));
        Assert.True(fact.AppliesTo("maid_chan_420"));
        // A fact learned before accounts were recorded (no fields): an op still drops it (nothing says whom it binds).
        SpawnProtectionFact.Retract(obs, 0);
        obs.Upsert(new Observation("protected_region", "spawn", "spawn protection: can't break blocks within 16 of 0,66,-3") { Bounds = Box.Of(-16, 0, -19, 16, 127, 13) });
        BotHost.ForgetSpawnProtection(h.Runtime, null, "maid_chan");
        Assert.Null(SpawnProtectionFact.Read(obs, 0));
        // The old row form still reads (center and radius from its box).
        obs.Upsert(new Observation("protected_region", "spawn", "spawn protection") { Bounds = Box.Of(-16, 0, -19, 16, 127, 13) });
        Assert.Equal((16, 0, -3), (SpawnProtectionFact.Read(obs, 0)!.Radius, SpawnProtectionFact.Read(obs, 0)!.Center.X, SpawnProtectionFact.Read(obs, 0)!.Center.Z));
    }

    [Fact]
    public void Spawn_protection_off_drops_the_remembered_fact_for_clones_too_at_startup()
    {
        var (h, _) = TestBot.Create();
        using var _h = h;
        var obs = h.Runtime.Observations;
        var fact = SpawnProtectionFact.Learned(null, Region(), "maid_chan_101");
        SpawnProtectionFact.Learned(fact, Region(), "maid_chan_102").Write(obs, 256); // binds two clones
        Assert.True(SpawnProtectionFact.Read(obs, 0)!.AppliesTo("maid_chan_102"));
        // Not op and the server still protects spawn (defaults minus op): the fact stays for the clone.
        BotHost.ApplySpawnProtectionAtStartup(h.Runtime, null, new ProtectionOptions { ServerOp = false }, "maid_chan_101");
        Assert.True(SpawnProtectionFact.Read(obs, 0)!.AppliesTo("maid_chan_101"));
        // The owner says the server has no spawn protection: gone for everyone, even a non-op clone's account.
        BotHost.ApplySpawnProtectionAtStartup(h.Runtime, null, new ProtectionOptions { ServerOp = false, SpawnProtectionOff = true }, "maid_chan_101");
        Assert.Null(SpawnProtectionFact.Read(obs, 0)); // not just this clone exempt: the other clone is free too
    }
}
