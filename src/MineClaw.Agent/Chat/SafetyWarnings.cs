using System.Text.RegularExpressions;

namespace MineClaw.Agent.Chat;

/// <summary>
/// A safety warning to a player or bot ("Robin, a zombie is attacking you", "creeper behind you!", "watch out, lava") is never
/// held back by a quiet gate: not the startup-chatter cap, not a quiet turn's "nobody is waiting", not a "stay quiet unless spoken
/// to" rule (live 10-28 16:21: say answered "not said" for "Robin, a zombie is attacking you" right after a restart). Rate-limited
/// to one per threat (and who it's about) per <see cref="Window"/>; past that the normal gates apply again.
/// </summary>
public sealed partial class SafetyWarnings(Func<DateTimeOffset> now)
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);

    /// <summary>Each threat's name and the word starts that name it, checked in order (anything else: the word minus a plural 's').</summary>
    private static readonly (string Name, string[] Starts)[] ThreatNames =
    [
        ("zombie", ["zombie"]),
        ("skeleton", ["skeleton", "arrow"]),
        ("creeper", ["creeper", "hiss", "explo"]),
        ("spider", ["spider"]),
        ("lava", ["lava", "fire", "burn"]),
        ("void", ["void", "fall", "edge"]),
        ("death", ["die", "dying", "dead", "died"]),
    ];

    /// <summary>The threat a warning line is about ("zombie", "creeper", "lava", "void", "death"), or null when it isn't one.</summary>
    public static string? Threat(string? text)
    {
        var t = text ?? "";
        var m = ThreatRx().Match(t);
        if (!m.Success) return null;
        // A warning: urgency (an exclamation, "watch out"/"behind you"/"run"), or a threat acting on someone ("… is attacking you").
        if (!UrgentRx().IsMatch(t) && !t.Contains('!')) return null;
        return ThreatName(m.Value.ToLowerInvariant());
    }

    private static string ThreatName(string w)
    {
        foreach (var (name, starts) in ThreatNames)
            foreach (var s in starts)
                if (w.StartsWith(s, StringComparison.Ordinal)) return name;
        return w.TrimEnd('s');
    }

    /// <summary>The line is a warning whose threat wasn't warned about within <see cref="Window"/> (no state change).</summary>
    public bool Overrides(string? text, IReadOnlyCollection<string>? audience = null)
    {
        if (Threat(text) is not { } threat) return false;
        lock (_last) return !_last.TryGetValue(Key(threat, audience), out var at) || now() - at >= Window;
    }

    /// <summary>Records the warning as said (its threat's window starts now). False when it was warned about too recently.</summary>
    public bool Take(string? text, IReadOnlyCollection<string>? audience = null)
    {
        if (Threat(text) is not { } threat) return false;
        var key = Key(threat, audience);
        lock (_last)
        {
            var t = now();
            if (_last.TryGetValue(key, out var at) && t - at < Window) return false;
            _last[key] = t;
            return true;
        }
    }

    private static string Key(string threat, IReadOnlyCollection<string>? audience) =>
        threat + "|" + string.Join(",", (audience ?? []).Select(a => a.ToLowerInvariant()).OrderBy(a => a, StringComparer.Ordinal));

    [GeneratedRegex(@"\b(?:zombies?|skeletons?|arrows?|creepers?|hiss\w*|explod\w*|spiders?|endermen|enderman|slimes?|mobs?|lava|fire|burning|void|fall(?:ing)?|edge|die|dying|died|dead)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ThreatRx();

    [GeneratedRegex(@"\b(?:watch out|look out|careful|behind you|run|get away|back off|move|attack(?:ing|ed|s)?|hurt(?:ing|s)?|hitting|chasing|after you|on you|near you|next to you|coming|incoming|help)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrgentRx();
}
