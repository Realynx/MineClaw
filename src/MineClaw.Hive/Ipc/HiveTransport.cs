using System.Collections.Concurrent;
using System.IO.Pipes;

namespace MineClaw.Hive.Ipc;

/// <summary>Main's side of one clone's link before the clone connects (a listening pipe, or an in-memory rendezvous).</summary>
public interface IHiveEndpoint : IDisposable
{
    /// <summary>Completes with the connected duplex stream once the clone connects; cancelled with <paramref name="ct"/> or a dispose.</summary>
    Task<Stream> AcceptAsync(CancellationToken ct);
}

/// <summary>
/// How main and a clone reach each other. <see cref="PipeTransport"/>: a named pipe between two processes (the process isolation
/// mode, <c>Hive:Isolation=process</c>). <see cref="MemoryTransport"/>: a pair of in-memory streams inside main's own process (the
/// default, <c>inproc</c>). Both carry the same length-prefixed frames to the same <see cref="HiveLink"/>s, so every message type
/// (hello/welcome, task/result, recall/bye, snap, usage, claims, bus envelopes with their tokens...) works the same in either mode.
/// </summary>
public interface IHiveTransport
{
    /// <summary>"pipe" | "memory" (logs).</summary>
    string Kind { get; }
    /// <summary>A fresh unique address for one launch of <paramref name="clone"/>.</summary>
    string NewAddress(string clone, string? unixDirectory = null);
    /// <summary>Main: starts listening on <paramref name="address"/> (before the clone is started). Disposing the endpoint is for a
    /// launch that failed: once <see cref="IHiveEndpoint.AcceptAsync"/> handed out the stream, the link owns it.</summary>
    IHiveEndpoint Listen(string address);
    /// <summary>The clone: connects to main's endpoint; null = nobody listens there (or not within <paramref name="timeout"/>).</summary>
    Task<Stream?> ConnectAsync(string address, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Named pipes (Unix sockets on Linux), current user only: the clone-process transport, unchanged from before.</summary>
public sealed class PipeTransport : IHiveTransport
{
    public static readonly PipeTransport Instance = new();

    public string Kind => "pipe";

    public string NewAddress(string clone, string? unixDirectory = null) => PipeNames.For(clone, unixDirectory);

    public IHiveEndpoint Listen(string address) => new Endpoint(PipeNames.Server(address));

    public async Task<Stream?> ConnectAsync(string address, TimeSpan timeout, CancellationToken ct)
    {
        var client = PipeNames.Client(address);
        try
        {
            await client.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
            return client;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            client.Dispose();
            return null;
        }
    }

    private sealed class Endpoint(NamedPipeServerStream server) : IHiveEndpoint
    {
        public async Task<Stream> AcceptAsync(CancellationToken ct)
        {
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            if (!server.IsConnected) throw new IOException("pipe not connected");
            return server;
        }

        public void Dispose() { try { server.Dispose(); } catch (Exception) { } }
    }
}

/// <summary>
/// In-process hives (<c>Hive:Isolation=inproc</c>): main and each hive running in main's process meet at an in-memory address
/// (<c>mem:&lt;clone&gt;:&lt;guid&gt;</c>, unique per launch, known only to the supervisor and the launcher it hands it to) and talk over a
/// pair of <see cref="MemoryDuplexStream"/>s. Nothing leaves the process, so there is no pipe ACL to get right; the bus token and the
/// "envelope only from the link that endpoint owns" rule still apply, exactly as over a pipe. One instance per main process.
/// </summary>
public sealed class MemoryTransport : IHiveTransport
{
    private readonly ConcurrentDictionary<string, Endpoint> _listening = new(StringComparer.Ordinal);

    public string Kind => "memory";

    public string NewAddress(string clone, string? unixDirectory = null) => $"mem:{clone}:{Guid.NewGuid():N}";

    public IHiveEndpoint Listen(string address)
    {
        var e = new Endpoint(this, address);
        if (!_listening.TryAdd(address, e)) throw new InvalidOperationException($"already listening on {address}");
        return e;
    }

    /// <summary>Addresses listened on and not connected yet (tests).</summary>
    public int Listening => _listening.Count;

    public async Task<Stream?> ConnectAsync(string address, TimeSpan timeout, CancellationToken ct)
    {
        // The supervisor listens before it starts the launch, so this is normally immediate; a short wait covers a racing caller.
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            if (_listening.TryRemove(address, out var e))
            {
                var (mine, theirs) = MemoryDuplexStream.CreatePair();
                if (e.Offer(theirs)) return mine;
                mine.Dispose();
                theirs.Dispose();
                return null;
            }
            if (DateTime.UtcNow >= until || ct.IsCancellationRequested) return null;
            try { await Task.Delay(20, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return null; }
        }
    }

    private sealed class Endpoint(MemoryTransport owner, string address) : IHiveEndpoint
    {
        private readonly TaskCompletionSource<Stream> _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Offer(Stream s) => _accepted.TrySetResult(s);

        public Task<Stream> AcceptAsync(CancellationToken ct) => _accepted.Task.WaitAsync(ct);

        /// <summary>Only on a failed launch (an accepted stream belongs to the link, which disposes it): the clone's side sees an
        /// end of stream at once, as with a pipe server that closes.</summary>
        public void Dispose()
        {
            owner._listening.TryRemove(new KeyValuePair<string, Endpoint>(address, this));
            if (!_accepted.TrySetCanceled() && _accepted.Task.IsCompletedSuccessfully) _accepted.Task.Result.Dispose();
        }
    }
}

/// <summary>
/// One direction of an in-memory link: written chunks queue up; reads block (the link's dedicated reader thread) or await (async
/// readers). <see cref="Close"/> ends the stream: readers drain what is queued, then see 0 (end of stream); writes throw.
/// </summary>
internal sealed class ByteQueue
{
    private readonly object _lock = new();
    private readonly Queue<byte[]> _chunks = new();
    private int _offset;
    private bool _closed;
    private TaskCompletionSource? _waiter;

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        TaskCompletionSource? w;
        lock (_lock)
        {
            if (_closed) throw new IOException("in-memory hive link closed");
            _chunks.Enqueue(data.ToArray());
            Monitor.PulseAll(_lock);
            w = _waiter;
            _waiter = null;
        }
        w?.TrySetResult();
    }

    public int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        lock (_lock)
        {
            while (_chunks.Count == 0 && !_closed) Monitor.Wait(_lock);
            return _chunks.Count == 0 ? 0 : Take(buffer);
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (buffer.IsEmpty) return 0;
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_chunks.Count > 0) return Take(buffer.Span);
                if (_closed) return 0;
                _waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _waiter.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private int Take(Span<byte> buffer)
    {
        int got = 0;
        while (got < buffer.Length && _chunks.Count > 0)
        {
            var head = _chunks.Peek();
            int n = Math.Min(buffer.Length - got, head.Length - _offset);
            head.AsSpan(_offset, n).CopyTo(buffer[got..]);
            got += n;
            _offset += n;
            if (_offset == head.Length) { _chunks.Dequeue(); _offset = 0; }
        }
        return got;
    }

    public void Close()
    {
        TaskCompletionSource? w;
        lock (_lock)
        {
            _closed = true;
            Monitor.PulseAll(_lock);
            w = _waiter;
            _waiter = null;
        }
        w?.TrySetResult();
    }
}

/// <summary>One end of an in-memory duplex link. Disposing either end closes both directions (the other side reads end of stream).</summary>
public sealed class MemoryDuplexStream : Stream
{
    private readonly ByteQueue _in;
    private readonly ByteQueue _out;
    private int _disposed;

    private MemoryDuplexStream(ByteQueue readFrom, ByteQueue writeTo)
    {
        _in = readFrom;
        _out = writeTo;
    }

    public static (MemoryDuplexStream A, MemoryDuplexStream B) CreatePair()
    {
        var ab = new ByteQueue();
        var ba = new ByteQueue();
        return (new MemoryDuplexStream(ba, ab), new MemoryDuplexStream(ab, ba));
    }

    public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) => _in.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _in.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _in.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(MemoryDuplexStream));
        _out.Write(buffer);
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _out.Close();
            _in.Close();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
