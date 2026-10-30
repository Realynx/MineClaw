namespace MineClaw.Bot.Bodies;

/// <summary>
/// Login pacing for bodies and clones from one IP: up to <c>batch</c> logins go at once, the next batch <c>gap</c> after the last
/// one started. b1.8.1's server throttles joins from one IP &lt; ~4 s apart (batch 1); vanilla 1.12.2 has no throttle
/// (NetHandlerHandshakeTCP/NetHandlerLoginServer), so its bodies log in 10 at a time (live 10-30: 25 bodies 4.5 s apart took ~2 min).
/// </summary>
public sealed class JoinPacer(Func<DateTimeOffset> now)
{
    private readonly object _gate = new();
    private DateTimeOffset? _start;
    private int _count;

    /// <summary>How long this login waits for its turn.</summary>
    public TimeSpan Next(TimeSpan gap, int batch)
    {
        batch = Math.Max(1, batch);
        lock (_gate)
        {
            var t = now();
            if (_start is not { } s || _count >= batch || t >= s + gap)
            {
                var at = _start is { } p && _count >= batch ? (p + gap > t ? p + gap : t) : t;
                if (_start is { } q && at < q) at = q;
                _start = at;
                _count = 0;
            }
            _count++;
            return _start.Value > t ? _start.Value - t : TimeSpan.Zero;
        }
    }
}
