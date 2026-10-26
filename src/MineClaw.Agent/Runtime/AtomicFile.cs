using System.Text.Json;
using System.Text.Json.Serialization;

namespace MineClaw.Agent.Runtime;

/// <summary>Crash-safe JSON persistence: write to a temp file in the same directory, then atomically replace.</summary>
public static class AtomicFile
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static void WriteAllText(string path, string contents)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(fs))
        {
            w.Write(contents);
            w.Flush();
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, full, overwrite: true);
    }

    public static void WriteJson<T>(string path, T value) => WriteAllText(path, JsonSerializer.Serialize(value, Json));

    /// <summary>Reads JSON; returns null if missing. A corrupt file is renamed aside (*.bad) rather than lost.</summary>
    public static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            try { File.Move(path, path + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bad", overwrite: true); } catch (IOException) { }
            return null;
        }
    }
}
