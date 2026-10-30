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

    /// <summary>Never sent, even when a trusted player gives the exact command.</summary>
    public string[] Blocked { get; set; } =
        ["/stop", "/op", "/deop", "/ban", "/ban-ip", "/pardon", "/pardon-ip", "/whitelist", "/save-off", "/kick"];

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
        public int Used;
    }

    private static readonly Regex CommandInText = new(@"(?:^|[\s`'""(])(?<cmd>/[A-Za-z][^`'""\r\n]*)", RegexOptions.Compiled);

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

    /// <summary>"/time set day" → "/time" (lower case), or null for non-commands.</summary>
    public static string? NameOf(string line)
    {
        var t = (line ?? "").Trim();
        if (t.Length < 2 || t[0] != '/') return null;
        int sp = t.IndexOfAny([' ', '\t']);
        return (sp < 0 ? t : t[..sp]).ToLowerInvariant();
    }

    private static bool InList(string[] list, string? name) =>
        name != null && list.Any(c => string.Equals(c.Trim(), name, StringComparison.OrdinalIgnoreCase));

    public bool IsAlwaysAllowed(string line) => InList(_o.AlwaysAllowed, NameOf(line));
    public bool IsBlocked(string line) => InList(_o.Blocked, NameOf(line));

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
            // A request of theirs to this bot that names no command: the commands it takes may be written by the bot (Authorize,
            // RequestSeconds). A line that gives the exact command authorises just that one, as before.
            if (_o.RequestSeconds > 0 && msg.Text.Trim().Length > 0 && !CommandInText.IsMatch(msg.Text))
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
                    _log.LogInformation("run_command {Command} for {Player}'s request: {Text}", cmd, req.Player, req.Text);
                    return new(true, "ok", $"{req.Player} (asked: \"{(req.Text.Length > 80 ? req.Text[..80] + "…" : req.Text)}\")");
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
        if (TakeManagedWhitelist(t.Trim())) return true;
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
