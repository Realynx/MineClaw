using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.Hive.Ipc;
using MineClaw.World;
using BlockPos = MineClaw.World.BlockPos;

namespace MineClaw.Hive.Inventory;

/// <summary>
/// <see cref="IInventoryPort"/> over the real client: inventory from the player window, chests (with free-slot estimates)
/// from the WorldIndexer's container observations, deposit via <c>DepositAllAsync</c> with an everything filter, toss via
/// window clicks outside the window, and placing a storage chest next to the bot (labelled in shared memory as a waypoint:
/// the b1.8.1 client has no sign-writing action yet).
/// </summary>
public sealed class GameInventoryPort(IGameClient client, ObservationStore observations, MemoryStore? memory = null) : IInventoryPort
{

    public int Dimension => client.World.Dimension;

    public static List<ItemMsg> Snapshot(WorldState w)
    {
        var slots = w.Inventory.PlayerSlots();
        var list = new List<ItemMsg>();
        for (int i = 1; i < slots.Length; i++) // 0 = crafting output (not real)
            if (!slots[i].IsEmpty) list.Add(new ItemMsg(slots[i].Id, slots[i].Damage, slots[i].Count, Names.Item(slots[i].Id, slots[i].Damage)));
        var cur = w.Inventory.Cursor;
        if (!cur.IsEmpty) list.Add(new ItemMsg(cur.Id, cur.Damage, cur.Count, Names.Item(cur.Id, cur.Damage)));
        return list
            .GroupBy(i => (i.Id, i.Damage, i.Name))
            .Select(g => new ItemMsg(g.Key.Id, g.Key.Damage, g.Sum(x => x.Count), g.Key.Name)).ToList();
    }

    public IReadOnlyList<ItemMsg> Items() => Snapshot(client.World);

    /// <summary>
    /// Window closed (cursor/grid back into the inventory), the slot packets quiet, then a forced server resync: the
    /// server answers after every earlier click (TCP order), so the model it leaves behind is the server's, not a
    /// prediction left over from a cancelled worker action.
    /// </summary>
    public async Task<bool> SettleAsync(CancellationToken ct)
    {
        if (!client.IsPlaying) return false;
        if (client.World.Inventory.OpenWindow != null) await client.CloseWindowAsync(ct).ConfigureAwait(false);
        await QuietAsync(client.World.Inventory, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        if (client is not MinecraftGameClient mg) return true;
        var r = await mg.Inner.ResyncInventoryAsync(ct).ConfigureAwait(false);
        await QuietAsync(client.World.Inventory, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        return r.Ok;
    }

    /// <summary>Waits until no inventory change for <paramref name="quiet"/> (or <paramref name="max"/> elapsed).</summary>
    internal static async Task QuietAsync(InventoryManager inv, TimeSpan quiet, TimeSpan max, CancellationToken ct)
    {
        long last = Environment.TickCount64;
        void OnChanged() => Interlocked.Exchange(ref last, Environment.TickCount64);
        inv.Changed += OnChanged;
        try
        {
            long start = Environment.TickCount64;
            while (Environment.TickCount64 - Interlocked.Read(ref last) < quiet.TotalMilliseconds && Environment.TickCount64 - start < max.TotalMilliseconds)
                await Task.Delay(25, ct).ConfigureAwait(false);
        }
        finally { inv.Changed -= OnChanged; }
    }

    public bool CanActSafely
    {
        get
        {
            if (!client.IsPlaying) return false;
            // Putting things away is not combat: no hp gate (live 10-27: 67 at hp 7 and 1337 must still deposit before logging out;
            // an hp ≤ 6 gate made the logout deposit wait out its deadline and leave carrying everything). Over the void: no.
            var p = client.World.Player;
            return !client.World.IsPlayerOverVoid(p.Position);
        }
    }

    public IReadOnlyList<StorageChest> Chests()
    {
        var me = client.World.Player.Position;
        // Every remembered chest (upstairs and behind doors too): FreeSlots is memory's estimate (free slots, or 1 when only
        // partial stacks of what we carry have room), never a reason to skip a chest unopened.
        var storing = client.World.Inventory.PlayerSlots().Skip(9).Where(s => !s.IsEmpty).Select(s => (int)s.Id).ToHashSet();
        var list = new List<StorageChest>();
        foreach (var (row, dist, _) in MineClaw.Agent.Rag.ChestRoom.Ranked(observations, me.X, me.Y, me.Z, storing))
        {
            var b = row.Bounds!.Value;
            var (free, partial) = MineClaw.Agent.Rag.ChestRoom.Estimate(b, observations.Contents(row.Id), storing);
            list.Add(new StorageChest(new BlockPos((int)Math.Floor(b.MinX), (int)Math.Floor(b.MinY), (int)Math.Floor(b.MinZ)),
                free > 0 ? free : partial > 0 ? 1 : 0, dist));
        }
        return list.OrderBy(c => c.Distance).ToList();
    }

    /// <summary>Armor, crafting-grid and cursor items aren't in a chest window: shift-click them into the main inventory first.</summary>
    private async Task NormaliseAsync(CancellationToken ct)
    {
        if (client.World.Inventory.OpenWindow != null) await client.CloseWindowAsync(ct).ConfigureAwait(false);
        var slots = client.World.Inventory.PlayerSlots();
        for (int i = 1; i <= 8; i++)
            if (!slots[i].IsEmpty) await client.ClickSlotAsync(i, shift: true, ct: ct).ConfigureAwait(false);
    }

    public async Task<ActionResult> DepositToAsync(BlockPos chest, CancellationToken ct)
    {
        await NormaliseAsync(ct).ConfigureAwait(false);
        return await client.DepositAllAsync(chest, _ => true, ct).ConfigureAwait(false);
    }

    public async Task<ActionResult> TossAllAtAsync(Vec3d at, CancellationToken ct)
    {
        var r = await client.GotoAsync(new GoalNear(new BlockPos((int)Math.Floor(at.X), (int)Math.Floor(at.Y), (int)Math.Floor(at.Z)), 2), null, ct).ConfigureAwait(false);
        if (!r.Ok) return ActionResult.Fail("can't reach main: " + r.Message);
        if (client is MinecraftGameClient mg) await mg.Inner.LookAtAsync(at, ct).ConfigureAwait(false);
        await NormaliseAsync(ct).ConfigureAwait(false);
        int tossed = 0;
        var slots = client.World.Inventory.PlayerSlots();
        for (int i = 9; i < slots.Length; i++)
        {
            if (slots[i].IsEmpty) continue;
            var pick = await client.ClickSlotAsync(i, ct: ct).ConfigureAwait(false);
            if (!pick.Ok) continue;
            var drop = await client.ClickSlotAsync(InventoryManager.OutsideSlot, ct: ct).ConfigureAwait(false);
            if (drop.Ok) tossed += slots[i].Count;
        }
        return tossed > 0 ? ActionResult.Success($"tossed {tossed} item(s)", tossed) : ActionResult.Fail("nothing tossed");
    }

    public async Task<bool> GoNearAsync(Vec3d at, CancellationToken ct)
    {
        if (!client.IsPlaying) return false;
        if (client.World.Player.Position.DistanceTo(at) <= 2.5) return true;
        var r = await client.GotoAsync(new GoalNear(new BlockPos((int)Math.Floor(at.X), (int)Math.Floor(at.Y), (int)Math.Floor(at.Z)), 2), null, ct).ConfigureAwait(false);
        return r.Ok;
    }

    /// <summary>
    /// Out of a hole / generator cell: the nearest standable spot within 6 blocks, no lower than here, with open air on at
    /// least 3 sides and solid ground (never over the void, never in liquid), then walk there.
    /// </summary>
    public async Task<bool> StepOutAsync(CancellationToken ct)
    {
        if (!client.IsPlaying) return false;
        var w = client.World;
        var me = w.Player.Position.ToBlockPos();
        var spot = OpenSpot(w, me);
        if (spot is not { } p) return false;
        var r = await client.GotoAsync(new GoalNear(p, 0), null, ct).ConfigureAwait(false);
        return r.Ok;
    }

    /// <summary>The nearest open standable cell around <paramref name="me"/> (see <see cref="StepOutAsync"/>), or null.</summary>
    internal static BlockPos? OpenSpot(WorldState w, BlockPos me, int radius = 6)
    {
        if (OpenSides(w, me.X, me.Y, me.Z) >= 3) return null; // already in the open
        BlockPos? best = null;
        double bestD = double.MaxValue;
        for (int dx = -radius; dx <= radius; dx++)
        for (int dz = -radius; dz <= radius; dz++)
        for (int dy = 0; dy <= 2; dy++)
        {
            int x = me.X + dx, y = me.Y + dy, z = me.Z + dz;
            if (dx == 0 && dz == 0 && dy == 0) continue;
            if (!IsOpenStand(w, x, y, z)) continue;
            double d = dx * dx + dz * dz + dy * 0.5;
            if (d < bestD) { bestD = d; best = new BlockPos(x, y, z); }
        }
        return best;
    }

    private static bool Air(WorldState w, int x, int y, int z) => w.GetBlock(x, y, z) == 0;

    /// <summary>How many of the four sides at (x, y, z) are air.</summary>
    private static int OpenSides(WorldState w, int x, int y, int z) =>
        (Air(w, x + 1, y, z) ? 1 : 0) + (Air(w, x - 1, y, z) ? 1 : 0) + (Air(w, x, y, z + 1) ? 1 : 0) + (Air(w, x, y, z - 1) ? 1 : 0);

    /// <summary>Two air blocks over solid, non-liquid ground, not over the void, with at least three open sides.</summary>
    private static bool IsOpenStand(WorldState w, int x, int y, int z)
    {
        if (!Air(w, x, y, z) || !Air(w, x, y + 1, z)) return false;
        int below = w.GetBlock(x, y - 1, z);
        if (below == 0 || !BlockShapes.HasCollision(w, x, y - 1, z) || BlockShapes.IsLiquid(below)) return false;
        if (w.IsPlayerOverVoid(new Vec3d(x + 0.5, y, z + 0.5))) return false;
        return OpenSides(w, x, y, z) >= 3;
    }

    public async Task<(ActionResult Result, BlockPos? Chest)> PlaceLabelledChestAsync(string label, CancellationToken ct)
    {
        if (client.World.Inventory.CountItem(Blocks.Chest) <= 0) return (ActionResult.Fail("no chest item to place"), null);
        var w = client.World;
        var me = w.Player.Position.ToBlockPos();
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1) })
        {
            var p = new BlockPos(me.X + dx, me.Y, me.Z + dz);
            if (w.GetBlock(p.X, p.Y, p.Z) != 0 || w.GetBlock(p.X, p.Y + 1, p.Z) != 0) continue;
            if (!BlockShapes.HasCollision(w, p.X, p.Y - 1, p.Z)) continue; // needs a floor (never over the void)
            var r = await client.PlaceBlockAsync(p.X, p.Y, p.Z, Blocks.Chest, ct).ConfigureAwait(false);
            if (!r.Ok) continue;
            memory?.SetWaypoint(label, p.X, p.Y, p.Z, "hive storage chest");
            return (ActionResult.Success($"placed chest '{label}' at {p.X},{p.Y},{p.Z}"), p);
        }
        return (ActionResult.Fail("no free spot for a chest"), null);
    }
}
