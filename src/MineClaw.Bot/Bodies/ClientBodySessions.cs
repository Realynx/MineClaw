using MineClaw.Client;

namespace MineClaw.Bot.Bodies;

/// <summary>A body's session over a real <see cref="MinecraftClient"/> (no auto-reconnect: the body's runner owns reconnects).</summary>
public static class ClientBodySessions
{
    /// <summary>Joins and waits for the join state (vitals, ground) like main does; a no-op while still connected.</summary>
    public static async Task ConnectAsync(MinecraftClient c, CancellationToken ct)
    {
        if (c.State is ClientState.Playing) return;
        await c.ConnectAsync(ct).ConfigureAwait(false);
        await c.WaitForJoinStateAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
    }

    /// <summary>Completes when the connection ends: a duplicate-login kick is <see cref="BodyExitKind.ExternalLogin"/>, anything else lost.</summary>
    public static async Task<BodyExit> RunAsync(MinecraftClient c, CancellationToken ct)
    {
        var done = new TaskCompletionSource<BodyExit>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<string> kicked = r => done.TrySetResult(BodyExit.IsDuplicateLogin(r) ? new BodyExit(BodyExitKind.ExternalLogin, r) : BodyExit.Lost("kicked: " + r));
        Action<string> gone = r => done.TrySetResult(BodyExit.IsDuplicateLogin(r) ? new BodyExit(BodyExitKind.ExternalLogin, r) : BodyExit.Lost(r));
        c.Kicked += kicked;
        c.Disconnected += gone;
        try
        {
            if (c.State is ClientState.Disconnected) return BodyExit.Lost("not connected");
            using var reg = ct.Register(() => done.TrySetCanceled(ct));
            return await done.Task.ConfigureAwait(false);
        }
        finally
        {
            c.Kicked -= kicked;
            c.Disconnected -= gone;
        }
    }

    public static async Task DisconnectAsync(MinecraftClient c, string reason)
    {
        try { await c.DisconnectAsync(reason).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception) { /* already gone */ }
    }
}
