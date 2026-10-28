using MineClaw.App.Viewport;
using Microsoft.Extensions.Configuration;

namespace MineClaw.App.Hosting;

/// <summary>Host settings ("Host" section).</summary>
public sealed class HostSettings
{
    /// <summary>Text logs + usage-*.jsonl (relative = under the root). CLI --log-dir, env MINECLAW_Host__LogDirectory.</summary>
    public string LogDirectory { get; set; } = "logs";
    /// <summary>Whole graceful-shutdown budget (stop actions, agent, pre-logout, disconnect, flush). Keep systemd TimeoutStopSec above it.</summary>
    public double ShutdownTimeoutSeconds { get; set; } = 30;
    /// <summary>Reserved inside the budget for the hive's "deposit items before logout" step (0 = none yet).</summary>
    public double PreLogoutSeconds { get; set; } = 0;
    /// <summary>auto | simple | systemd | json (auto = systemd under journald, else simple; colours only on a TTY).</summary>
    public string LogFormat { get; set; } = "auto";
    public TuiSettings Tui { get; set; } = new();
}

public sealed class TuiSettings
{
    /// <summary>Redraws per second.</summary>
    public double Fps { get; set; } = 4;
    public ViewportOptions Viewport { get; set; } = new();
}


/// <summary>
/// Layered configuration. Lowest to highest: appsettings.json next to the exe → src/MineClaw.App/appsettings.json in a
/// repo checkout → <c>--config</c> file (or env MINECLAW_CONFIG) → plain env vars (legacy, e.g. Agent__Llm__ApiKey) →
/// <c>MINECLAW_</c>-prefixed env vars (e.g. MINECLAW_Agent__Llm__ApiKey; what a systemd EnvironmentFile sets) → CLI paths.
/// </summary>
public static class AppConfig
{
    public static IConfigurationRoot Build(AppCli cli, string root, IDictionary<string, string?>? env = null)
    {
        var configPath = cli.ConfigPath ?? Get(env, "MINECLAW_CONFIG");
        if (configPath != null)
        {
            configPath = Path.GetFullPath(configPath);
            if (!File.Exists(configPath)) throw new FileNotFoundException($"config file not found: {configPath}", configPath);
        }
        var b = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(root, "src", "MineClaw.App", "appsettings.json"), optional: true);
        if (configPath != null) b.AddJsonFile(configPath, optional: false);
        if (env == null) b.AddEnvironmentVariables().AddEnvironmentVariables("MINECLAW_");
        else b.AddInMemoryCollection(Prefixed(env));
        var overrides = new Dictionary<string, string?>();
        if (cli.DataDir != null) overrides["Agent:DataDirectory"] = cli.DataDir;
        if (cli.LogDir != null) overrides["Host:LogDirectory"] = cli.LogDir;
        if (cli.LogFormat != null) overrides["Host:LogFormat"] = cli.LogFormat;
        if (cli.ServerVersion != null) overrides[VersionSetup.ConfigKey] = cli.ServerVersion;
        if (cli.ShutdownTimeoutSeconds is { } s) overrides["Host:ShutdownTimeoutSeconds"] = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
        b.AddInMemoryCollection(overrides);
        return b.Build();
    }

    /// <summary>Test seam: MINECLAW_-prefixed entries of a fake environment, mapped like the env provider does.</summary>
    private static IEnumerable<KeyValuePair<string, string?>> Prefixed(IDictionary<string, string?> env) =>
        env.Where(kv => kv.Key.StartsWith("MINECLAW_", StringComparison.OrdinalIgnoreCase))
           .Select(kv => new KeyValuePair<string, string?>(kv.Key["MINECLAW_".Length..].Replace("__", ":"), kv.Value));

    private static string? Get(IDictionary<string, string?>? env, string key) =>
        env != null ? (env.TryGetValue(key, out var v) ? v : null) : Environment.GetEnvironmentVariable(key);
}

public static class AppPaths
{
    /// <summary>Repo root (folder with MineClaw.sln) above the exe or the working directory; else the working directory.</summary>
    public static string FindRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "MineClaw.sln"))) return d.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    public static string Under(string root, string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(root, path));

    /// <summary>Like <see cref="Under"/>, but a relative path that doesn't exist under the root falls back to the exe folder
    /// (published builds ship docs/knowledge and seed/ next to the binary).</summary>
    public static string UnderOrBeside(string root, string path)
    {
        var p = Under(root, path);
        if (Path.IsPathRooted(path) || Directory.Exists(p) || File.Exists(p)) return p;
        var beside = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        return Directory.Exists(beside) || File.Exists(beside) ? beside : p;
    }
}

/// <summary>Decides between the TUI and headless line logs.</summary>
public static class TerminalDetect
{
    public sealed record Facts(bool OutputRedirected, bool InputRedirected, string? Term, bool UnderSystemd, bool SpectreInteractive);

    public static Facts Current() => new(
        Console.IsOutputRedirected, Console.IsInputRedirected, Environment.GetEnvironmentVariable("TERM"),
        UnderSystemd(), SafeInteractive());

    /// <summary>systemd sets INVOCATION_ID for services and JOURNAL_STREAM when stdout/stderr go to the journal.</summary>
    public static bool UnderSystemd() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JOURNAL_STREAM"));

    public static bool UnderJournal() => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JOURNAL_STREAM"));

    /// <summary>TUI only on a real, interactive terminal (both directions), never under systemd or TERM=dumb.</summary>
    public static (bool UseTui, string Why) Decide(TuiChoice choice, Facts f)
    {
        if (choice == TuiChoice.Off) return (false, "--no-tui");
        string? why = f.OutputRedirected ? "stdout is not a terminal"
            : f.InputRedirected ? "stdin is not a terminal"
            : f.UnderSystemd ? "running under systemd"
            : string.Equals(f.Term, "dumb", StringComparison.OrdinalIgnoreCase) ? "TERM=dumb"
            : !f.SpectreInteractive ? "terminal is not interactive"
            : null;
        if (why == null) return (true, choice == TuiChoice.On ? "--tui" : "interactive terminal");
        return choice == TuiChoice.On ? (true, "--tui (forced; " + why + ")") : (false, why);
    }

    private static bool SafeInteractive()
    {
        try { return Spectre.Console.AnsiConsole.Profile.Capabilities.Interactive; }
        catch (Exception) { return false; }
    }
}
