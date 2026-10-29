using MineClaw.Agent.Rag;
using MineClaw.Client;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-28: clone 420 reported "server undid digs within 16 of spawn (spawn protection)"; only main is op. Main's own chopping near
/// spawn retracted the shared fact (and main's ServerOp forgot it at startup), so every other body kept digging there. The fact is
/// learned from the world and says whom it binds: an op's success makes that account exempt, never retracts it for the others.
/// </summary>
public class SpawnProtectionFactTests
{
    private static readonly BlockPos Spawn = new(0, 66, -3);

    private static LearnedProtectedRegion Region(int radius = 16) =>
        new(Spawn, radius, 0, 2, "server undid dig at 5,66,2, 5 from spawn (2 undone edits)", DateTime.UtcNow);

    [Fact]
    public void Learned_from_undone_edits_an_op_success_exempts_the_op_and_keeps_the_fact()
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

        // Main (op) chops right next to spawn and it works: main is exempt, the fact stays for everyone else.
        BotHost.NoteEditWorked(h.Runtime, "maid_chan", new BlockPos(3, 66, 1), 0, 256, null);
        fact = SpawnProtectionFact.Read(obs, 0)!;
        Assert.NotNull(fact);
        Assert.False(fact.AppliesTo("maid_chan"));
        Assert.True(fact.AppliesTo("maid_chan_67") && fact.AppliesTo("maid_chan_102"));
        Assert.Contains("maid_chan can edit there (op)", fact.Text());
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

        // Only accounts it bound can disprove it: 420 and 67 both edit inside and it works (they got op, or it was lifted).
        BotHost.NoteEditWorked(h.Runtime, "maid_chan_420", new BlockPos(1, 66, 0), 0, 256, null);
        Assert.NotNull(SpawnProtectionFact.Read(obs, 0));
        BotHost.NoteEditWorked(h.Runtime, "maid_chan_67", new BlockPos(1, 66, 0), 0, 256, null);
        Assert.Null(SpawnProtectionFact.Read(obs, 0));
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
}
