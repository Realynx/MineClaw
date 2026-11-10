using System.Security.Cryptography;
using System.Text;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests.WorldIndex;

/// <summary>
/// Pins the session rollup sentence (<c>PlayerActivityTracker.Rollup</c>: "kiwi mined 23 cobblestone, placed 6 planks and opened
/// the chest at the cobble gen", WP03 refactor): seeded random sessions over every rolled-up kind, confidence level and place
/// kind; text, short text, confidence, salience and category counts hashed.
/// </summary>
public class ActivityRollupGoldenTests
{
    private static readonly string[] Kinds =
    [
        ActivityKinds.Broke, ActivityKinds.Harvested, ActivityKinds.Planted, ActivityKinds.Placed, ActivityKinds.WroteSign, ActivityKinds.Opened,
        ActivityKinds.Took, ActivityKinds.Stored, ActivityKinds.LitFurnace, ActivityKinds.Crafted, ActivityKinds.PickedUp, ActivityKinds.Fought,
        ActivityKinds.Died, ActivityKinds.Chat, ActivityKinds.Equipped,
    ];

    private static string Dump()
    {
        using var f = new IndexFixture();
        var tracker = new PlayerActivityTracker(f.World, new ManualPlayerEvents(), null, null, new ActivityOptions(), "maid_chan", time: f.Clock);
        var rnd = new Random(1104);
        var t0 = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        PlaceRef?[] places = [null, new PlaceRef(IndexKinds.Island, "0,0", "main island", default, default),
            new PlaceRef(IndexKinds.Generator, "1,64,1", "cobble gen", default, default),
            new PlaceRef(IndexKinds.UnknownStructure, "u1", "unknown build", default, default),
            new PlaceRef(IndexKinds.Chest, "4,64,4", "chest", default, default)];
        int[] blocks = [Blocks.Stone, Blocks.Cobblestone, Blocks.Log, Blocks.Crops, Blocks.Planks, Blocks.Dirt];
        int[] items = [Items.IronIngot, Blocks.Cobblestone, Items.Bread, Items.Stick];
        double[] confs = [0.3, 0.55, 0.7, 0.95];
        var sb = new StringBuilder();
        for (int n = 0; n < 400; n++)
        {
            var events = new List<ActivityEvent>();
            int count = rnd.Next(0, 12);
            for (int i = 0; i < count; i++)
            {
                var kind = Kinds[rnd.Next(Kinds.Length)];
                var place = places[rnd.Next(places.Length)];
                events.Add(new ActivityEvent("kiwi", kind, t0.AddSeconds(i * 7), confs[rnd.Next(confs.Length)])
                {
                    BlockId = rnd.Next(5) == 0 ? null : blocks[rnd.Next(blocks.Length)],
                    ItemId = rnd.Next(5) == 0 ? null : items[rnd.Next(items.Length)],
                    Count = rnd.Next(1, 30),
                    Pos = rnd.Next(3) == 0 ? null : new BlockPos(rnd.Next(-3, 4), 64, rnd.Next(-3, 4)),
                    Place = place,
                    Detail = kind == ActivityKinds.Died ? (rnd.Next(2) == 0 ? null : "kiwi fell from a high place")
                        : kind == ActivityKinds.Fought ? "zombie" : kind == ActivityKinds.WroteSign ? "home" : null,
                });
            }
            var session = places[rnd.Next(places.Length)];
            var r = tracker.Rollup(events, "kiwi", session, t0, t0.AddMinutes(3), new BlockPos(-3, 64, -3), new BlockPos(3, 64, 3), includeTimes: false);
            sb.Append(n).Append('|');
            if (r is null) { sb.Append("null\n"); continue; }
            sb.Append(r.Text).Append('|').Append(r.Short).Append('|').Append(r.Confidence.ToString("R")).Append('|')
                .Append(r.Salience.ToString("R")).Append('|').Append(r.Events).Append('|')
                .Append(string.Join(",", r.Counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value))).Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void Rollup_text_matches_the_golden_hash()
    {
        var dump = Dump();
        var outDir = Environment.GetEnvironmentVariable("WP03_GOLDEN_OUT");
        if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, "activity-rollup.txt"), dump);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)))[..24];
        Assert.Equal(Expected, hash);
    }

    /// <summary>Generated from the pre-refactor code (WP03).</summary>
    private const string Expected = "F35DC3CCCBF0C521BFCD7D10";
}
