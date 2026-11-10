using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Events;

namespace MineClaw.Agent.Tests;

/// <summary>Pins <see cref="EventBatch.Format"/> (WP06 refactor): chat grouping per speaker, repeats, [bodies] coalescing, fresh tags.</summary>
public class EventBatchGoldenTests
{
    private const string Expected = "29B5A0BED0770DA612A8717B9695B4E99228B148369CB2EEB80293D810E757DC";

    [Fact]
    public void Batches_format_the_same()
    {
        var t0 = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        List<GameEvent[]> batches =
        [
            [],
            [GameEvent.Chat("fox", "mine the gen"), GameEvent.Chat("kiwi", "hi"), GameEvent.Chat("Fox", "actually the left cell") with { Count = 3 },
                GameEvent.Chat("fox", "not to you", addressed: false), GameEvent.Whisper("fox", "psst"), GameEvent.Whisper("fox", "again")],
            [GameEvent.TaskDone("64 cobble") with { Body = "maid_chan_67" }, GameEvent.Chat("fox", "nice"), GameEvent.TaskFail("no free cell") with { Body = "maid_chan_69" },
                GameEvent.TaskDone("main done"), GameEvent.Emote("kiwi", "pats you"), GameEvent.LowHealth(5)],
            [GameEvent.TaskDone("one body") with { Body = "maid_chan_67" }, GameEvent.TaskDone("same body") with { Body = "maid_chan_67" },
                GameEvent.Gesture("fox", "nods yes at you", EventPriority.Normal), GameEvent.Notice("budget economy")],
            [GameEvent.Emote("kiwi", "waves", addressed: false), GameEvent.Joined("bob"), GameEvent.Chat("bob", "yo") with { Wakes = false },
                GameEvent.Chat("bob", "[weird] text"), GameEvent.Activity("kiwi probably built a gen", "kiwi")],
        ];
        var sb = new StringBuilder();
        foreach (var b in batches)
        {
            var batch = new EventBatch(b.Select((e, i) => e with { At = t0.AddSeconds(i) }).ToList());
            sb.Append("--\n").Append(batch.Format().Replace("\r", "")).Append("\n-- fresh\n").Append(batch.Format(fresh: true).Replace("\r", "")).Append('\n');
        }
        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "event-batch.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
