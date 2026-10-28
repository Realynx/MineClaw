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
  --no-key-check           skip the Anthropic models check at startup
  --demo [--demo-bots n]   offline TUI demo with fake bots (no server, no API key)
  --frame <WxH>            print one ANSI TUI frame and exit (use with --demo; NO_COLOR=1 for plain text)
  --bot <n>                initially selected bot (1-5)   --view top|side|iso (default iso on 24-bit terminals)
  --clone <name> --parent-pipe <pipe>   hive clone mode (started by the main process; headless)
Env: MINECLAW_<Section>__<Key> overrides config, e.g. MINECLAW_Agent__Llm__ApiKey.
Exit codes: 0 clean, 1 crash, 2 config, 3 auth, 64 usage, 75 transient startup failure.
""";

    public static AppCli Parse(string[] args)
    {
        var errors = new List<string>();
        var c = new AppCli();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i], name = a;
            string? inline = null;
            int eq = a.IndexOf('=');
            if (a.StartsWith("--") && eq > 2) { name = a[..eq]; inline = a[(eq + 1)..]; }

            string? Value()
            {
                if (inline != null) return inline;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) return args[++i];
                errors.Add($"{name} needs a value");
                return null;
            }
            double? Number()
            {
                var v = Value();
                if (v == null) return null;
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0) return d;
                errors.Add($"{name}: '{v}' is not a number");
                return null;
            }

            switch (name)
            {
                case "--script": c = c with { Script = Value() }; break;
                case "--minutes": c = c with { Minutes = Number() }; break;
                case "--void-abort": c = c with { VoidAbort = true }; break;
                case "--record": c = c with { Record = inline ?? (i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : Path.Combine("logs", "recordings")) }; break;
                case "--no-key-check": c = c with { NoKeyCheck = true }; break;
                case "--no-tui": c = c with { Tui = TuiChoice.Off }; break;
                case "--tui": c = c with { Tui = TuiChoice.On }; break;
                case "--stdin-commands": c = c with { StdinCommands = true }; break;
                case "--demo": c = c with { Demo = true }; break;
                case "--demo-bots": c = c with { DemoBots = Math.Clamp((int)(Number() ?? 3), 1, 5) }; break;
                case "--frame":
                    {
                        var v = inline ?? (i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "120x40");
                        var parts = v.ToLowerInvariant().Split('x');
                        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w >= 20 && h >= 10)
                            c = c with { Frame = (w, h) };
                        else errors.Add($"--frame: '{v}' should look like 120x40");
                        break;
                    }
                case "--bot": c = c with { Bot = Math.Max(1, (int)(Number() ?? 1)) }; break;
                case "--view": c = c with { ViewMode = Value() }; break;
                case "--data-dir": c = c with { DataDir = Value() }; break;
                case "--log-dir": c = c with { LogDir = Value() }; break;
                case "--config": c = c with { ConfigPath = Value() }; break;
                case "--shutdown-timeout": c = c with { ShutdownTimeoutSeconds = Number() }; break;
                case "--log-format":
                    {
                        var v = Value()?.ToLowerInvariant();
                        if (v is "auto" or "simple" or "systemd" or "json") c = c with { LogFormat = v };
                        else if (v != null) errors.Add($"--log-format: '{v}' (auto|simple|systemd|json)");
                        break;
                    }
                case "--clone": c = c with { CloneName = Value() }; break;
                case "--parent-pipe": c = c with { ParentPipe = Value() }; break;
                case "-h" or "--help" or "-?": c = c with { Help = true }; break;
                case "--schema-version": c = c with { PrintSchemaVersion = true }; break;
                default: errors.Add($"unknown option '{a}'"); break;
            }
        }
        if (c.Frame != null && !c.Demo) errors.Add("--frame needs --demo");
        if ((c.CloneName == null) != (c.ParentPipe == null)) errors.Add("--clone and --parent-pipe go together (clones never run unsupervised)");
        return c with { Errors = errors };
    }
}
