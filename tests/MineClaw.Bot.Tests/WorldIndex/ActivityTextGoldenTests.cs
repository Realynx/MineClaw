using System.Security.Cryptography;
using System.Text;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests.WorldIndex;

/// <summary>
/// Pins <see cref="ActivityText.Describe"/> and <see cref="ActivityKinds.CategoryOf"/> (WP03 refactor) over every kind (and an
/// unknown one) × block/item/count/place/position/detail shapes: one hash of the whole dump, the sentences being what the
/// activity log and digest say about players.
/// </summary>
public class ActivityTextGoldenTests
{
    private static readonly string[] Kinds =
    [
        ActivityKinds.Broke, ActivityKinds.Placed, ActivityKinds.Opened, ActivityKinds.LitFurnace, ActivityKinds.PickedUp, ActivityKinds.Dropped,
        ActivityKinds.Equipped, ActivityKinds.Hurt, ActivityKinds.Fought, ActivityKinds.Died, ActivityKinds.Slept, ActivityKinds.Rode,
        ActivityKinds.WroteSign, ActivityKinds.Joined, ActivityKinds.Left, ActivityKinds.Chat, ActivityKinds.Arrived, ActivityKinds.Departed,
        ActivityKinds.Crafted, ActivityKinds.Took, ActivityKinds.Stored, ActivityKinds.Harvested, ActivityKinds.Planted, "waved", "",
    ];

    private static string Dump()
    {
        var sb = new StringBuilder();
        var at = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        PlaceRef?[] places = [null, new PlaceRef(IndexKinds.Island, "0,0", "main island", default, default),
            new PlaceRef(IndexKinds.Generator, "1,64,1", "cobble gen", default, default)];
        BlockPos?[] positions = [null, new BlockPos(3, 64, -2)];
        int?[] blocks = [null, Blocks.Stone, Blocks.Crops, Blocks.Log, Blocks.SugarCane, 9999];
        int?[] items = [null, Items.IronIngot, Blocks.Cobblestone, 31999];
        int[] counts = [0, 1, 2, 9, 10, 57, 123];
        string?[] details = [null, "", "zombie", "was slain by Zombie"];
        foreach (var kind in Kinds)
        foreach (var place in places)
        foreach (var pos in positions)
        foreach (var block in blocks)
        foreach (var count in counts)
        {
            var item = items[(count + (block ?? 0)) % items.Length];
            var detail = details[(count + kind.Length) % details.Length];
            var e = new ActivityEvent("Fox", kind, at, 0.9) { BlockId = block, ItemId = item, Count = count, Pos = pos, Place = place, Detail = detail };
            sb.Append(kind).Append('|').Append(e.Category).Append('|').Append(ActivityText.Describe(e)).Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void Describe_and_category_match_the_golden_hash()
    {
        var dump = Dump();
        var outDir = Environment.GetEnvironmentVariable("WP03_GOLDEN_OUT");
        if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, "activity-text.txt"), dump);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)))[..24];
        Assert.Equal(Expected, hash);
    }

    /// <summary>Generated from the pre-refactor code (WP03).</summary>
    private const string Expected = "E5CEB13AE1175B8367C19700";
}
