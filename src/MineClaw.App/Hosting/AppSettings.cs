using MineClaw.Agent;
using MineClaw.Agent.Telemetry;
using MineClaw.Bot;
using MineClaw.Hive;
using Microsoft.Extensions.Configuration;

namespace MineClaw.App.Hosting;

/// <summary>
/// Every options object one bot runs with, bound from the layered config (<see cref="AppConfig"/>). Bound afresh per call, so main
/// and each hive running in main's process (<see cref="InProcHiveLauncher"/>) own separate objects: <see cref="CloneMode.Prepare"/>
/// and a brain profile (<c>LlmOptions.ApplyBrainProfile</c>) mutate a clone's copy and never main's.
/// </summary>
public sealed record AppSettings(MinecraftSettings Mc, AgentOptions Agent, BotOptions Bot, HostSettings Host, HiveConfig Hive,
    TelemetryOptions Telemetry, Supervisor.SupervisorOptions Supervisor, MineClaw.Bot.Bodies.BodiesOptions Bodies, string LogDir, string TelemetryDir)
{
    public static AppSettings Bind(IConfiguration config, string root)
    {
        var mc = config.GetSection("Minecraft").Get<MinecraftSettings>() ?? new MinecraftSettings();
        var agentOptions = new AgentOptions();
        config.GetSection("Agent").Bind(agentOptions);
        var botOptions = new BotOptions();
        config.GetSection("Bot").Bind(botOptions);
        botOptions.Vision.ModernClientJar ??= config["Render:Assets:Jar"]; // the 1.12.2 client jar for textures/models (read at runtime)
        config.GetSection("Scripting").Bind(botOptions.Scripts); // JS engine limits + the Lua migration flags ("Scripting:MigrateLua")
        var host = config.GetSection("Host").Get<HostSettings>() ?? new HostSettings();
        var hive = config.GetSection("Hive").Get<HiveConfig>() ?? new HiveConfig();
        config.GetSection("Hive").Bind(agentOptions.Hive); // one shared memory/self profile for every hive (HiveOptions.SharedSelf)
        // No fixed roster (docs/design/hive-hierarchy.md): old Hive:CloneUsernames are only adopted into the managed account list.
        hive.IgnoreLegacyRoster();
        agentOptions.Hive.IgnoreLegacyRoster();
        var telemetryOptions = config.GetSection("Telemetry").Get<TelemetryOptions>() ?? new TelemetryOptions();
        var supervisorOptions = config.GetSection("Supervisor").Get<Supervisor.SupervisorOptions>() ?? new Supervisor.SupervisorOptions();
        var bodiesOptions = config.GetSection("Bodies").Get<MineClaw.Bot.Bodies.BodiesOptions>() ?? new MineClaw.Bot.Bodies.BodiesOptions();
        if (agentOptions.Llm.TiersUseAnthropic && (string.IsNullOrWhiteSpace(agentOptions.Llm.ApiKey) || agentOptions.Llm.ApiKey.EndsWith("...")))
            agentOptions.Llm.ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(agentOptions.Llm.MockScriptPath))
            agentOptions.Llm.MockScriptPath = AppPaths.Under(root, agentOptions.Llm.MockScriptPath);
        agentOptions.DataDirectory = AppPaths.Under(root, agentOptions.DataDirectory);
        if (agentOptions.KnowledgeDirectory != null) agentOptions.KnowledgeDirectory = AppPaths.UnderOrBeside(root, agentOptions.KnowledgeDirectory);
        var logDir = AppPaths.Under(root, host.LogDirectory);
        var telemetryDir = string.IsNullOrWhiteSpace(telemetryOptions.Directory) ? logDir : AppPaths.Under(root, telemetryOptions.Directory);
        return new AppSettings(mc, agentOptions, botOptions, host, hive, telemetryOptions, supervisorOptions, bodiesOptions, logDir, telemetryDir);
    }

    /// <summary>
    /// The version decisions that follow the resolved profile (<see cref="VersionSetup"/>): b1.8.1's join throttle (one login at a
    /// time), the knowledge of this version only, the world mode from the db's packs. <paramref name="appliesWorldMode"/>: main's
    /// runtime sets the process's world mode; a hive in main's process reads it and never sets it again.
    /// </summary>
    public void ApplyVersion(MineClaw.Client.Versioning.GameVersionProfile profile, bool appliesWorldMode)
    {
        if (profile.Version == MineClaw.Protocol.Versioning.GameVersion.Beta181) Hive.LaunchBatch = Bodies.LaunchBatch = 1;
        Agent.GameVersion = profile.Version.Id; // knowledge docs of another version (b1.8.1/skyblock on 1.12.2) stay out
        Agent.ApplyWorldMode = appliesWorldMode;
    }
}
