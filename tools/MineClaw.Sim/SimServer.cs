using System.Net;
using System.Net.Sockets;

namespace MineClaw.Sim;

/// <summary>Loopback-only, single-client TCP sandbox. Each connection starts a fresh island.</summary>
public sealed class SimServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly TaskCompletionSource<FakeServer> _firstSession = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FakeServer? _session;

    public SimServer(int port = 25565)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
    }

    public event Action<string>? ChatReceived;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public FakeServer? Session => Volatile.Read(ref _session);
    public Task<FakeServer> WaitForSessionAsync(CancellationToken ct = default) => _firstSession.Task.WaitAsync(ct);

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var tcp = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                tcp.NoDelay = true;
                using var session = new FakeServer(tcp.GetStream());
                SkyblockStart.Configure(session);
                session.Hook = packet =>
                {
                    if (packet is MineClaw.Protocol.Packets.ChatPacket chat) ChatReceived?.Invoke(chat.Message);
                    return false;
                };
                session.OnLogin = () =>
                {
                    SkyblockStart.SpawnPlayer(session);
                    _firstSession.TrySetResult(session);
                };
                Volatile.Write(ref _session, session);
                session.Start();
                try { await session.Completion.WaitAsync(ct).ConfigureAwait(false); }
                finally { Volatile.Write(ref _session, null); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public void Say(string text)
    {
        var session = Session;
        if (session?.LoggedIn != true) throw new InvalidOperationException("No bot is logged in yet");
        SkyblockStart.Say(session, text);
    }

    public void Dispose()
    {
        _listener.Stop();
        Session?.Dispose();
    }
}
