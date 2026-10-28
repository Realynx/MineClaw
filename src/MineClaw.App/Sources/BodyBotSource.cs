using MineClaw.Agent.Telemetry;
using MineClaw.Bot.Bodies;

namespace MineClaw.App.Sources;

/// <summary>
/// A clone body's TUI/supervisor source in <c>Bodies:Mode=inproc</c>: an <see cref="InProcessBotSource"/> over the body's own
/// client once the registry has built it (offline before its first spawn and after it logs out). Usage isn't forwarded: one
/// brain, one usage stream (main's source has it).
/// </summary>
public sealed class BodyBotSource(string name, Func<IBody?> find, Func<IBody, InProcessBotSource> create) : IBotTelemetrySource, IDisposable
{
    private readonly object _lock = new();
    private IBody? _body;
    private InProcessBotSource? _inner;

    public string Name => name;

    public event Action<PromptUsageRecord>? UsageRecorded { add { } remove { } }

    private InProcessBotSource? Inner()
    {
        var b = find();
        if (b == null) return null;
        lock (_lock)
        {
            if (!ReferenceEquals(b, _body)) { _inner?.Dispose(); _inner = create(b); _body = b; }
            return _inner;
        }
    }

    public BotSnapshot Snapshot(bool full) => Inner()?.Snapshot(full) ?? BotSnapshot.Offline(name);

    public Task<bool> SendAsync(string line, CancellationToken ct) => Inner()?.SendAsync(line, ct) ?? Task.FromResult(false);

    public void Dispose()
    {
        lock (_lock) { _inner?.Dispose(); _inner = null; }
    }
}
