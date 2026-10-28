using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Skills.Tests;

/// <summary>
/// Live 10-28 01:56, Fox: "the 3 of you harvest the grown wheat now, replant it too". Main's worker came back with "Wheat field
/// -1..4,64..65,-22..-8 is protected ($ roles, owned by others, maid_chan_67/1337 work it); ripeness not verifiable, no wheat
/// harvested. Needs owner permission to harvest", and the clones each ran harvest_wheat over the whole field. Now: one field,
/// three bots, each harvests a disjoint part (strips taken through hive leases), ripe crops only (block meta 7), no refusal.
/// </summary>
public class CrewFarmTests
{
    private static readonly string[] Crew = ["maid_chan", "maid_chan_67", "maid_chan_1337"];

    /// <summary>A 15x3 field along x (farmland y63, crops y64): stage 7 except every 4th crop at stage 3; a dirt path at z=-1.</summary>
    private static (FakeGame G, List<BlockPos> Ripe, List<BlockPos> Unripe) Field(double standX)
    {
        var g = new FakeGame();
        g.Fill(-3, 60, -4, 18, 62, 6, Blocks.Dirt);
        g.Fill(-3, 63, -4, 18, 63, -1, Blocks.Dirt);
        g.Fill(-3, 63, 3, 18, 63, 6, Blocks.Dirt);
        var ripe = new List<BlockPos>();
        var unripe = new List<BlockPos>();
        int n = 0;
        for (int x = 0; x <= 14; x++)
            for (int z = 0; z <= 2; z++)
            {
                g.Set(x, 63, z, Blocks.Farmland);
                bool young = n++ % 4 == 3;
                g.Set(x, 64, z, Blocks.Crops, young ? 3 : 7);
                (young ? unripe : ripe).Add(new BlockPos(x, 64, z));
            }
        g.Give(Items.StoneHoe, 1);
        g.Stand(standX, 64, -1.5);
        return (g, ripe, unripe);
    }

    /// <summary>The hive's board as the bots see it: leases on areas/blocks conflict by overlap, released when done.</summary>
    private sealed class Board
    {
        public readonly List<(ClaimKey Key, string Owner)> Held = new();
        public readonly List<(ClaimKey Key, string Owner)> Granted = new();
        public readonly Turns Turns = new(Crew);
        public IClaimBoard For(string owner) => new View(this, owner);

        private sealed class View(Board b, string owner) : IClaimBoard
        {
            public string Owner => owner;
            public bool IsFree(ClaimKey key) { lock (b.Held) return !b.Held.Any(h => h.Owner != owner && h.Key.ConflictsWith(key)); }
            public async Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default)
            {
                await b.Turns.EnterAsync(owner);
                lock (b.Held)
                {
                    if (b.Held.FirstOrDefault(h => h.Owner != owner && h.Key.ConflictsWith(key)) is { Owner: not null } other)
                        return ClaimResult.Refused(other.Owner, DateTimeOffset.MaxValue);
                    b.Held.Add((key, owner));
                    b.Granted.Add((key, owner));
                    return ClaimResult.Granted(owner, DateTimeOffset.MaxValue);
                }
            }
            public Task ReleaseAsync(ClaimKey key, CancellationToken ct = default)
            {
                lock (b.Held) b.Held.RemoveAll(h => h.Owner == owner && h.Key == key);
                return Task.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Deterministic interleaving of the crew: one bot runs at a time; a bot hands the turn on when it asks for its next lease
    /// (so three bots on one field alternate strip by strip, the way three live bodies working at once do).
    /// </summary>
    private sealed class Turns(string[] order)
    {
        private readonly object _lock = new();
        private readonly List<string> _active = [.. order];
        private readonly HashSet<string> _entered = new();
        private readonly Dictionary<string, TaskCompletionSource> _waiting = new();
        private string? _holder = order[0];

        public Task EnterAsync(string owner)
        {
            lock (_lock)
            {
                if (_holder == owner && _entered.Add(owner)) return Task.CompletedTask; // its first turn
                if (_holder == owner)
                {
                    var next = Next(owner);
                    if (next == owner) return Task.CompletedTask;
                    Give(next);
                }
                _entered.Add(owner);
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiting[owner] = tcs;
                return tcs.Task;
            }
        }

        public void Done(string owner)
        {
            lock (_lock)
            {
                bool had = _holder == owner;
                var next = Next(owner);
                _active.Remove(owner);
                if (had) { if (_active.Count > 0 && next != owner) Give(next); else _holder = null; }
            }
        }

        private string Next(string owner)
        {
            int i = _active.IndexOf(owner);
            return _active[(i + 1) % _active.Count];
        }

        private void Give(string to)
        {
            _holder = to;
            _entered.Add(to);
            if (_waiting.Remove(to, out var t)) t.SetResult();
        }
    }

    private static Task<SkillResult> Run(FakeGame g, IClaimBoard claims, object? args = null)
    {
        var reg = SkillRegistry.CreateDefault();
        var ctx = new SkillContext(g, reg) { Claims = claims };
        return reg.RunAsync(ctx, "harvest_wheat", args is null ? SkillJson.Empty : FakeGame.J(args), CancellationToken.None);
    }

    private static List<BlockPos> Dug(FakeGame g) =>
        g.Log.Where(l => l.StartsWith("dig ", StringComparison.Ordinal)).Select(l => l[4..]).Select(Parse).ToList();

    private static BlockPos Parse(string s)
    {
        var p = s.Trim('(', ')').Split(',').Select(t => int.Parse(t.Trim())).ToArray();
        return new BlockPos(p[0], p[1], p[2]);
    }

    [Fact]
    public async Task Three_bots_on_one_field_harvest_disjoint_strips_ripe_only_with_no_refusals()
    {
        // One server world: every block change one bot makes, the others see.
        var bots = new[] { Field(1.5), Field(7.5), Field(13.5) };
        for (int i = 0; i < bots.Length; i++)
        {
            int me = i;
            bots[i].G.Changed = (p, id, meta) =>
            {
                for (int j = 0; j < bots.Length; j++) if (j != me) bots[j].G.World.SetBlock(p.X, p.Y, p.Z, id, meta);
            };
        }
        var board = new Board();
        var runs = Enumerable.Range(0, 3).Select(i => Task.Run(async () =>
        {
            try { return await Run(bots[i].G, board.For(Crew[i])); }
            finally { board.Turns.Done(Crew[i]); }
        })).ToArray();
        var results = await Task.WhenAll(runs);

        var ripe = bots[0].Ripe;
        var dug = bots.Select(b => Dug(b.G)).ToArray();
        for (int i = 0; i < 3; i++)
        {
            Assert.True(results[i].Ok, $"{Crew[i]}: {results[i].Text}");
            Assert.DoesNotContain("protected", results[i].Text);
            Assert.DoesNotContain("permission", results[i].Text);
            Assert.NotEmpty(dug[i]);                                       // everyone works a part
            Assert.All(dug[i], p => Assert.Contains(p, ripe));             // ripe only (meta 7)
        }
        // Disjoint parts covering every ripe crop exactly once.
        var all = dug.SelectMany(d => d).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(ripe.OrderBy(p => p.X).ThenBy(p => p.Z), all.OrderBy(p => p.X).ThenBy(p => p.Z));
        // Growing crops were left alone, everything harvested was replanted.
        var w = bots[0].G.World;
        foreach (var u in bots[0].Unripe) Assert.Equal((Blocks.Crops, 3), (w.GetBlock(u.X, u.Y, u.Z), w.GetMeta(u.X, u.Y, u.Z)));
        foreach (var r in ripe) Assert.Equal(Blocks.Crops, w.GetBlock(r.X, r.Y, r.Z));
        // Leases were strips of the field, never two bots on overlapping ones, all released at the end.
        Assert.Empty(board.Held);
        Assert.Equal(3, board.Granted.Select(gr => gr.Owner).Distinct().Count());
        Assert.All(board.Granted, gr => Assert.Equal(ClaimKind.Area, gr.Key.Kind));
        // Each bot dug only inside strips it held the lease on.
        for (int i = 0; i < 3; i++)
        {
            var mine = board.Granted.Where(gr => gr.Owner == Crew[i]).Select(gr => gr.Key).ToList();
            Assert.All(dug[i], p => Assert.Contains(mine, k => k.Contains(p)));
        }
    }

    [Fact]
    public async Task A_field_a_clone_holds_is_its_part_not_protection_or_permission()
    {
        var (g, ripe, _) = Field(7.5);
        var board = new Board();
        board.Turns.Done("maid_chan_67");
        board.Turns.Done("maid_chan_1337");
        // maid_chan_67 claimed the whole field (the live shape: one clone's area lease over everything).
        board.Held.Add((ClaimKey.Area(new BlockPos(-1, 62, -1), new BlockPos(15, 65, 3)), "maid_chan_67"));
        var r = await Run(g, board.For("maid_chan"));
        Assert.True(r.Ok, r.Text);
        Assert.Contains("nothing left in my part", r.Text);
        Assert.Contains($"{ripe.Count} ripe in parts other hive bots are working (theirs)", r.Text);
        Assert.DoesNotContain("protected", r.Text);
        Assert.DoesNotContain("permission", r.Text);
        Assert.Empty(Dug(g));
    }

    [Fact]
    public async Task Part_i_of_n_splits_the_field_into_disjoint_bands_that_cover_it()
    {
        var dug = new List<List<BlockPos>>();
        List<BlockPos>? ripe = null;
        for (int i = 1; i <= 3; i++)
        {
            var (g, rp, _) = Field(1.5 + 6 * (i - 1));
            ripe ??= rp;
            var r = await Run(g, NullClaimBoard.Instance, new { part = $"{i}/3" });
            Assert.True(r.Ok, r.Text);
            Assert.Contains("ripe in parts other hive bots are working (theirs)", r.Text);
            dug.Add(Dug(g));
        }
        var all = dug.SelectMany(d => d).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(ripe!.OrderBy(p => p.X).ThenBy(p => p.Z), all.OrderBy(p => p.X).ThenBy(p => p.Z));
        Assert.All(dug, d => Assert.NotEmpty(d));
    }
}
