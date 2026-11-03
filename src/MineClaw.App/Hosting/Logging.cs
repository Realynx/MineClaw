using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace MineClaw.App.Hosting;

public sealed record LogLine(DateTimeOffset At, LogLevel Level, string Category, string Message);

/// <summary>In-memory ring of recent log lines for the TUI (Information+ from the App, Warning+ from everything else).</summary>
public sealed class RingLoggerProvider : ILoggerProvider
{
    private readonly object _lock = new();
    private readonly LinkedList<LogLine> _lines = new();
    private readonly int _capacity;

    public RingLoggerProvider(int capacity = 300) => _capacity = capacity;

    public IReadOnlyList<LogLine> Lines() { lock (_lock) return _lines.ToArray(); }

    public void Add(LogLine l)
    {
        lock (_lock)
        {
            _lines.AddLast(l);
            while (_lines.Count > _capacity) _lines.RemoveFirst();
        }
    }

    public static bool Wanted(string category, LogLevel level) =>
        level >= LogLevel.Warning || (level >= LogLevel.Information && category == "MineClaw.App");

    public ILogger CreateLogger(string categoryName) => new Ring(this, categoryName);
    public void Dispose() { }

    private sealed class Ring(RingLoggerProvider p, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => Wanted(category, logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            if (exception != null) msg += " (" + exception.GetType().Name + ": " + exception.Message + ")";
            p.Add(new LogLine(DateTimeOffset.Now, logLevel, category, msg.Replace('\n', ' ').Replace("\r", "")));
        }
    }
}

/// <summary>Logger wiring for the two console modes.</summary>
public static class AppLogging
{
    /// <summary>The console's lowest level (Host:ConsoleLogLevel), read by the console filter on every line, so it changes live.</summary>
    public static LogLevel ConsoleLevel { get; private set; } = LogLevel.Information;

    /// <summary>Sets <see cref="ConsoleLevel"/> from a level name; false (unchanged) when it isn't one.</summary>
    public static bool SetConsoleLevel(string? level)
    {
        if (!TryParseLevel(level, out var l)) return false;
        ConsoleLevel = l;
        return true;
    }

    public static bool TryParseLevel(string? level, out LogLevel l)
    {
        l = LogLevel.Information;
        return !string.IsNullOrWhiteSpace(level) && Enum.TryParse(level.Trim(), ignoreCase: true, out l) && l is >= LogLevel.Trace and <= LogLevel.Critical;
    }

    /// <summary>
    /// Resolves "auto": systemd formatter (<c>&lt;6&gt;</c> priority prefixes, one line, no colour) when stdout goes to the
    /// journal, else the simple single-line formatter (colours only when stdout is a TTY).
    /// </summary>
    public static string ResolveFormat(string? configured, bool underJournal) =>
        (configured ?? "auto").ToLowerInvariant() switch
        {
            "simple" => "simple",
            "systemd" => "systemd",
            "json" => "json",
            _ => underJournal ? "systemd" : "simple",
        };

    public static ILoggerFactory Create(bool tui, string format, FileLoggerProvider file, RingLoggerProvider ring)
    {
        return LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(file);
            if (tui)
            {
                b.AddProvider(ring);
                return;
            }
            bool tty = !Console.IsOutputRedirected;
            switch (format)
            {
                case "systemd":
                    b.AddSystemdConsole(o => { o.IncludeScopes = false; o.TimestampFormat = null; });
                    break;
                case "json":
                    b.AddJsonConsole(o =>
                    {
                        o.IncludeScopes = false;
                        o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffK";
                        o.UseUtcTimestamp = true;
                        o.JsonWriterOptions = new JsonWriterOptions { Indented = false };
                    });
                    break;
                default:
                    b.AddSimpleConsole(o =>
                    {
                        o.SingleLine = true;
                        o.IncludeScopes = false;
                        o.TimestampFormat = tty ? "HH:mm:ss " : "yyyy-MM-ddTHH:mm:ss.fffK ";
                        o.ColorBehavior = tty ? LoggerColorBehavior.Enabled : LoggerColorBehavior.Disabled;
                    });
                    break;
            }
            // Console: the client's chatty debug stays in the file log.
            b.AddFilter<ConsoleLoggerProvider>((cat, lvl) => lvl >= (cat?.StartsWith("MineClaw.Client") == true ? (LogLevel)Math.Max((int)LogLevel.Warning, (int)ConsoleLevel) : ConsoleLevel));
        });
    }
}
