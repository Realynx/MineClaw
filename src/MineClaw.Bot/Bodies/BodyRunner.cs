using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Bot.Bodies;

/// <summary>Why a body's connection ended.</summary>
public enum BodyExitKind
{
    /// <summary>We logged it out (despawn, shutdown).</summary>
    Stopped,
    /// <summary>Kicked or the connection dropped: reconnect after a backoff (not a crash).</summary>
    Lost,
    /// <summary>"You logged in from another location": someone else (a player's viewer) has the account. Never fight the login.</summary>
    ExternalLogin,
}

public sealed record BodyExit(BodyExitKind Kind, string Reason)
{
    public static BodyExit Stopped(string reason = "stopped") => new(BodyExitKind.Stopped, reason);
    public static BodyExit Lost(string reason) => new(BodyExitKind.Lost, reason);

    /// <summary>The server's kick text when another session logs in with the same name (vanilla/CraftBukkit b1.8.1).</summary>
    public static bool IsDuplicateLogin(string? reason) =>
        reason != null && reason.Contains("logged in from another location", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What one body's session is made of (the registry's host supplies the real client's; tests supply fakes).</summary>
/// <param name="Gate">Waits for this login's turn (the launch gap), before every connect.</param>
/// <param name="Connect">Joins the server; throws on failure (retried with backoff, not a crash).</param>
/// <param name="Run">Runs until the connection ends; an exception is a crash (counted, restart after backoff).</param>
/// <param name="Reset">After a crash: drop whatever is left of the connection before the restart.</param>
public sealed record BodySession(Func<CancellationToken, Task> Gate, Func<CancellationToken, Task> Connect,
    Func<CancellationToken, Task<BodyExit>> Run, Func<Task> Reset);

/// <summary>
/// The per-body supervisor loop (one brain, many bodies; the hive supervisor's CloneSlot rules, ported): connect with
/// backoff, run, catch everything, restart only this body. A crash is any exception out of the session (or one reported by
/// <see cref="Fault"/>); <see cref="BodiesOptions.MaxCrashes"/> within <see cref="BodiesOptions.CrashWindowMinutes"/> retire it.
/// A lost connection reconnects with a growing delay; a duplicate-login kick ends in <see cref="BodyState.Occupied"/>.
/// </summary>
public sealed class BodyRunner : IAsyncDisposable
{
    private readonly BodySession _session;
    private readonly BodiesOptions _o;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly List<DateTimeOffset> _crashes = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _current;
    private Exception? _fault;
    private Task _loop = Task.CompletedTask;
    private BodyState _state = BodyState.Offline;

    public BodyRunner(string name, BodySession session, BodiesOptions? options = null, ILogger? log = null, TimeProvider? time = null)
    {
        Name = name;
        _session = session;
        _o = options ?? new BodiesOptions();
        _log = log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    public string Name { get; }

    public BodyState State { get { lock (_lock) return _state; } }

    /// <summary>Crashes still inside the window.</summary>
    public IReadOnlyList<DateTimeOffset> Crashes { get { lock (_lock) return _crashes.ToArray(); } }

    /// <summary>Sessions run so far (the first one included).</summary>
    public int Sessions { get; private set; }

    /// <summary>The loop; completes when the body is stopped, retired or occupied.</summary>
    public Task Completion => _loop;

    public bool IsRunning => !_loop.IsCompleted;

    public event Action<BodyState>? StateChanged;
    /// <summary>(exception, retired): a crash, counted. Raised on the runner's flow; handlers must not throw (they're guarded).</summary>
    public event Action<Exception, bool>? Crashed;
    /// <summary>The loop ended (stopped, external login, retired), with the last exit.</summary>
    public event Action<BodyExit>? Ended;

    public void Start(CancellationToken outer = default)
    {
        lock (_lock)
        {
            if (!_loop.IsCompleted) return;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            _fault = null;
            var ct = _cts.Token;
            _loop = Task.Run(() => LoopAsync(ct), CancellationToken.None);
        }
    }

    /// <summary>Stops the loop (the session's token is cancelled; the host logs the connection out) and waits for it.</summary>
    public async Task StopAsync()
    {
        Task loop;
        lock (_lock)
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            loop = _loop;
        }
        try { await loop.ConfigureAwait(false); } catch (Exception) { /* the loop never throws; belt and braces */ }
    }

    /// <summary>A part of this body failed beyond repair (an unobserved fault in its wiring): end the session as a crash.</summary>
    public void Fault(Exception ex)
    {
        lock (_lock)
        {
            _fault ??= ex;
            try { _current?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private void Set(BodyState s)
    {
        lock (_lock)
        {
            if (_state == s) return;
            _state = s;
        }
        try { StateChanged?.Invoke(s); } catch (Exception ex) { _log.LogDebug(ex, "body {Name}: state handler failed", Name); }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var exit = BodyExit.Stopped();
        var reconnect = TimeSpan.FromSeconds(Math.Max(0, _o.ReconnectSeconds));
        var maxWait = TimeSpan.FromSeconds(Math.Max(0, _o.BackoffMaxSeconds));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Set(BodyState.Connecting);
                try
                {
                    await _session.Gate(ct).ConfigureAwait(false);
                    await _session.Connect(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _log.LogWarning("body {Name}: connect failed: {Error}; retrying in {S:0.#}s", Name, ex.Message, reconnect.TotalSeconds);
                    if (!await DelayAsync(reconnect, ct).ConfigureAwait(false)) break;
                    reconnect = Next(reconnect, maxWait);
                    continue;
                }

                Set(BodyState.Online);
                Sessions++;
                var started = _time.GetUtcNow();
                using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock (_lock) _current = session;
                Exception? crash = null;
                try { exit = await _session.Run(session.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { exit = BodyExit.Stopped(); }
                catch (Exception ex) { crash = ex; }
                lock (_lock)
                {
                    _current = null;
                    if (_fault != null) { crash = _fault; _fault = null; }
                }
                if (ct.IsCancellationRequested && crash == null) break;

                if (crash != null)
                {
                    var (count, retired) = Record(crash);
                    try { await _session.Reset().ConfigureAwait(false); } catch (Exception ex) { _log.LogDebug(ex, "body {Name}: reset failed", Name); }
                    if (retired)
                    {
                        _log.LogError(crash, "body {Name} crashed {N} times in {W:0} min: retired for this session", Name, count, _o.CrashWindowMinutes);
                        exit = new BodyExit(BodyExitKind.Stopped, "retired: " + crash.Message);
                        Set(BodyState.Retired);
                        RaiseCrashed(crash, true);
                        return;
                    }
                    var backoff = TimeSpan.FromSeconds(Math.Min(_o.BackoffMaxSeconds, _o.BackoffBaseSeconds * Math.Pow(2, count - 1)));
                    _log.LogWarning(crash, "body {Name} crashed ({N}/{Max}); restarting it alone in {S:0.#}s", Name, count, _o.MaxCrashes, backoff.TotalSeconds);
                    Set(BodyState.Offline);
                    RaiseCrashed(crash, false);
                    if (!await DelayAsync(backoff, ct).ConfigureAwait(false)) break;
                    continue;
                }

                if (exit.Kind == BodyExitKind.Stopped) break;
                if (exit.Kind == BodyExitKind.ExternalLogin)
                {
                    _log.LogWarning("body {Name}: someone else logged in with this account ({Reason}); not reconnecting", Name, exit.Reason);
                    Set(BodyState.Occupied);
                    return;
                }
                // Lost (kick, network): reconnect. A session that lasted a while starts the backoff over.
                if (_time.GetUtcNow() - started > TimeSpan.FromMinutes(1)) reconnect = TimeSpan.FromSeconds(Math.Max(0, _o.ReconnectSeconds));
                _log.LogInformation("body {Name}: connection lost ({Reason}); reconnecting in {S:0.#}s", Name, exit.Reason, reconnect.TotalSeconds);
                Set(BodyState.Connecting);
                if (!await DelayAsync(reconnect, ct).ConfigureAwait(false)) break;
                reconnect = Next(reconnect, maxWait);
            }
            Set(BodyState.Offline);
        }
        catch (Exception ex)
        {
            // Never reached in practice (every await above is guarded); a runner must never take the process down.
            _log.LogError(ex, "body {Name}: runner loop failed", Name);
            Set(BodyState.Offline);
        }
        finally
        {
            try { Ended?.Invoke(exit); } catch (Exception ex) { _log.LogDebug(ex, "body {Name}: ended handler failed", Name); }
        }
    }

    private (int Count, bool Retired) Record(Exception crash)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            _crashes.Add(now);
            _crashes.RemoveAll(t => now - t > TimeSpan.FromMinutes(_o.CrashWindowMinutes));
            return (_crashes.Count, _crashes.Count >= Math.Max(1, _o.MaxCrashes));
        }
    }

    private void RaiseCrashed(Exception ex, bool retired)
    {
        try { Crashed?.Invoke(ex, retired); } catch (Exception e) { _log.LogDebug(e, "body {Name}: crash handler failed", Name); }
    }

    private static TimeSpan Next(TimeSpan t, TimeSpan max) => TimeSpan.FromTicks(Math.Min(Math.Max(t.Ticks * 2, TimeSpan.FromSeconds(1).Ticks), Math.Max(max.Ticks, t.Ticks)));

    private async Task<bool> DelayAsync(TimeSpan t, CancellationToken ct)
    {
        if (t <= TimeSpan.Zero) return !ct.IsCancellationRequested;
        try { await Task.Delay(t, _time, ct).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_lock) { _cts?.Dispose(); _cts = null; }
    }
}
