using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Ipc;

/// <summary>Length-prefixed JSON frames: 4-byte little-endian payload length, then UTF-8 JSON of a <see cref="HiveMessage"/>.</summary>
public static class FrameCodec
{
    public const int MaxFrame = 4 * 1024 * 1024;

    public static async Task WriteAsync(Stream s, HiveMessage m, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(m, HiveJson.Options);
        if (body.Length > MaxFrame) throw new InvalidOperationException($"hive frame too large ({body.Length} bytes)");
        var buf = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buf, body.Length);
        body.CopyTo(buf, 4);
        await s.WriteAsync(buf, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Next frame, or null at end of stream (the other side is gone).</summary>
    public static async Task<HiveMessage?> ReadAsync(Stream s, CancellationToken ct)
    {
        var head = new byte[4];
        if (!await FillAsync(s, head, ct).ConfigureAwait(false)) return null;
        int len = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (len < 0 || len > MaxFrame) throw new InvalidDataException($"bad hive frame length {len}");
        var body = new byte[len];
        if (!await FillAsync(s, body, ct).ConfigureAwait(false)) return null;
        return JsonSerializer.Deserialize<HiveMessage>(body, HiveJson.Options);
    }

    private static async Task<bool> FillAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(got), ct).ConfigureAwait(false);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }
}

/// <summary>Pipe names: short (Unix sockets have a ~100-char path limit) and unique per main process.</summary>
public static class PipeNames
{
    public static string For(string clone, string? unixDirectory = null)
    {
        var name = $"mc-{Environment.ProcessId}-{clone}-{Guid.NewGuid().ToString("N")[..6]}";
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(unixDirectory)) return name;
        Directory.CreateDirectory(unixDirectory);
        return Path.Combine(Path.GetFullPath(unixDirectory), name + ".sock"); // absolute path = used as the socket path
    }

    public static NamedPipeServerStream Server(string name) =>
        new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    public static NamedPipeClientStream Client(string name) =>
        new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
}

public sealed class HiveIpcException(string message) : Exception(message);

/// <summary>
/// One duplex IPC connection (either side). A single reader loop dispatches frames: replies complete pending requests,
/// notifications run the <see cref="Handler"/> in order, requests run it concurrently and send its result back. The link
/// closes (raising <see cref="Closed"/> exactly once) on end of stream, an I/O error, or <see cref="DisposeAsync"/>.
/// </summary>
public sealed class HiveLink : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<HiveMessage>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private long _nextId;
    private Task? _reader;
    private int _closed;
    private long _lastReceivedTicks = DateTimeOffset.UtcNow.UtcTicks;

    public HiveLink(Stream stream, string name, ILogger? log = null)
    {
        _stream = stream;
        Name = name;
        _log = log ?? NullLogger.Instance;
    }

    public string Name { get; }
    public bool IsOpen => Volatile.Read(ref _closed) == 0;
    public string? CloseReason { get; private set; }
    public DateTimeOffset LastReceived => new(Interlocked.Read(ref _lastReceivedTicks), TimeSpan.Zero);

    /// <summary>Handles incoming notifications and requests; the return value is the reply payload of a request.</summary>
    public Func<HiveMessage, CancellationToken, Task<object?>>? Handler { get; set; }

    /// <summary>Raised once with the reason when the link is closed.</summary>
    public event Action<string>? Closed;

    public void Start() => _reader ??= Task.Run(ReadLoopAsync);

    private async Task ReadLoopAsync()
    {
        string reason = "pipe closed";
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var m = await FrameCodec.ReadAsync(_stream, _cts.Token).ConfigureAwait(false);
                if (m == null) break;
                Interlocked.Exchange(ref _lastReceivedTicks, DateTimeOffset.UtcNow.UtcTicks);
                if (m.Re != 0)
                {
                    if (_pending.TryRemove(m.Re, out var tcs)) tcs.TrySetResult(m);
                    continue;
                }
                if (m.Id != 0) _ = Task.Run(() => AnswerAsync(m));
                else await DispatchAsync(m).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { reason = "link disposed"; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or JsonException)
        {
            reason = "pipe error: " + ex.Message;
        }
        Close(reason);
    }

    private async Task DispatchAsync(HiveMessage m)
    {
        if (Handler is not { } h) return;
        try { await h(m, _cts.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "hive {Link}: handling {Type} failed", Name, m.T); }
    }

    private async Task AnswerAsync(HiveMessage m)
    {
        object? result = null;
        string? err = null;
        try { result = Handler is { } h ? await h(m, _cts.Token).ConfigureAwait(false) : null; }
        catch (Exception ex) when (ex is not OperationCanceledException) { err = ex.Message; }
        catch (OperationCanceledException) { return; }
        try { await WriteAsync(new HiveMessage { T = Msg.Reply, Re = m.Id, D = HiveJson.ToElement(result), Err = err }, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* closed */ }
    }

    private async Task WriteAsync(HiveMessage m, CancellationToken ct)
    {
        if (!IsOpen) throw new HiveIpcException($"hive link {Name} is closed");
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try { await FrameCodec.WriteAsync(_stream, m, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Close("pipe error: " + ex.Message);
            throw new HiveIpcException($"hive link {Name}: {ex.Message}");
        }
        finally { _write.Release(); }
    }

    /// <summary>Fire-and-forget notification. Returns false if the link is closed.</summary>
    public async Task<bool> SendAsync(string type, object? payload = null, CancellationToken ct = default)
    {
        if (!IsOpen) return false;
        try
        {
            await WriteAsync(new HiveMessage { T = type, D = HiveJson.ToElement(payload) }, ct).ConfigureAwait(false);
            return true;
        }
        catch (HiveIpcException) { return false; }
    }

    /// <summary>Request/response. Throws <see cref="HiveIpcException"/> when closed, on a remote error or timeout.</summary>
    public async Task<HiveMessage> RequestAsync(string type, object? payload, TimeSpan timeout, CancellationToken ct = default)
    {
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<HiveMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await WriteAsync(new HiveMessage { T = type, Id = id, D = HiveJson.ToElement(payload) }, ct).ConfigureAwait(false);
            HiveMessage reply;
            try { reply = await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false); }
            catch (TimeoutException) { throw new HiveIpcException($"hive {type}: no answer in {timeout.TotalSeconds:0.#}s"); }
            if (reply.Err != null) throw new HiveIpcException($"hive {type}: {reply.Err}");
            return reply;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public async Task<T?> RequestAsync<T>(string type, object? payload, TimeSpan timeout, CancellationToken ct = default) =>
        (await RequestAsync(type, payload, timeout, ct).ConfigureAwait(false)).As<T>();

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        CloseReason = reason;
        foreach (var (_, tcs) in _pending) tcs.TrySetException(new HiveIpcException($"hive link {Name} closed: {reason}"));
        _pending.Clear();
        try { _stream.Dispose(); } catch (Exception) { }
        try { Closed?.Invoke(reason); } catch (Exception ex) { _log.LogWarning(ex, "hive {Link}: Closed handler failed", Name); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        Close("link disposed");
        if (_reader != null) { try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { } }
        _cts.Dispose();
    }
}
