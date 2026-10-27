using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Bot;
using MineClaw.Bot.Game;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Hosting;

public sealed class MinecraftSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 25565;
    public string Username { get; set; } = "";
    /// <summary>Wait this long before reconnecting after "You logged in from another location" (someone else is on the account).</summary>
    public int DuplicateLoginBackoffSeconds { get; set; } = 600;
}

public static class ConfigCheck
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

public static class KeyCheck
{
    /// <summary>Retrieves each configured model (Models API: no tokens billed): proves the key works and the ids exist.</summary>
    /// <returns>Transient = network/timeout/5xx (worth a systemd restart), as opposed to a rejected key or unknown model.</returns>
    public static async Task<(bool Ok, string Message, bool Transient)> RunAsync(string apiKey, IEnumerable<string> models, CancellationToken ct = default)
    {
        var client = new Anthropic.AnthropicClient { ApiKey = apiKey };
        var found = new List<string>();
        foreach (var id in models.Distinct())
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                var m = await client.Models.Retrieve(id, cancellationToken: cts.Token);
                found.Add(m.ID);
            }
            catch (Anthropic.Exceptions.AnthropicUnauthorizedException) { return (false, "the API key was rejected (401). Check Agent:Llm:ApiKey.", false); }
            catch (Anthropic.Exceptions.AnthropicNotFoundException) { return (false, $"model '{id}' not found (404). Fix Agent:Llm:CheapModel/MainModel.", false); }
            catch (Exception ex) { return (false, $"{ex.GetType().Name}: {ex.Message}", true); }
        }
        return (true, string.Join(", ", found), false);
    }
}

/// <summary>Runs <see cref="BotAgent.RunAsync"/> with pause/resume (pause = cancel the loop; events keep queueing).</summary>
public sealed class PausableAgent(AgentRuntime rt, ILogger log)
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

public static class VoidWatch
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

public sealed class ConsoleCommands(IGameClient c, AgentRuntime rt, PausableAgent agent, ILogger log, CancellationTokenSource shutdown)
{
    /// <summary>Hive: raised with each injected /as line so every hive process arbitrates the same message.</summary>
    public Action<string>? Injected { get; set; }

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
                    var raw = $"<{parts[1]}> {parts[2]}";
                    var msg = rt.Chat.OnServerChat(raw, test: true);
                    Injected?.Invoke(raw);
                    log.LogInformation("injected chat from {Player} ({Kind})", msg.Player, msg.Kind);
                    break;
                case "/status":
                    log.LogInformation("status: {Status} | header: {Header} | agent {State}", c.DescribeStatus(), GameBridge.StateHeader(c), agent.Paused ? "paused" : "running");
                    break;
                case "/usage":
                    {
                        var today = rt.Usage.SnapshotTotals().Today;
                        var purposes = today.Purposes is { Count: > 0 } p
                            ? string.Join(", ", p.OrderByDescending(kv => kv.Value.Usd).Select(kv => $"{kv.Key} {kv.Value.Calls}c ${kv.Value.Usd:0.000}"))
                            : "none yet";
                        log.LogInformation("usage: {Usage} | last cache hit {Hit:P0} | by purpose: {Purposes}", rt.Usage.Summary(), rt.Usage.LastCacheHitRatio, purposes);
                        break;
                    }
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
