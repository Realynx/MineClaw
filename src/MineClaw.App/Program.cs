// MineClaw host: loads config, checks the Anthropic key (free models call), connects the b1.8.1 client, runs the Claude
// agent with the game tools and shows the terminal UI (or headless line logs for systemd), reconnects on drops and shuts
// down gracefully on SIGTERM/SIGINT, q, /quit or --minutes. See AppCli.Usage for options and exit codes.

using MineClaw.App.Hosting;

try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch (Exception) { /* no console */ }
var cli = AppCli.Parse(args);
if (cli.PrintSchemaVersion && cli.Errors.Count == 0)
{
    Console.Out.WriteLine(MineClaw.Agent.Rag.AgentDb.LatestSchemaVersion);
    return ExitCodes.Ok;
}
if (cli.Help)
{
    Console.Out.WriteLine(AppCli.Usage);
    return ExitCodes.Ok;
}
if (cli.Errors.Count > 0)
{
    foreach (var e in cli.Errors) Console.Error.WriteLine("MineClaw: " + e);
    Console.Error.WriteLine(AppCli.Usage);
    return ExitCodes.Usage;
}

try
{
    if (cli.ResetMemory) return await MemoryReset.RunAsync(cli, Console.Out);
    return cli.Demo ? await AppHost.RunDemoAsync(cli) : await AppHost.RunAsync(cli);
}
catch (Exception ex)
{
    Console.Error.WriteLine("MineClaw crashed: " + ex);
    return ExitCodes.Error;
}
