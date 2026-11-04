using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Context;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins the history the model sees (WP06 refactor of <see cref="ContextManager"/>): compaction (summary, rejected checkpoint,
/// mechanical fallback, no summarizer), the cheap-tier renderings, <see cref="ContextManager.Shrink"/> and
/// <see cref="ContextManager.Seal"/>, dumped as the exact messages each produces.
/// </summary>
public class ContextGoldenTests
{
    private const string Expected = "8B82124C07F602B9368D0EA04C5D21BEE04FF23223C75B6BCB74C58E873819AB";

    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static Episode ToolEpisode(int n, int resultChars = 300)
    {
        var e = new Episode(ModelTier.Main, T0.AddMinutes(n));
        e.Messages.Add(LlmMessage.User($"pos 0,64,0\n[chat] Alice: job {n}\n[journal] x\n[memory] y\n[self] z\n[task ok] mined {n}"));
        e.Messages.Add(LlmMessage.Assistant(new LlmThinking("hmm", "sig"), new LlmText(n % 2 == 0 ? "ok" : "(silent)"), new LlmToolUse($"a{n}", "mine", J(new { block = "cobblestone", count = n })),
            new LlmToolUse($"b{n}", "look", J(new { }))));
        e.Messages.Add(LlmMessage.User(new LlmToolResult($"a{n}", new string('r', resultChars)), new LlmToolResult($"b{n}", "grass\nand dirt", IsError: n % 3 == 0)
        { Image = n == 2 ? new LlmImage([1, 2, 3], 4, 4, "[image omitted]") : null }));
        e.Messages.Add(LlmMessage.Assistant($"done with job {n}"));
        return e;
    }

    private static string Dump(IEnumerable<LlmMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            sb.Append("  ").Append(m.Role).Append(':');
            foreach (var b in m.Content)
                sb.Append(" [").Append(b switch
                {
                    LlmText t => "text " + t.Text,
                    LlmToolUse u => $"use {u.Id} {u.Name} {u.Input.GetRawText()}",
                    LlmToolResult r => $"result {r.ToolUseId} {r.IsError} {r.Content} img={r.Image != null}",
                    LlmThinking th => "thinking " + th.Thinking,
                    _ => b.GetType().Name,
                }).Append(']');
            sb.Append('\n');
        }
        return sb.ToString().Replace("\r", "");
    }

    [Fact]
    public async Task History_renders_compacts_shrinks_and_seals_the_same()
    {
        var sb = new StringBuilder();
        Func<string, CancellationToken, Task<string>>?[] summarizers =
        [
            (p, _) => Task.FromResult("  Alice gave jobs; all done  "),
            null,
            (_, _) => Task.FromResult("   "),
            (_, _) => throw new HttpRequestException("down"),
            (_, _) => throw new IncompleteCompactionSummaryException("max_tokens"),
        ];
        foreach (var (maxJ, maxTokens) in new[] { (200, 100_000), (60, 100_000), (2000, 900), (5, 100_000) })
            for (int s = 0; s < summarizers.Length; s++)
            {
                var ctx = new ContextManager(new ContextOptions
                {
                    CompactAtTokens = 400, CompactToTokens = 200, KeepFullEpisodes = 1, JournalMaxChars = maxJ, MaxTokens = maxTokens,
                    OldToolResultChars = 50,
                });
                for (int i = 0; i < 8; i++) ctx.Commit(ToolEpisode(i));
                string? prompt = null;
                var sum = summarizers[s];
                int folded = await ctx.CompactAsync(sum == null ? null : (p, ct) => { prompt = p; return sum(p, ct); }, default);
                sb.Append("compact ").Append(maxJ).Append(' ').Append(maxTokens).Append(' ').Append(s).Append(" => ").Append(folded)
                  .Append(" journal=").Append(ctx.Journal.Replace("\r", "")).Append(" est=").Append(ctx.EstimateTokens()).Append('\n');
                sb.Append(" prompt=").Append(prompt?.Replace("\r", "") ?? "null").Append('\n');
                sb.Append(Dump(ctx.BuildMessages(ModelTier.Main, [LlmMessage.User("[chat] Alice: next")])));
                sb.Append(Dump(ctx.BuildMessages(ModelTier.Cheap, [LlmMessage.User("[chat] Alice: next")])));
            }

        foreach (var max in new[] { 10_000, 200, 120, 60, 20, 1 })
        {
            var msgs = new List<LlmMessage>();
            for (int i = 0; i < 3; i++) msgs.AddRange(ToolEpisode(i, 900).Messages);
            msgs.Add(LlmMessage.User(new LlmText(new string('t', 700)), new LlmText("short")));
            int est = ContextManager.Shrink(msgs, max, 60);
            sb.Append("shrink ").Append(max).Append(" => ").Append(est).Append('\n').Append(Dump(msgs));
        }

        List<List<LlmMessage>> broken =
        [
            [],
            [LlmMessage.User("hi")],
            [LlmMessage.User("hi"), LlmMessage.Assistant(new LlmText(""), new LlmToolUse("u1", "goto", J(new { to = "x" })))],
            [LlmMessage.User("hi"), LlmMessage.Assistant(new LlmToolUse("u1", "goto", J(new { })), new LlmToolUse("u2", "look", J(new { }))),
                LlmMessage.User(new LlmText("[chat] more"), new LlmToolResult("u2", "ok"))],
            [LlmMessage.User(new LlmText("  ")), LlmMessage.Assistant(new LlmText(" ")), LlmMessage.User("x")],
            [LlmMessage.Assistant(new LlmToolUse("u9", "stop", J(new { }))), LlmMessage.Assistant("again")],
        ];
        foreach (var b in broken)
        {
            ContextManager.Seal(b);
            sb.Append("seal\n").Append(Dump(b));
        }

        foreach (var n in new[] { 0, 1, 2, 3 })
        {
            var (u, a) = ContextManager.CheapRendering(ToolEpisode(n), n == 3 ? 40 : 2000);
            sb.Append("cheap ").Append(n).Append('\n').Append(Dump([u, a]));
        }

        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "context.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
