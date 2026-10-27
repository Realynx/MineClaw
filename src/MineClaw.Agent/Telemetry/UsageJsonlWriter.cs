using System.Text;

namespace MineClaw.Agent.Telemetry;

/// <summary>
/// Appends <see cref="PromptUsageRecord"/>s to <c>{dir}/usage-YYYY-MM-DD.jsonl</c> (local date; a new file each day), one
/// JSON object per line, flushed per record. Files older than <c>keepDays</c> are deleted on roll. IO errors are swallowed.
/// </summary>
public sealed class UsageJsonlWriter : IDisposable
{
    private readonly string _dir;
    private readonly int _keepDays;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string? _day;

    public UsageJsonlWriter(string directory, int keepDays = 30, TimeProvider? time = null)
    {
        _dir = directory;
        _keepDays = keepDays;
        _time = time ?? TimeProvider.System;
    }

    public string Directory => _dir;

    public static string FileName(DateTimeOffset localDay) => $"usage-{localDay:yyyy-MM-dd}.jsonl";

    public string PathFor(DateTimeOffset localDay) => Path.Combine(_dir, FileName(localDay));

    public void Write(PromptUsageRecord r)
    {
        var line = r.ToJsonLine();
        var now = _time.GetLocalNow();
        var day = now.ToString("yyyy-MM-dd");
        lock (_lock)
        {
            try
            {
                if (_writer == null || day != _day) Roll(now, day);
                _writer!.WriteLine(line);
                _writer.Flush();
            }
            catch (IOException) { _writer?.Dispose(); _writer = null; }
            catch (UnauthorizedAccessException) { _writer = null; }
        }
    }

    /// <summary>Records of one local day (oldest first); empty when the file is missing. Bad lines are skipped.</summary>
    public IReadOnlyList<PromptUsageRecord> Read(DateTimeOffset localDay)
    {
        var path = PathFor(localDay);
        var list = new List<PromptUsageRecord>();
        if (!File.Exists(path)) return list;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            while (sr.ReadLine() is { } line)
                if (line.Length > 0 && PromptUsageRecord.FromJsonLine(line) is { } r) list.Add(r);
        }
        catch (IOException) { }
        return list;
    }

    private void Roll(DateTimeOffset now, string day)
    {
        _writer?.Dispose();
        System.IO.Directory.CreateDirectory(_dir);
        _day = day;
        _writer = new StreamWriter(new FileStream(PathFor(now), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
        if (_keepDays <= 0) return;
        var cutoff = now.Date.AddDays(-_keepDays).ToString("yyyy-MM-dd");
        foreach (var f in new DirectoryInfo(_dir).GetFiles("usage-*.jsonl"))
        {
            var stem = f.Name["usage-".Length..^".jsonl".Length];
            if (string.CompareOrdinal(stem, cutoff) < 0) try { f.Delete(); } catch (IOException) { }
        }
    }

    public void Dispose()
    {
        lock (_lock) { _writer?.Dispose(); _writer = null; }
    }
}
