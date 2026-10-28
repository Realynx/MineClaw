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

    /// <summary>The threat a warning line is about ("zombie", "creeper", "lava", "void", "death"), or null when it isn't one.</summary>
    public static string? Threat(string? text)
    {
        var t = text ?? "";
        var m = ThreatRx().Match(t);
        if (!m.Success) return null;
        // A warning: urgency (an exclamation, "watch out"/"behind you"/"run"), or a threat acting on someone ("… is attacking you").
        if (!UrgentRx().IsMatch(t) && !t.Contains('!')) return null;
        var w = m.Value.ToLowerInvariant();
        return w switch
        {
            _ when w.StartsWith("zombie", StringComparison.Ordinal) => "zombie",
            _ when w.StartsWith("skeleton", StringComparison.Ordinal) || w.StartsWith("arrow", StringComparison.Ordinal) => "skeleton",
            _ when w.StartsWith("creeper", StringComparison.Ordinal) || w.StartsWith("hiss", StringComparison.Ordinal) || w.StartsWith("explo", StringComparison.Ordinal) => "creeper",
            _ when w.StartsWith("spider", StringComparison.Ordinal) => "spider",
            _ when w.StartsWith("lava", StringComparison.Ordinal) || w.StartsWith("fire", StringComparison.Ordinal) || w.StartsWith("burn", StringComparison.Ordinal) => "lava",
            _ when w.StartsWith("void", StringComparison.Ordinal) || w.StartsWith("fall", StringComparison.Ordinal) || w.StartsWith("edge", StringComparison.Ordinal) => "void",
            _ when w.StartsWith("die", StringComparison.Ordinal) || w.StartsWith("dying", StringComparison.Ordinal) || w.StartsWith("dead", StringComparison.Ordinal) || w.StartsWith("died", StringComparison.Ordinal) => "death",
            _ => w.TrimEnd('s'),
        };
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
