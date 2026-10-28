using MineClaw.Agent.Events;
using MineClaw.Agent.Tools;
using MineClaw.Client;
using MineClaw.Client.Combat;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// Combat tools (terse: they sit in the cached prefix). All fighting runs in C# (<see cref="CombatEngine"/>); the model
/// only hears "fight started" / "fight ended: ..." / critical moments, posted here as events.
/// Registration hook (BotHost): <c>if (client is MinecraftGameClient mg) CombatTools.Register(rt.Tools, mg.Inner, rt.Events);</c>
/// </summary>
public static class CombatTools
{
    public static readonly string[] ToolNames = ["spar", "stop_spar", "defend", "guard", "attack", "combat_status"];

    public static void Register(ToolRegistry tools, MinecraftClient client, EventQueue? events = null)
    {
        Register(tools, () => client);
        if (events is not null) AttachEvents(client, events);
    }

    /// <summary>The tools over a client resolved per call (the body router: the body in scope).</summary>
    public static void Register(ToolRegistry tools, Func<MinecraftClient> body)
    {
        tools.Register("spar", "Friendly PvP with a player who asked to spar. Checks arena, counts down, stops before anyone is in danger.",
            ToolSchema.Create().Str("player").Int("rounds", "first to 3 hits each", required: false).Build(),
            async (i, ct) => GameToolSet.R(await body().SparAsync(i.ReqStr("player"), i.Int("rounds"), ct)), longRunning: true);
        tools.Register("stop_spar", "End the spar/fight now.", ToolSchema.Empty,
            (_, _) => Task.FromResult(GameToolSet.R(body().StopSpar())));
        tools.Register("defend", "Self-defense reflex on/off.",
            ToolSchema.Create().Str("mode", enumValues: ["on", "off"]).Build(),
            (i, _) => Task.FromResult(GameToolSet.R(body().SetDefend(!string.Equals(i.ReqStr("mode"), "off", StringComparison.OrdinalIgnoreCase)))));
        tools.Register("guard", "Protect a player from mobs, or 'off'.",
            ToolSchema.Create().Str("player", "name or off").Build(),
            (i, _) => Task.FromResult(GameToolSet.R(body().SetGuard(i.ReqStr("player")))));
        tools.Register("attack", "Kill the nearest mob of a type (mobs only; passive ones only when asked).",
            ToolSchema.Create().Str("target", "mob type e.g. zombie, pig").Build(),
            async (i, ct) => GameToolSet.R(await body().AttackAsync(i.ReqStr("target"), ct)), longRunning: true);
        tools.Register("combat_status", "Current fight/spar and reflex settings.", ToolSchema.Empty,
            (_, _) => Task.FromResult(ToolResult.Success(body().CombatStatus())), cheapTier: true);
    }

    /// <summary>One body's fight reports as events.</summary>
    /// <param name="body">One brain, many bodies: a clone body's short name, stamped on its reports (null = main's, as before).</param>
    public static void AttachEvents(MinecraftClient client, EventQueue events, string? body = null) =>
        client.CombatEventRaised += e => events.Post(body is { Length: > 0 } b ? ToGameEvent(e) with { Body = b } : ToGameEvent(e));

    /// <summary>Start: low; end: normal (background when a tool call already returns it); critical moments: high.</summary>
    public static GameEvent ToGameEvent(CombatEvent e) => e.Kind switch
    {
        CombatEventKind.Critical => GameEvent.Notice(e.Message, EventPriority.High),
        CombatEventKind.FightEnded => GameEvent.Notice(e.Message, e.Requested ? EventPriority.Background : EventPriority.Normal),
        _ => GameEvent.Notice(e.Message, e.Requested ? EventPriority.Background : EventPriority.Low),
    };
}
