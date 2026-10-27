using System.Text;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    /// <summary>Blocks worth pointing out by position (nearest instance + count).</summary>
    private static readonly int[] InterestingBlocks =
    [
        Blocks.Chest, Blocks.Furnace, Blocks.LitFurnace, Blocks.Workbench, Blocks.Water, Blocks.FlowingWater, Blocks.Lava,
        Blocks.FlowingLava, Blocks.Sapling, Blocks.Log, Blocks.CoalOre, Blocks.IronOre, Blocks.GoldOre, Blocks.DiamondOre,
        Blocks.LapisOre, Blocks.RedstoneOre, Blocks.Crops, Blocks.SugarCane, Blocks.Cactus, Blocks.Pumpkin, Blocks.Melon,
        Blocks.Bed, Blocks.WoodenDoor, Blocks.Dispenser, Blocks.SignPost, Blocks.WallSign, Blocks.RedMushroom,
        Blocks.BrownMushroom, Blocks.Ice, Blocks.Sand, Blocks.Obsidian, Blocks.PumpkinStem, Blocks.MelonStem, Blocks.Farmland,
    ];

    /// <summary>True once the server sent this session's first 0x08 health (before that health/food are defaults).</summary>
    public bool VitalsKnown => Volatile.Read(ref _lastHealth) >= 0;

    /// <summary>True once the column under the feet is loaded and the post-login/teleport safety freeze verified the ground.</summary>
    public bool GroundKnown
    {
        get
        {
            var p = World.Player.Position;
            return !IsFrozenForSafety && World.IsLoaded((int)Math.Floor(p.X), (int)Math.Floor(p.Z));
        }
    }

    /// <summary>Waits (at most <paramref name="max"/>) until health and the ground under the feet are known, so a join status
    /// line never shows the defaults. True when both arrived.</summary>
    public async Task<bool> WaitForJoinStateAsync(TimeSpan max, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + max;
        while (!(VitalsKnown && GroundKnown))
        {
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>One line: position, health, food, held item, dimension, time of day, current action.</summary>
    public string DescribeStatus()
    {
        var p = World.Player.Snapshot;
        var held = HeldStack();
        string dim = p.Dimension == -1 ? "nether" : "overworld";
        long tod = World.TimeOfDay;
        int hours = (int)((tod / 1000 + 6) % 24), minutes = (int)(tod % 1000 * 60 / 1000);
        var sb = new StringBuilder();
        sb.Append($"pos {p.Position.X:0.#},{p.Position.Y:0.#},{p.Position.Z:0.#}");
        // Live 10-27 21:03: the join line said "hp 20/20 food 20/20 | airborne | OVER VOID" from the defaults before the 0x08
        // health and the chunks under the feet arrived (a second later: food 10, ground verified). Unknown = "loading…".
        sb.Append(p.IsDead ? " | DEAD" : VitalsKnown ? $" | hp {p.Health}/20 food {p.Food}/20" : " | hp/food loading…");
        sb.Append($" | held {(held.IsEmpty ? "nothing" : ItemName(held.Id, held.Damage) + (held.Count > 1 ? " x" + held.Count : ""))}");
        sb.Append($" | {dim} {hours:00}:{minutes:00} {(World.IsNight ? "night" : "day")}{(IsRaining ? " rain" : "")}");
        if (!GroundKnown) sb.Append(" | ground loading…");
        else
        {
            if (!p.OnGround) sb.Append(" | airborne");
            if (World.IsPlayerOverVoid(p.Position)) sb.Append(" | OVER VOID");
        }
        sb.Append(CurrentAction is { } a ? $" | doing {a}" : " | idle");
        if (_state != ClientState.Playing) sb.Append($" | {_state.ToString().ToLowerInvariant()}");
        return sb.ToString();
    }

    /// <summary>Grouped inventory ("cobblestone x64, ..."), held item, free slots, armor. <paramref name="detail"/> adds the hotbar layout.</summary>
    public string DescribeInventory(bool detail = false, int maxChars = 600)
    {
        var slots = World.Inventory.PlayerSlots();
        var groups = new Dictionary<(int, int), int>();
        int used = 0;
        for (int i = 9; i < 45; i++)
        {
            var s = slots[i];
            if (s.IsEmpty) continue;
            used++;
            int dmgKey = Items.Get(s.Id).MaxDamage > 0 ? 0 : s.Damage; // tools: group regardless of wear
            groups[(s.Id, dmgKey)] = groups.GetValueOrDefault((s.Id, dmgKey)) + s.Count;
        }
        var sb = new StringBuilder();
        var held = HeldStack();
        sb.Append($"held: {(held.IsEmpty ? "nothing" : ItemName(held.Id, held.Damage))} (slot {World.Player.HeldSlot + 1})");
        sb.Append($" | {used}/36 slots used: ");
        sb.Append(groups.Count == 0 ? "empty" : string.Join(", ", groups.OrderByDescending(kv => kv.Value)
            .Select(kv => $"{ItemName(kv.Key.Item1, kv.Key.Item2)} x{kv.Value}")));
        var armor = Enumerable.Range(5, 4).Select(i => slots[i]).Where(s => !s.IsEmpty).Select(s => ItemName(s.Id, s.Damage)).ToList();
        sb.Append(" | armor: ").Append(armor.Count == 0 ? "none" : string.Join(", ", armor));
        if (detail)
        {
            sb.Append(" | hotbar: ");
            sb.Append(string.Join(" ", Enumerable.Range(0, 9).Select(i =>
            {
                var s = slots[36 + i];
                return $"{i + 1}={(s.IsEmpty ? "-" : ItemName(s.Id, s.Damage) + (s.Count > 1 ? "x" + s.Count : ""))}";
            })));
            var cur = World.Inventory.Cursor;
            if (!cur.IsEmpty) sb.Append($" | cursor: {ItemName(cur.Id, cur.Damage)} x{cur.Count}");
        }
        return Truncate(sb.ToString(), detail ? Math.Max(maxChars, 1200) : maxChars);
    }

    /// <summary>
    /// Compact view of the area: what's underfoot, distance to the void in each direction, counts of notable blocks,
    /// nearest chests/furnaces/water/lava/ores/saplings..., and nearby players, mobs and dropped items.
    /// </summary>
    public string DescribeSurroundings(double radius = 12, bool detail = false, int maxChars = 600)
    {
        if (detail) maxChars = Math.Max(maxChars, 1500);
        var me = World.Player.Position;
        int fx = (int)Math.Floor(me.X), fy = (int)Math.Floor(me.Y + 0.001), fz = (int)Math.Floor(me.Z);
        var sb = new StringBuilder();

        // Underfoot + void edges.
        int below = World.GetBlock(fx, fy - 1, fz);
        sb.Append($"at {fx},{fy},{fz} on {BlockName(below, 0)}");
        var edges = new List<string>();
        foreach (var (name, dx, dz) in new[] { ("N", 0, -1), ("E", 1, 0), ("S", 0, 1), ("W", -1, 0) })
        {
            for (int d = 1; d <= 8; d++)
            {
                int x = fx + dx * d, z = fz + dz * d;
                if (BlockShapes.HasCollision(World, x, fy, z) && BlockShapes.HasCollision(World, x, fy + 1, z)) break; // wall
                if (World.IsOverVoid(x, fy, z)) { edges.Add($"{name}{d}"); break; }
            }
        }
        sb.Append(edges.Count > 0 ? $"; void {string.Join(" ", edges)}" : "; no void within 8");
        if (Safety.VoidNeighbourCount(World, fx, fy, fz) > 0) sb.Append(" (EDGE)");

        // Block census.
        var all = World.FindBlocks(me, radius, (id, _) => id != 0, 40000);
        // Variant blocks keep their type ("cobblestone_slab", not every slab as "stone_slab": live 10-27 the brain read
        // the generator's cobble slabs as smooth-stone ones and planned smelting).
        var counts = all.GroupBy(f => CensusName(f.Id, f.Meta)).ToDictionary(g => g.Key, g => g.Count());
        sb.Append("; blocks: ");
        sb.Append(counts.Count == 0 ? "none" : string.Join(", ", counts.OrderByDescending(kv => kv.Value).Take(detail ? 16 : 7)
            .Select(kv => $"{kv.Key} {kv.Value}")));

        // Points of interest: nearest of each type.
        var poi = new List<string>();
        foreach (var grp in all.Where(f => InterestingBlocks.Contains(f.Id)).GroupBy(f => Canonical(f.Id)))
        {
            var nearest = grp.MinBy(f => f.DistanceSquared);
            int n = grp.Count();
            poi.Add($"{BlockName(grp.Key, 0)}@{nearest.Pos.X},{nearest.Pos.Y},{nearest.Pos.Z}{(n > 1 ? $"(x{n})" : "")}");
        }
        if (poi.Count > 0) sb.Append("; near: ").Append(string.Join(" ", poi.Take(detail ? 24 : 10)));

        // Entities.
        var ents = World.Entities.Nearby(me, Math.Max(radius * 2, 24));
        var players = ents.Where(e => e.Kind == EntityKind.Player).Select(e => $"{e.Name} {e.Position.DistanceTo(me):0}m");
        var mobs = ents.Where(e => e.Kind == EntityKind.Mob).GroupBy(e => e.DisplayName)
            .Select(g => $"{(g.First().IsHostileMob ? "!" : "")}{g.Key}{(g.Count() > 1 ? "x" + g.Count() : "")} {g.Min(e => e.Position.DistanceTo(me)):0}m");
        var items = ents.Where(e => e.Kind == EntityKind.Item).GroupBy(e => ItemName(e.Item.Id, e.Item.Damage))
            .Select(g => $"{g.Key} x{g.Sum(e => e.Item.Count)}");
        var pl = players.ToList();
        var mb = mobs.ToList();
        var it = items.ToList();
        if (pl.Count > 0) sb.Append("; players: ").Append(string.Join(", ", pl));
        if (mb.Count > 0) sb.Append("; mobs: ").Append(string.Join(", ", mb.Take(detail ? 12 : 5)));
        if (it.Count > 0) sb.Append("; drops: ").Append(string.Join(", ", it.Take(detail ? 12 : 5)));
        var online = World.Entities.OnlinePlayers.Where(n => !string.Equals(n, Options.Username, StringComparison.OrdinalIgnoreCase)).ToList();
        if (detail && online.Count > 0) sb.Append("; online: ").Append(string.Join(", ", online));
        return Truncate(sb.ToString(), maxChars);
    }

    /// <summary>Census name: slab/wool/stone-brick variants by type (they craft from different things), else the block name.</summary>
    internal static string CensusName(int id, int meta) =>
        id is Blocks.Slab or Blocks.Wool or Blocks.StoneBricks ? Items.VariantName(id, meta) : BlockName(id, 0);

    private static int Canonical(int id) => id switch
    {
        Blocks.FlowingWater => Blocks.Water,
        Blocks.FlowingLava => Blocks.Lava,
        Blocks.LitFurnace => Blocks.Furnace,
        Blocks.WallSign => Blocks.SignPost,
        _ => id,
    };
}
