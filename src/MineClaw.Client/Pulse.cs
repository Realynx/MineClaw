namespace MineClaw.Client;

/// <summary>
/// A "something changed" signal: <see cref="Pulse"/> wakes everyone awaiting <see cref="Next"/>. Used for race-free
/// condition waits: capture <see cref="Next"/>, check the condition, then await the captured task.
/// </summary>
internal sealed class ChangeSignal
{
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Next => Volatile.Read(ref _tcs).Task;

    public void Pulse()
    {
        var old = Interlocked.Exchange(ref _tcs, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        old.TrySetResult();
    }

    /// <summary>Waits until <paramref name="condition"/> holds (re-checked on every pulse). False on timeout.</summary>
    public async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var next = Next;
            if (condition()) return true;
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return false;
            try
            {
                await next.WaitAsync(left < TimeSpan.FromMilliseconds(250) ? left : TimeSpan.FromMilliseconds(250), ct)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException) { /* re-check */ }
        }
    }
}
