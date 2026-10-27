using System.Runtime.CompilerServices;
using MineClaw.Agent;
using MineClaw.Agent.Rag.Beliefs;
using MineClaw.Bot.Game;
using MineClaw.Bot.WorldIndex;
using MineClaw.World.Claims;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Beliefs;

/// <summary>
/// One-call wiring of the belief system into a bot (BotHost calls <see cref="Attach"/> once): binds the shared
/// <see cref="BeliefStore"/> to the trust feed, the world view and the bot/hive names, feeds it every live container
/// snapshot the indexer takes (whenever the bot looks inside a chest, for any reason), and starts the idle-time checker.
/// </summary>
public static class BeliefBot
{
    private static readonly ConditionalWeakTable<AgentRuntime, BeliefChecker> Checkers = new();

    /// <summary>The checker attached to a runtime (null if none).</summary>
    public static BeliefChecker? Of(AgentRuntime rt) => Checkers.TryGetValue(rt, out var c) ? c : null;

    public static BeliefChecker Attach(AgentRuntime rt, IGameClient client, IClaimBoard? claims = null, TimeProvider? time = null,
        ILogger? log = null, WorldIndexer? indexer = null, BeliefCheckOptions? options = null)
    {
        var store = BeliefStore.For(rt.Memory);
        if (time != null) store.Time = time;
        store.CloneId = client.Username;
        store.Growth = rt.Growth;
        store.World = new GameBeliefWorld(client);
        store.IsBot = p => rt.Options.Hive.IsBot(p) || p.Equals(client.Username, StringComparison.OrdinalIgnoreCase)
            || p.Equals(rt.Chat.DisplayName, StringComparison.OrdinalIgnoreCase) || rt.Chat.Aliases.Any(a => a.Equals(p, StringComparison.OrdinalIgnoreCase));
        var checker = new BeliefChecker(store, client, claims, () => rt.Activities.IsBusy, options, time, log);
        if (indexer != null) checker.Follow(indexer);
        checker.Start();
        Checkers.AddOrUpdate(rt, checker);
        return checker;
    }
}
