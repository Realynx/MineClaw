using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Memory.Grooming;

/// <summary>
/// Log ingestion through the groomer: raw log text (the bot's console/file log, a journald dump, a pasted excerpt) → one candidate
/// doc per line (console timestamps kept as "10-27 06:46: "; debug-only lines dropped up front) → the chain of the source
/// (<c>log</c> / <c>paste</c>): de-noise, normalize, redact, phrase, merge (repeats extend one doc), size, and optionally the LLM rewrite.
/// Refs are stable per fact, so ingesting the same log twice changes nothing.
/// </summary>
public static partial class LogIngest
{
    [GeneratedRegex(@"^(\d{2}):(\d{2}):\d{2}(?:\.\d+)? (INF|DBG|WRN|ERR|TRC|FTL|CRT) [\w.]+: (.*)$")]
    private static partial Regex Console();

    [GeneratedRegex(@"(\d{4})(\d{2})(\d{2})")]
    private static partial Regex FileDay();

    /// <summary>"MM-dd" from a log file name like mineclaw-20261027.log (null when it has no date).</summary>
    public static string? DayOf(string? fileName) =>
        fileName != null && FileDay().Match(Path.GetFileName(fileName)) is { Success: true } m ? $"{m.Groups[2].Value}-{m.Groups[3].Value}" : null;

    /// <summary>Raw text → candidate docs (not groomed yet).</summary>
    public static List<RagDoc> Parse(string text, string source = "log", string? day = null, string kind = "episode")
    {
        var docs = new List<RagDoc>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) continue;
            var m = Console().Match(line);
            if (m.Success)
            {
                if (m.Groups[3].Value is "DBG" or "TRC") continue;
                line = (day != null ? $"{day} {m.Groups[1].Value}:{m.Groups[2].Value}: " : "") + m.Groups[4].Value;
            }
            docs.Add(new RagDoc("", kind, "", line) { Source = source });
        }
        return docs;
    }

    /// <summary>A stable ref for a groomed fact (re-ingesting the same log updates, never duplicates).</summary>
    public static string RefFor(string source, string body)
    {
        var key = MergeStage.FactKey(body);
        var h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        return $"{source}:{h}";
    }

    /// <summary>Grooms the text into the docs that would be stored (nothing is written; <paramref name="ctx"/> decides dry-run).</summary>
    public static async Task<IReadOnlyList<RagDoc>> GroomAsync(MemoryGroomer groomer, string text, GroomContext ctx, string source = "log",
        string? day = null, CancellationToken ct = default)
    {
        foreach (var d in Parse(text, source, day))
        {
            ct.ThrowIfCancellationRequested();
            // The ref is the fact's: give the candidate one now so a repeat lands on the same doc.
            var withRef = d with { Ref = RefFor(source, NormalizeStage.Normalize(NormalizeStage.StripPrefix(d.Body), ctx)) };
            await groomer.GroomAsync(withRef, ctx, ct).ConfigureAwait(false);
        }
        return ctx.Emitted.ToList();
    }

    /// <summary>Grooms and stores the text (the stored docs are returned).</summary>
    public static async Task<IReadOnlyList<RagDoc>> IngestAsync(RagIndex rag, MemoryGroomer groomer, string text, string source = "log",
        string? day = null, CancellationToken ct = default)
    {
        var ctx = groomer.Context();
        var docs = await GroomAsync(groomer, text, ctx, source, day, ct).ConfigureAwait(false);
        rag.Db.Tx(() => { foreach (var d in docs) rag.UpsertGroomed(d); });
        return docs;
    }
}

/// <summary>
/// The optional LLM rewrite stage's model: the memory tier (Haiku-class 5.5 model, <see cref="ModelTier.Cheap"/>), one call per doc,
/// a hard daily dollar cap (<see cref="GroomRewriteOptions.DailyMaxUsd"/>); over the cap or on any failure the text stays as it is.
/// </summary>
public sealed class LlmGroomRewriter(ILlmClient llm, GroomRewriteOptions options, UsageTracker? usage = null, PriceTable? prices = null,
    TimeProvider? time = null) : IGroomRewriter
{
    private readonly object _lock = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateOnly _day;
    private decimal _spent;

    public decimal SpentToday { get { lock (_lock) return _spent; } }

    private const string System = "You groom raw bot logs into memory notes for a Minecraft bot. Rewrite the text as one to three short " +
        "third-person sentences in plain past tense (\"maid_chan mined 20 cobblestone at the cobble gen\"). Keep every name, number, " +
        "place and time exactly; drop telemetry, costs and debug details; invent nothing. Answer with the sentences only.";

    public async Task<string?> RewriteAsync(string source, string text, CancellationToken ct)
    {
        lock (_lock)
        {
            var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
            if (today != _day) { _day = today; _spent = 0; }
            if (_spent >= options.DailyMaxUsd) return null;
        }
        try
        {
            var response = await llm.CompleteAsync(new LlmRequest
            {
                Tier = ModelTier.Cheap,
                System = [new LlmSystemBlock(System)],
                Messages = [LlmMessage.User(text)],
                MaxTokens = Math.Max(32, options.MaxOutputTokens),
                CacheHistory = false,
                Purpose = "groom",
            }, ct).ConfigureAwait(false);
            decimal cost = usage?.Record(response.Model, response.Usage, ModelTier.Cheap) ?? (prices ?? new PriceTable()).Cost(response.Model, response.Usage);
            lock (_lock) _spent += cost;
            var t = response.Text.Trim();
            return t.Length == 0 ? null : t;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }
}
