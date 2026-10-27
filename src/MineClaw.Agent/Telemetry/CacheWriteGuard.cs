using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Telemetry;

/// <summary>
/// Flags one call that writes a lot of prompt cache: with an append-only history every call should write only its new tail
/// (a few hundred tokens), so &gt; <see cref="WarnTokens"/> means the prefix was rebuilt. The warning names the likely cause
/// (live 10-27 15:55: <c>4 call(s) $0.4194 [cw 77425]</c> went unnoticed in the INF stream; it was an effort switch).
/// </summary>
public static class CacheWriteGuard
{
    public const int WarnTokens = 30_000;

    /// <summary>
    /// Most likely reason a call re-wrote its history cache, judged before it is sent: a compaction since the last call, an
    /// effort/thinking/model variant whose cache is cold (the message cache is per variant and found only within 5 min and ~20
    /// blocks), or the TTL running out while idle.
    /// </summary>
    /// <param name="lastKey">Variant of the previous call (null = first call).</param>
    /// <param name="key">Variant of this call (<see cref="Context.CacheWarmth.Key"/>).</param>
    /// <param name="coldReason">Why this variant's cache is cold (<see cref="Context.CacheWarmth.ColdReason"/>), null = warm.</param>
    public static string Cause(string? lastKey, string key, string? coldReason, DateTimeOffset? lastCallAt, DateTimeOffset now, bool compacted)
    {
        if (compacted) return "history was compacted since the last call (expected once per compaction)";
        if (lastKey == null || lastCallAt == null) return "first call of this process (cold cache)";
        if (lastKey != key && coldReason != null)
            return $"variant switched {Describe(lastKey)} -> {Describe(key)}, whose cache was cold: {coldReason} (the history cache is per effort/thinking/model)";
        var idle = now - lastCallAt.Value;
        if (idle > TimeSpan.FromMinutes(5)) return $"cache TTL expired: no call for {(int)idle.TotalMinutes} min";
        return coldReason != null ? $"cache cold: {coldReason}" : "prefix changed (history or identity block edited)";
    }

    /// <summary>The WARN text, or null when the call stayed under <see cref="WarnTokens"/>.</summary>
    public static string? Check(LlmUsage usage, string label, int round, string cause, bool interrupted = false) =>
        usage.CacheWriteTokens <= WarnTokens ? null
            : $"cache write spike: {label} round {round}{(interrupted ? " (interrupted call)" : "")} wrote {usage.CacheWriteTokens} cache tokens " +
              $"(read {usage.CacheReadTokens}); likely: {cause}";

    private static string Describe(string key)
    {
        var p = key.Split('|');
        string effort = p.Length > 1 && p[1] != "-" ? p[1] : "configured effort";
        string thinking = p.Length > 2 && p[2] != "-" ? $", thinking {p[2]}" : "";
        string model = p.Length > 0 && p[0] != "-" ? $" on {p[0]}" : "";
        return effort + thinking + model;
    }
}
