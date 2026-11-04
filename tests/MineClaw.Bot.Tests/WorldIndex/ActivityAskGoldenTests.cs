using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests.WorldIndex;

/// <summary>
/// Pins <see cref="ActivityLog.Ask"/> (WP03 refactor) word for word: time windows (the default day, last hour, this week, an
/// explicit since), named players or everyone, rollups then raw events (repeats collapsed, "probably" for medium
/// confidence), the full-text fallback and the empty answers, at several output caps. Only windows relative to now (the
/// local-midnight ones depend on the machine's time zone) and clock times masked, so the golden holds in any zone.
/// </summary>
public class ActivityAskGoldenTests
{
    private static readonly string[] Questions =
    [
        "what did robin do", "what did kiwi do", "what happened", "robin last hour", "kiwi this week", "what did everyone do lately",
        "who mined cobblestone", "what did fox do", "anything recent?", "robin and kiwi past hour", "glass", "nothing like this",
    ];

    private static string Mask(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\b\d\d:\d\d\b", "hh:mm");

    [Fact]
    public void Ask_answers_match_the_golden_text()
    {
        using var f = new IndexFixture();
        var log = new ActivityLog(f.Db, f.Rag, new ActivityOptions(), f.Clock);
        var t0 = f.Clock.GetUtcNow();
        var events = new List<ActivityEvent>();
        void E(string who, string kind, double hoursAgo, double conf, int? block = null, int count = 1, string? detail = null) =>
            events.Add(new ActivityEvent(who, kind, t0 - TimeSpan.FromHours(hoursAgo), conf)
                { BlockId = block, Count = count, Detail = detail, Pos = new BlockPos(3, 64, -2) });
        E("robin", ActivityKinds.Broke, 0.2, 0.9, Blocks.Cobblestone);
        E("robin", ActivityKinds.Broke, 0.19, 0.9, Blocks.Cobblestone);
        E("robin", ActivityKinds.Broke, 0.18, 0.9, Blocks.Cobblestone);
        E("robin", ActivityKinds.Placed, 0.5, 0.5, Blocks.Glass);
        E("robin", ActivityKinds.Chat, 2, 1, detail: "brb food");
        E("kiwi", ActivityKinds.Placed, 3, 0.9, Blocks.Planks, 12);
        E("kiwi", ActivityKinds.Died, 30, 0.95, detail: "fell into the void");
        E("kiwi", ActivityKinds.Harvested, 50, 0.9, Blocks.Crops, 7);
        E("robin", ActivityKinds.Opened, 0.1, 0.3);
        var stored = log.Append(events);
        log.AddRollup(new ActivityRollup("kiwi", t0 - TimeSpan.FromHours(4), t0 - TimeSpan.FromHours(3), "kiwi built 12 planks (rolled up)", 0.9)
            { Short = "kiwi built 12 planks" }, stored.Where(e => e.Player == "kiwi" && e.Kind == ActivityKinds.Placed).Select(e => e.Id));
        log.AddRollup(new ActivityRollup("robin", t0 - TimeSpan.FromHours(26), t0 - TimeSpan.FromHours(25), "robin fished for an hour", 0.8)
            { Short = "robin fished" }, []);
        var actual = new List<string>();
        foreach (var q in Questions)
            foreach (var cap in new[] { 500, 60 })
                actual.Add($"{q} [{cap}] => {Mask(log.Ask(q, t0, cap))}");
        actual.Add("since => " + Mask(log.Ask("what did robin do", t0, 500, t0 - TimeSpan.FromMinutes(15))));
        var outDir = Environment.GetEnvironmentVariable("WP03_GOLDEN_OUT");
        if (outDir is { Length: > 0 }) { File.WriteAllLines(Path.Combine(outDir, "activity-ask.txt"), actual); return; }
        Assert.Equal(Expected, actual);
    }

    /// <summary>Generated from the pre-refactor code (WP03).</summary>
    private static readonly string[] Expected =
    [
        "what did robin do [500] => robin said \"brb food\" hh:mm; probably robin placed glass at 3,64,-2 hh:mm; robin mined cobblestone at 3,64,-2 x3 hh:mm",
        "what did robin do [60] => robin said \"brb food\" hh:mm",
        "what did kiwi do [500] => kiwi built 12 planks (rolled up)",
        "what did kiwi do [60] => kiwi built 12 planks (rolled up)",
        "what happened [500] => kiwi built 12 planks (rolled up); robin said \"brb food\" hh:mm; probably robin placed glass at 3,64,-2 hh:mm; robin mined cobblestone at 3,64,-2 x3 hh:mm",
        "what happened [60] => kiwi built 12 planks (rolled up)",
        "robin last hour [500] => probably robin placed glass at 3,64,-2 hh:mm; robin mined cobblestone at 3,64,-2 x3 hh:mm",
        "robin last hour [60] => probably robin placed glass at 3,64,-2 hh:mm",
        "kiwi this week [500] => kiwi built 12 planks (rolled up); kiwi harvested 7 wheat at 3,64,-2 hh:mm; kiwi fell into the void hh:mm",
        "kiwi this week [60] => kiwi built 12 planks (rolled up)",
        "what did everyone do lately [500] => robin fished for an hour; kiwi built 12 planks (rolled up); kiwi harvested 7 wheat at 3,64,-2 hh:mm; kiwi fell into the void hh:mm; robin said \"brb food\" hh:mm; probably robin placed glass at 3,64,-2 hh:mm; robin mined cobblestone at 3,64,-2 x3 hh:mm",
        "what did everyone do lately [60] => robin fished for an hour; kiwi built 12 planks (rolled up)",
        "who mined cobblestone [500] => kiwi built 12 planks (rolled up); robin said \"brb food\" hh:mm; probably robin placed glass at 3,64,-2 hh:mm; robin mined cobblestone at 3,64,-2 x3 hh:mm",
        "who mined cobblestone [60] => kiwi built 12 planks (rolled up)",
        "what did fox do [500] => kiwi built 12 planks (rolled up); robin said \"brb food\" hh:mm; probably robin placed glass at 3,64,-2 hh:mm; robin mined cobblestone at 3,64,-2 x3 hh:mm",
        "what did fox do [60] => kiwi built 12 planks (rolled up)",
        "anything recent? [500] => kiwi built 12 planks (rolled up); robin said \"brb food\" hh:mm; probably robin placed glass at 3,64,-2 hh:mm; robin mined cobblestone at 3,64,-2 x3 hh:mm",
        "anything recent? [60] => kiwi built 12 planks (rolled up)",
        "robin and kiwi past hour [500] => probably robin placed glass at 3,64,-2 hh:mm; robin mined cobblestone at 3,64,-2 x3 hh:mm",
        "robin and kiwi past hour [60] => probably robin placed glass at 3,64,-2 hh:mm",
        "glass [500] => kiwi built 12 planks (rolled up); robin said \"brb food\" hh:mm; probably robin placed glass at 3,64,-2 hh:mm; robin mined cobblestone at 3,64,-2 x3 hh:mm",
        "glass [60] => kiwi built 12 planks (rolled up)",
        "nothing like this [500] => kiwi built 12 planks (rolled up); robin said \"brb food\" hh:mm; probably robin placed glass at 3,64,-2 hh:mm; robin mined cobblestone at 3,64,-2 x3 hh:mm",
        "nothing like this [60] => kiwi built 12 planks (rolled up)",
        "since => robin mined cobblestone at 3,64,-2 x3 hh:mm",
    ];
}
