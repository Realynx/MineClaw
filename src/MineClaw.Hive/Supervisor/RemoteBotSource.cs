using MineClaw.Agent.Telemetry;
using MineClaw.Hive.Ipc;

namespace MineClaw.Hive.Supervisor;

/// <summary>
/// TUI source for one hive clone: serves the <see cref="BotSnapshot"/> the clone streams over its pipe (full form: viewport
/// and thought included, trimmed for the bot strip), forwards its <see cref="PromptUsageRecord"/>s and sends console lines
/// to it. Offline/greyed out whenever the clone isn't connected.
/// </summary>
public sealed class RemoteBotSource : IBotTelemetrySource
{
    private readonly CloneSlot _slot;

    public RemoteBotSource(CloneSlot slot)
    {
        _slot = slot;
        _slot.Usage += r => UsageRecorded?.Invoke(r);
    }

    public string Name => _slot.Name;

    public event Action<PromptUsageRecord>? UsageRecorded;

    public BotSnapshot Snapshot(bool full)
    {
        var s = _slot.Snapshot;
        if (s == null || !_slot.Connected)
        {
            var off = BotSnapshot.Offline(_slot.Name);
            return _slot.State switch
            {
                CloneState.Starting => off with { Status = BotStatus.Connecting, Task = _slot.Task?.Text },
                CloneState.Backoff => off with { Task = "restarting after a crash" },
                CloneState.Retired => off with { Task = "retired: " + _slot.LastExit },
                _ => off,
            };
        }
        var t = s with { Task = s.Task ?? _slot.Task?.Text };
        if (_slot.State == CloneState.Stopping) t = t with { Task = "logging out: " + (_slot.RecallReason ?? "") };
        return full ? t : t with { Viewport = null, Thought = null, Header = null };
    }

    public async Task<bool> SendAsync(string line, CancellationToken ct)
    {
        if (_slot.Link is not { IsOpen: true } l || !_slot.Connected) return false;
        try { return (await l.RequestAsync(Msg.Cmd, new CmdMsg(line), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false)).As<bool>(); }
        catch (HiveIpcException) { return false; }
    }
}
