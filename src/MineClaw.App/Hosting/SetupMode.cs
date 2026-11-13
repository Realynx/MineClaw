using MineClaw.App.Supervisor;
using MineClaw.App.Supervisor.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Hosting;

/// <summary>
/// First run = in-app: a fresh install whose config can't start a bot yet (no Anthropic key, no Minecraft host, a rejected key...)
/// waits for the web panel instead of exiting with <see cref="ExitCodes.Config"/>, which a service manager would leave dead. It logs
/// one line starting <c>setup mode:</c> and serves only the Administration settings routes (<see cref="SetupSupervisor"/>); saving
/// and restarting from the page ends the process with exit 0 and the service manager starts it again with the new settings.
/// Only main does this, and only with the supervisor on and the settings override layer available; anything else exits as before.
/// </summary>
public static class SetupMode
{
    public const string LogPrefix = "setup mode:";

    /// <summary>The problem setup mode names when the Anthropic check rejects the key (or a model id) at startup.</summary>
    public const string KeyRejected = "Agent:Llm:ApiKey was rejected by the Anthropic check";

    /// <summary>Whether this process waits for the web panel instead of exiting: main only (never a hive clone), Supervisor:Enabled,
    /// and the settings override layer the Administration page writes is on.</summary>
    public static bool Applies(AppCli cli, IConfigurationRoot config, SupervisorOptions supervisor) =>
        cli.CloneName == null && supervisor.Enabled && SettingsOverrideFile.Of(config) != null;

    /// <summary>The startup config check: its problems, and whether they wait in setup mode (never without problems).</summary>
    public static (IReadOnlyList<string> Problems, bool Setup) Check(AppCli cli, IConfigurationRoot config, AppSettings settings)
    {
        var problems = ConfigCheck.Validate(settings.Mc, settings.Agent);
        return (problems, problems.Count > 0 && Applies(cli, config, settings.Supervisor));
    }

    /// <summary>The one line a service's health check looks for.</summary>
    public static string Line(IEnumerable<string> problems) =>
        $"{LogPrefix} waiting for configuration from the web panel ({string.Join("; ", problems)})";

    /// <summary>Serves the setup supervisor until a shutdown (a signal or the page's restart): exit 0, or a config error when it
    /// can't listen.</summary>
    public static async Task<int> RunAsync(IConfigurationRoot config, AppSettings settings, IReadOnlyList<string> problems, bool useTui, ILogger log,
        ShutdownSignals signals)
    {
        var line = Line(problems);
        log.LogWarning("{SetupLine}", line);
        if (useTui) Console.Error.WriteLine("MineClaw " + line);
        var o = settings.Supervisor;
        if (!o.AllowAdminWrites || string.IsNullOrEmpty(o.Token))
            log.LogWarning("{Prefix} the web panel can't save settings until Supervisor:AllowAdminWrites is true and Supervisor:Token is set", LogPrefix);
        if (SetupSupervisor.Create(config, settings, signals.Request, log) is not { } server)
        {
            log.LogCritical("{Prefix} the settings override file is off, nothing can be configured from the web panel", LogPrefix);
            return ExitCodes.Config;
        }
        try { server.Start(); }
        catch (Exception ex)
        {
            log.LogCritical("{Prefix} can't listen on {Bind}:{Port} ({Error})", LogPrefix, o.Bind, o.Port, ex.Message);
            await server.DisposeAsync().ConfigureAwait(false);
            return ExitCodes.Config;
        }
        try { await Task.Delay(Timeout.Infinite, signals.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        log.LogInformation("{Prefix} stopping ({Reason})", LogPrefix, signals.Reason ?? "shutdown");
        await server.DisposeAsync().ConfigureAwait(false);
        return ExitCodes.Ok;
    }
}
