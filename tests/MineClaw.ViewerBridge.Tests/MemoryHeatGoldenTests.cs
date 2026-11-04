using System.Text;
using System.Text.Json;
using MineClaw.ViewerBridge.Live;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>
/// Pins <see cref="MemoryHeat.Compute"/> (WP07) without a tree (each memory its own node): seeded reads across brains (inside and
/// outside the window, a few seconds ahead, unknown docs falling back to their node or dropped), in-context and evicted copies (fresh and
/// stale), a brain filter, a zero half-life. The whole response is dumped as JSON.
/// </summary>
public class MemoryHeatGoldenTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Brains = ["main", "Hive-2", "hive-2", "maid_chan_67"];

    internal static string Dump()
    {
        var rng = new Random(77);
        var events = new List<MemoryEvent>();
        for (int i = 0; i < 120; i++)
        {
            var at = At.AddSeconds(-rng.Next(-20, 1200));
            string id = rng.Next(6) == 0 ? "x" + i : rng.Next(1, 30).ToString(System.Globalization.CultureInfo.InvariantCulture);
            int? node = rng.Next(3) == 0 ? rng.Next(100, 110) : null;
            events.Add(new MemoryEvent(i, at, Brains[rng.Next(Brains.Length)], null, "fact", id, node, "rag", null, null));
        }
        var inCtx = new List<InContext>();
        for (int i = 0; i < 40; i++)
        {
            var seen = At.AddSeconds(-rng.Next(0, 2000));
            string id = rng.Next(5) == 0 ? "y" + i : rng.Next(1, 30).ToString(System.Globalization.CultureInfo.InvariantCulture);
            inCtx.Add(new InContext(Brains[rng.Next(Brains.Length)], "fact", id, rng.Next(4) == 0 ? rng.Next(100, 110) : null, seen.AddMinutes(-5), seen,
                rng.Next(1, 9), rng.Next(3) == 0 ? null : rng.Next(0, 50)));
        }
        int? NodeOfDoc(long doc) => doc % 7 == 0 ? null : (int)(doc % 13);
        var sb = new StringBuilder();
        foreach (var (window, half, brain) in new[] { (900, 60.0, (string?)null), (900, 60.0, "hive-2"), (300, 0.0, null), (3000, 600.0, "main"), (900, 60.0, "nobody") })
        {
            var r = MemoryHeat.Compute(events, inCtx, null, NodeOfDoc, "state-1", At, window, half, brain);
            sb.Append($"== window {window} half {half} brain {brain ?? "-"}\n").Append(JsonSerializer.Serialize(r)).Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void Heat_matches_the_golden_dump()
    {
        var actual = Dump().ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("WP07_GOLDEN_OUT") is { Length: > 0 } dir) File.WriteAllText(Path.Combine(dir, "memory-heat.txt"), actual);
        Assert.Equal(MemoryHeatGoldenData.Expected.ReplaceLineEndings("\n").TrimEnd('\n'), actual.TrimEnd('\n'));
    }
}
