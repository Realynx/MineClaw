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
    private const int ChestSlots = 27;

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

    public bool CanActSafely
    {
        get
        {
            if (!client.IsPlaying) return false;
            var p = client.World.Player;
            return p.Health > 6 && !client.World.IsPlayerOverVoid(p.Position);
        }
    }

    public IReadOnlyList<StorageChest> Chests()
    {
        var me = client.World.Player.Position;
        var rows = observations.Nearest("chest", me.X, me.Y, me.Z, limit: 16, maxDistance: 96);
        var list = new List<StorageChest>();
        foreach (var (row, dist) in rows)
        {
            if (row.Removed != null || row.Bounds is not { } b) continue;
            bool dbl = Math.Abs(b.MaxX - b.MinX) >= 1 || Math.Abs(b.MaxZ - b.MinZ) >= 1;
            int capacity = dbl ? ChestSlots * 2 : ChestSlots;
            int used = 0;
            foreach (var c in observations.Contents(row.Id))
            {
                int max = MineClaw.Data.Items.Exists(c.ItemId) ? Math.Max(1, MineClaw.Data.Items.Get(c.ItemId).MaxStack) : 64;
                used += (c.Count + max - 1) / max;
            }
            list.Add(new StorageChest(new BlockPos((int)b.MinX, (int)b.MinY, (int)b.MinZ), Math.Max(0, capacity - used), dist));
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
