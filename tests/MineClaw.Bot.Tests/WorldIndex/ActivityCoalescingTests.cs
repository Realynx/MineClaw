using System.Globalization;
using MineClaw.Agent.Memory.Grooming;
using MineClaw.Bot.WorldIndex;
using MineClaw.World;

namespace MineClaw.Bot.Tests.WorldIndex;

/// <summary>The live write path: ActivityLog rollups go through the memory groomer and coalesce into a few episode docs.</summary>
public sealed class ActivityCoalescingTests
{
    private static ActivityRollup Rollup(string player, DateTimeOffset start, DateTimeOffset end, string what, PlaceRef place)
    {
        var shortText = $"{player} {what} at the {place.Name}";
        var times = string.Create(CultureInfo.InvariantCulture, $" ({start.ToLocalTime():MM-dd HH:mm}-{end.ToLocalTime():HH:mm})");
        return new ActivityRollup(player, start, end, shortText + times, 0.9)
        {
            Place = place, Min = new BlockPos(1, 64, 1), Max = new BlockPos(2, 64, 2), Events = 3, Short = shortText,
            Counts = new Dictionary<string, int> { ["mined"] = 3 },
        };
    }

    [Fact]
    public void A_minute_by_minute_gen_stream_becomes_at_most_two_episodes_and_rollups_point_at_them()
    {
        using var f = new IndexFixture();
        f.Rag.Groomer = new MemoryGroomer(new GroomOptions(), new GroomEnvironment
        {
            Db = f.Db, MainName = "maid_chan", Crew = ["maid_chan", "maid_chan_67"], Time = f.Clock,
        });
        var log = new ActivityLog(f.Db, f.Rag, new ActivityOptions(), f.Clock);
        var gen = new PlaceRef("generator", "gen@1,64,1", "cobble gen", new BlockPos(1, 64, 1), new BlockPos(2, 64, 2));
        // 08:00 local: the rollup text carries local wall times, and the stream must not cross local midnight.
        var t0 = new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 28, 8, 0, 0)));
        for (int m = 0; m < 180; m++)
        {
            var s = t0.AddMinutes(m);
            log.AddRollup(Rollup(m % 2 == 0 ? "maid_chan" : "maid_chan_67", s, s.AddSeconds(45), "mined 3 cobblestone", gen), []);
        }
        var docs = f.Db.Query("SELECT ref, body FROM docs WHERE source = 'activity' ORDER BY id", r => (Ref: r.GetString(0), Body: r.GetString(1)));
        Assert.InRange(docs.Count, 1, 2);
        Assert.All(docs, d => Assert.StartsWith("the crew (main, 67) mined", d.Body));
        Assert.Equal(540, docs.Sum(d => int.Parse(System.Text.RegularExpressions.Regex.Match(d.Body, @"mined (\d+) cobblestone").Groups[1].Value, CultureInfo.InvariantCulture)));
        // Every rollup row now names the episode doc that holds it.
        var refs = f.Db.Query("SELECT DISTINCT doc_ref FROM activity_rollups", r => r.GetString(0)).ToHashSet();
        Assert.Equal(docs.Select(d => d.Ref).ToHashSet(), refs);
        Assert.Equal(180, f.Db.ScalarLong("SELECT count(*) FROM activity_rollups"));
    }
}
