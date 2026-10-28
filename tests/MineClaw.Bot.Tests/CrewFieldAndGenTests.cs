using MineClaw.Agent.Rag;
using MineClaw.Agent.Worker;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Memory;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-28 01:57, main's worker on "the 3 of you harvest the grown wheat": "Wheat field ... is protected ($ roles, owned by
/// others, maid_chan_67/1337 work it); ripeness not verifiable, no wheat harvested. Needs owner permission to harvest." The
/// '$' came from the role overlay (crops and farm soil drawn as SoftProtected), the ripeness question went to the perception
/// model (100 % refuted), and a hive lease read as protection. Crops are their own role from their metadata, a lease is
/// coordination ("busy, take another part"), and ripeness is answered from the voxels.
/// </summary>
public class CrewFieldVisionTests
{
    private static readonly BlockPos RipeCrop = new(2, 65, -6), YoungCrop = new(3, 65, -6), Soil = new(2, 64, -6);
    private const string LiveQuestion = "which wheat is fully grown (ripe), and which farmland is harvested/empty; middle rows";

    /// <summary>A board where another bot (maid_chan_67) holds a lease over the whole field.</summary>
    private sealed class FieldHeld : IClaimBoard
    {
        public static readonly ClaimKey Key = ClaimKey.Area(new BlockPos(-2, 63, -11), new BlockPos(5, 66, -3));
        public string Owner => "maid_chan";
        public bool IsFree(ClaimKey key) => !Key.ConflictsWith(key);
        public string? HolderOf(ClaimKey key) => IsFree(key) ? null : "maid_chan_67";
        public Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default) =>
            Task.FromResult(IsFree(key) ? ClaimResult.Granted(Owner, DateTimeOffset.MaxValue) : ClaimResult.Refused("maid_chan_67", DateTimeOffset.MaxValue));
        public Task ReleaseAsync(ClaimKey key, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A 6x7 field (x -1..4, z -10..-4): farmland y64, wheat y65, every other crop ripe; the bot stands beside it.</summary>
    private static (WorldState W, Protection P, RoleClassifier R) Field()
    {
        var c = new FakeGameClient();
        c.Floor(63, 12, Blocks.Dirt);
        c.Floor(64, 12, Blocks.Dirt);
        for (int x = -1; x <= 4; x++)
            for (int z = -10; z <= -4; z++)
            {
                c.World.SetBlock(x, 64, z, Blocks.Farmland, 1);
                c.World.SetBlock(x, 65, z, 0, 0);
                if (z != -4) c.World.SetBlock(x, 65, z, Blocks.Crops, (x + z) % 2 == 0 ? 7 : 4); // z=-4: a row of empty farmland
            }
        c.World.SetBlock(RipeCrop.X, RipeCrop.Y, RipeCrop.Z, Blocks.Crops, 7);
        c.World.SetBlock(YoungCrop.X, YoungCrop.Y, YoungCrop.Z, Blocks.Crops, 3);
        c.World.Player.ServerTeleport(new Vec3d(2.5, 65, -1.5), 66.62, 0, 0, true);
        var w = c.World;
        var p = new Protection(new BotOptions(), new NoMemory(), new NoMemory())
        {
            Claims = new FieldHeld(), BlockMeta = q => w.GetMeta(q.X, q.Y, q.Z), Dimension = () => w.Dimension,
        };
        var r = new RoleClassifier { Blocks = w.PlanningView, Verdict = p.Check };
        return (w, p, r);
    }

    [Fact]
    public void Crops_are_ripe_or_growing_by_their_metadata_never_the_protected_role()
    {
        var (_, _, r) = Field();
        Assert.Equal(BlockRole.CropRipe, r.RoleOf(RipeCrop.X, RipeCrop.Y, RipeCrop.Z));
        Assert.Equal(BlockRole.CropGrowing, r.RoleOf(YoungCrop.X, YoungCrop.Y, YoungCrop.Z));
        Assert.NotEqual(BlockRole.SoftProtected, r.RoleOf(Soil.X, Soil.Y, Soil.Z)); // farm soil: floor, not '$'
        Assert.Equal("crop (ripe 7/7)", RoleClassifier.CropText(Blocks.Crops, 7));
        Assert.Equal("crop (growing 3/7)", RoleClassifier.CropText(Blocks.Crops, 3));
    }

    [Fact]
    public void The_look_map_draws_the_field_as_crops_not_dollar_signs()
    {
        var (w, _, r) = Field();
        var map = AsciiMap.Render(w, 2, 65, -6, 13, roles: r.Map);
        var grid = string.Join("\n", map.Split('\n').Skip(2).Take(13));
        Assert.DoesNotContain("$", grid);
        Assert.Contains("H", grid);
        Assert.Contains("H ripe crop=HARVEST", map);
        Assert.DoesNotContain("$ protected", map);
    }

    [Fact]
    public void The_live_ripeness_question_is_answered_from_metadata_without_the_model()
    {
        var (w, _, r) = Field();
        Assert.Equal(VisionIntent.Things, VisionRouter.Classify(LiveQuestion));
        var a = VisionRouter.Answer(LiveQuestion, w.PlanningView, r.Map, (2, 65, -6), 8)!;
        Assert.StartsWith(VisionRouter.Prefix, a);
        Assert.Contains("ripe (7/7)", a);
        Assert.Contains("growing (stage", a);
        Assert.Contains("empty farmland 6", a);
        Assert.Contains("2,65,-6", a);                 // the ripe one at the focus, by coordinates
        Assert.DoesNotContain("protected", a);
        var focus = VisionRouter.Answer("what is at the focus?", w.PlanningView, r.Map, (2, 65, -6), 8)!;
        Assert.Contains("crop (ripe 7/7)", focus);
    }

    [Fact]
    public void Another_bots_lease_on_the_field_is_busy_not_protected_and_needs_no_permission()
    {
        var (_, p, r) = Field();
        Assert.True(p.IsProtected(RipeCrop, Blocks.Crops));        // coordination: my own choices leave its part alone
        var v = p.Explain(RipeCrop, Blocks.Crops);
        Assert.Equal(ProtectionText.ClaimedReason, v.Reason);
        var text = ProtectionText.Refusal("wheat at 2,65,-6", v);
        Assert.DoesNotContain(ProtectionText.Marker, text);
        Assert.DoesNotContain("you can break it", text);
        Assert.Contains("not protection", text);
        Assert.False(WorkerAgent.IsProtectionRefusal(text)); // the worker never ends the job on it
        Assert.Equal(BlockRole.CropRipe, r.RoleOf(RipeCrop.X, RipeCrop.Y, RipeCrop.Z));
    }
}

/// <summary>
/// Live 10-28 02:05-02:08 at Fox's gen: main's worker "Gen cells 4,64,6 and 5,64,7 are in use by other bots (maid_chan_1337)"
/// (one clone held two cells), "pickaxe 1/131, no cobble or sticks to craft a new one" (300+ of both in the chests), and clone
/// 67's script died on "nothing to deposit" after mining 0 in 3 min instead of naming the cause.
/// </summary>
public class GenCrewTests
{
    private static readonly BlockPos A = new(4, 64, 6), B = new(5, 64, 7), C = new(6, 64, 8);

    /// <summary>A hive board that records who holds what; optionally leaves one claim unanswered (an IPC timeout).</summary>
    private sealed class Board : IClaimBoard
    {
        public readonly Dictionary<ClaimKey, string> Held = new();
        public readonly List<int> HeldByMeOverTime = new();
        public ClaimKey? Unanswered;
        public string Owner => "maid_chan_1337";
        public bool IsFree(ClaimKey key) { lock (Held) return !Held.Any(h => h.Value != Owner && h.Key.ConflictsWith(key)); }
        public string? HolderOf(ClaimKey key) { lock (Held) return Held.FirstOrDefault(h => h.Value != Owner && h.Key.ConflictsWith(key)).Value; }
        public Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default)
        {
            lock (Held)
            {
                if (key == Unanswered) { Held[key] = Owner; return Task.FromResult(new ClaimResult(false, null, default, "no answer from main")); } // granted late
                if (Held.TryGetValue(key, out var o) && o != Owner) return Task.FromResult(ClaimResult.Refused(o, DateTimeOffset.MaxValue));
                Held[key] = Owner;
                HeldByMeOverTime.Add(Held.Count(h => h.Value == Owner && h.Key.Kind == ClaimKind.Block));
                return Task.FromResult(ClaimResult.Granted(Owner, DateTimeOffset.MaxValue));
            }
        }
        public Task ReleaseAsync(ClaimKey key, CancellationToken ct = default)
        {
            lock (Held) if (Held.TryGetValue(key, out var o) && o == Owner) Held.Remove(key);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_gen_run_nested_in_another_digs_the_cell_already_held_never_a_second_one()
    {
        var inner = new FakeGameClient { Username = "maid_chan_1337" };
        var board = new Board();
        var c = new ClaimAwareGameClient(inner, board);
        bool nested = false;
        // Mid-batch the pickaxe re-tool mines cobble at "the nearest" cell, B, through the same claim-aware client.
        inner.Effects["generator"] = () =>
        {
            if (nested) return;
            nested = true;
            c.HarvestGeneratorAsync(B.X, B.Y, B.Z, 3).GetAwaiter().GetResult();
        };
        var r = await c.HarvestGeneratorAsync(A.X, A.Y, A.Z, 8);
        Assert.True(r.Ok, r.Message);
        Assert.All(board.HeldByMeOverTime, n => Assert.True(n <= 1, $"held {n} gen cells at once"));
        Assert.DoesNotContain(inner.CallList, x => x.StartsWith($"generator {B.X},{B.Y},{B.Z}", StringComparison.Ordinal));
        Assert.Contains($"generator {A.X},{A.Y},{A.Z} 3", inner.CallList); // the nested batch dug the held cell
        Assert.Empty(board.Held);                                          // and everything was released
        Assert.Null(c.GenCell);
    }

    [Fact]
    public async Task An_unanswered_claim_that_main_granted_late_is_released_not_left_held()
    {
        var inner = new FakeGameClient { Username = "maid_chan_1337" };
        var board = new Board { Unanswered = ClaimKey.Block(A.X, A.Y, A.Z) };
        var c = new ClaimAwareGameClient(inner, board);
        var r = await c.HarvestGeneratorAsync(A.X, A.Y, A.Z, 8);
        Assert.False(r.Ok);
        Assert.Contains("didn't answer", r.Message);
        Assert.Empty(board.Held); // no orphan lease starving the next bot
    }

    [Fact]
    public void No_free_cell_names_who_holds_them()
    {
        var board = new Board();
        board.Held[ClaimKey.Block(A.X, A.Y, A.Z)] = "maid_chan_1337x";
        board.Held[ClaimKey.Block(B.X, B.Y, B.Z)] = "maid_chan_1337x";
        board.Held[ClaimKey.Block(C.X, C.Y, C.Z)] = "maid_chan_69";
        var note = GameToolSet.HeldBy(new Viewer(board), [A, B, C], 0);
        Assert.Equal("; no free gen cell: held by maid_chan_1337x ×2, 69", note);
    }

    /// <summary>The board as maid_chan_67 sees it.</summary>
    private sealed class Viewer(Board b) : IClaimBoard
    {
        public string Owner => "maid_chan_67";
        public bool IsFree(ClaimKey key) { lock (b.Held) return !b.Held.Any(h => h.Value != Owner && h.Key.ConflictsWith(key)); }
        public string? HolderOf(ClaimKey key) { lock (b.Held) return b.Held.FirstOrDefault(h => h.Value != Owner && h.Key.ConflictsWith(key)).Value; }
        public Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReleaseAsync(ClaimKey key, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_worn_pickaxe_is_replaced_from_chest_cobble_and_sticks_before_it_breaks()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        c.Floor(64, 12, Blocks.Grass);
        c.World.SetBlock(3, 65, 3, Blocks.Workbench, 0);
        c.Give(36, Items.StonePickaxe, 1, damage: Items.Get(Items.StonePickaxe).MaxDamage - 1); // 1/131 left
        c.World.SetBlock(2, 65, 2, Blocks.Chest, 0);
        h.Runtime.Observations.Upsert(new Observation("chest", "2,65,2", "chest")
        {
            Bounds = Box.Point(2, 65, 2),
            Contents = [new ItemCount(Blocks.Cobblestone, "cobblestone", 300), new ItemCount(Items.Stick, "stick", 40)],
        });
        c.Effects["withdraw"] = () =>
        {
            var parts = c.CallList.Last(x => x.StartsWith("withdraw ", StringComparison.Ordinal)).Split(' ');
            AddItem(c, int.Parse(parts[1]), int.Parse(parts[2]));
        };
        c.Effects["craft"] = () =>
        {
            var parts = c.CallList.Last(x => x.StartsWith("craft ", StringComparison.Ordinal)).Split(' ');
            AddItem(c, int.Parse(parts[1]), Math.Max(1, int.Parse(parts[2])));
        };
        var r = await Acquisition.Planner(c, h.Runtime.Observations).AcquireAsync(new Need("pickaxe"), default);
        Assert.True(r.Ok, r.Text);
        Assert.Contains("crafted stone pickaxe", r.Text);
        Assert.Contains("replacing stone pickaxe (worn: 1/131 left)", r.Text);
        Assert.Contains($"craft {Items.StonePickaxe} 1", c.CallList);
        Assert.False(r.Text.StartsWith("had ", StringComparison.Ordinal), r.Text); // the 1/131 pick no longer counts as "had a pickaxe"
        Assert.True(Loadout.HaveUsable(c.World, Loadout.Kinds["pickaxe"]) > 0);
    }

    private static void AddItem(FakeGameClient c, int id, int n)
    {
        var slots = c.World.Inventory.PlayerSlots();
        foreach (int s in Enumerable.Range(36, 9).Concat(Enumerable.Range(9, 27)))
            if (slots[s].IsEmpty) { c.Give(s, id, Math.Min(64, n)); return; }
    }

    [Fact]
    public async Task Deposit_with_nothing_to_deposit_is_ok_so_a_script_goes_on()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        c.World.SetBlock(2, 65, 2, Blocks.Chest, 0);
        c.Results["deposit"] = () => ActionResult.Fail("nothing to deposit");
        var r = await h.Run("deposit", new { at = "2,65,2" });
        Assert.True(r.Ok, r.Text);
        Assert.StartsWith("nothing to deposit", r.Text);
    }
}
