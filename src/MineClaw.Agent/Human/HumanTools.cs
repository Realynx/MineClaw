using MineClaw.Agent.Chat;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Human;

/// <summary>Human-in-the-loop tools: ask a player something and wait; watch a player demonstrate a structure.</summary>
public static class HumanTools
{
    public const string AskPlayerDescription =
        "ONLY for a real question you need answered to continue; waits for and eats their next message. " +
        "To answer or chat, write plain text.";

    /// <summary>Error returned (nothing is sent) when ask_player is called without a question.</summary>
    public const string NotAQuestion = "not sent: ask_player is only for questions you need answered. To reply, write plain text.";

    public static void RegisterAskPlayer(ToolRegistry registry, ChatRouter chat, int defaultTimeoutSeconds = 60)
    {
        // Brain only: the worker never talks to players and the cheap tier replies in plain text.
        registry.Register("ask_player", AskPlayerDescription,
            ToolSchema.Create().Str("question", "ends with ?").Str("name", "player to ask; omit for anyone", required: false)
                .Int("timeout", "seconds, default 60, max 180", required: false).Build(),
            async (input, ct) =>
            {
                var question = input.ReqStr("question").Trim();
                // A normal answer sent through ask_player swallows the player's next message and holds the turn: refuse it.
                if (!question.Contains('?')) return ToolResult.Error(NotAQuestion);
                var name = input.Str("name");
                int timeout = Math.Clamp(input.Int("timeout") ?? defaultTimeoutSeconds, 5, 180);
                var text = name != null && !question.Contains(name, StringComparison.OrdinalIgnoreCase) ? $"{name}, {question}" : question;
                // Coordinates only when the player used them first or asked for them; otherwise landmarks ("the one by the lava").
                text = Questions.Humanize(text, chat.RecentFrom(name), chat.Landmark);
                // Hive: this bot owns the conversation thread with that player until they answer or it times out.
                using var thread = name != null ? chat.HoldThread(name, TimeSpan.FromSeconds(timeout + 5)) : null;
                // Register the waiter before speaking so a fast reply can't be missed.
                var wait = chat.WaitForReplyAsync(name, TimeSpan.FromSeconds(timeout), ct);
                await chat.SayAsync(text, audience: name != null ? [name] : null, ct: ct).ConfigureAwait(false);
                var reply = await wait.ConfigureAwait(false);
                return reply != null ? ToolResult.Success(reply) : ToolResult.Error($"no reply in {timeout}s");
            }, tiers: ToolTiers.Main);
    }

    /// <summary>Registers <c>watch_demo</c> (only when the client layer supplies a demonstration source).</summary>
    public static void RegisterWatchDemo(ToolRegistry registry, ChatRouter chat, MemoryStore memory, IDemonstrationSource source)
    {
        registry.Register(new DelegateTool("watch_demo",
            DemoWait.ToolDescription,
            ToolSchema.Create().Str("player").Str("structure").Int("max_seconds", "default 120", required: false).Build(),
            async (input, ct) =>
            {
                var player = input.ReqStr("player");
                var name = input.ReqStr("structure");
                int max = Math.Clamp(input.Int("max_seconds") ?? 120, 10, 600);
                var known = memory.GetStructure(name);
                (BlockPos, BlockPos)? area = null;
                if (known?.Min != null)
                {
                    var mx = known.Max ?? known.Min;
                    area = (new BlockPos(known.Min.X - 4, known.Min.Y - 3, known.Min.Z - 4), new BlockPos(mx.X + 4, mx.Y + 3, mx.Z + 4));
                }

                await using var session = await source.StartAsync(player, area, ct).ConfigureAwait(false);
                var waited = await DemoWait.RunAsync(chat, session, player, max, null, ct).ConfigureAwait(false);

                var actions = await session.StopAsync(ct).ConfigureAwait(false);
                var steps = DemonstrationSummarizer.Summarize(actions, known?.Min);
                if (steps.Count == 0) return ToolResult.Error("saw nothing to learn" + DemoWait.SaidSuffix(waited));
                memory.UpsertStructure(name, s =>
                {
                    s.Steps.Clear();
                    s.Steps.AddRange(steps);
                    s.TaughtBy = player;
                    s.Confidence = Math.Max(s.Confidence, 0.7);
                });
                return ToolResult.Success($"learned {name} ({steps.Count} steps): " + string.Join("; ", steps) + DemoWait.SaidSuffix(waited));
            }, longRunning: false));
    }
}
