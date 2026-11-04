using System.Security.Cryptography;
using System.Text;
using MineClaw.Bot.Memory;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins the protection policy's soft verdicts (WP03 refactor): <see cref="Protection.Check(BlockPos, int)"/> over a grid of cells ×
/// every block id, with config blocks and a config region, labelled memory boxes (a house, a farm), every index kind, hive leases,
/// crop stages and dig cells. What the bot may dig must not change, nor the refusal words.
/// </summary>
public class ProtectionGoldenTests
{
    private sealed class Boxes : IProtectedRegionSource, IReservedItemSource
    {
        public IReadOnlyList<(BlockPos Min, BlockPos Max)> ProtectedRegions() => LabeledRegions().Select(r => (r.Min, r.Max)).ToList();
        public IReadOnlyList<(BlockPos Min, BlockPos Max, string? Label)> LabeledRegions() =>
        [
            (new BlockPos(0, 60, 0), new BlockPos(5, 70, 5), "main house"),
            (new BlockPos(-6, 60, -6), new BlockPos(-1, 66, -1), "wheat farm"),
            (new BlockPos(2, 62, -4), new BlockPos(7, 66, -1), null),
        ];
        public IReadOnlyCollection<string> ReservedItemNames() => [];
    }

    private sealed class Leases : IClaimBoard
    {
        public string Owner => "maid_chan";
        public bool IsFree(ClaimKey key) => key.ToString().Length % 5 != 0;
        public Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ReleaseAsync(ClaimKey key, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static int H(BlockPos p, int m) => (int)((uint)(p.X * 73856093 ^ p.Y * 19349663 ^ p.Z * 83492791) % (uint)m);

    private static readonly string?[] Kinds =
    [
        null, null, null, IndexKinds.UnknownStructure, IndexKinds.Generator, IndexKinds.Farm, IndexKinds.Tree, IndexKinds.Portal,
        IndexKinds.Sign, WorldIndexer.FloorKind, IndexKinds.Chest, IndexKinds.Furnace, IndexKinds.Bed,
    ];

    [Fact]
    public void Verdicts_match_the_golden_hash()
    {
        var options = new BotOptions { ProtectedRegions = ["10,60,10:12,64,12"] };
        var sb = new StringBuilder();
        foreach (var withLeases in new[] { false, true })
        {
            var src = new Boxes();
            var p = new Protection(options, src, src)
            {
                IndexerKind = q => Kinds[H(q, Kinds.Length)],
                BlockMeta = q => H(q, 9),
                IsDigCell = q => H(q, 23) == 0,
                Claims = withLeases ? new Leases() : null,
            };
            var ids = Enumerable.Range(0, 140).Concat([Ids1122.Carrots, Ids1122.Potatoes, Ids1122.Beetroots, Ids1122.NetherWart, Ids1122.TrappedChest, Ids1122.Hopper]).Distinct().ToList();
            for (int x = -7; x <= 13; x += 2)
            for (int z = -7; z <= 13; z += 2)
            for (int y = 61; y <= 65; y += 2)
            {
                var pos = new BlockPos(x, y, z);
                foreach (var id in ids)
                {
                    if (!Blocks.Exists(id)) continue;
                    var v = p.Check(pos, id);
                    if (v.Protected) sb.Append($"{withLeases} {x},{y},{z} {id} {v.Level} {v.Reason} | {v.Why} | {v.Unlock}\n");
                }
            }
        }
        var dump = sb.ToString();
        var outDir = Environment.GetEnvironmentVariable("WP03_GOLDEN_OUT");
        if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, "protection.txt"), dump);
        Assert.Equal(Expected, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)))[..24]);
    }

    /// <summary>Generated from the pre-refactor code (WP03).</summary>
    private const string Expected = "4EF84D1273CB54EB650C4A17";
}
