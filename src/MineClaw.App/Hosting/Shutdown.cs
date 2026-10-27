using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Hosting;

/// <summary>
/// SIGTERM / SIGINT / SIGQUIT (Ctrl+C, Ctrl+Break, console close on Windows) → one cancellation of <see cref="Token"/>,
/// so the host shuts down gracefully and exits 0. A second signal while shutting down falls through to the default
/// (immediate termination).
/// </summary>
public sealed class ShutdownSignals : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<PosixSignalRegistration> _regs = new();
    private int _signalled;

    public ShutdownSignals(Action<string>? onSignal = null)
    {
        foreach (var sig in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT })
        {
            try
            {
                _regs.Add(PosixSignalRegistration.Create(sig, ctx =>
                {
                    if (Interlocked.Increment(ref _signalled) > 1) return; // second signal: default action (terminate)
                    ctx.Cancel = true;
                    Reason ??= ctx.Signal.ToString();
                    onSignal?.Invoke(ctx.Signal.ToString());
                    _cts.Cancel();
                }));
            }
            catch (PlatformNotSupportedException) { }
        }
    }

    public CancellationToken Token => _cts.Token;
    public CancellationTokenSource Source => _cts;
    public string? Reason { get; private set; }

    /// <summary>Requests shutdown from inside the app (/quit, q in the TUI, --minutes).</summary>
    public void Request(string reason)
    {
        Reason ??= reason;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        foreach (var r in _regs) r.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// Ordered shutdown steps under one deadline: each step gets min(its cap, time left); a slow step is abandoned (logged),
/// never allowed to eat the budget of the steps after it — flushing the db/logs always runs.
/// </summary>
public sealed class ShutdownSequence(TimeSpan budget, ILogger log)
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly List<string> _timedOut = new();

    public TimeSpan Remaining => budget - _sw.Elapsed;
    public IReadOnlyList<string> TimedOut => _timedOut;

    public async Task StepAsync(string name, TimeSpan cap, Func<CancellationToken, Task> step)
    {
        var allowed = cap < Remaining ? cap : Remaining;
        if (allowed <= TimeSpan.Zero)
        {
            _timedOut.Add(name);
            log.LogWarning("shutdown: skipping {Step} (budget used up)", name);
            return;
        }
        using var cts = new CancellationTokenSource(allowed);
        try { await step(cts.Token).WaitAsync(allowed).ConfigureAwait(false); }
        catch (TimeoutException) { _timedOut.Add(name); log.LogWarning("shutdown: {Step} timed out after {S:0.0}s", name, allowed.TotalSeconds); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { _timedOut.Add(name); log.LogWarning("shutdown: {Step} cancelled after {S:0.0}s", name, allowed.TotalSeconds); }
        catch (Exception ex) { log.LogWarning("shutdown: {Step} failed: {Error}", name, ex.Message); }
    }

    /// <summary>Synchronous final steps (dispose/flush): always run, errors logged.</summary>
    public void Final(string name, Action step)
    {
        try { step(); }
        catch (Exception ex) { log.LogWarning("shutdown: {Step} failed: {Error}", name, ex.Message); }
    }
}
