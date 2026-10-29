using System.Text.RegularExpressions;

namespace MineClaw.Agent.Chat;

/// <summary>
/// Players who just died (the server's death line, both versions' wording), so a warning or advice still queued for them is not
/// said after the fact (live 10-29: right after "kiwi was slain by Zombie", main said "robin run to the river chest, maid-chan will
/// bonk it there"). Covers every sender through <see cref="ChatRouter.SayAsync"/>: the brain's say, the reactive voice, tools.
/// </summary>
public sealed partial class RecentDeaths(Func<DateTimeOffset> now)
{
    /// <summary>How long after a death a warning/advice line to that player is stale.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, DateTimeOffset> _died = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>vanilla death messages (<c>death.attack.*</c>, <c>death.fell.*</c>): "X was slain by …", "X drowned", "X died"…</summary>
    [GeneratedRegex(
        @"^(?<n>[A-Za-z0-9_]{2,16})\s+(?:was\s+(?:slain|shot|killed|blown\s+up|pricked|fireballed|pummeled|squashed|impaled|struck|squished|doomed|knocked)\b|drowned\b|died\b|blew\s+up\b|hit\s+the\s+ground\b|fell\b|tried\s+to\s+swim\s+in\s+lava\b|went\s+up\s+in\s+flames\b|burned\s+to\s+death\b|walked\s+into\b|starved\s+to\s+death\b|suffocated\b|withered\s+away\b|experienced\s+kinetic\s+energy\b)",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeathRx();

    /// <summary>Advice or a warning to act ("run to the chest", "watch out", "get inside", "come here", a threat warning).</summary>
    [GeneratedRegex(
        @"\b(?:run|go|get|hide|come|stay|move|back\s+off|jump|watch\s+out|look\s+out|careful|behind\s+(?:you|u)|head\s+(?:to|for|back)|hurry|quick|duck|flee|escape)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdviceRx();

    /// <summary>A server line: records the player when it is a death message. Returns the name, or null.</summary>
    public string? Observe(string? line)
    {
        if (line is null || DeathRx().Match(line.Trim()) is not { Success: true } m) return null;
        var n = m.Groups["n"].Value;
        lock (_lock) _died[n] = now();
        return n;
    }

    /// <summary>The player died within <see cref="Window"/>.</summary>
    public bool JustDied(string player)
    {
        lock (_lock) return _died.TryGetValue(player, out var t) && now() - t <= Window;
    }

    /// <summary>
    /// Drops the warning/advice sentences of <paramref name="text"/> meant for <paramref name="audience"/> when every one of them
    /// just died: telling a dead player to run is stale (what's left — condolences, "ur stuff is at …" — stays).
    /// </summary>
    public string Filter(string text, IReadOnlyCollection<string> audience, out List<string> dropped)
    {
        dropped = [];
        if (string.IsNullOrWhiteSpace(text) || audience.Count == 0 || !audience.All(JustDied)) return text;
        var kept = new List<string>();
        foreach (var s in Regex.Split(text, @"(?<=[.!?~])\s+"))
        {
            if (s.Trim().Length == 0) continue;
            if (AdviceRx().IsMatch(s) || SafetyWarnings.Threat(s) is not null) dropped.Add(s);
            else kept.Add(s);
        }
        return string.Join(" ", kept);
    }
}
