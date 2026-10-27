using System.Text.RegularExpressions;
using MineClaw.Client;

namespace MineClaw.Bot.WorldIndex;

public enum ChatLineKind { Player, Emote, Whisper, ServerSay, System }

public sealed record ClassifiedLine(ChatLineKind Kind, string Text, string? Sender = null, string? Message = null);

/// <summary>
/// Classifies raw chat lines by format. b1.8's chat packet has no sender, so spoof resistance rests on the server always
/// framing player text (<c>&lt;name&gt; msg</c>, <c>* name</c>, whispers, <c>[Server]</c>); only <see cref="ChatLineKind.System"/>
/// lines may be parsed as deaths/joins. Patterns are configurable for plugin chat formats — if a plugin lets player text
/// appear unframed, death lines can be spoofed and only corroboration (online list, vanish/status/respawn) protects.
/// </summary>
public sealed class ChatLineClassifier
{
    public sealed class Options
    {
        public string PlayerPattern { get; set; } = @"^<([A-Za-z0-9_]{1,16})> ?(.*)$";
        public string EmotePattern { get; set; } = @"^\* ?([A-Za-z0-9_]{1,16}) (.*)$";
        public string WhisperPattern { get; set; } = @"^([A-Za-z0-9_]{1,16}) whispers:? (.*)$|^\[([A-Za-z0-9_]{1,16}) -> [^\]]+\] (.*)$";
        public string ServerSayPattern { get; set; } = @"^\[(?:Server|CONSOLE|Console|Broadcast)\] ?(.*)$";
        /// <summary>Extra framed formats (e.g. plugin prefixes "[Admin] &lt;name&gt; msg") treated as player chat.</summary>
        public string[] ExtraPlayerPatterns { get; set; } = [@"^\[[^\]]{1,20}\] ?<([A-Za-z0-9_]{1,16})> ?(.*)$", @"^([A-Za-z0-9_]{1,16}): (.*)$"];
    }

    private readonly Regex _player, _emote, _whisper, _say;
    private readonly Regex[] _extra;

    public ChatLineClassifier(Options? options = null)
    {
        var o = options ?? new Options();
        _player = new Regex(o.PlayerPattern, RegexOptions.CultureInvariant);
        _emote = new Regex(o.EmotePattern, RegexOptions.CultureInvariant);
        _whisper = new Regex(o.WhisperPattern, RegexOptions.CultureInvariant);
        _say = new Regex(o.ServerSayPattern, RegexOptions.CultureInvariant);
        _extra = o.ExtraPlayerPatterns.Select(p => new Regex(p, RegexOptions.CultureInvariant)).ToArray();
    }

    public ClassifiedLine Classify(string raw)
    {
        var s = ChatText.StripColours(raw).Trim();
        Match m;
        if ((m = _player.Match(s)).Success) return new(ChatLineKind.Player, s, m.Groups[1].Value, m.Groups[2].Value);
        if ((m = _emote.Match(s)).Success) return new(ChatLineKind.Emote, s, m.Groups[1].Value, m.Groups[2].Value);
        if ((m = _whisper.Match(s)).Success)
            return new(ChatLineKind.Whisper, s, m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value, m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value);
        if ((m = _say.Match(s)).Success) return new(ChatLineKind.ServerSay, s, null, m.Groups[1].Value);
        foreach (var x in _extra)
            if ((m = x.Match(s)).Success) return new(ChatLineKind.Player, s, m.Groups[1].Value, m.Groups[2].Value);
        return new(ChatLineKind.System, s);
    }
}

/// <summary>Parses death lines (system lines only) into (player, cause).</summary>
public sealed class DeathLineParser(IEnumerable<string> patterns)
{
    private readonly Regex[] _rx = patterns.Select(p => new Regex(p, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)).ToArray();

    public (string Player, string Cause)? Parse(ClassifiedLine line)
    {
        if (line.Kind != ChatLineKind.System) return null;
        foreach (var r in _rx)
        {
            var m = r.Match(line.Text);
            if (!m.Success) continue;
            var cause = m.Groups.Count > 2 ? m.Groups[2].Value : "died";
            return (m.Groups[1].Value, Humanize(cause));
        }
        return null;
    }

    /// <summary>"fell out of the world" → "fell into the void".</summary>
    public static string Humanize(string cause) => cause.Trim().TrimEnd('.') switch
    {
        "fell out of the world" or "fell into the void" => "fell into the void",
        var c => c,
    };
}
