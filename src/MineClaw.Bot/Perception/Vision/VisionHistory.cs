using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Perception.Vision;

/// <summary>
/// One picture a model received (or was about to): what the Live Hive's "what the AI saw" shows. <see cref="Sha256"/> is of the
/// exact PNG bytes the LLM request carried (the same array, never re-rendered or re-encoded).
/// </summary>
/// <param name="Kind">sheet (the classic <c>see</c> sheet), composite, hive (the overview), picture (the legacy single view).</param>
/// <param name="Source">see (the brain/worker tool) or auto… (auto-vision: a job step, a build check).</param>
/// <param name="Mode">direct (the image went to the caller's model) or perception (to the perception tier, the caller got text).</param>
/// <param name="Views">The panels as asked ("eye", "iso yaw=200 pitch=50", "cam tower NE 14").</param>
/// <param name="Call">The tool input JSON (null for auto-vision): paste it into <c>see</c> to render the same sheet again.</param>
/// <param name="Answer">The perception tier's verified answer text (null in direct mode / until it answered).</param>
/// <param name="Note">"cached: no image sent", "not sent: budget", a model error…</param>
public sealed record VisionSheetInfo(long Id, DateTimeOffset At, string Body, string Kind, string Source, string Mode, IReadOnlyList<string> Views,
    int[]? Focus, int Radius, string? Question, string? Call, int Width, int Height, int Tokens, int Bytes, string Sha256, string Legend,
    string? Answer = null, string? Note = null, string? Model = null, long? ModelInputTokens = null, long? ModelOutputTokens = null);

/// <summary>
/// The last N vision sheets of one body, kept in memory (a ring: the oldest falls off) with an optional on-disk ring
/// (<c>{dir}/{id}.png</c> + <c>.json</c>, the oldest files deleted past <see cref="DiskKeep"/>). Recording costs a SHA-256 over
/// the PNG and a list insert: the PNG is the array the request already holds (no copy, no re-render). Thread-safe.
/// </summary>
public sealed class VisionHistory
{
    private readonly LinkedList<(VisionSheetInfo Info, byte[] Png)> _ring = new();
    private readonly object _lock = new();
    private readonly ILogger? _log;
    private long _next;

    public VisionHistory(int capacity = 20, string? diskDir = null, int diskKeep = 200, ILogger? log = null)
    {
        Capacity = Math.Clamp(capacity, 1, 500);
        DiskDir = string.IsNullOrWhiteSpace(diskDir) ? null : diskDir;
        DiskKeep = Math.Max(1, diskKeep);
        _log = log;
        _next = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 10; // ids stay unique across restarts (the disk ring keeps old ones)
    }

    public int Capacity { get; }
    public string? DiskDir { get; }
    public int DiskKeep { get; }
    public int Count { get { lock (_lock) return _ring.Count; } }

    public static string Hash(byte[] png) => Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();

    /// <summary>Stores one sheet (newest first in <see cref="List"/>); returns its record.</summary>
    public VisionSheetInfo Add(byte[] png, int width, int height, VisionSheetInfo template)
    {
        VisionSheetInfo info;
        lock (_lock)
        {
            long id = ++_next;
            info = template with { Id = id, Width = width, Height = height, Tokens = (width * height + 749) / 750, Bytes = png.Length, Sha256 = Hash(png) };
            _ring.AddFirst((info, png));
            while (_ring.Count > Capacity) _ring.RemoveLast();
        }
        Persist(info, png);
        return info;
    }

    /// <summary>The perception tier's answer (and its model/tokens) once it came back.</summary>
    public VisionSheetInfo? Update(long id, Func<VisionSheetInfo, VisionSheetInfo> change)
    {
        VisionSheetInfo? updated = null;
        byte[]? png = null;
        lock (_lock)
            for (var n = _ring.First; n != null; n = n.Next)
                if (n.Value.Info.Id == id)
                {
                    updated = change(n.Value.Info) with { Id = id, Sha256 = n.Value.Info.Sha256, Bytes = n.Value.Info.Bytes };
                    png = n.Value.Png;
                    n.Value = (updated, png);
                    break;
                }
        if (updated != null && DiskDir != null) PersistJson(updated);
        return updated;
    }

    /// <summary>Newest first; <paramref name="body"/> filters (null = all); <paramref name="since"/> = only ids above it.</summary>
    public IReadOnlyList<VisionSheetInfo> List(string? body = null, long since = 0, int max = 500)
    {
        lock (_lock)
            return _ring.Select(x => x.Info).Where(i => i.Id > since && (body == null || i.Body.Equals(body, StringComparison.OrdinalIgnoreCase))).Take(max).ToList();
    }

    public VisionSheetInfo? Get(long id)
    {
        lock (_lock) return _ring.Select(x => x.Info).FirstOrDefault(i => i.Id == id);
    }

    /// <summary>The exact PNG bytes (memory first, then the disk ring); null when gone.</summary>
    public byte[]? Png(long id)
    {
        lock (_lock)
            foreach (var (info, png) in _ring)
                if (info.Id == id) return png;
        if (DiskDir == null) return null;
        try
        {
            var f = Path.Combine(DiskDir, id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png");
            return File.Exists(f) ? File.ReadAllBytes(f) : null;
        }
        catch (Exception ex) { _log?.LogDebug(ex, "vision history: disk read failed"); return null; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false,
    };

    private void Persist(VisionSheetInfo info, byte[] png)
    {
        if (DiskDir == null) return;
        try
        {
            Directory.CreateDirectory(DiskDir);
            File.WriteAllBytes(Path.Combine(DiskDir, info.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png"), png);
            PersistJson(info);
            var files = new DirectoryInfo(DiskDir).GetFiles("*.png").OrderByDescending(f => f.Name.Length).ThenByDescending(f => f.Name, StringComparer.Ordinal).ToList();
            foreach (var old in files.Skip(DiskKeep))
            {
                old.Delete();
                var meta = Path.ChangeExtension(old.FullName, ".json");
                if (File.Exists(meta)) File.Delete(meta);
            }
        }
        catch (Exception ex) { _log?.LogDebug(ex, "vision history: disk write failed"); }
    }

    private void PersistJson(VisionSheetInfo info)
    {
        try { File.WriteAllText(Path.Combine(DiskDir!, info.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json"), JsonSerializer.Serialize(info, Json)); }
        catch (Exception ex) { _log?.LogDebug(ex, "vision history: disk write failed"); }
    }
}
