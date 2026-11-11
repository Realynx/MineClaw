using System.Text.RegularExpressions;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>The test classes that take the process-wide story perf figures (<see cref="StoryPerf.TakeInside"/>) run one at a time.</summary>
[CollectionDefinition("story-perf")]
public sealed class StoryPerfCollection;

/// <summary>
/// The haul search (work:haul: live 11-10 about 0.9 s a call, which the bench in the live storage shape does not reproduce) is split on
/// the perf line, so the live line says where its time goes: the chest-memory sweep, the world's entities, the drops, the re-sort and the
/// look lanes ("haul parts"); the host's list of chest looks is timed ("chest-looks"); and the most entities and drops one haul search
/// saw are printed ("haul scans"), so the live drop count is known.
/// </summary>
[Collection("story-perf")]
public sealed class StoryHaulPerfLineTests
{
    [Fact]
    public void The_haul_search_shows_its_parts_and_its_drop_count_on_the_perf_line()
    {
        var r = StoryHaulFixture.Make(drops: 60);
        var n = r.Names[0];
        r.C.Candidate(WorkTypes.Haul, r.C.Pawns[n], r.H.Live(n)); // every chest's memory asked: the 5 s ask window starts
        StoryPerf.TakeInside(); // start over
        r.Clock.Now += TimeSpan.FromSeconds(6); // the window is over: the next sweep reads the list of chest looks again
        r.C.Candidate(WorkTypes.Haul, r.C.Pawns[n], r.H.Live(n));
        // Other test classes haul at the same time (more parts, more scans): at least this search's figures are there.
        var line = StoryPerf.TakeInside();
        var parts = line[line.IndexOf("haul parts:", StringComparison.Ordinal)..line.IndexOf(", chest-looks", StringComparison.Ordinal)];
        foreach (var part in new[] { "seen", "ents", "drops" }) Assert.Matches($@"\b{part} [1-9]\d*x \d+ ms max \d+ ms", parts);
        var m = Regex.Match(line, @", chest-looks ([1-9]\d*)x \d+ ms max \d+ ms, haul scans ([1-9]\d*): entities max (\d+), drops max (\d+)$");
        Assert.True(m.Success, line);
        Assert.True(int.Parse(m.Groups[4].Value) >= 60, line);
        Assert.True(int.Parse(m.Groups[3].Value) >= int.Parse(m.Groups[4].Value), line);
    }
}
