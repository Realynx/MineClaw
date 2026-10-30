using MineClaw.Bot.Bodies;

namespace MineClaw.Bot.Tests;

public class JoinPacerTests
{
    private static List<double> Waits(int n, int batch, double gapS, Func<int, double>? at = null)
    {
        var t0 = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        double now = 0;
        var p = new JoinPacer(() => t0 + TimeSpan.FromSeconds(now));
        var waits = new List<double>();
        for (int i = 0; i < n; i++)
        {
            now = at?.Invoke(i) ?? 0;
            waits.Add(Math.Round(p.Next(TimeSpan.FromSeconds(gapS), batch).TotalSeconds, 3));
        }
        return waits;
    }

    [Fact]
    public void Twenty_five_logins_go_ten_at_a_time_batches_a_gap_apart()
    {
        // Live 10-30: 25 bodies 4.5 s apart took ~2 min to log in on 1.12.2, which has no join throttle.
        var w = Waits(25, 10, 4.5);
        Assert.All(w.Take(10), x => Assert.Equal(0, x));
        Assert.All(w.Skip(10).Take(10), x => Assert.Equal(4.5, x));
        Assert.All(w.Skip(20), x => Assert.Equal(9, x));
    }

    [Fact]
    public void Batch_one_is_the_old_b181_spacing()
    {
        Assert.Equal([0, 4.5, 9, 13.5], Waits(4, 1, 4.5));
    }

    [Fact]
    public void A_login_after_the_gap_starts_a_new_batch_at_once()
    {
        // Three now, one 10 s later: no wait (its batch is new).
        var w = Waits(4, 10, 4.5, i => i < 3 ? 0 : 10);
        Assert.Equal([0, 0, 0, 0], w);
    }
}
