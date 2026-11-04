using System.ComponentModel;
using System.Globalization;

namespace MineClaw.Agent;

/// <summary>How hard non-chat model output is compressed (caveman-style internal register).</summary>
public enum TerseLevel
{
    /// <summary>Normal prose.</summary>
    Off = 0,
    /// <summary>No filler, hedging or pleasantries; full sentences are fine.</summary>
    Lite = 1,
    /// <summary>Telegraphic fragments, articles and filler dropped.</summary>
    Full = 2,
    /// <summary>Maximum compression: key=value, symbols, abbreviations.</summary>
    Ultra = 3,
}

/// <summary>The output kinds the terse register can apply to (keys of <see cref="TerseOptions.Purposes"/>).</summary>
public static class TersePurpose
{
    /// <summary>The brain's (Opus) thinking.</summary>
    public const string Thinking = "thinking";
    /// <summary>delegate task/done_when (and ask_memory questions) the brain writes.</summary>
    public const string Delegate = "delegate";
    /// <summary>Worker (Sonnet) reasoning, inter-tool narration and the ok:/failed:/needs_input: result line.</summary>
    public const string Worker = "worker";
    /// <summary>ask_memory answers (Haiku).</summary>
    public const string AskMemory = "ask_memory";
    /// <summary>The compaction journal.</summary>
    public const string Compaction = "compaction";
    /// <summary>Self-reflection diffs (entry text and reasons).</summary>
    public const string Reflection = "reflection";
    /// <summary>RAPTOR / self-tree summaries: the paper's prompt by default; at most lite.</summary>
    public const string Raptor = "raptor";

    public static readonly string[] All = [Thinking, Delegate, Worker, AskMemory, Compaction, Reflection, Raptor];

    /// <summary>
    /// Built-in level of a purpose when nothing is configured. Planned full for delegate/worker/ask_memory/compaction/
    /// reflection; the live A/B (tools/TerseAB, off vs full vs lite, 2026-10-26) moved three back:
    /// delegate → lite (full dropped counts, e.g. "12 oak logs", judge −0.6, and Opus output rose: thinking dominates);
    /// ask_memory → lite (no output saving: lookups dominate; one judge drop); compaction → off (judge −1.5 at full, no
    /// saving at lite). Worker (−26% output, judge +1.5) and reflection stay full; thinking lite; raptor off (fidelity).
    /// </summary>
    public static TerseLevel Default(string purpose) => Key(purpose) switch
    {
        "thinking" or "delegate" or "askmemory" => TerseLevel.Lite,
        "worker" or "reflection" => TerseLevel.Full,
        _ => TerseLevel.Off, // compaction, raptor
    };

    /// <summary>Lookup key: lower case, '_' / '-' / ' ' removed ("ask_memory" == "AskMemory" == "ask-memory").</summary>
    internal static string Key(string purpose) =>
        new((purpose ?? "").Where(c => c is not ('_' or '-' or ' ')).Select(char.ToLowerInvariant).ToArray());
}

/// <summary>
/// "Agent:Terse": the caveman-style internal register for model output nobody but the bot reads (thinking, tool inputs,
/// worker/memory results, journal, reflection). Player-facing chat is never affected: it keeps the full persona.
/// Either a scalar (<c>"Terse": "full"</c>: one level for every purpose except raptor) or an object
/// (<c>"Terse": { "Level": "full", "Purposes": { "thinking": "lite", "raptor": "lite" } }</c>).
/// Resolution per purpose: <see cref="Purposes"/> entry → <see cref="Level"/> (not raptor) → built-in default
/// (<see cref="TersePurpose.Default"/>: full for worker/reflection, lite for thinking/delegate/ask_memory, compaction and
/// raptor off).
/// Raptor is capped at lite (summary fidelity). The rule text is static per run and lives in each tier's cached prefix.
/// </summary>
[TypeConverter(typeof(TerseOptionsConverter))]
public sealed class TerseOptions
{
    /// <summary>off | lite | full | ultra; null/empty = per-purpose defaults.</summary>
    public string? Level { get; set; }

    /// <summary>Per-purpose overrides (keys from <see cref="TersePurpose"/>, case/underscore-insensitive).</summary>
    public Dictionary<string, string> Purposes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Effective level for one purpose.</summary>
    public TerseLevel For(string purpose)
    {
        var key = TersePurpose.Key(purpose);
        bool raptor = key == "raptor";
        foreach (var (k, v) in Purposes)
            if (TersePurpose.Key(k) == key && TryParse(v, out var explicitLevel))
                return raptor ? Cap(explicitLevel) : explicitLevel;
        if (!raptor && TryParse(Level, out var global)) return global;
        return TersePurpose.Default(purpose);

        static TerseLevel Cap(TerseLevel l) => l > TerseLevel.Lite ? TerseLevel.Lite : l;
    }

    /// <summary>"thinking=lite delegate=full ..." (startup log).</summary>
    public string Describe() => string.Join(" ", TersePurpose.All.Select(p => $"{p}={For(p).ToString().ToLowerInvariant()}"));

    private static readonly Dictionary<string, TerseLevel> LevelWords = new(StringComparer.Ordinal)
    {
        ["off"] = TerseLevel.Off, ["none"] = TerseLevel.Off, ["false"] = TerseLevel.Off, ["0"] = TerseLevel.Off,
        ["lite"] = TerseLevel.Lite, ["light"] = TerseLevel.Lite, ["1"] = TerseLevel.Lite,
        ["full"] = TerseLevel.Full, ["on"] = TerseLevel.Full, ["true"] = TerseLevel.Full, ["2"] = TerseLevel.Full,
        ["ultra"] = TerseLevel.Ultra, ["3"] = TerseLevel.Ultra,
    };

    public static bool TryParse(string? text, out TerseLevel level)
    {
        if (LevelWords.TryGetValue((text ?? "").Trim().ToLowerInvariant(), out level)) return true;
        level = TerseLevel.Off;
        return false;
    }
}

/// <summary>Lets the configuration binder accept the scalar form <c>"Agent:Terse": "full"</c>.</summary>
public sealed class TerseOptionsConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
        value is string s ? new TerseOptions { Level = s.Trim() } : base.ConvertFrom(context, culture, value);
}
