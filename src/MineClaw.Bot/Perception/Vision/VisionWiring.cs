using MineClaw.Agent;
using MineClaw.Agent.Perception;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Memory;
using MineClaw.Bot.WorldIndex;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Perception.Vision;

/// <summary>Wires vision v2 into a bot host: role layer, perception tier, the <c>see</c> tool and auto-vision.</summary>
public static class VisionWiring
{
    public sealed record Result(VisionHub? Hub, PerceptionService Perception, AutoVision? Auto, RoleClassifier Roles);

    public static Result Attach(AgentRuntime rt, IGameClient client, IPlaceResolver places, BotOptions bot, Protection protection,
        Func<BlockPos, bool> digCells, WorldIndexer? indexer, GestureTracker? gestures, Func<BlockPos, IReadOnlyList<BlockPos>>? genCells,
        Func<IEnumerable<string>>? hiveBots, TimeProvider? time, ILogger? log, bool persist)
    {
        var llm = rt.Options.Llm;
        // Roles reuse the protection policy's verdicts (no telemetry: Check, not Explain), dig cells and the world index.
        var roles = RoleClassifier.For(client.World, protection, digCells, indexer == null ? null : indexer.ProtectionKind);
        var (model, problem) = PerceptionModelFactory.Create(llm, rt.Llm);
        if (model == null && !llm.Perception.Direct) log?.LogWarning("perception tier off ({Why}): see returns pictures (direct mode)", problem);
        else if (model != null) log?.LogInformation("perception tier: {Provider} {Model}", model.Provider, model.Model);
        var telemetry = new PerceptionTelemetry(log, persist ? rt.Options.DataDirectory : null, time);
        var perception = new PerceptionService(model, llm.Perception, rt.Usage, rt.Usage.Prices, telemetry, time, log);
        var hub = VisionTools.Register(rt, client, places, bot.Vision, gestures, hiveBots, time, log, roles, perception); // see (brain+worker) + look map
        AutoVision? auto = null;
        if (hub != null && perception.Available && llm.Perception.Auto.Enabled)
        {
            auto = new AutoVision(hub, client, llm.Perception.Auto, genCells, log);
            auto.Attach(rt, gestures);
            // Brain: the latest verified answer rides in the next turn (never waits); worker: after its spatial step.
            var prior = rt.Agent.TurnPerception;
            rt.Agent.TurnPerception = () =>
            {
                string? a = null, b = null;
                try { a = prior?.Invoke(); } catch (Exception ex) { log?.LogDebug(ex, "turn perception failed"); }
                b = auto.TakeReady();
                return string.IsNullOrWhiteSpace(a) ? b : string.IsNullOrWhiteSpace(b) ? a : a + "\n" + b;
            };
            if (rt.Worker != null) rt.Worker.RoundNote = auto.ForWorkerAsync;
        }
        return new Result(hub, perception, auto, roles);
    }
}
