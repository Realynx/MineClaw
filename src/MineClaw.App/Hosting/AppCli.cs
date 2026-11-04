using System.Globalization;

namespace MineClaw.App.Hosting;

public enum TuiChoice { Auto, On, Off }

/// <summary>Process exit codes (systemd: Restart=on-failure, RestartPreventExitStatus=2 3 64).</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    /// <summary>Unexpected crash.</summary>
    public const int Error = 1;
    /// <summary>Config missing/invalid: won't fix itself, don't restart-loop.</summary>
    public const int Config = 2;
    /// <summary>API key rejected / model id unknown.</summary>
    public const int Auth = 3;
    /// <summary>Bad command line (EX_USAGE).</summary>
    public const int Usage = 64;
    /// <summary>Transient failure at startup (e.g. API unreachable during the key check): systemd retries (EX_TEMPFAIL).</summary>
    public const int TempFail = 75;
}

/// <summary>Command line of MineClaw.App. <c>--opt value</c> and <c>--opt=value</c> both work.</summary>
public sealed record AppCli
{
    public string? Script { get; init; }
    public double? Minutes { get; init; }
    public bool VoidAbort { get; init; }
    public bool NoKeyCheck { get; init; }
    public TuiChoice Tui { get; init; } = TuiChoice.Auto;
    /// <summary>Headless: read console commands from stdin (off by default so a service never blocks on stdin).</summary>
    public bool StdinCommands { get; init; }
    public bool Demo { get; init; }
    public int DemoBots { get; init; } = 3;
    /// <summary>Print one rendered TUI frame as plain text and exit (with --demo: no server, no key).</summary>
    public (int Width, int Height)? Frame { get; init; }
    /// <summary>1-based bot to select initially (TUI / --frame).</summary>
    public int Bot { get; init; } = 1;
    public string? ViewMode { get; init; }
    public string? DataDir { get; init; }
    public string? LogDir { get; init; }
    public string? ConfigPath { get; init; }
    public double? ShutdownTimeoutSeconds { get; init; }
    /// <summary>auto | simple | systemd | json.</summary>
    public string? LogFormat { get; init; }
    public bool Help { get; init; }
    /// <summary>Print the db schema version this build opens (AgentDb migrations) and exit: deploy/ct-release.sh records it
    /// per release so a rollback never starts a build older than the live db (live 10-27: crash loop on schema v6).</summary>
    public bool PrintSchemaVersion { get; init; }
    /// <summary>Hive clone mode (started by the main process's supervisor): this process logs in as that clone, headless.</summary>
    public string? CloneName { get; init; }
    /// <summary>Hive clone mode: the supervisor's named pipe (no pipe = no clone: clones never run unsupervised).</summary>
    public string? ParentPipe { get; init; }
    /// <summary>Record every packet into this directory (<c>--record [dir]</c>; default logs/recordings).</summary>
    public string? Record { get; init; }
    /// <summary>auto | b1.8.1 | 1.12.2: overrides Bot:Server:Version (main passes the resolved id to its hive clones).</summary>
    public string? ServerVersion { get; init; }
    /// <summary>The one-shot fresh-world memory reset (<see cref="MemoryReset"/>): back up the data dir, fresh db, import the packs, exit.</summary>
    public bool ResetMemory { get; init; }
    /// <summary>--pack: the version pack the reset imports (e.g. shards/defaults/1.12.2-gamedata.mcshard).</summary>
    public string? ResetPack { get; init; }
    /// <summary>--modes: mode packs to add after the reset ("none" = a plain world, the default).</summary>
    public IReadOnlyList<string> ResetModes { get; init; } = [];
    /// <summary>--no-sdk: don't add the sdk-js pack found beside the version pack.</summary>
    public bool ResetNoSdk { get; init; }
    /// <summary>--backup-dir: where the reset backs up the data dir (default: a timestamped sibling of it).</summary>
    public string? ResetBackupDir { get; init; }
    /// <summary>--yes: confirms a destructive one-shot command (the reset refuses without it).</summary>
    public bool Yes { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    public const string Usage = """
Usage: MineClaw.App [options]
  --no-tui                 headless: line logs to stdout (also automatic when stdout/stdin isn't a terminal)
  --tui                    force the terminal UI
  --stdin-commands         headless: also read console commands from stdin
  --config <file>          extra appsettings JSON layered over the defaults (env MINECLAW_CONFIG)
  --schema-version         print the db schema version this build opens and exit (deploy rollback guard)
  --data-dir <dir>         data directory (db, usage.json)        [Agent:DataDirectory]
  --log-dir <dir>          log + usage-*.jsonl directory          [Host:LogDirectory]
  --log-format <f>         auto | simple | systemd | json         [Host:LogFormat]
  --shutdown-timeout <s>   graceful shutdown budget in seconds    [Host:ShutdownTimeoutSeconds]
  --script <file>          run console commands from a file ("/wait N" pauses)
  --minutes <n>            quit after n minutes
  --void-abort             quit if the bot is ever over the void (test runs)
  --record [dir]           record all packets (default logs/recordings; env MINECLAW_RECORD=1)
  --server-version <v>     auto | b1.8.1 | 1.12.2 (auto pings the server)   [Bot:Server:Version]
  --no-key-check           skip the Anthropic models check at startup
  --demo [--demo-bots n]   offline TUI demo with fake bots (no server, no API key)
  --frame <WxH>            print one ANSI TUI frame and exit (use with --demo; NO_COLOR=1 for plain text)
  --bot <n>                initially selected bot (1-5)   --view top|side|iso (default iso on 24-bit terminals)
  --clone <name> --parent-pipe <pipe>   hive clone mode (started by the main process; headless)
  --reset-memory --pack <file> [--modes none|a.mcshard,b.mcshard] [--no-sdk] [--backup-dir <dir>] --yes
                           fresh-world reset (service stopped!): back up the whole data dir to <data>-backup-<utc>, fresh db with
                           only the version pack (+ modes, + sdk-js.mcshard beside it), server-version.txt := the pack's version;
                           usage.json and config are kept. Without --yes it prints the plan and changes nothing.
Env: MINECLAW_<Section>__<Key> overrides config, e.g. MINECLAW_Agent__Llm__ApiKey.
Exit codes: 0 clean, 1 crash, 2 config, 3 auth, 64 usage, 75 transient startup failure.
""";

    public static AppCli Parse(string[] args)
    {
        var errors = new List<string>();
        var c = new AppCli();
        var cursor = new ArgCursor(args, errors);
        while (cursor.Next())
        {
            if (Options.TryGetValue(cursor.Name, out var apply)) c = apply(c, cursor);
            else errors.Add($"unknown option '{cursor.Arg}'");
        }
        CheckCombinations(c, errors);
        CheckReset(c, errors);
        return c with { Errors = errors };
    }

    /// <summary>One option: the parsed command line so far and the cursor on the option → the command line with it applied.</summary>
    private delegate AppCli Option(AppCli c, ArgCursor a);

    private static readonly IReadOnlyDictionary<string, Option> Options = new Dictionary<string, Option>(StringComparer.Ordinal)
    {
        ["--script"] = (c, a) => c with { Script = a.Value() },
        ["--minutes"] = (c, a) => c with { Minutes = a.Number() },
        ["--void-abort"] = (c, _) => c with { VoidAbort = true },
        ["--record"] = (c, a) => c with { Record = a.ValueOr(Path.Combine("logs", "recordings")) },
        ["--no-key-check"] = (c, _) => c with { NoKeyCheck = true },
        ["--no-tui"] = (c, _) => c with { Tui = TuiChoice.Off },
        ["--tui"] = (c, _) => c with { Tui = TuiChoice.On },
        ["--stdin-commands"] = (c, _) => c with { StdinCommands = true },
        ["--demo"] = (c, _) => c with { Demo = true },
        ["--demo-bots"] = (c, a) => c with { DemoBots = Math.Clamp((int)(a.Number() ?? 3), 1, 5) },
        ["--frame"] = FrameOption,
        ["--bot"] = (c, a) => c with { Bot = Math.Max(1, (int)(a.Number() ?? 1)) },
        ["--view"] = (c, a) => c with { ViewMode = a.Value() },
        ["--data-dir"] = (c, a) => c with { DataDir = a.Value() },
        ["--log-dir"] = (c, a) => c with { LogDir = a.Value() },
        ["--config"] = (c, a) => c with { ConfigPath = a.Value() },
        ["--shutdown-timeout"] = (c, a) => c with { ShutdownTimeoutSeconds = a.Number() },
        ["--log-format"] = LogFormatOption,
        ["--clone"] = (c, a) => c with { CloneName = a.Value() },
        ["--parent-pipe"] = (c, a) => c with { ParentPipe = a.Value() },
        ["--server-version"] = ServerVersionOption,
        ["-h"] = HelpOption,
        ["--help"] = HelpOption,
        ["-?"] = HelpOption,
        ["--schema-version"] = (c, _) => c with { PrintSchemaVersion = true },
        ["--reset-memory"] = (c, _) => c with { ResetMemory = true },
        ["--pack"] = (c, a) => c with { ResetPack = a.Value() },
        ["--modes"] = ModesOption,
        ["--no-sdk"] = (c, _) => c with { ResetNoSdk = true },
        ["--backup-dir"] = (c, a) => c with { ResetBackupDir = a.Value() },
        ["--yes"] = YesOption,
        ["-y"] = YesOption,
    };

    private static AppCli HelpOption(AppCli c, ArgCursor a) => c with { Help = true };
    private static AppCli YesOption(AppCli c, ArgCursor a) => c with { Yes = true };

    private static AppCli FrameOption(AppCli c, ArgCursor a)
    {
        var v = a.ValueOr("120x40");
        var parts = v.ToLowerInvariant().Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w >= 20 && h >= 10)
            return c with { Frame = (w, h) };
        a.Error($"--frame: '{v}' should look like 120x40");
        return c;
    }

    private static AppCli LogFormatOption(AppCli c, ArgCursor a)
    {
        var v = a.Value()?.ToLowerInvariant();
        if (v is "auto" or "simple" or "systemd" or "json") return c with { LogFormat = v };
        if (v != null) a.Error($"--log-format: '{v}' (auto|simple|systemd|json)");
        return c;
    }

    private static AppCli ServerVersionOption(AppCli c, ArgCursor a)
    {
        var v = a.Value();
        if (v == null) return c;
        if (MineClaw.Protocol.Versioning.GameVersion.TryParse(v, out _)) return c with { ServerVersion = v };
        a.Error($"--server-version: '{v}' (auto|{string.Join("|", MineClaw.Protocol.Versioning.GameVersion.Known.Select(k => k.Id))})");
        return c;
    }

    private static AppCli ModesOption(AppCli c, ArgCursor a)
    {
        var v = a.Value();
        if (v == null) return c;
        return c with
        {
            ResetModes = v.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? [] : v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
    }

    private static void CheckCombinations(AppCli c, List<string> errors)
    {
        if (c.Frame != null && !c.Demo) errors.Add("--frame needs --demo");
        if ((c.CloneName == null) != (c.ParentPipe == null)) errors.Add("--clone and --parent-pipe go together (clones never run unsupervised)");
    }

    private static void CheckReset(AppCli c, List<string> errors)
    {
        if (c.ResetMemory && string.IsNullOrWhiteSpace(c.ResetPack)) errors.Add("--reset-memory needs --pack <version pack .mcshard>");
        if (!c.ResetMemory && HasResetOptions(c)) errors.Add("--pack/--modes/--no-sdk/--backup-dir go with --reset-memory");
        if (c.ResetMemory && c.CloneName != null) errors.Add("--reset-memory runs alone, never as a hive clone");
    }

    private static bool HasResetOptions(AppCli c) => c.ResetPack != null || c.ResetModes.Count > 0 || c.ResetNoSdk || c.ResetBackupDir != null;

    /// <summary>Walks the arguments: <c>--opt value</c> and <c>--opt=value</c>; a value never starts with <c>--</c>.</summary>
    private sealed class ArgCursor(string[] args, List<string> errors)
    {
        private int _i = -1;
        private string? _inline;

        /// <summary>The whole argument (with any <c>=value</c>).</summary>
        public string Arg { get; private set; } = "";
        /// <summary>The option name (before an inline <c>=</c>).</summary>
        public string Name { get; private set; } = "";

        public bool Next()
        {
            if (++_i >= args.Length) return false;
            Arg = Name = args[_i];
            _inline = null;
            int eq = Arg.IndexOf('=');
            if (Arg.StartsWith("--") && eq > 2) { Name = Arg[..eq]; _inline = Arg[(eq + 1)..]; }
            return true;
        }

        /// <summary>The next argument when it isn't an option (consumed), else null.</summary>
        private string? Following() => _i + 1 < args.Length && !args[_i + 1].StartsWith("--") ? args[++_i] : null;

        /// <summary>The option's value; a missing one is an error.</summary>
        public string? Value()
        {
            if ((_inline ?? Following()) is { } v) return v;
            errors.Add($"{Name} needs a value");
            return null;
        }

        /// <summary>The option's value, or <paramref name="fallback"/> when it has none (not an error).</summary>
        public string ValueOr(string fallback) => _inline ?? Following() ?? fallback;

        /// <summary>A non-negative number value; a missing or bad one is an error.</summary>
        public double? Number()
        {
            var v = Value();
            if (v == null) return null;
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0) return d;
            errors.Add($"{Name}: '{v}' is not a number");
            return null;
        }

        public void Error(string message) => errors.Add(message);
    }
}
