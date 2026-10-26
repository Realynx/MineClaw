using MineClaw.Agent.Budget;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Human;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Routines;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent;

/// <summary>
/// Composition root for the agent layer. Typical host wiring:
/// <code>
/// // options.Chat.Username = "maid_chan" (login), options.Chat.DisplayName = "claudechan" (chat name, renameable)
/// var rt = AgentRuntime.Create(options, (line, ct) => client.SendChatAsync(line, ct), position: () => ...);
/// rt.Tools.Register("mine", "Mine N blocks of a type nearby.", ToolSchema.Create().Str("block").Int("count").Build(),
///     async (input, ct) => ToolResult.Success(await client.MineAsync(...)), longRunning: true);
/// client.ChatReceived += line => rt.Chat.OnServerChat(line);
/// client.HealthChanged += hp => { if (hp &lt;= 6) rt.Events.Post(GameEvent.LowHealth(hp)); };
/// rt.Agent.StateHeader = () => $"pos {x},{y},{z} hp {hp}/20 food {food} held {item}";
/// await rt.Agent.RunAsync(ct);
/// </code>
/// Register game tools before the first turn; the tool list is part of the cached prefix.
/// </summary>
public sealed class AgentRuntime
{
    public required AgentOptions Options { get; init; }
    public required ToolRegistry Tools { get; init; }
    public required EventQueue Events { get; init; }
    public required ChatRouter Chat { get; init; }
    public required UsageTracker Usage { get; init; }
    public required MemoryStore Memory { get; init; }
    public required ContextManager Context { get; init; }
    public required ActivityManager Activities { get; init; }
    public required RoutineExecutor Routines { get; init; }
    public required BotAgent Agent { get; init; }
    public required ILlmClient Llm { get; init; }
    /// <summary>True if the knowledge text hit <see cref="AgentOptions.MaxKnowledgeChars"/>.</summary>
    public bool KnowledgeTruncated { get; init; }
    public int KnowledgeChars { get; init; }

    /// <param name="sendChat">Sends one raw chat line (≤100 chars) to the server.</param>
    /// <param name="llm">Defaults to <see cref="AnthropicLlmClient"/> (API key from ANTHROPIC_API_KEY).</param>
    /// <param name="position">Current block position, for memory tools that default to "here".</param>
    /// <param name="demonstrations">Enables <c>watch_demo</c> when the client layer can record players.</param>
    public static AgentRuntime Create(AgentOptions options, Func<string, CancellationToken, Task> sendChat,
        ILlmClient? llm = null, Func<BlockPos?>? position = null, IDemonstrationSource? demonstrations = null,
        ILoggerFactory? loggers = null, TimeProvider? time = null, bool persist = true)
    {
        time ??= TimeProvider.System;
        llm ??= new AnthropicLlmClient(options.Llm);
        string? Data(string file) => persist ? Path.Combine(options.DataDirectory, file) : null;

        var tools = new ToolRegistry(options.MaxToolResultChars);
        var events = new EventQueue(options.Events, time);
        var chat = new ChatRouter(options.Chat, events, sendChat, time);
        var usage = new UsageTracker(options.Budget, options.Prices, time, Data(options.Budget.UsageFile));
        var memory = new MemoryStore(Data("memory.json"), time);
        var context = new ContextManager(options.Context);
        var activities = new ActivityManager(tools, events, time);
        var routines = new RoutineExecutor(tools);

        // Built-in, game-agnostic tools (registered first so their order is stable).
        tools.Register("stop", "Stop the current task/activity.", ToolSchema.Empty, (_, _) =>
        {
            var what = activities.Cancel("stop");
            return Task.FromResult(ToolResult.Success(what != null ? $"stopped {what}" : "nothing running"));
        }, cheapTier: true);
        MemoryTools.RegisterAll(tools, memory, position);
        IdentityTools.ApplyPersisted(memory, chat);
        IdentityTools.RegisterAll(tools, memory, chat);
        RoutineTools.RegisterAll(tools, memory, routines);
        HumanTools.RegisterAskPlayer(tools, chat);
        if (demonstrations != null) HumanTools.RegisterWatchDemo(tools, chat, memory, demonstrations);

        var knowledge = Prompts.LoadKnowledge(options.KnowledgeDirectory, options.MaxKnowledgeChars, out bool truncated);
        var agent = new BotAgent(options, llm, tools, events, chat, usage, context, activities, knowledge, time,
            loggers?.CreateLogger<BotAgent>());

        return new AgentRuntime
        {
            Options = options, Tools = tools, Events = events, Chat = chat, Usage = usage, Memory = memory,
            Context = context, Activities = activities, Routines = routines, Agent = agent, Llm = llm,
            KnowledgeTruncated = truncated, KnowledgeChars = knowledge.Length,
        };
    }
}
