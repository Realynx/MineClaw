using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Ipc;

/// <summary>Length-prefixed JSON frames: 4-byte little-endian payload length, then UTF-8 JSON of a <see cref="HiveMessage"/>.</summary>
public static class FrameCodec
{
    public const int MaxFrame = 4 * 1024 * 1024;

    public static byte[] Encode(HiveMessage m)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(m, HiveJson.Options);
        if (body.Length > MaxFrame) throw new InvalidOperationException($"hive frame too large ({body.Length} bytes)");
        var buf = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buf, body.Length);
        body.CopyTo(buf, 4);
        return buf;
    }

    public static async Task WriteAsync(Stream s, HiveMessage m, CancellationToken ct)
    {
        var buf = Encode(m);
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

    /// <summary>Blocking read (the link's dedicated reader thread: independent of the thread pool).</summary>
    public static HiveMessage? Read(Stream s)
    {
        var head = new byte[4];
        if (!Fill(s, head)) return null;
        int len = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (len < 0 || len > MaxFrame) throw new InvalidDataException($"bad hive frame length {len}");
        var body = new byte[len];
        if (!Fill(s, body)) return null;
        return JsonSerializer.Deserialize<HiveMessage>(body, HiveJson.Options);
    }

    private static bool Fill(Stream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n == 0) return false;
            got += n;
        }
        return true;
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
        new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); // local, this user only (the hive bus rides it)

    public static NamedPipeClientStream Client(string name) =>
        new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}

public sealed class HiveIpcException(string message) : Exception(message);

/// <summary>
/// One duplex IPC connection (either side). Liveness never depends on the thread pool or on what the process is busy with:
/// a dedicated reader thread (blocking reads) stamps <see cref="LastReceived"/> on EVERY frame and answers pings itself, and
/// a dedicated heartbeat thread (<see cref="StartHeartbeat"/>) writes heartbeats and pongs synchronously. Replies complete
/// pending requests; notifications run the <see cref="Handler"/> in order on a separate queue (a slow handler never stops
/// the reading); requests run it concurrently and send its result back. The link closes (raising <see cref="Closed"/>
/// exactly once) on end of stream, an I/O error, or <see cref="DisposeAsync"/>.
/// </summary>
public sealed class HiveLink : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<HiveMessage>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<HiveMessage> _inbox = Channel.CreateUnbounded<HiveMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly ConcurrentQueue<long> _pongs = new();
    private readonly AutoResetEvent _beatWake = new(false);
    private long _nextId;
    private Thread? _reader;
    private Thread? _beat;
    private Task? _dispatch;
    private int _closed;
    private long _beats;
    private long _lastReceivedTicks = DateTimeOffset.UtcNow.UtcTicks;
    private long _lastSentTicks = DateTimeOffset.UtcNow.UtcTicks;

    public HiveLink(Stream stream, string name, ILogger? log = null)
    {
        _stream = stream;
        Name = name;
        _log = log ?? NullLogger.Instance;
    }

    public string Name { get; }
    public bool IsOpen => Volatile.Read(ref _closed) == 0;
    public string? CloseReason { get; private set; }
    /// <summary>When ANY frame (heartbeat, ping, pong, reply, notification...) last arrived: every message is liveness.</summary>
    public DateTimeOffset LastReceived => new(Interlocked.Read(ref _lastReceivedTicks), TimeSpan.Zero);
    public DateTimeOffset LastSent => new(Interlocked.Read(ref _lastSentTicks), TimeSpan.Zero);
    /// <summary>Heartbeats written by the heartbeat thread so far.</summary>
    public long HeartbeatsSent => Interlocked.Read(ref _beats);

    /// <summary>Handles incoming notifications and requests; the return value is the reply payload of a request.</summary>
    public Func<HiveMessage, CancellationToken, Task<object?>>? Handler { get; set; }

    /// <summary>Raised once with the reason when the link is closed.</summary>
    public event Action<string>? Closed;

    public void Start()
    {
        if (_reader != null) return;
        _dispatch = Task.Run(DispatchLoopAsync);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "hive-read-" + Name };
        _reader.Start();
    }

    /// <summary>
    /// Heartbeats from a dedicated thread, written synchronously: independent of the agent loop, the db and the thread pool.
    /// The same thread flushes pongs (answers to <see cref="Msg.Ping"/>) as soon as the reader queues them.
    /// </summary>
    public void StartHeartbeat(TimeSpan every)
    {
        if (_beat != null) return;
        var period = every <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : every;
        _beat = new Thread(() => BeatLoop(period)) { IsBackground = true, Name = "hive-beat-" + Name };
        _beat.Start();
    }

    private void BeatLoop(TimeSpan period)
    {
        try
        {
            var hb = FrameCodec.Encode(new HiveMessage { T = Msg.Heartbeat });
            while (IsOpen)
            {
                try { _beatWake.WaitOne(period); } catch (ObjectDisposedException) { return; }
                if (!IsOpen) return;
                while (_pongs.TryDequeue(out var id))
                    if (!WriteSync(FrameCodec.Encode(new HiveMessage { T = Msg.Reply, Re = id }), TimeSpan.FromSeconds(5))) break;
                if (DateTimeOffset.UtcNow - LastSent >= period * 0.9 && WriteSync(hb, period)) Interlocked.Increment(ref _beats);
            }
        }
        catch (Exception ex)
        {
            // A dedicated thread: an escape would end the process; a link that can't beat is a closed link.
            _log.LogWarning(ex, "hive {Link}: heartbeat thread failed", Name);
            Close("heartbeat error: " + ex.Message);
        }
    }

    /// <summary>A frame written from the heartbeat thread. If another writer holds the pipe longer than <paramref name="wait"/>
    /// this beat is skipped (the frame in flight proves liveness by itself).</summary>
    private bool WriteSync(byte[] frame, TimeSpan wait)
    {
        if (!IsOpen) return false;
        bool held = false;
        try
        {
            held = _write.Wait(wait);
            if (!held || !IsOpen) return false;
            _stream.Write(frame, 0, frame.Length);
            _stream.Flush();
            Interlocked.Exchange(ref _lastSentTicks, DateTimeOffset.UtcNow.UtcTicks);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Close("pipe error: " + ex.Message);
            return false;
        }
        finally { if (held) { try { _write.Release(); } catch (ObjectDisposedException) { } } }
    }

    private void ReadLoop()
    {
        string reason = "pipe closed";
        try { ReadFrames(); }
        catch (Exception) when (_cts.IsCancellationRequested || !IsOpen) { reason = "link disposed"; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or JsonException or InvalidOperationException
                                       or OperationCanceledException)
        {
            reason = "pipe error: " + ex.Message;
        }
        catch (Exception ex)
        {
            // A dedicated thread: anything escaping it ends the process (with in-process hives, main and every hive in it).
            reason = "link error: " + ex.GetType().Name + ": " + ex.Message;
            _log.LogWarning(ex, "hive {Link}: reader failed", Name);
        }
        DrainInbox();
        Close(reason);
    }

    /// <summary>Notifications already read (a "bye" right before the pipe closes) are handled BEFORE Closed fires, as with inline
    /// dispatch: the other side's last words decide whether its exit was planned.</summary>
    private void DrainInbox()
    {
        if (!IsOpen || _dispatch == null) return;
        _inbox.Writer.TryComplete();
        try { _dispatch.Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { }
    }

    /// <summary>Reads frames until the pipe ends (null frame) or the link is cancelled.</summary>
    private void ReadFrames()
    {
        while (!_cts.IsCancellationRequested)
        {
            var m = FrameCodec.Read(_stream);
            if (m == null) break;
            Interlocked.Exchange(ref _lastReceivedTicks, DateTimeOffset.UtcNow.UtcTicks);
            OnFrame(m);
        }
    }

    /// <summary>One frame: a reply completes its request; a heartbeat is liveness only (stamped already); a ping is answered by the
    /// link; a request is answered off the reader; a notification goes to the inbox.</summary>
    private void OnFrame(HiveMessage m)
    {
        if (m.Re != 0)
        {
            if (_pending.TryRemove(m.Re, out var tcs)) tcs.TrySetResult(m);
            return;
        }
        if (m.T == Msg.Heartbeat) return;
        if (m.T == Msg.Ping && m.Id != 0) { AnswerPing(m.Id); return; }
        if (m.Id != 0) AnswerLater(m);
        else _inbox.Writer.TryWrite(m);
    }

    private void AnswerLater(HiveMessage m) => _ = Task.Run(() => AnswerAsync(m));

    /// <summary>Answered by the link itself, never by the (possibly busy) process.</summary>
    private void AnswerPing(long id)
    {
        if (_beat != null)
        {
            _pongs.Enqueue(id);
            try { _beatWake.Set(); } catch (ObjectDisposedException) { }
        }
        else ReplyLater(id);
    }

    private void ReplyLater(long id) =>
        _ = Task.Run(async () => { try { await WriteAsync(new HiveMessage { T = Msg.Reply, Re = id }, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { } });

    private async Task DispatchLoopAsync()
    {
        try
        {
            await foreach (var m in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
                await DispatchAsync(m).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException) { }
    }

    private async Task DispatchAsync(HiveMessage m)
    {
        if (Handler is not { } h) return;
        try { await h(m, _cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "hive {Link}: handling {Type} failed", Name, m.T); }
    }

    private async Task AnswerAsync(HiveMessage m)
    {
        object? result = null;
        string? err = null;
        try { result = Handler is { } h ? await h(m, _cts.Token).ConfigureAwait(false) : null; }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { err = ex.Message; }
        try { await WriteAsync(new HiveMessage { T = Msg.Reply, Re = m.Id, D = HiveJson.ToElement(result), Err = err }, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* closed */ }
    }

    private async Task WriteAsync(HiveMessage m, CancellationToken ct)
    {
        if (!IsOpen) throw new HiveIpcException($"hive link {Name} is closed");
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(_stream, m, ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastSentTicks, DateTimeOffset.UtcNow.UtcTicks);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Close("pipe error: " + ex.Message);
            throw new HiveIpcException($"hive link {Name}: {ex.Message}");
        }
        finally { try { _write.Release(); } catch (ObjectDisposedException) { } }
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

    /// <summary>
    /// Ping/pong probe before declaring the other side dead: true when the pong (or any other frame) arrived within
    /// <paramref name="timeout"/>. The pong comes from the other side's link threads, so a busy process still answers.
    /// </summary>
    public async Task<bool> ProbeAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IsOpen) return false;
        var before = LastReceived;
        try
        {
            await RequestAsync(Msg.Ping, null, timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (HiveIpcException) { return IsOpen && LastReceived > before; }
    }

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        CloseReason = reason;
        foreach (var (_, tcs) in _pending) tcs.TrySetException(new HiveIpcException($"hive link {Name} closed: {reason}"));
        _pending.Clear();
        _inbox.Writer.TryComplete();
        try { _beatWake.Set(); } catch (ObjectDisposedException) { }
        try { _stream.Dispose(); } catch (Exception) { }
        try { Closed?.Invoke(reason); } catch (Exception ex) { _log.LogWarning(ex, "hive {Link}: Closed handler failed", Name); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        Close("link disposed");
        if (_reader is { } r && r != Thread.CurrentThread)
            await Task.Run(() => r.Join(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        if (_dispatch != null) { try { await _dispatch.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { } }
    }
}
