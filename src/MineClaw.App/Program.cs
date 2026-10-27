// MineClaw console host: loads config, checks the Anthropic key (free models call), connects the b1.8.1 client,
// runs the Claude agent with the game tools, reconnects on drops and shuts down cleanly on Ctrl+C or /quit.
//
// Usage: MineClaw.App [--script file] [--minutes N] [--void-abort] [--no-key-check]
//   --script   run console commands from a file (one per line; "/wait N" pauses N seconds), then keep reading stdin
//   --minutes  quit automatically after N minutes (for supervised test runs)
//   --void-abort  disconnect and quit if the bot is ever over the void (test runs)

using System.Text.Json;
using MineClaw.Agent;
using MineClaw.App;
using MineClaw.Bot;
using MineClaw.Bot.Game;
using MineClaw.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

var cli = CommandLine.Parse(args);
var root = Paths.FindRoot();

// ---------------------------------------------------------------- config
IConfigurationRoot config;
try
{
    config = new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
        .AddJsonFile(Path.Combine(root, "src", "MineClaw.App", "appsettings.json"), optional: true)
        .AddEnvironmentVariables()
        .Build();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not read appsettings.json: {ex.Message}");
    return 2;
}

var mc = config.GetSection("Minecraft").Get<MinecraftSettings>() ?? new MinecraftSettings();
var agentOptions = new AgentOptions();
config.GetSection("Agent").Bind(agentOptions);
var botOptions = new BotOptions();
config.GetSection("Bot").Bind(botOptions);
if (string.IsNullOrWhiteSpace(agentOptions.Llm.ApiKey) || agentOptions.Llm.ApiKey.EndsWith("..."))
    agentOptions.Llm.ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
agentOptions.DataDirectory = Paths.Under(root, agentOptions.DataDirectory);
if (agentOptions.KnowledgeDirectory != null) agentOptions.KnowledgeDirectory = Paths.Under(root, agentOptions.KnowledgeDirectory);
Directory.CreateDirectory(agentOptions.DataDirectory);

var problems = ConfigCheck.Validate(mc, agentOptions);
if (problems.Count > 0)
{
    Console.Error.WriteLine("MineClaw can't start, the config needs attention:");
    foreach (var p in problems) Console.Error.WriteLine("  - " + p);
    Console.Error.WriteLine($"Edit src/MineClaw.App/appsettings.json (copy appsettings.example.json to start), or set env vars like Agent__Llm__ApiKey.");
    return 2;
}

// ---------------------------------------------------------------- logging
var fileLog = new FileLoggerProvider(Path.Combine(root, "logs"));
using var loggers = LoggerFactory.Create(b =>
{
    b.SetMinimumLevel(LogLevel.Debug);
    b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
    b.AddFilter<ConsoleLoggerProvider>((cat, lvl) => lvl >= (cat?.StartsWith("MineClaw.Client") == true ? LogLevel.Warning : LogLevel.Information));
    b.AddProvider(fileLog);
});
var log = loggers.CreateLogger("MineClaw.App");
log.LogInformation("MineClaw starting (root {Root}, data {Data}, log {Log})", root, agentOptions.DataDirectory, Path.Combine(root, "logs"));

// ---------------------------------------------------------------- key check (models API: free)
if (!cli.NoKeyCheck)
{
    var check = await KeyCheck.RunAsync(agentOptions.Llm.ApiKey!, [agentOptions.Llm.CheapModel, agentOptions.Llm.MainModel, agentOptions.Llm.WorkerModel]);
    if (!check.Ok)
    {
        Console.Error.WriteLine("Anthropic API check failed: " + check.Message);
        return 3;
    }
    log.LogInformation("Anthropic key ok: {Models}", check.Message);
}

// ---------------------------------------------------------------- client + bot
var clientLog = loggers.CreateLogger("MineClaw.Client");
await using var client = new MinecraftClient(new ClientOptions
{
    Host = mc.Host, Port = mc.Port, Username = mc.Username,
    AutoReconnect = true, AutoRespawn = true,
    Log = m => clientLog.LogDebug("{Msg}", m),
});
var game = new MinecraftGameClient(client);
using var bot = BotHost.Create(game, agentOptions, botOptions, loggers: loggers);
var rt = bot.Runtime;
var toolChars = rt.Tools.Definitions().Sum(d => d.Name.Length + d.Description.Length + d.InputSchema.GetRawText().Length);
log.LogInformation("{Tools} tools (~{Tok} tokens of definitions), knowledge {K} chars{Trunc}", rt.Tools.All.Count, (int)Math.Ceiling(toolChars / 3.5),
    rt.KnowledgeChars, rt.KnowledgeTruncated ? " (TRUNCATED)" : "");
rt.Agent.EpisodeCompleted += o => log.LogInformation("episode: {Tier} {Calls} call(s) ${Cost:0.0000} [{Usage}] said: {Said}{Skip}",
    o.Tier?.ToString() ?? "-", o.Calls, o.CostUsd, o.Usage, string.Join(" | ", o.Said), o.Skipped != null ? " skipped: " + o.Skipped : "");
rt.Usage.ModeChanged += (mode, why) => log.LogWarning("budget mode {Mode}: {Why}", mode, why);
client.ChatReceived += line => log.LogInformation("chat: {Line}", ChatText.StripColours(line));
client.Kicked += r => log.LogWarning("kicked: {Reason}", r);
client.Disconnected += r => log.LogWarning("disconnected: {Reason}", r);
client.Danger += d => log.LogWarning("danger {Kind}: {Msg}", d.Kind, d.Message);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; log.LogInformation("Ctrl+C: shutting down"); shutdown.Cancel(); };
if (cli.Minutes is { } minutes) shutdown.CancelAfter(TimeSpan.FromMinutes(minutes));

var agent = new PausableAgent(rt, log);
var commands = new ConsoleCommands(game, rt, agent, log, shutdown);

// Connect (retry with backoff until it works or we're told to stop; the client reconnects by itself afterwards).
var delay = TimeSpan.FromSeconds(3);
while (!shutdown.IsCancellationRequested)
{
    try
    {
        log.LogInformation("connecting to {Host}:{Port} as {User}", mc.Host, mc.Port, mc.Username);
        await client.ConnectAsync(shutdown.Token);
        log.LogInformation("in game: {Status}", client.DescribeStatus());
        break;
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
    catch (Exception ex)
    {
        log.LogWarning("connect failed: {Error}; retrying in {Delay}s", ex.Message, delay.TotalSeconds);
        try { await Task.Delay(delay, shutdown.Token); } catch (OperationCanceledException) { break; }
        delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
    }
}

var tasks = new List<Task>();
if (!shutdown.IsCancellationRequested)
{
    agent.Start(shutdown.Token);
    if (cli.VoidAbort) tasks.Add(VoidWatch.RunAsync(game, log, shutdown));
    if (cli.Script != null) tasks.Add(commands.RunScriptAsync(cli.Script, shutdown.Token));
    commands.StartStdin(shutdown.Token);
    try { await Task.Delay(Timeout.Infinite, shutdown.Token); } catch (OperationCanceledException) { }
}

// ---------------------------------------------------------------- clean shutdown
log.LogInformation("stopping: {Usage}", rt.Usage.Summary());
try { await client.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
await agent.StopAsync();
try { await client.DisconnectAsync("Bye!").WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception ex) { log.LogDebug("disconnect: {Error}", ex.Message); }
try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
log.LogInformation("bye");
fileLog.Dispose();
return 0;

// =====================================================================================================================

internal sealed record CommandLine(string? Script, double? Minutes, bool VoidAbort, bool NoKeyCheck)
{
    public static CommandLine Parse(string[] args)
    {
        string? script = null; double? minutes = null; bool voidAbort = false, noKey = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--script" when i + 1 < args.Length: script = args[++i]; break;
                case "--minutes" when i + 1 < args.Length: minutes = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--void-abort": voidAbort = true; break;
                case "--no-key-check": noKey = true; break;
            }
        }
        return new(script, minutes, voidAbort, noKey);
    }
}

internal sealed class MinecraftSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 25565;
    public string Username { get; set; } = "";
}

internal static class Paths
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
}

internal static class ConfigCheck
{
    public static List<string> Validate(MinecraftSettings mc, AgentOptions a)
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(mc.Host)) p.Add("Minecraft:Host is empty");
        if (mc.Port is <= 0 or > 65535) p.Add($"Minecraft:Port {mc.Port} is not a valid port");
        if (string.IsNullOrWhiteSpace(mc.Username) || mc.Username.Length > 16) p.Add("Minecraft:Username must be 1-16 characters");
        if (string.IsNullOrWhiteSpace(a.Llm.ApiKey)) p.Add("Agent:Llm:ApiKey is missing (or set ANTHROPIC_API_KEY)");
        else if (!a.Llm.ApiKey.StartsWith("sk-ant-")) p.Add("Agent:Llm:ApiKey doesn't look like an Anthropic key (sk-ant-...)");
        if (string.IsNullOrWhiteSpace(a.Llm.CheapModel) || string.IsNullOrWhiteSpace(a.Llm.MainModel)) p.Add("Agent:Llm:CheapModel/MainModel must be set");
        if (a.Budget.DailyCapUsd <= 0) p.Add("Agent:Budget:DailyCapUsd must be > 0");
        foreach (var m in new[] { a.Llm.CheapModel, a.Llm.MainModel })
            if (!string.IsNullOrWhiteSpace(m) && !a.Prices.Models.ContainsKey(m)) p.Add($"no price for model {m}: add Agent:Prices:Models:{m}");
        if (!string.Equals(a.Chat.Username, mc.Username, StringComparison.OrdinalIgnoreCase))
            p.Add($"Agent:Chat:Username ({a.Chat.Username}) should equal Minecraft:Username ({mc.Username})");
        return p;
    }
}

internal static class KeyCheck
{
    /// <summary>Retrieves each configured model (Models API: no tokens billed): proves the key works and the ids exist.</summary>
    public static async Task<(bool Ok, string Message)> RunAsync(string apiKey, IEnumerable<string> models)
    {
        var client = new Anthropic.AnthropicClient { ApiKey = apiKey };
        var found = new List<string>();
        foreach (var id in models.Distinct())
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var m = await client.Models.Retrieve(id, cancellationToken: cts.Token);
                found.Add(m.ID);
            }
            catch (Anthropic.Exceptions.AnthropicUnauthorizedException) { return (false, "the API key was rejected (401). Check Agent:Llm:ApiKey."); }
            catch (Anthropic.Exceptions.AnthropicNotFoundException) { return (false, $"model '{id}' not found (404). Fix Agent:Llm:CheapModel/MainModel."); }
            catch (Exception ex) { return (false, $"{ex.GetType().Name}: {ex.Message}"); }
        }
        return (true, string.Join(", ", found));
    }
}

/// <summary>Runs <see cref="BotAgent.RunAsync"/> with pause/resume (pause = cancel the loop; events keep queueing).</summary>
internal sealed class PausableAgent(AgentRuntime rt, ILogger log)
{
    private CancellationTokenSource? _cts;
    private Task _run = Task.CompletedTask;
    private CancellationToken _outer;
    public bool Paused { get; private set; }

    public void Start(CancellationToken outer)
    {
        _outer = outer;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var token = _cts.Token;
        _run = Task.Run(async () =>
        {
            try { await rt.Agent.RunAsync(token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.LogError(ex, "agent loop crashed"); }
        });
        Paused = false;
    }

    public async Task PauseAsync()
    {
        if (Paused) return;
        Paused = true;
        rt.Activities.Cancel("paused");
        _cts?.Cancel();
        try { await _run.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
    }

    public void Resume()
    {
        if (!Paused || _outer.IsCancellationRequested) return;
        Start(_outer);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        try { await _run.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
    }
}

internal static class VoidWatch
{
    /// <summary>Test-run guard: if the bot is ever over the void, stop everything and quit.</summary>
    public static async Task RunAsync(IGameClient c, ILogger log, CancellationTokenSource shutdown)
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                await Task.Delay(100, shutdown.Token);
                var pos = c.World.Player.Position;
                // Unloaded columns count as void; only judge once the chunk under us has arrived.
                if (c.IsPlaying && c.World.IsLoaded((int)Math.Floor(pos.X), (int)Math.Floor(pos.Z)) && c.World.IsPlayerOverVoid(pos))
                {
                    log.LogError("VOID ABORT: over the void at {Pos}; stopping and quitting", c.World.Player.Position);
                    await c.StopAsync();
                    shutdown.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}

internal sealed class ConsoleCommands(IGameClient c, AgentRuntime rt, PausableAgent agent, ILogger log, CancellationTokenSource shutdown)
{
    private const string Help = "commands: <text> (say in chat) | /as <player> <msg> | /status | /usage | /pause | /resume | /tool <name> [json] | /walktest | /wait <s> | /quit";

    public void StartStdin(CancellationToken ct)
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = Console.ReadLine();
                    if (line == null) return; // stdin closed: keep running headless
                    _ = ExecuteAsync(line, ct);
                }
            }
            catch (Exception ex) { log.LogDebug("stdin reader ended: {Error}", ex.Message); }
        }) { IsBackground = true, Name = "stdin" };
        thread.Start();
    }

    public async Task RunScriptAsync(string path, CancellationToken ct)
    {
        try
        {
            foreach (var raw in await File.ReadAllLinesAsync(path, ct))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                log.LogInformation("script> {Line}", line);
                await ExecuteAsync(line, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.LogError("script failed: {Error}", ex.Message); }
    }

    public async Task ExecuteAsync(string line, CancellationToken ct)
    {
        line = line.Trim();
        if (line.Length == 0) return;
        try
        {
            if (!line.StartsWith('/'))
            {
                var r = await c.SendChatAsync(line, ct);
                log.LogInformation("say: {Result}", r);
                return;
            }
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToLowerInvariant())
            {
                case "/as" when parts.Length == 3:
                    // Pretend a player said this (vanilla "<name> msg" line) — test the agent without a second account.
                    // Tagged as test chat: memory stores it as source=test and the self-profile ignores it.
                    var msg = rt.Chat.OnServerChat($"<{parts[1]}> {parts[2]}", test: true);
                    log.LogInformation("injected chat from {Player} ({Kind})", msg.Player, msg.Kind);
                    break;
                case "/status":
                    log.LogInformation("status: {Status} | header: {Header} | agent {State}", c.DescribeStatus(), GameBridge.StateHeader(c), agent.Paused ? "paused" : "running");
                    break;
                case "/usage":
                    log.LogInformation("usage: {Usage} | last cache hit {Hit:P0}", rt.Usage.Summary(), rt.Usage.LastCacheHitRatio);
                    break;
                case "/pause":
                    await agent.PauseAsync();
                    await c.StopAsync(ct);
                    log.LogInformation("agent paused (events keep queueing; /resume to continue)");
                    break;
                case "/resume":
                    agent.Resume();
                    log.LogInformation("agent resumed");
                    break;
                case "/wait" when parts.Length >= 2:
                    await Task.Delay(TimeSpan.FromSeconds(double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)), ct);
                    break;
                case "/tool" when parts.Length >= 2:
                    {
                        var input = parts.Length == 3 ? JsonDocument.Parse(parts[2]).RootElement.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                        var r = await rt.Tools.ExecuteAsync(parts[1], input, ct);
                        log.LogInformation("tool {Tool}: {Result}", parts[1], r);
                        break;
                    }
                case "/walktest":
                    await WalkTestAsync(ct);
                    break;
                case "/quit":
                case "/exit":
                    shutdown.Cancel();
                    break;
                default:
                    log.LogInformation("{Help}", Help);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.LogWarning("command '{Line}' failed: {Error}", line, ex.Message); }
    }

    /// <summary>Walks 3 blocks to a spot with solid ground all around (never next to the void) and back, via the goto tool.</summary>
    private async Task WalkTestAsync(CancellationToken ct)
    {
        var w = c.World;
        var start = w.Player.Position.ToBlockPos();
        if (!SafeCell(start)) { log.LogWarning("walktest: start {Start} isn't on safe ground; not moving", start); return; }
        MineClaw.World.BlockPos? target = null;
        foreach (var (dx, dz) in new[] { (3, 0), (-3, 0), (0, 3), (0, -3), (2, 2), (-2, 2), (2, -2), (-2, -2) })
        {
            bool ok = true;
            for (int s = 1; s <= 3 && ok; s++)
            {
                var p = new MineClaw.World.BlockPos(start.X + (int)Math.Round(dx * s / 3.0), start.Y, start.Z + (int)Math.Round(dz * s / 3.0));
                ok = SafeCell(p);
            }
            if (ok) { target = new MineClaw.World.BlockPos(start.X + dx, start.Y, start.Z + dz); break; }
        }
        if (target is not { } t) { log.LogWarning("walktest: no safe spot 3 blocks away; not moving"); return; }
        foreach (var dest in new[] { t, start })
        {
            var r = await rt.Tools.ExecuteAsync("goto", JsonSerializer.SerializeToElement(new { to = $"{dest.X},{dest.Y},{dest.Z}", dist = 0 }), ct);
            log.LogInformation("walktest goto {Dest}: {Result} (now {Pos})", dest, r, GameBridge.StateHeader(c));
            if (!r.Ok) return;
        }
    }

    /// <summary>Floor solid, feet+head clear, and every cell within 2 blocks also has solid ground (well inside the island).</summary>
    private bool SafeCell(MineClaw.World.BlockPos p)
    {
        var w = c.World;
        if (MineClaw.World.BlockShapes.HasCollision(w, p.X, p.Y, p.Z) || MineClaw.World.BlockShapes.HasCollision(w, p.X, p.Y + 1, p.Z)) return false;
        for (int dx = -2; dx <= 2; dx++)
        for (int dz = -2; dz <= 2; dz++)
        {
            if (!MineClaw.World.BlockShapes.HasCollision(w, p.X + dx, p.Y - 1, p.Z + dz)) return false;
            if (MineClaw.World.Safety.IsHazard(w, w.GetBlock(p.X + dx, p.Y, p.Z + dz))) return false;
        }
        return true;
    }
}
