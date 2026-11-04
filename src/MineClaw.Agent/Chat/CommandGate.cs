using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent.Chat;

/// <summary>"Agent:Commands": which server commands the bot may ever send.</summary>
public sealed class CommandOptions
{
    /// <summary>Commands the bot may always send (roleplay and whispers). Nothing else is free.</summary>
    public string[] AlwaysAllowed { get; set; } = ["/me", "/tell", "/msg"];

    /// <summary>
    /// Never sent, even when a trusted player gives the exact command. <c>/execute</c> is here because it runs a command the
    /// caller names (<c>/execute as @a run op bob</c>): whatever follows "run" is checked against this list too.
    /// </summary>
    public string[] Blocked { get; set; } =
        ["/stop", "/op", "/deop", "/ban", "/ban-ip", "/pardon", "/pardon-ip", "/whitelist", "/save-off", "/kick", "/execute"];

    /// <summary>
    /// When not empty, the only commands the bot may ever send besides <see cref="AlwaysAllowed"/> (e.g. ["/tp", "/time",
    /// "/weather"]): anything else counts as blocked, even when a trusted player gives the exact command. Empty = every
    /// command not in <see cref="Blocked"/> may be authorised.
    /// </summary>
    public string[] Allowed { get; set; } = [];

    /// <summary>How long a trusted player's "run /x" line authorises exactly one <c>run_command</c> of /x.</summary>
    public int AuthorizationSeconds { get; set; } = 60;

    /// <summary>
    /// How long a trusted player's request addressed to the bot ("set it to day", "give everyone a stone pickaxe") lets the bot
    /// write and run the commands it needs itself, up to <see cref="RequestCommands"/> of them (blocked ones never). 0 = off:
    /// only exact commands. Fox 10-30: "If I tell it to execute a command explicitly, it should be allowed to do so. I
    /// shouldn't have to type that command out."
    /// </summary>
    public int RequestSeconds { get; set; } = 120;

    /// <summary>Commands one trusted request may run.</summary>
    public int RequestCommands { get; set; } = 8;

    /// <summary>Audit log of executed commands (JSON lines), under the data directory. Empty = log only.</summary>
    public string AuditFile { get; set; } = "command-audit.log";
}

/// <summary>Result of <see cref="CommandGate.Authorize"/>.</summary>
public sealed record CommandDecision(bool Ok, string Text, string? AuthorizedBy = null);

/// <summary>
/// C#-level gate on server commands. The model can never send one by itself: plain text output is chat, and every outgoing
/// line starting with '/' passes <see cref="AllowOutgoing"/>, which only lets through <see cref="CommandOptions.AlwaysAllowed"/>
/// commands and lines armed by a successful <see cref="Authorize"/>. Authorisations come only from live chat lines
/// (<see cref="Observe"/>): a <see cref="CommandOptions"/>-trusted player's own <c>&lt;name&gt;</c> line or whisper, handled by
/// this bot, addressing it, containing the exact command. One authorisation = one execution; blocklisted commands never run.
/// </summary>
public sealed class CommandGate
{
    private sealed record Grant(string Command, string Player, DateTimeOffset At);
    private sealed class Request(string player, string text, DateTimeOffset at)
    {
        public string Player { get; } = player;
        public string Text { get; } = text;
        public DateTimeOffset At { get; } = at;
        /// <summary>Where the request came from when not the game (<see cref="ObserveRemote"/>: "viewer"); null = in game.</summary>
        public string? Via { get; init; }
        public string By => Via == null ? Player : $"{Player} ({Via})";
        public int Used;
    }

    private static readonly Regex CommandInText = new(@"(?:^|[\s`'""(])(?<cmd>/[A-Za-z][^`'""\r\n]*)", RegexOptions.Compiled);
    private static readonly Regex NamespacedName = new(@"^/(?:[A-Za-z0-9_.\-]+):(?<cmd>[A-Za-z0-9_.\-]+)$", RegexOptions.Compiled);
    private static readonly Regex RunKeyword = new(@"(?:^|\s)run\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly CommandOptions _o;
    private readonly Func<IReadOnlyCollection<string>> _trusted;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly string? _auditPath;
    private readonly object _lock = new();
    private readonly List<Grant> _grants = new();
    private readonly List<Request> _requests = new();
    private readonly List<(string Line, DateTimeOffset Until)> _armed = new();

    public CommandGate(CommandOptions options, Func<IReadOnlyCollection<string>> trustedPlayers, TimeProvider? time = null,
        ILogger? log = null, string? auditPath = null)
    {
        _o = options;
        _trusted = trustedPlayers;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _auditPath = auditPath;
    }

    public CommandOptions Options => _o;

    /// <summary>Raised for every executed command (who authorised it, the exact text, when).</summary>
    public event Action<string, string, DateTimeOffset>? Executed;

    private TimeSpan Window => TimeSpan.FromSeconds(Math.Max(1, _o.AuthorizationSeconds));

    /// <summary>"/time set day" → "/time", "/minecraft:op bob" → "/op" (lower case), or null for non-commands.</summary>
    public static string? NameOf(string line)
    {
        var t = (line ?? "").Trim();
        if (t.Length < 2 || t[0] != '/') return null;
        int sp = t.IndexOfAny([' ', '\t']);
        return WithoutNamespace((sp < 0 ? t : t[..sp]).ToLowerInvariant());
    }

    /// <summary>"/minecraft:op" → "/op": the server accepts a namespace prefix on any command.</summary>
    private static string WithoutNamespace(string name) =>
        NamespacedName.Match(name) is { Success: true } m ? "/" + m.Groups["cmd"].Value : name;

    private static bool InList(string[] list, string? name) =>
        name != null && list.Any(c => string.Equals(c.Trim(), name, StringComparison.OrdinalIgnoreCase));

    public bool IsAlwaysAllowed(string line) => InList(_o.AlwaysAllowed, NameOf(line));

    /// <summary>
    /// True when the line must not be sent. <c>/execute ... run &lt;command&gt;</c> runs a command the caller names, so what follows
    /// "run" counts too, however deep it nests: an /execute the config allows still can't wrap a blocked command.
    /// </summary>
    public bool IsBlocked(string line)
    {
        var text = (line ?? "").Trim();
        if (_o.Allowed is { Length: > 0 } && NameOf(text) is { } first && !InList(_o.Allowed, first) && !InList(_o.AlwaysAllowed, first))
            return true; // an allowlist is set and this command isn't on it
        for (int depth = 0; depth < 16; depth++)
        {
            var name = NameOf(text);
            if (name == null) return false;
            if (InList(_o.Blocked, name)) return true;
            if (!string.Equals(name, "/execute", StringComparison.Ordinal)) return false;
            var m = RunKeyword.Match(text);
            if (!m.Success) return false;
            text = "/" + text[(m.Index + m.Length)..].TrimStart('/', ' ').Trim();
        }
        return true; // absurdly nested: refuse rather than guess
    }

    private bool IsTrusted(string? player) =>
        player != null && _trusted().Any(p => string.Equals(p, player, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- inbound

    /// <summary>
    /// Records the commands in a chat line as one-shot authorisations, if (and only if) it's a public <c>&lt;name&gt;</c> line
    /// or a whisper from a trusted player, this bot handles it (<paramref name="mine"/>: the hive arbiter's verdict) and it
    /// addresses this bot (<paramref name="explicitAddress"/> by name, or a whisper). Emotes, system lines, "X says:" claims
    /// and memory never get here as authorisations. Returns how many commands were recorded.
    /// </summary>
    public int Observe(ChatMessage msg, bool mine, bool explicitAddress)
    {
        if (msg.Player == null || !IsTrusted(msg.Player)) return 0;
        if (!mine)
        {
            // They've moved on (talking to another bot now): their open request with this one is over, so the next exact
            // command addressed elsewhere can't ride it (one body, one request at a time).
            lock (_lock) _requests.RemoveAll(r => string.Equals(r.Player, msg.Player, StringComparison.OrdinalIgnoreCase));
            return 0;
        }
        if (msg.Kind == ChatKind.Whisper) { /* the whisper format itself is the proof */ }
        else if (msg.Kind != ChatKind.Public || !explicitAddress || !IsOwnLine(msg)) return 0;
        var now = _time.GetUtcNow();
        int n = 0;
        lock (_lock)
        {
            Prune(now);
            // An example /command doesn't cancel the request in plain words ("teleport him: /tp it to ...").
            // Keep both paths; Authorize still enforces the allowlist and blocklist before either grant is used.
            if (_o.RequestSeconds > 0 && msg.Text.Trim().Length > 0)
                _requests.Add(new Request(msg.Player, msg.Text.Trim(), now));
            foreach (Match m in CommandInText.Matches(msg.Text))
            {
                var cmd = m.Groups["cmd"].Value.Trim();
                if (cmd.Length < 2) continue;
                _grants.Add(new Grant(cmd, msg.Player, now));
                n++;
            }
        }
        if (n > 0) _log.LogInformation("command authorisation from {Player}: {Text}", msg.Player, msg.Text);
        return n;
    }

    /// <summary>
    /// A trusted player's line from an authenticated surface outside the game (the web viewer, with its request and the host
    /// config both allowing commands) that this bot took: records its request or exact commands like <see cref="Observe"/>, but
    /// there is no server framing to check, so the caller vouches for the player. Grants and audits read "player (origin)".
    /// Never clears the player's in-game requests.
    /// </summary>
    public int ObserveRemote(string player, string text, string origin)
    {
        if (!IsTrusted(player) || string.IsNullOrWhiteSpace(text)) return 0;
        var now = _time.GetUtcNow();
        var by = $"{player} ({origin})";
        int n = 0;
        lock (_lock)
        {
            Prune(now);
            if (_o.RequestSeconds > 0 && !CommandInText.IsMatch(text))
                _requests.Add(new Request(player, text.Trim(), now) { Via = origin });
            foreach (Match m in CommandInText.Matches(text))
            {
                var cmd = m.Groups["cmd"].Value.Trim();
                if (cmd.Length < 2) continue;
                _grants.Add(new Grant(cmd, by, now));
                n++;
            }
        }
        if (n > 0) _log.LogInformation("command authorisation from {Player}: {Text}", by, text);
        return n;
    }

    /// <summary>The raw line really is the vanilla "&lt;name&gt; msg" form for this player (not "[x] name: msg" or a server notice).</summary>
    private static bool IsOwnLine(ChatMessage msg)
    {
        var line = ChatText.StripColors(msg.Raw ?? "").Trim();
        var m = Regex.Match(line, @"^<(?<name>[^<>]{1,40})> ");
        if (!m.Success) return false;
        var tokens = Regex.Matches(m.Groups["name"].Value, "[A-Za-z0-9_]{1,16}");
        return tokens.Count > 0 && string.Equals(tokens[^1].Value, msg.Player, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- run_command

    /// <summary>
    /// The model asks to run <paramref name="command"/>. Ok only for an exact, unexpired, unused authorisation (or an
    /// always-allowed command); the matching grant is consumed and the line is armed for exactly one send.
    /// </summary>
    public CommandDecision Authorize(string? command)
    {
        var cmd = (command ?? "").Trim();
        var name = NameOf(cmd);
        if (name == null) return new(false, "refused: not a /command");
        if (cmd.Length > 100) return new(false, "refused: too long");
        if (IsBlocked(cmd))
            return new(false, $"refused: {name} is blocked (Agent:Commands:Blocked in the config; only the owner can change that)");
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            Prune(now);
            if (IsAlwaysAllowed(cmd))
            {
                Arm(cmd, now);
                return new(true, "ok", null);
            }
            int i = _grants.FindIndex(g => string.Equals(g.Command, cmd, StringComparison.Ordinal));
            if (i < 0)
            {
                // A trusted player asked this bot for something just now: the bot may write the commands it takes.
                var req = _requests.LastOrDefault(r => r.Used < Math.Max(1, _o.RequestCommands));
                if (req != null)
                {
                    req.Used++;
                    Arm(cmd, now);
                    _log.LogInformation("run_command {Command} for {Player}'s request: {Text}", cmd, req.By, req.Text);
                    return new(true, "ok", $"{req.By} (asked: \"{(req.Text.Length > 80 ? req.Text[..80] + "…" : req.Text)}\")");
                }
                _log.LogWarning("run_command refused (no trusted request): {Command}", cmd);
                return new(false, $"refused: no trusted player asked you for this in the last {Math.Max(_o.RequestSeconds, (int)Window.TotalSeconds)}s");
            }
            var g = _grants[i];
            _grants.RemoveAt(i); // one authorisation = one execution
            Arm(cmd, now);
            return new(true, "ok", g.Player);
        }
    }

    private void Arm(string line, DateTimeOffset now) => _armed.Add((line, now + TimeSpan.FromSeconds(15)));

    private void Prune(DateTimeOffset now)
    {
        _grants.RemoveAll(g => now - g.At > Window);
        _requests.RemoveAll(r => now - r.At > TimeSpan.FromSeconds(Math.Max(1, _o.RequestSeconds)));
        _armed.RemoveAll(a => a.Until < now);
    }

    // ---------------------------------------------------------------- outgoing choke point

    /// <summary>
    /// Final check on every outgoing chat line: non-commands pass; always-allowed commands pass; any other '/' line passes
    /// only if <see cref="Authorize"/> armed exactly that line (consumed here). Everything else is dropped and logged.
    /// </summary>
    public bool AllowOutgoing(string line)
    {
        var t = (line ?? "").TrimStart();
        if (t.Length == 0 || t[0] != '/') return true;
        if (TakeManagedWhitelist(t.Trim()) || TakeStoryArmed(t.Trim())) return true;
        if (IsAlwaysAllowed(t) && !IsBlocked(t)) return true;
        lock (_lock)
        {
            Prune(_time.GetUtcNow());
            int i = _armed.FindIndex(a => string.Equals(a.Line, t.Trim(), StringComparison.Ordinal));
            if (i >= 0 && !IsBlocked(t))
            {
                _armed.RemoveAt(i);
                return true;
            }
        }
        _log.LogWarning("dropped outgoing command (not authorised): {Line}", t);
        return false;
    }

    // ---------------------------------------------------------------- the hive's scoped /whitelist exception

    private static readonly Regex ManagedWhitelistLine = new(@"^/whitelist (?<op>add|remove) (?<name>[A-Za-z0-9_]{3,16})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly List<(string Line, DateTimeOffset Until)> _hiveArmed = new();

    /// <summary>
    /// Main only: the accounts the hive manages (its persisted list of clone accounts it created). Null = no exception at all
    /// (clone processes, no hive). <c>/whitelist</c> stays blocked for the model (<see cref="Authorize"/>); this path is C# only.
    /// </summary>
    public Func<string, bool>? ManagedAccount { get; set; }

    /// <summary>
    /// Arms exactly one <c>/whitelist add|remove &lt;name&gt;</c> for the hive's account manager: only that exact shape, only a name
    /// <see cref="ManagedAccount"/> accepts. Anything else (another subcommand, another name, a second command) is refused.
    /// </summary>
    public bool ArmManagedWhitelist(string line)
    {
        var t = (line ?? "").Trim();
        var m = ManagedWhitelistLine.Match(t);
        if (!m.Success || ManagedAccount is not { } managed || !managed(m.Groups["name"].Value))
        {
            _log.LogWarning("hive whitelist command refused (not a managed account): {Line}", t);
            return false;
        }
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _hiveArmed.RemoveAll(a => a.Until < now);
            _hiveArmed.Add((t, now + TimeSpan.FromSeconds(15)));
        }
        return true;
    }

    private bool TakeManagedWhitelist(string line)
    {
        if (!line.StartsWith("/whitelist ", StringComparison.OrdinalIgnoreCase)) return false;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _hiveArmed.RemoveAll(a => a.Until < now);
            int i = _hiveArmed.FindIndex(a => string.Equals(a.Line, line, StringComparison.Ordinal));
            if (i < 0) return false;
            _hiveArmed.RemoveAt(i);
        }
        Audit(line, "hive account manager");
        return true;
    }

    // ---------------------------------------------------------------- Story Mode's scoped /tp exception

    private static readonly Regex ManagedTeleportLine = new(@"^/tp (?<name>[A-Za-z0-9_]{3,16}) -?\d{1,8} -?\d{1,3} -?\d{1,8}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly List<(string Line, string By, DateTimeOffset Until)> _tpArmed = new();
    /// <summary>How long an armed Story line waits to be sent: main's chat queue paces commands, and 15 s ran out behind a queue of
    /// them (live 11-04: a rescue /tp was "dropped (not authorised)" when it finally went out).</summary>
    public static readonly TimeSpan StoryArmWindow = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Arms exactly one <c>/tp &lt;name&gt; x y z</c> for Story Mode: the colony owner founded the colony or moved its home (an
    /// authenticated request on the viewer), so its pawns are brought there. Only that exact shape (whole-number coordinates, one
    /// account), only an account <see cref="ManagedAccount"/> accepts (the hive's own <c>maid_</c> bodies: never a player, never main),
    /// and never when the config blocks <c>/tp</c>. C# only: the model can't reach it. <paramref name="by"/> goes to the audit.
    /// </summary>
    public bool ArmManagedTeleport(string line, string by)
    {
        var t = (line ?? "").Trim();
        var m = ManagedTeleportLine.Match(t);
        if (!m.Success || IsBlocked(t) || ManagedAccount is not { } managed || !managed(m.Groups["name"].Value))
        {
            _log.LogWarning("story teleport refused (not a managed account, or /tp blocked): {Line}", t);
            return false;
        }
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _tpArmed.RemoveAll(a => a.Until < now);
            _tpArmed.Add((t, by, now + StoryArmWindow));
        }
        return true;
    }

    private static readonly Regex StoryTimeLine = new(@"^/time set \d{1,5}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Arms exactly one <c>/time set &lt;n&gt;</c> for Story Mode's time slider (the authenticated story-role owner's request on the
    /// viewer). Only that shape, never when the config blocks <c>/time</c>. C# only; <paramref name="by"/> goes to the audit.
    /// </summary>
    public bool ArmStoryTime(string line, string by)
    {
        var t = (line ?? "").Trim();
        if (!StoryTimeLine.IsMatch(t) || IsBlocked(t))
        {
            _log.LogWarning("story time command refused (wrong shape, or /time blocked): {Line}", t);
            return false;
        }
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _tpArmed.RemoveAll(a => a.Until < now);
            _tpArmed.Add((t, by, now + StoryArmWindow));
        }
        return true;
    }

    private static readonly Regex ManagedSpeedLine = new(@"^/effect (?<name>[A-Za-z0-9_]{3,16}) minecraft:speed (?:0|\d{1,7} [0-4] true)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Arms exactly one <c>/effect &lt;name&gt; minecraft:speed &lt;seconds&gt; &lt;amplifier&gt; true</c> (or <c>… minecraft:speed 0</c>, which
    /// clears it) for Story Mode's Fast / Faster: the colony owner chose the speed on the viewer, so the server gives the colony's pawns a
    /// Speed effect. Only that shape, only an account <see cref="ManagedAccount"/> accepts (the hive's own bodies: never a player, never
    /// main), never when the config blocks <c>/effect</c>. C# only; <paramref name="by"/> goes to the audit.
    /// </summary>
    public bool ArmManagedSpeed(string line, string by)
    {
        var t = (line ?? "").Trim();
        var m = ManagedSpeedLine.Match(t);
        if (!m.Success || IsBlocked(t) || ManagedAccount is not { } managed || !managed(m.Groups["name"].Value))
        {
            _log.LogWarning("story speed effect refused (not a managed account, wrong shape, or /effect blocked): {Line}", t);
            return false;
        }
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _tpArmed.RemoveAll(a => a.Until < now);
            _tpArmed.Add((t, by, now + StoryArmWindow));
        }
        return true;
    }

    private bool TakeStoryArmed(string line)
    {
        if (!line.StartsWith("/tp ", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("/time ", StringComparison.OrdinalIgnoreCase)
            && !line.StartsWith("/effect ", StringComparison.OrdinalIgnoreCase)) return false;
        string by;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _tpArmed.RemoveAll(a => a.Until < now);
            int i = _tpArmed.FindIndex(a => string.Equals(a.Line, line, StringComparison.Ordinal));
            if (i < 0 || IsBlocked(line)) return false;
            by = _tpArmed[i].By;
            _tpArmed.RemoveAt(i);
        }
        Audit(line, by);
        return true;
    }

    /// <summary>Logs an executed command to the regular log and the audit file.</summary>
    public void Audit(string command, string? authorizedBy)
    {
        var at = _time.GetUtcNow();
        var by = authorizedBy ?? "(always allowed)";
        _log.LogWarning("COMMAND executed: {Command} (authorised by {By} at {At:O})", command, by, at);
        if (!string.IsNullOrWhiteSpace(_auditPath))
        {
            try
            {
                var line = JsonSerializer.Serialize(new { at = at.ToString("O"), command, authorized_by = by });
                lock (_lock) File.AppendAllText(_auditPath, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogWarning("command audit write failed: {Error}", ex.Message); }
        }
        try { Executed?.Invoke(command, by, at); } catch (Exception) { }
    }
}
