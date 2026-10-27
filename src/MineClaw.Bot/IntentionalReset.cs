using MineClaw.Agent;
using MineClaw.Agent.Events;
using MineClaw.Agent.Rag.Beliefs;
using MineClaw.Bot.Game;
using MineClaw.Client;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot;

/// <summary>
/// The Bot side of the deliberate void step (<see cref="MinecraftClient.StepIntoVoidAsync"/>, user design decision 10-27):
/// announce it once in persona, report the death as an intentional reset (never a danger, never grief), record the outcome
/// "respawned at X with hp 20, food 20" for the brain, and turn it into evidence for the trusted players' claim that a void
/// reset after depositing restores hp/hunger, so the strategy is LEARNED (memory + verified belief), not coded.
/// </summary>
public static class IntentionalReset
{
    /// <summary>Fox's teaching (live 10-27), kept as a trusted-player claim in shared memory (idempotent).</summary>
    public const string Claimant = "PoofimaFox";
    public const string ClaimText = "void reset after depositing restores hp/hunger";

    /// <summary>The announcement, once per step.</summary>
    public const string Announcement = "/me hops into the void to respawn";

    public static void Attach(AgentRuntime rt, IGameClient client, ILogger? log)
    {
        if (client is not MinecraftGameClient mg) return;
        var beliefs = SeedClaim(rt, log);
        mg.Inner.VoidStepStarted += step =>
        {
            log?.LogInformation("into_void: {Reason}", step.Reason);
            _ = rt.Chat.SayAsync(Announcement);
        };
        mg.Inner.IntentionalResetDone += outcome =>
        {
            try
            {
                var text = $"intentional reset ({outcome.Reason}): {outcome}";
                log?.LogInformation("{Text}", text);
                rt.Events.Post(GameEvent.Notice(text, EventPriority.Normal));
                if (beliefs is not null) RecordOutcome(beliefs, outcome, log);
            }
            catch (Exception ex) { log?.LogWarning(ex, "intentional reset outcome failed"); }
        };
    }

    /// <summary>Makes sure the trusted player's claim is in memory; returns the store (null when beliefs are off/unavailable).</summary>
    public static BeliefStore? SeedClaim(AgentRuntime rt, ILogger? log)
    {
        try
        {
            var store = BeliefStore.For(rt.Db, rt.Rag);
            if (!store.Options.Enabled) return null;
            if (Find(store) is null) store.Ingest(Claimant, ClaimText);
            return store;
        }
        catch (Exception ex) { log?.LogDebug(ex, "void-reset claim seed failed"); return null; }
    }

    /// <summary>The claim about void resets (by its words), or null.</summary>
    public static BeliefClaim? Find(BeliefStore store) =>
        store.Recent(200).FirstOrDefault(c => c.Text.Contains("void", StringComparison.OrdinalIgnoreCase)
                                              && (c.Text.Contains("hp", StringComparison.OrdinalIgnoreCase) || c.Text.Contains("hunger", StringComparison.OrdinalIgnoreCase)
                                                  || c.Text.Contains("health", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The bot's own experiment: a deliberate void step that respawned it at full hp and food supports the claim (verified);
    /// a respawn without full vitals contradicts it.
    /// </summary>
    public static BeliefClaim? RecordOutcome(BeliefStore store, IntentionalResetOutcome outcome, ILogger? log = null)
    {
        if (Find(store) is not { } claim) return null;
        bool full = outcome.Health >= 20 && outcome.Food >= 20;
        var r = store.AddEvidence(claim.Id, EvidenceKind.Experiment, full ? 1 : -1, 0.9, "void_reset",
            $"did it myself: {outcome}");
        log?.LogInformation("belief claim#{Id} '{Text}' now {Status} after my own void reset", claim.Id, claim.Text, r?.Status);
        return r;
    }
}
