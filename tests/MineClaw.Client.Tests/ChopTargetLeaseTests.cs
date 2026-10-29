using MineClaw.Client.Trees;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 10-29: 12 bodies of one hive logged "chop: oak tree at 167,68,295" within 0.4 s; then 19 on the birch at 174,64,296. chop_tree
/// now leases the tree it picks on the hive's board (one body per tree), takes the next free one when a sibling holds it, and when
/// nothing is left says so typed: "exhausted: …" with the nearest free trees beyond.
/// </summary>
public class ChopTargetLeaseTests
{
    private sealed class Leases : IClaimBoard
    {
        public readonly Dictionary<ClaimKey, string> Held = new();
        public string Owner => "maid_chan";
        public bool IsFree(ClaimKey key) { lock (Held) return !Held.Any(kv => kv.Value != Owner && kv.Key.ConflictsWith(key)); }
        public string? HolderOf(ClaimKey key) { lock (Held) return Held.FirstOrDefault(kv => kv.Value != Owner && kv.Key.ConflictsWith(key)).Value; }
        public Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default)
        {
            lock (Held)
            {
                if (HolderOf(key) is { } h) return Task.FromResult(ClaimResult.Refused(h, DateTimeOffset.MaxValue));
                Held[key] = Owner;
                return Task.FromResult(ClaimResult.Granted(Owner, DateTimeOffset.MaxValue));
            }
        }
        public Task ReleaseAsync(ClaimKey key, CancellationToken ct = default)
        {
            lock (Held) if (Held.TryGetValue(key, out var o) && o == Owner) Held.Remove(key);
            return Task.CompletedTask;
        }
    }

    private static void Oak(FakeServer srv, int x, int z)
    {
        srv.Blocks[(x, 63, z)] = Blocks.Grass;
        for (int y = 64; y <= 67; y++) srv.Blocks[(x, y, z)] = Blocks.Log;
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
        for (int y = 66; y <= 68; y++)
            if ((dx, dz) != (0, 0) || y == 68) srv.Blocks[(x + dx, y, z + dz)] = Blocks.Leaves;
    }

    private static Task<(MinecraftClient C, FakeServer S)> Start() =>
        Harness.StartAsync(srv =>
        {
            for (int x = 0; x <= 28; x++) for (int z = 0; z <= 12; z++) srv.Blocks[(x, 63, z)] = Blocks.Grass;
            Oak(srv, 6, 6);
            Oak(srv, 12, 6);
            Oak(srv, 26, 6);
            srv.SpawnAt = (3.5, 64, 6.5);
        });

    [Fact]
    public async Task A_tree_a_sibling_holds_is_skipped_for_the_next_free_one_and_a_used_up_area_reports_exhausted_with_the_frontier()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Start();
        await using var _ = c;
        using var __ = s;
        var leases = new Leases();
        c.TargetClaims = leases;
        var near = TreeScan.Analyze(c.World, new BlockPos(6, 64, 6))!;
        var next = TreeScan.Analyze(c.World, new BlockPos(12, 64, 6))!;
        lock (leases.Held) leases.Held[MinecraftClient.TreeKey(near, 0)] = "maid_chan_67"; // 67 is felling the nearest oak

        // The nearest is 67's: this body picks (and leases) the next one; never the same tree.
        var pick = PickOnly(c, leases);
        Assert.Equal(next.Base, pick);

        // Both near ones held by siblings, the chop radius small: exhausted, with the free tree beyond.
        lock (leases.Held) leases.Held[MinecraftClient.TreeKey(next, 0)] = "maid_chan_69";
        var r = await c.ChopTreeAsync(null, null, TreeChopOptions.Default with { SearchRadius = 12 });
        Assert.False(r.Ok);
        Assert.StartsWith("exhausted: the 2 tree(s) within 12 are all taken by other bodies (maid_chan_67, maid_chan_69)", r.Message);
        Assert.Contains("free trees beyond: oak 26,64,6", r.Message);
        Assert.DoesNotContain(leases.Held, kv => kv.Value == "maid_chan"); // nothing of ours left behind

        // Nothing at all in range: exhausted, nearest beyond.
        lock (leases.Held) leases.Held.Clear();
        var none = await c.ChopTreeAsync(null, null, TreeChopOptions.Default with { SearchRadius = 1 });
        Assert.False(none.Ok);
        Assert.StartsWith("exhausted: no tree within 1; nearest beyond: oak 6,64,6", none.Message);
    }

    /// <summary>The tree a chop would take (and lease) right now: the lease it holds after it starts, read once.</summary>
    private static BlockPos? PickOnly(MinecraftClient c, Leases leases)
    {
        var run = c.ChopTreeAsync();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            lock (leases.Held)
                if (leases.Held.FirstOrDefault(kv => kv.Value == "maid_chan").Key is { Name: { } n } && n.StartsWith("tree:"))
                {
                    var p = n["tree:".Length..].Split('@')[0].Split(',').Select(int.Parse).ToArray();
                    _ = c.StopAsync();
                    try { run.Wait(5000); } catch (Exception) { }
                    return new BlockPos(p[0], p[1], p[2]);
                }
            Thread.Sleep(5);
        }
        return null;
    }
}
