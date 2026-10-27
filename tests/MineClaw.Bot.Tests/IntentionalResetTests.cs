using MineClaw.Agent.Events;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Beliefs;
using MineClaw.Client;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>Item 18: the void reset is a learned strategy — a trusted claim in memory that the bot's own deliberate step verifies.</summary>
public class IntentionalResetTests
{
    [Fact]
    public void Foxs_claim_is_in_memory_once_and_only_the_bots_own_reset_verifies_it()
    {
        var (h, _) = TestBot.Create();
        using var __ = h;
        var store = IntentionalReset.SeedClaim(h.Runtime, null)!;
        Assert.NotNull(store);
        IntentionalReset.SeedClaim(h.Runtime, null); // idempotent
        var claim = Assert.Single(store.Recent(200), c => c.Text.Contains("void", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("PoofimaFox", claim.Claimants, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(ClaimStatus.Verified, claim.Status); // a player's word alone never verifies a void claim

        var outcome = new IntentionalResetOutcome("reset hp and hunger", new Vec3d(11.5, -70, 8.5), new Vec3d(8.5, 64, 8.5), 20, 20);
        var after = IntentionalReset.RecordOutcome(store, outcome)!;
        Assert.Equal(ClaimStatus.Verified, after.Status);
        Assert.Contains(store.Evidence(after.Id), e => e.Kind == EvidenceKind.Experiment && e.Polarity > 0 && e.Note!.Contains("hp 20, food 20"));
    }

    [Fact]
    public void A_reset_that_did_not_restore_vitals_refutes_it()
    {
        using var db = AgentDb.Open(null);
        var store = new BeliefStore(db, null);
        store.Ingest(IntentionalReset.Claimant, IntentionalReset.ClaimText);
        var r = IntentionalReset.RecordOutcome(store, new IntentionalResetOutcome("test", default, new Vec3d(8.5, 64, 8.5), 20, 6))!;
        Assert.Equal(ClaimStatus.Refuted, r.Status);
    }

    [Fact]
    public void Other_safety_claims_stay_info_only()
    {
        using var db = AgentDb.Open(null);
        var store = new BeliefStore(db, null);
        var c = store.Ingest("PoofimaFox", "it's safe to jump off the edge here").Claim!;
        store.AddEvidence(c.Id, EvidenceKind.Demo, 1, 0.9, "demo", "watched");
        Assert.Equal(ClaimStatus.Unverified, store.Get(c.Id)!.Status);
    }

    [Fact]
    public async Task A_death_during_a_deliberate_void_step_is_an_intentional_reset_not_a_death()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        c.ActiveVoidStep = new IntentionalVoidStep("reset hp and hunger", new Vec3d(10.5, 64, 8.5), DateTimeOffset.UtcNow);
        c.RaiseDied();
        GameEvent? e = null;
        for (int i = 0; i < 100 && e == null; i++)
        {
            e = h.Runtime.Events.Snapshot().FirstOrDefault(x => x.Text.StartsWith("intentional reset"));
            if (e == null) await Task.Delay(20);
        }
        Assert.NotNull(e);
        Assert.Equal(EventPriority.Background, e!.Priority);
        Assert.DoesNotContain(h.Runtime.Events.Snapshot(), x => x.Kind == EventKind.Death);

        c.ActiveVoidStep = null; // a normal death still is one
        c.RaiseDied();
        for (int i = 0; i < 100 && !h.Runtime.Events.Snapshot().Any(x => x.Kind == EventKind.Death); i++) await Task.Delay(20);
        Assert.Contains(h.Runtime.Events.Snapshot(), x => x.Kind == EventKind.Death);
    }
}
