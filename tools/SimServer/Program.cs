using MineClaw.Sim;

int port = 25565;
string? script = null;
if (!ParseArgs(args, ref port, ref script))
{
    Console.Error.WriteLine("Usage: SimServer [--port 25565] [--script commands.txt]");
    return 2;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var server = new SimServer(port);
Console.WriteLine($"Offline sandbox on 127.0.0.1:{server.Port}; say <text>, quit (Ctrl+C to stop).");
server.ChatReceived += line => Console.WriteLine($"bot: {line}");
var run = server.RunAsync(stop.Token);
var commands = Task.Run(CommandsAsync);
try { await Task.WhenAny(run, commands); if (commands.IsFaulted) await commands; await run; }
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
finally { stop.Cancel(); await run; }
return 0;

// --port N (0..65535) and --script FILE; false on anything else.
static bool ParseArgs(string[] args, ref int port, ref string? script)
{
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out var p) && p is >= 0 and <= 65535) port = p;
        else if (args[i] == "--script" && i + 1 < args.Length) script = args[++i];
        else return false;
    }
    return true;
}

async Task CommandsAsync()
{
    using var input = script != null ? File.OpenText(script) : null;
    var reader = (TextReader?)input ?? Console.In;
    if (script != null) await server.WaitForSessionAsync(stop.Token);
    while (!stop.IsCancellationRequested && await reader.ReadLineAsync(stop.Token) is { } line)
    {
        line = line.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        if (await RunCommandAsync(line)) break;
    }
    // EOF leaves the server running, so redirected/scripted input can finish before the bot's reply.
}

// One command line: true on quit (the server stops).
async Task<bool> RunCommandAsync(string line)
{
    if (line == "quit") { stop.Cancel(); return true; }
    if (line.StartsWith("wait ") && int.TryParse(line[5..], out int ms) && ms >= 0)
        await Task.Delay(ms, stop.Token);
    else if (line.StartsWith("say "))
    {
        try { server.Say(line[4..]); }
        catch (InvalidOperationException ex) { Console.WriteLine(ex.Message); }
    }
    else Console.WriteLine("Commands: say <text>, wait <milliseconds>, quit");
    return false;
}
