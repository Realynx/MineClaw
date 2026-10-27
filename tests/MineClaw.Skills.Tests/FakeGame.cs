using System.Text.Json;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.Skills;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

/// <summary>
/// Offline game for skill tests: a real <see cref="WorldState"/> (real b1.8.1 block table), the real pathfinder for
/// goto, the World's click prediction for windows, and a tiny server model for doors/redstone/buckets/liquids.
/// Every effect is applied synchronously, so <c>WaitUntilAsync</c> just evaluates its condition.
/// </summary>
public sealed class FakeGame : IGameActions
{
    public FakeGame()
    {
        int size = 64;
        World.ApplyChunkRegion(-32, 0, -32, size, 128, size, new byte[size * 128 * size], null, null, null);
        World.Player.SetHealth(20, 20, 5);
        World.Time = 6000;
    }

    public WorldState World { get; } = new();
    public string Username => "maid_chan";
    public Primitive Supported { get; set; } = Primitive.UseOnBlock | Primitive.UseEntity | Primitive.WriteSign | Primitive.ReleaseUse;
    public bool IsRaining { get; set; }
    public double Reach => 4.25;
    public IReadOnlySet<int> ReservedItems { get; set; } = new HashSet<int>();
    public HashSet<BlockPos> ProtectedBlocks { get; } = new();
    public bool IsProtected(BlockPos pos, int id) => ProtectedBlocks.Contains(pos);
    public Dictionary<BlockPos, string[]> SignText { get; } = new();
    public IReadOnlyDictionary<BlockPos, string[]> Signs => SignText;

    // ---- observation for assertions ----
    public List<string> Log { get; } = new();
    public List<Vec3d> Visited { get; } = new();
    public List<string> Violations { get; } = new();
    public List<BlockPos> Mixed { get; } = new();
    public Dictionary<BlockPos, ItemStack[]> Chests { get; } = new();
    public Dictionary<BlockPos, int> Opens { get; } = new();
    public HashSet<BlockPos> Locked { get; } = new();
    public int Clicks { get; private set; }
    /// <summary>1-based click number the "server" rejects (0 = none).</summary>
    public int RejectClick { get; set; }
    private BlockPos? _openChest;
    private int _windowId;
    private Vec3d _look;

    // ---- setup helpers ----
    public void Fill(int x0, int y0, int z0, int x1, int y1, int z1, int id, int meta = 0)
    {
        for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
        for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
        for (int z = Math.Min(z0, z1); z <= Math.Max(z0, z1); z++)
            World.SetBlock(x, y, z, id, meta);
    }

    public void Set(int x, int y, int z, int id, int meta = 0) => World.SetBlock(x, y, z, id, meta);
    public void Set(BlockPos p, int id, int meta = 0) => World.SetBlock(p.X, p.Y, p.Z, id, meta);

    public void Stand(double x, double y, double z) => World.Player.ServerTeleport(new Vec3d(x, y, z), y + 1.62, 0, 0, true);

    public void Give(int id, int count, int damage = 0)
    {
        int max = Math.Max(1, Items.Get(id).MaxStack);
        while (count > 0)
        {
            int slot = Enumerable.Range(0, 36).Select(i => i < 9 ? 36 + i : i).FirstOrDefault(s => World.Inventory.GetPlayerSlot(s).IsEmpty, -1);
            if (slot < 0) return;
            int n = Math.Min(max, count);
            World.Inventory.SetSlot(0, slot, new ItemStack((short)id, (byte)n, (short)damage));
            count -= n;
        }
    }

    public void SetSlot(int playerSlot, int id, int count, int damage = 0) => World.Inventory.SetSlot(0, playerSlot, new ItemStack((short)id, (byte)count, (short)damage));

    private bool Take(int id, int n = 1)
    {
        for (int s = 9; s < 45 && n > 0; s++)
        {
            var st = World.Inventory.GetPlayerSlot(s);
            if (st.IsEmpty || st.Id != id) continue;
            int k = Math.Min(n, st.Count);
            World.Inventory.SetSlot(0, s, k == st.Count ? ItemStack.Empty : st with { Count = (byte)(st.Count - k) });
            n -= k;
        }
        return n == 0;
    }

    public SkillContext Context(SkillRegistry? registry = null, SkillOptions? options = null) => new(this, registry ?? SkillRegistry.CreateDefault(), options);

    public static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    public Task<SkillResult> Run(string skill, object? args = null, SkillRegistry? registry = null) =>
        (registry ?? SkillRegistry.CreateDefault()).RunAsync(Context(registry), skill, args is null ? SkillJson.Empty : J(args), CancellationToken.None);

    private static Task<ActionResult> Ok(string m) => Task.FromResult(ActionResult.Success(m));
    private static Task<ActionResult> No(string m) => Task.FromResult(ActionResult.Fail(m));

    // ---- movement ----
    public Task<ActionResult> GotoAsync(Goal goal, GotoOptions? options, CancellationToken ct)
    {
        var o = options ?? GotoOptions.Default;
        Log.Add($"goto {goal}");
        if (goal.IsReached(Pathfinder.StartCellFor(World, World.Player.Position) ?? World.Player.Position.ToBlockPos())) return Ok("arrived");
        int scaffold = o.AllowPlace ? Math.Min(o.ScaffoldBudget, World.Inventory.CountItem(Blocks.Cobblestone)) : 0;
        var path = Pathfinder.FindPath(World, World.Player.Position, goal, PathOptions.Default with
        {
            AllowPlace = scaffold > 0, ScaffoldBudget = scaffold, AllowBreak = o.AllowBreak, AllowPartial = false,
        }, ct);
        if (!path.Success) return No($"no path to {goal} ({path.Reason ?? path.Status.ToString()})");
        foreach (var step in path.Steps)
        {
            foreach (var b in step.Break) Set(b, 0);
            if (step.Place is { } p) { Take(Blocks.Cobblestone); Set(p, Blocks.Cobblestone); }
            if (step.Opens.Count > 0) return No($"fake follower can't open {step.Opens[0]}");
            var feet = step.Target;
            Stand(feet.X, feet.Y, feet.Z);
            Visited.Add(feet);
            if (Safety.IsPlayerOverVoid(World, feet)) Violations.Add($"over void at {feet}");
        }
        return Ok($"arrived at {World.Player.Position}");
    }

    /// <summary>Called right after a dig changes the world (simulates what the server does next, e.g. trampled farmland).</summary>
    public Action<BlockPos>? AfterDig { get; set; }

    // ---- blocks ----
    public Task<ActionResult> DigAsync(BlockPos p, CancellationToken ct)
    {
        int id = World.GetBlock(p), meta = World.GetMeta(p);
        Log.Add($"dig {p}");
        if (id == 0) return Ok("already clear");
        if (ProtectedBlocks.Contains(p)) return No("protected");
        if (Safety.SupportBlocks(World, World.Player.Position).Contains(p)) return No("won't dig the block I'm standing on");
        if (id is Blocks.SugarCane or Blocks.Cactus)
        {
            int n = 0;
            for (var q = p; World.GetBlock(q) == id; q = q.Up()) { Set(q, 0); n++; }
            Give(id == Blocks.SugarCane ? Items.SugarCane : Blocks.Cactus, n);
            return Ok($"dug {n}");
        }
        Set(p, 0);
        AfterDig?.Invoke(p);
        if (id == Blocks.Crops) { if (meta >= 7) Give(Items.Wheat, 1); Give(Items.Seeds, meta >= 7 ? 2 : 1); }
        else if (id == Blocks.Melon) Give(Items.MelonSlice, 4);
        else if (Drops.Primary(id, meta) is { } d) Give(d.ItemId, Math.Max(1, d.Min), d.Damage);
        return Ok($"dug {Blocks.Get(id).Name}");
    }

    public Task<ActionResult> PlaceAsync(BlockPos p, int itemId, CancellationToken ct)
    {
        Log.Add($"place {p} {itemId}");
        if (World.Inventory.CountItem(itemId) == 0) return No($"no item {itemId}");
        if (!Aiming.IsReplaceable(World, World.GetBlock(p))) return No("occupied");
        if (!BlockFaceExtensions.All.Any(f => BlockShapes.HasCollision(World, p.Offset(f).X, p.Offset(f).Y, p.Offset(f).Z))) return No("no support face");
        if (Safety.IsPlayerOverVoid(World, World.Player.Position)) Violations.Add($"placing while over void at {World.Player.Position}");
        if (itemId == Items.FlintAndSteel)
        {
            if (PortalFrame(p) is { } inside) { foreach (var c in inside) Set(c, Blocks.Portal); return Ok("portal"); }
            Set(p, Blocks.Fire);
            return No("fire didn't appear (still replaceable)");
        }
        int block = itemId switch
        {
            Items.Seeds => World.GetBlock(p.Down()) == Blocks.Farmland ? Blocks.Crops : -1,
            Items.SugarCane => Blocks.SugarCane, Items.Sign => Blocks.SignPost, _ => itemId < 256 ? itemId : -1,
        };
        if (block < 0) return No("can't place that here");
        int meta = 0;
        if (block == Blocks.Ladder)
        {
            var wall = new[] { (2, new BlockPos(0, 0, 1)), (3, new BlockPos(0, 0, -1)), (4, new BlockPos(1, 0, 0)), (5, new BlockPos(-1, 0, 0)) }
                .FirstOrDefault(w => BlockShapes.IsFullCube(World, (p + w.Item2).X, (p + w.Item2).Y, (p + w.Item2).Z));
            meta = wall.Item1;
        }
        Take(itemId);
        Set(p, block, meta);
        return Ok($"placed {block}");
    }

    private List<BlockPos>? PortalFrame(BlockPos inside)
    {
        foreach (bool x in new[] { true, false })
        {
            // inside is the bottom-left or bottom-right interior cell
            foreach (int shift in new[] { 0, -1 })
            {
                var i0 = x ? inside.Offset(shift, 0, 0) : inside.Offset(0, 0, shift);
                if (!PortalCheck(i0, x)) continue;
                var cells = new List<BlockPos>();
                for (int u = 0; u < 2; u++) for (int v = 0; v < 3; v++) cells.Add(x ? i0.Offset(u, v, 0) : i0.Offset(0, v, u));
                return cells;
            }
        }
        return null;
    }

    private bool PortalCheck(BlockPos i0, bool x)
    {
        BlockPos A(int u, int v) => x ? i0.Offset(u, v, 0) : i0.Offset(0, v, u);
        for (int u = 0; u < 2; u++) { if (World.GetBlock(A(u, -1)) != Blocks.Obsidian || World.GetBlock(A(u, 3)) != Blocks.Obsidian) return false; }
        for (int v = 0; v < 3; v++) { if (World.GetBlock(A(-1, v)) != Blocks.Obsidian || World.GetBlock(A(2, v)) != Blocks.Obsidian) return false; }
        return true;
    }

    public Task<ActionResult> ActivateAsync(BlockPos p, CancellationToken ct)
    {
        int id = World.GetBlock(p), meta = World.GetMeta(p);
        Log.Add($"activate {p}");
        if (id == 0 || !Blocks.Get(id).Interactable) return No("not interactable");
        if (Locked.Contains(p) || Locked.Contains(p.Down())) return Ok("clicked"); // plugin swallows it: no block change
        switch (id)
        {
            case Blocks.WoodenDoor:
            {
                var lower = (meta & 8) != 0 ? p.Down() : p;
                ToggleDoor(lower);
                break;
            }
            case Blocks.Trapdoor or Blocks.FenceGate: Set(p, id, meta ^ 4); break;
            case Blocks.Lever:
                Set(p, id, meta ^ 8);
                PowerIronDoors(p, (meta & 8) == 0);
                break;
            case Blocks.StoneButton:
                Set(p, id, meta | 8);
                PowerIronDoors(p, true);
                break;
            case Blocks.Bed:
                if (World.IsNight) Set(p, id, meta | 4);
                break;
            case Blocks.Cake: if (meta >= 5) Set(p, 0); else Set(p, id, meta + 1); break;
            case Blocks.RepeaterOff or Blocks.RepeaterOn: Set(p, id, (meta & 3) | (((meta >> 2) + 1 & 3) << 2)); break;
            case Blocks.Jukebox: Set(p, id, meta == 0 ? 1 : 0); break;
        }
        return Ok("activated");
    }

    private void ToggleDoor(BlockPos lower)
    {
        int id = World.GetBlock(lower), m = World.GetMeta(lower);
        Set(lower, id, m ^ 4);
        if (World.GetBlock(lower.Up()) == id) Set(lower.Up(), id, World.GetMeta(lower.Up()) ^ 4);
    }

    private void PowerIronDoors(BlockPos from, bool on)
    {
        foreach (var f in World.FindBlocks(from.Center, 2.5, Blocks.IronDoor))
        {
            if ((f.Meta & 8) != 0) continue;
            if (Openables.IsOpen(f.Meta) != on) ToggleDoor(f.Pos);
        }
    }

    public Task<ActionResult> LookAtAsync(Vec3d point, CancellationToken ct) { _look = point; return Ok("looking"); }

    public Task<ActionResult> UseItemAsync(int? itemId, CancellationToken ct)
    {
        int id = itemId ?? World.Inventory.HeldItem(World.Player.HeldSlot).Id;
        Log.Add($"use {id}");
        if (World.Inventory.CountItem(id) == 0) return No("no item");
        var eye = World.Player.EyePosition;
        if (id == Items.Bucket)
        {
            if (BucketOps.FirstHit(World, eye, _look, liquids: true) is not { } src) return Ok("nothing");
            bool lava = BlockShapes.IsLava(World.GetBlock(src));
            Set(src, 0);
            Take(Items.Bucket);
            Give(lava ? Items.LavaBucket : Items.WaterBucket, 1);
            return Ok("filled");
        }
        if (id is Items.WaterBucket or Items.LavaBucket)
        {
            var hit = Raycast.Cast(World, eye, _look - eye, 6);
            if (hit is not { } h) return Ok("nothing");
            var target = h.Pos.Offset(h.Face);
            if (!Aiming.IsReplaceable(World, World.GetBlock(target))) return Ok("blocked");
            bool lava = id == Items.LavaBucket;
            Set(target, lava ? Blocks.Lava : Blocks.Water, 0);
            Take(id);
            Give(Items.Bucket, 1);
            Settle(target, lava);
            return Ok("poured");
        }
        if (Items.Food(id) is not null) { Take(id); return Ok("ate"); }
        Take(id);
        return Ok("used");
    }

    /// <summary>Tiny liquid model: infinite sources (≥2 source neighbours over a solid/source floor) and hardening.</summary>
    private void Settle(BlockPos placed, bool lava)
    {
        // Hardening around the placed liquid.
        foreach (var f in BlockFaceExtensions.All)
        {
            var n = placed.Offset(f);
            int nid = World.GetBlock(n);
            if (!lava && BlockShapes.IsLava(nid) && f != BlockFace.Bottom) { Set(n, World.GetMeta(n) == 0 ? Blocks.Obsidian : Blocks.Cobblestone); Mixed.Add(n); }
            if (lava && BlockShapes.IsWater(nid) && f != BlockFace.Bottom) { Set(placed, Blocks.Obsidian); Mixed.Add(placed); return; }
        }
        if (lava) return;
        // One step of flow: water running off the placed source onto lava below its neighbours hardens it.
        foreach (var o in SkillWorld.Horizontal)
        {
            var n = placed + o;
            if (World.GetBlock(n) != 0) continue;
            var under = n.Down();
            if (BlockShapes.IsLava(World.GetBlock(under))) { Set(under, World.GetMeta(under) == 0 ? Blocks.Obsidian : Blocks.Cobblestone); Mixed.Add(under); }
        }
        for (int pass = 0; pass < 4; pass++)
        {
            var cells = World.FindBlocks(placed.Center, 4, (bid, m) => bid == 0 || (BlockShapes.IsWater(bid) && m != 0)).Select(c => c.Pos).Where(c => c.Y == placed.Y).ToList();
            foreach (var c in cells)
            {
                int src = SkillWorld.Horizontal.Count(o => BlockShapes.IsWater(World.GetBlock(c + o)) && World.GetMeta(c + o) == 0);
                int below = World.GetBlock(c.Down());
                bool floor = BlockShapes.IsFullCube(World, c.X, c.Y - 1, c.Z) || (BlockShapes.IsWater(below) && World.GetMeta(c.Down()) == 0);
                bool walled = SkillWorld.Horizontal.All(o => BlockShapes.HasCollision(World, (c + o).X, (c + o).Y, (c + o).Z) || BlockShapes.IsWater(World.GetBlock(c + o)));
                if (src >= 2 && floor && walled) Set(c, Blocks.Water, 0);
            }
        }
    }

    public Task<ActionResult> EatAsync(CancellationToken ct) => World.Player.Food >= 20 ? Ok("already full (food 20/20)") : Ok("ate bread");
    public Task<ActionResult> CollectDropsAsync(double radius, CancellationToken ct) { Log.Add("collect"); return Ok("collected"); }
    public Task<ActionResult> MineAsync(string blockName, int count, double radius, CancellationToken ct) { Log.Add($"mine {blockName} {count}"); return Ok($"mined {count} {blockName}"); }
    public Task<ActionResult> CraftAsync(string itemName, int count, CancellationToken ct) { Log.Add($"craft {itemName} {count}"); return Ok($"crafted {count} {itemName}"); }
    public Task<ActionResult> SmeltAsync(int input, int count, int? fuel, bool wait, BlockPos? furnace, CancellationToken ct)
    {
        Log.Add($"smelt {input} {count} fuel {fuel} at {furnace}");
        Take(input, count);
        return Ok($"smelting {count}");
    }
    public Task<ActionResult> CollectFurnaceAsync(BlockPos? furnace, CancellationToken ct) { Log.Add($"collect_furnace {furnace}"); return Ok("took 8 stone"); }

    // ---- windows ----
    public Task<ActionResult> OpenContainerAsync(BlockPos p, CancellationToken ct)
    {
        if (!Chests.TryGetValue(p, out var items)) return No($"no chest at {p}");
        Opens[p] = Opens.GetValueOrDefault(p) + 1;
        _windowId++;
        World.Inventory.Open(_windowId, WindowType.Chest, "Chest", items.Length);
        World.Inventory.SetWindowItems(_windowId, items);
        _openChest = p;
        return Ok("opened");
    }

    public Task<ActionResult> CloseWindowAsync(CancellationToken ct)
    {
        if (!World.Inventory.Cursor.IsEmpty) Violations.Add($"closed with {World.Inventory.Cursor} on the cursor");
        if (World.Inventory.OpenWindow is { } w && _openChest is { } p)
            Chests[p] = Enumerable.Range(0, w.ContainerSlots).Select(w.Get).ToArray();
        World.Inventory.Close(-1);
        _openChest = null;
        return Ok("closed");
    }

    public Task<ActionResult> ClickSlotAsync(int slot, bool rightClick, bool shift, CancellationToken ct)
    {
        Clicks++;
        if (Clicks == RejectClick) return No("click rejected by server (resynced)");
        World.Inventory.ApplyClick(slot, rightClick, shift);
        return Ok("clicked");
    }

    public Task<ActionResult> DepositAllAsync(BlockPos? container, Func<ItemStack, bool>? filter, CancellationToken ct) => Ok("deposited");
    public Task<ActionResult> WithdrawAsync(int itemId, int count, BlockPos? container, int? damage, CancellationToken ct) => Ok("withdrew");
    public Task<ActionResult> SendChatAsync(string text, CancellationToken ct) => Ok("said");
    public Task WaitTicksAsync(int ticks, CancellationToken ct) { World.Time += ticks; return Task.CompletedTask; }
    public Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct) => Task.FromResult(condition());

    // ---- optional primitives ----
    public Task<ActionResult> UseOnBlockAsync(BlockPos pos, BlockFace face, int itemId, CancellationToken ct)
    {
        Log.Add($"use {pos} {itemId}");
        int id = World.GetBlock(pos);
        if (itemId is >= Items.WoodenHoe and <= Items.GoldenHoe && id is Blocks.Dirt or Blocks.Grass && World.GetBlock(pos.Up()) == 0) Set(pos, Blocks.Farmland);
        else if (itemId == Items.Dye && id == Blocks.Crops) { Set(pos, id, 7); Take(Items.Dye); }
        return Ok("used on block");
    }

    public Task<ActionResult> UseEntityAsync(int entityId, int? itemId, CancellationToken ct)
    {
        Log.Add($"use_entity {entityId}");
        var e = World.Entities.Get(entityId);
        if (e is { Kind: EntityKind.Object, TypeId: 1 or 10 }) { RidingEntityId = RidingEntityId == entityId ? -1 : entityId; return Ok("riding"); }
        if (e is { Kind: EntityKind.Mob, TypeId: MobTypes.Wolf } && itemId == Items.Bone && Take(Items.Bone))
        {
            int left = WolfBonesToTame.GetValueOrDefault(entityId, 1) - 1;
            WolfBonesToTame[entityId] = left;
            if (left <= 0) World.Entities.SetMetadata(entityId, new Dictionary<int, object> { [16] = (sbyte)4 });
            return Ok(left <= 0 ? "hearts" : "smoke");
        }
        return Ok("used");
    }
    public Task<ActionResult> WriteSignAsync(BlockPos pos, string[] lines, CancellationToken ct) { SignText[pos] = lines; return Ok("written"); }
    public Task<ActionResult> ReleaseUseAsync(CancellationToken ct) { Take(Items.Arrow); return Ok("released"); }

    // ---- face placement, bow, vehicles ----
    public List<(BlockPos Pos, int Item, BlockFace Face)> FacePlacements { get; } = new();

    public Task<ActionResult> PlaceAsync(BlockPos p, int itemId, BlockFace face, CancellationToken ct)
    {
        var n = face.Normal();
        var against = new BlockPos(p.X - n.X, p.Y - n.Y, p.Z - n.Z);
        Log.Add($"place {p} {itemId} face {face}");
        FacePlacements.Add((p, itemId, face));
        if (World.Inventory.CountItem(itemId) == 0) return No($"no item {itemId}");
        if (!Aiming.IsReplaceable(World, World.GetBlock(p))) return No("occupied");
        int aid = World.GetBlock(against);
        if (aid == 0 || Blocks.Get(aid).Interactable) return No($"can't place against {Blocks.Get(aid).Name}");
        if (itemId != Items.Sign) return PlaceAsync(p, itemId, ct);
        if (face == BlockFace.Bottom) return No("signs don't hang under blocks");
        Take(itemId);
        if (face == BlockFace.Top) Set(p, Blocks.SignPost, 0);
        else Set(p, Blocks.WallSign, (int)face);
        return Ok("placed sign");
    }

    public int Shots { get; private set; }

    public Task<ActionResult> ShootAsync(Vec3d target, int? entityId, int chargeTicks, CancellationToken ct)
    {
        Log.Add($"shoot {entityId?.ToString() ?? target.ToString()} charge {chargeTicks}");
        if (World.Inventory.CountItem(Items.Bow) == 0) return No("no bow");
        if (!Take(Items.Arrow)) return No("no arrows");
        Shots++;
        return Ok("shot an arrow");
    }

    public int RidingEntityId { get; set; } = -1;
    public Dictionary<int, int> WolfBonesToTame { get; } = new();

    public Task<ActionResult> DismountAsync(CancellationToken ct)
    {
        if (RidingEntityId < 0) return Ok("not riding anything");
        RidingEntityId = -1;
        return Ok("got out");
    }
    public Task<ActionResult> DropHeldAsync(bool wholeStack, CancellationToken ct) => Ok("dropped");
}
