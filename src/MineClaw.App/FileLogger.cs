using System.Text;
using Microsoft.Extensions.Logging;

namespace MineClaw.App;

/// <summary>
/// Minimal rolling file logger: one file per day (logs/mineclaw-yyyyMMdd.log), a new numbered file past
/// <see cref="MaxBytes"/>, and only the newest <see cref="KeepFiles"/> files kept.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly LogLevel _min;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string? _path;
    private DateTime _day;

    public long MaxBytes { get; init; } = 10 * 1024 * 1024;
    public int KeepFiles { get; init; } = 14;

    public FileLoggerProvider(string directory, LogLevel min = LogLevel.Debug)
    {
        _dir = directory;
        _min = min;
        Directory.CreateDirectory(directory);
    }

    public string? CurrentPath { get { lock (_lock) return _path; } }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(LogLevel level, string category, string message, Exception? ex)
    {
        if (level < _min) return;
        var now = DateTime.Now;
        var sb = new StringBuilder();
        sb.Append(now.ToString("HH:mm:ss.fff")).Append(' ').Append(Short(level)).Append(' ')
          .Append(category.StartsWith("MineClaw.") ? category[9..] : category).Append(": ").Append(message);
        if (ex != null) sb.AppendLine().Append(ex);
        lock (_lock)
        {
            try
            {
                if (_writer == null || now.Date != _day || _writer.BaseStream.Length > MaxBytes) Roll(now);
                _writer!.WriteLine(sb.ToString());
                _writer.Flush();
            }
            catch (IOException) { /* logging must never take the bot down */ }
        }
    }

    private void Roll(DateTime now)
    {
        _writer?.Dispose();
        _day = now.Date;
        string stem = Path.Combine(_dir, $"mineclaw-{now:yyyyMMdd}");
        string path = stem + ".log";
        for (int i = 1; File.Exists(path) && new FileInfo(path).Length > MaxBytes; i++) path = $"{stem}-{i}.log";
        _path = path;
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
        foreach (var old in new DirectoryInfo(_dir).GetFiles("mineclaw-*.log").OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepFiles))
            try { old.Delete(); } catch (IOException) { }
    }

    private static string Short(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
        LogLevel.Warning => "WRN", LogLevel.Error => "ERR", _ => "CRT",
    };

    public void Dispose()
    {
        lock (_lock) { _writer?.Dispose(); _writer = null; }
    }

    private sealed class FileLogger(FileLoggerProvider p, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= p._min;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) p.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
