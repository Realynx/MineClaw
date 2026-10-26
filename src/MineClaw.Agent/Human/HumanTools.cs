using System.Text.RegularExpressions;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Human;

/// <summary>Human-in-the-loop tools: ask a player something and wait; watch a player demonstrate a structure.</summary>
public static class HumanTools
{
    public static void RegisterAskPlayer(ToolRegistry registry, ChatRouter chat, int defaultTimeoutSeconds = 60)
    {
        registry.Register("ask_player",
            "Ask in chat and wait for the player's reply (returned as the result). Use when unsure what something is or what they want.",
            ToolSchema.Create().Str("question").Str("name", "player to ask; omit for anyone", required: false)
                .Int("timeout", "seconds, default 60, max 180", required: false).Build(),
            async (input, ct) =>
            {
                var question = input.ReqStr("question");
                var name = input.Str("name");
                int timeout = Math.Clamp(input.Int("timeout") ?? defaultTimeoutSeconds, 5, 180);
                var text = name != null && !question.Contains(name, StringComparison.OrdinalIgnoreCase) ? $"{name}, {question}" : question;
                // Register the waiter before speaking so a fast reply can't be missed.
                var wait = chat.WaitForReplyAsync(name, TimeSpan.FromSeconds(timeout), ct);
                await chat.SayAsync(text, audience: name != null ? [name] : null, ct: ct).ConfigureAwait(false);
                var reply = await wait.ConfigureAwait(false);
                return reply != null ? ToolResult.Success(reply) : ToolResult.Error($"no reply in {timeout}s");
            }, cheapTier: true);
    }

    /// <summary>Registers <c>watch_demo</c> (only when the client layer supplies a demonstration source).</summary>
    public static void RegisterWatchDemo(ToolRegistry registry, ChatRouter chat, MemoryStore memory, IDemonstrationSource source)
    {
        registry.Register(new DelegateTool("watch_demo",
            "Watch a player show how to use a structure; they say 'done' when finished. Steps get saved to the structure.",
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
                await chat.SayAsync($"{player}, show me! say done when finished", audience: [player], ct: ct).ConfigureAwait(false);

                var deadline = DateTimeOffset.UtcNow.AddSeconds(max);
                while (true)
                {
                    var left = deadline - DateTimeOffset.UtcNow;
                    if (left <= TimeSpan.Zero) break;
                    var reply = await chat.WaitForReplyAsync(player, left, ct).ConfigureAwait(false);
                    if (reply == null || Regex.IsMatch(reply, @"\b(done|finished|that's it|thats it|ok that's all)\b", RegexOptions.IgnoreCase)) break;
                }

                var actions = await session.StopAsync(ct).ConfigureAwait(false);
                var steps = DemonstrationSummarizer.Summarize(actions, known?.Min);
                if (steps.Count == 0) return ToolResult.Error("saw nothing to learn");
                memory.UpsertStructure(name, s =>
                {
                    s.Steps.Clear();
                    s.Steps.AddRange(steps);
                    s.TaughtBy = player;
                    s.Confidence = Math.Max(s.Confidence, 0.7);
                });
                return ToolResult.Success($"learned {name} ({steps.Count} steps): " + string.Join("; ", steps));
            }, longRunning: false));
    }
}
