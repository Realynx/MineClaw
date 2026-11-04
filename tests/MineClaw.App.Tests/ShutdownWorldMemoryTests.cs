using MineClaw.App.Hosting;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.Tests;
using MineClaw.Bot.WorldIndex;
using MineClaw.Client;
using MineClaw.Hive;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Tests;

/// <summary>
/// Shutdown order (live journal, every restart: "world memory write failed (441 columns) … BeginTransaction can only be called when
/// the connection is open"): a body's last world-memory write ran in its teardown, which outlasted its shutdown step, after the db had
/// closed. <see cref="InProcBodies.FlushWorldMemory"/> writes every body's pending columns before the db closes; the teardown after it
/// writes nothing and logs no failure.
/// </summary>
public class ShutdownWorldMemoryTests
{
    private sealed class Capture : ILoggerProvider, ILogger
    {
        public readonly List<string> Warnings = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) lock (Warnings) Warnings.Add(formatter(state, exception) + (exception != null ? " " + exception.Message : ""));
        }
    }

    [Fact]
    public async Task Pending_world_memory_is_written_before_the_db_closes()
    {
        var capture = new Capture();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(capture));
        var (bot, _) = TestBot.Create();
        await using var main = new MinecraftClient(new ClientOptions { Username = "maid_chan" });
        var inproc = InProcBodies.Create(new HiveConfig { CloneUsernames = ["maid_chan_67", "maid_chan"] },
            new BodiesOptions { Mode = "inproc", YieldCheckSeconds = 0, TickReportSeconds = 0 },
            new MinecraftSettings { Host = "127.0.0.1", Port = 1, Username = "maid_chan" }, bot.Runtime.Options, loggers)!;
        inproc.Attach(bot, main, CancellationToken.None);
        IBodyHost host = inproc;
        var body = host.Create("maid_chan_67");
        Assert.NotNull(body.WorldMemory);
        var world = body.Client!.World;
        var stone = new byte[16 * 128 * 16];
        Array.Fill(stone, (byte)1);
        for (int cx = 0; cx < 3; cx++) world.ApplyChunkRegion(cx * 16, 0, 0, 16, 128, 16, stone, null, null, null);
        var store = new SqliteWorldMemoryStore(bot.Runtime.Db, "maid_chan_67");
        Assert.Null(store.GetChunk(0, 1, 0));

        inproc.FlushWorldMemory();
        for (int cx = 0; cx < 3; cx++) Assert.NotNull(store.GetChunk(0, cx, 0));

        bot.Dispose(); // main's own memory flushes with the bot
        bot.Runtime.Dispose(); // then the db closes (AppHost: the "db" step)
        await inproc.DisposeAsync(); // the body's teardown: nothing left to write
        Assert.DoesNotContain(capture.Warnings, w => w.Contains("world memory", StringComparison.OrdinalIgnoreCase));
    }
}
