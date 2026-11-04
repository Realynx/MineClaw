using System.Text;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

/// <summary>
/// More areas of the WP09 golden traces: doors and redstone (open/close/toggle, iron doors by button/lever/plate, goto_via_doors),
/// smelting, repeater/jukebox, projectiles and bows, the right-click-a-mob skills, sleeping, giving, the nether portal frame,
/// buckets, promoted structure skills and the registry's preconditions (scenarios from the skills' behaviour tests).
/// </summary>
public partial class SkillTraceGoldenTests
{

    private static async Task Doors(StringBuilder sb)
    {
        sb.Append("## wooden door: open, again, close by kind, toggle twice\n");
        {
            var g = DoorTests.Room();
            await Run(sb, g, "open_door", new { pos = "0,65,0" });
            await Run(sb, g, "open_door", new { pos = "0,64,0" });
            await Run(sb, g, "close_door", new { kind = "door" });
            await Run(sb, g, "toggle_door");
            await Run(sb, g, "toggle_door", new { kind = "door" });
            await Run(sb, g, "open_door", new { kind = "gate" });
            await Run(sb, g, "open_door", new { pos = "3,64,3" });
        }
        sb.Append("## locked door\n");
        {
            var g = DoorTests.Room();
            g.Locked.Add(new BlockPos(0, 64, 0));
            await Run(sb, g, "open_door", new { pos = "0,64,0" });
            await Run(sb, g, "goto_via_doors", new { pos = "2,64,0" });
        }
        sb.Append("## fence gate\n");
        {
            var g = DoorTests.Room(Blocks.FenceGate);
            await Run(sb, g, "open_door", new { kind = "fence_gate" });
            await Run(sb, g, "close_door", new { kind = "gate" });
            await Run(sb, g, "goto_via_doors", new { pos = "2,64,0" });
        }
        sb.Append("## iron door: no trigger, a button, a lever, a plate\n");
        {
            var g = DoorTests.Room(Blocks.IronDoor);
            await Run(sb, g, "open_door", new { pos = "0,64,0" });
            await Run(sb, g, "open_door", new { kind = "iron_door" });
            g.Set(-1, 65, 1, Blocks.StoneButton);
            await Run(sb, g, "open_door", new { pos = "0,64,0" });
            await Run(sb, g, "close_door", new { pos = "0,64,0" });
            var l = DoorTests.Room(Blocks.IronDoor);
            l.Set(-1, 65, 1, Blocks.Lever, 0);
            await Run(sb, l, "use_lever", new { pos = "-1,65,1", on = true });
            await Run(sb, l, "use_lever", new { pos = "-1,65,1", on = true });
            await Run(sb, l, "close_door", new { pos = "0,64,0" });
            await Run(sb, l, "use_lever", new { on = false });
            await Run(sb, l, "use_lever", new { pos = "3,64,3" });
            await Run(sb, l, "press_button", new { pos = "-1,65,1" });
            var p = DoorTests.Room(Blocks.IronDoor);
            p.Set(-1, 64, 0, Blocks.StonePressurePlate, 0);
            await Run(sb, p, "open_door", new { pos = "0,64,0" });
            await Run(sb, p, "close_door", new { pos = "0,64,0" });
        }
        sb.Append("## goto via doors: through and back, leave open, an open door, no path\n");
        {
            var g = DoorTests.Room();
            await Run(sb, g, "goto_via_doors", new { pos = "2,64,0" });
            await Run(sb, g, "goto_via_doors", new { pos = "-2,64,0", leave_open = true });
            await Run(sb, g, "goto_via_doors", new { pos = "-3,64,1" });
            await Run(sb, g, "goto_via_doors");
            var open = DoorTests.Room(lowerMeta: 4);
            await Run(sb, open, "goto_via_doors", new { pos = "2,64,0" });
            var shut = DoorTests.Room(Blocks.IronDoor);
            await Run(sb, shut, "goto_via_doors", new { pos = "2,64,0" });
            var btn = DoorTests.Room(Blocks.IronDoor);
            btn.Set(-1, 65, 1, Blocks.StoneButton);
            await Run(sb, btn, "goto_via_doors", new { pos = "2,64,0" });
        }
        sb.Append("## trapdoor under me\n");
        {
            var g = new FakeGame();
            g.Fill(-3, 60, -3, 3, 60, 3, Blocks.Stone);
            g.Set(0, 63, 0, Blocks.Trapdoor, 0);
            g.Stand(0.5, 63.1875, 0.5);
            await Run(sb, g, "open_door", new { pos = "0,63,0" });
            await Run(sb, g, "open_door", new { kind = "trapdoor" });
        }
    }

    private static async Task Misc(StringBuilder sb)
    {
        sb.Append("## smelting\n");
        {
            var g = LiquidTests.Island();
            await Run(sb, g, "smelt_batch", new { item = "stone", count = 16 });
            g.Set(2, 64, 2, Blocks.Furnace);
            await Run(sb, g, "smelt_batch", new { item = "stone", count = 16 });
            g.Give(Blocks.Cobblestone, 20);
            await Run(sb, g, "smelt_batch", new { item = "stone", count = 16 });
            g.Give(Items.Coal, 3);
            await Run(sb, g, "smelt_batch", new { item = "stone", count = 16 });
            await Run(sb, g, "smelt_batch", new { item = "cobblestone", wait = true });
            await Run(sb, g, "smelt_batch", new { item = "dirt" });
            await Run(sb, g, "smelt_batch", new { item = "unobtainium" });
            await Run(sb, g, "refuel_furnace");
            await Run(sb, g, "refuel_furnace", new { pos = "2,64,2", fuel = "coal", count = 2 });
            await Run(sb, g, "refuel_furnace", new { pos = "3,64,3" });
            await Run(sb, g, "collect_furnace");
            await Run(sb, g, "collect_furnace", new { pos = "2,64,2" });
            var logs = LiquidTests.Island();
            logs.Set(2, 64, 2, Blocks.Furnace);
            logs.Set(-2, 64, -2, Blocks.Furnace);
            logs.Give(Blocks.Log, 8);
            logs.Give(Blocks.Planks, 4);
            await Run(sb, logs, "smelt_batch", new { item = "charcoal" });
            await Run(sb, logs, "refuel_furnace", new { fuel = "planks", count = 9 });
            await Run(sb, logs, "refuel_furnace", new { fuel = "coal" });
            var many = LiquidTests.Island();
            many.Set(2, 64, 2, Blocks.Furnace);
            many.Set(-2, 64, -2, Blocks.LitFurnace);
            many.Give(Blocks.Cobblestone, 64);
            many.Give(Blocks.Cobblestone, 64);
            many.Give(Blocks.Cobblestone, 10);
            many.Give(Items.Stick, 16);
            await Run(sb, many, "smelt_batch", new { item = "cobblestone", count = 138 });
            await Run(sb, LiquidTests.Island(), "collect_furnace");
            await Run(sb, LiquidTests.Island(), "refuel_furnace");
            var fu = LiquidTests.Island();
            fu.Set(2, 64, 2, Blocks.Furnace);
            fu.Chests[new BlockPos(2, 64, 2)] = Enumerable.Repeat(ItemStack.Empty, 3).ToArray();
            fu.Give(Items.Coal, 5);
            await Run(sb, fu, "refuel_furnace", new { count = 3 });
            await Run(sb, fu, "refuel_furnace", new { count = 9 });
            fu.Give(Blocks.Planks, 2);
            await Run(sb, fu, "refuel_furnace", new { fuel = "planks" });
        }
        sb.Append("## repeater, jukebox\n");
        {
            var g = LiquidTests.Island();
            await Run(sb, g, "set_repeater", new { pos = "2,64,2", delay = 3 });
            g.Set(2, 64, 2, Blocks.RepeaterOff, 1);
            await Run(sb, g, "set_repeater", new { pos = "2,64,2", delay = 3 });
            await Run(sb, g, "set_repeater", new { pos = "2,64,2", delay = 3 });
            await Run(sb, g, "set_repeater", new { pos = "2,64,2", delay = 9 });
            await Run(sb, g, "set_repeater", new { pos = "2,64,2" });
            g.ProtectedBlocks.Add(new BlockPos(2, 64, 2));
            await Run(sb, g, "set_repeater", new { pos = "2,64,2", delay = 2 });
            await Run(sb, g, "use_jukebox");
            g.Set(-2, 64, 2, Blocks.Jukebox, 0);
            await Run(sb, g, "use_jukebox");
            g.Give(Items.RecordCat, 1);
            await Run(sb, g, "use_jukebox");
            await Run(sb, g, "use_jukebox", new { eject = true });
            await Run(sb, g, "use_jukebox", new { pos = "-2,64,2", eject = true });
            g.Set(-2, 64, 2, Blocks.Jukebox, 1);
            await Run(sb, g, "use_jukebox");
            await Run(sb, g, "use_jukebox", new { pos = "-2,64,2", eject = false });
        }
        sb.Append("## projectiles and bows\n");
        {
            var g = LiquidTests.Island();
            await Run(sb, g, "shoot_bow");
            g.Give(Items.Bow, 1);
            g.Give(Items.Arrow, 2);
            await Run(sb, g, "shoot_bow");
            g.World.Entities.SpawnMob(77, MobTypes.Zombie, new Vec3d(3.5, 64, 3.5), 0, 0, null);
            await Run(sb, g, "shoot_bow", new { shots = 4 });
            g.Give(Items.Arrow, 3);
            await Run(sb, g, "shoot_bow", new { target = "4,64,-3", shots = 2 });
            await Run(sb, g, "shoot_bow", new { target = "zombie" });
            g.Give(Items.Snowball, 4);
            await Run(sb, g, "throw_projectile", new { count = 3 });
            await Run(sb, g, "throw_projectile", new { target = "3,64,-3", count = 2 });
            await Run(sb, g, "throw_projectile", new { item = "egg" });
            g.Give(Items.Egg, 2);
            await Run(sb, g, "throw_projectile", new { item = "egg", count = 5 });
            g.World.Entities.Destroy(77);
            await Run(sb, g, "throw_projectile");
            g.World.Entities.SpawnMob(21, MobTypes.PigZombie, new Vec3d(3, 64, 3), 0, 0);
            await Run(sb, g, "throw_projectile");
            await Run(sb, g, "shoot_bow");
        }
        sb.Append("## mobs: tame, shear, milk, ride\n");
        {
            var g = LiquidTests.Island();
            await Run(sb, g, "tame_wolf");
            g.Give(Items.Bone, 5);
            g.World.Entities.SpawnMob(55, MobTypes.Wolf, new Vec3d(2.5, 64, 0.5), 0, 0, null);
            g.World.Entities.SpawnMob(56, MobTypes.Wolf, new Vec3d(-2.5, 64, 0.5), 0, 0, null);
            g.WolfBonesToTame[55] = 3;
            g.WolfBonesToTame[56] = 9;
            await Run(sb, g, "tame_wolf", new { count = 2 });
            g.Give(Items.Shears, 1);
            g.World.Entities.SpawnMob(60, MobTypes.Sheep, new Vec3d(1.5, 64, 2.5), 0, 0, null);
            await Run(sb, g, "shear_sheep");
            await Run(sb, g, "shear_sheep");
            g.Give(Items.Bucket, 1);
            g.World.Entities.SpawnMob(61, MobTypes.Cow, new Vec3d(1.5, 64, -1.5), 0, 0, null);
            await Run(sb, g, "milk_cow");
            g.World.Entities.SpawnObject(90, 1, new Vec3d(1.5, 64, 0.5), default);
            await Run(sb, g, "ride_vehicle");
            await Run(sb, g, "ride_vehicle", new { exit = true });
        }
        sb.Append("## sleep, give\n");
        {
            var g = LiquidTests.Island();
            await Run(sb, g, "sleep_in_bed");
            g.Set(2, 64, 2, Blocks.Bed, 8);
            await Run(sb, g, "sleep_in_bed");
            g.World.Time = 14000;
            g.World.Entities.SpawnMob(9, MobTypes.Skeleton, new Vec3d(4.5, 64, 2.5), 0, 0);
            await Run(sb, g, "sleep_in_bed");
            g.World.Entities.Destroy(9);
            await Run(sb, g, "sleep_in_bed");
            g.Set(2, 64, 2, Blocks.Bed, 8);
            g.Set(-2, 64, 2, Blocks.Stone);
            await Run(sb, g, "sleep_in_bed", new { pos = "-2,64,2" });
            g.World.Dimension = -1;
            await Run(sb, g, "sleep_in_bed");
            var h = LiquidTests.Island();
            h.SetSlot(36, Blocks.Dirt, 10);
            await Run(sb, h, "give_items", new { player = "nobody", item = "dirt" });
            h.World.Entities.SpawnPlayer(11, "kiwi", new Vec3d(2.5, 64, 2.5), 0, 0);
            await Run(sb, h, "give_items", new { player = "kiwi", item = "dirt", count = 5 });
            await Run(sb, h, "give_items", new { player = "Kiwi", item = "dirt", count = 50 });
            await Run(sb, h, "give_items", new { player = "kiwi", item = "diamond" });
            await Run(sb, h, "give_items", new { player = "kiwi" });
            await Run(sb, h, "give_items", new { item = "dirt" });
        }
        sb.Append("## nether portal frame\n");
        {
            var g = LiquidTests.Island(6);
            await Run(sb, g, "build_portal");
            g.Give(Blocks.Obsidian, 6);
            await Run(sb, g, "build_portal");
            g.Give(Blocks.Obsidian, 4);
            g.Give(Blocks.Cobblestone, 8);
            g.Give(Items.FlintAndSteel, 1);
            await Run(sb, g, "build_portal", new { pos = "2,64,-1" });
            await Run(sb, g, "light_portal");
            var z = LiquidTests.Island(6);
            z.Give(Blocks.Obsidian, 10);
            await Run(sb, z, "build_portal", new { pos = "2,64,-1", axis = "z" });
            await Run(sb, z, "build_portal", new { pos = "9,64,9" });
            z.World.Dimension = -1;
            await Run(sb, z, "build_portal");
        }
        sb.Append("## buckets\n");
        {
            var g = LiquidTests.Island();
            g.Fluids = true;
            await Run(sb, g, "fill_bucket");
            g.Give(Items.Bucket, 2);
            await Run(sb, g, "fill_bucket");
            g.Set(3, 63, 3, Blocks.Water);
            await Run(sb, g, "fill_bucket");
            await Run(sb, g, "fill_bucket", new { liquid = "lava" });
            g.Set(-3, 63, -3, Blocks.Lava);
            await Run(sb, g, "fill_bucket", new { liquid = "lava" });
            await Run(sb, g, "empty_bucket");
            await Run(sb, g, "empty_bucket", new { pos = "2,64,-2" });
            await Run(sb, g, "empty_bucket", new { pos = "9,70,9" });
            await Run(sb, g, "fill_bucket", new { pos = "3,63,3" });
            await Run(sb, g, "empty_bucket", new { pos = "-2,64,2" });
            await Run(sb, g, "empty_bucket", new { pos = "-2,64,2" });
            await Run(sb, g, "infinite_water");
            await Run(sb, g, "infinite_water", new { pos = "1,63,-1" });
            await Run(sb, g, "make_obsidian");
        }
        sb.Append("## promoted structure skill\n");
        {
            var g = LiquidTests.Island();
            g.Set(2, 63, 2, Blocks.Stone);
            g.Set(2, 64, -2, Blocks.Lever, 0);
            var reg = SkillRegistry.CreateDefault();
            reg.Add(PromotedSkill.FromStructure("cobble gen", "generator", ["stand at 1,64,1", "mine stone at 2,63,2 3 times", "wait 2 s",
                "pick up at 1,64,1", "dig at 2,63,2 repeatedly", "walk to 0,64,0", "right-click lever at 2,64,-2", "flip at 2,64,-2"], new BlockPos(1, 64, 1)));
            reg.Add(PromotedSkill.FromStructure("bad farm", "farm", ["stand at 1,64,1", "dance a little"], null));
            reg.Add(PromotedSkill.FromStructure("gone", "farm", ["press at 9,90,9", "use at 1,64,1"], null));
            await Run(sb, g, "use_cobble_gen", new { times = 2 }, reg);
            await Run(sb, g, "use_bad_farm", null, reg);
            await Run(sb, g, "use_gone", null, reg);
        }
        sb.Append("## registry: unknown, stub, preconditions, acquire\n");
        {
            var g = LiquidTests.Island();
            await Run(sb, g, "chop_the_moon");
            await Run(sb, g, "go_fishing");
            g.Supported = Primitive.None;
            g.Give(Items.Shears, 1);
            await Run(sb, g, "shear_sheep");
            g.Supported = Primitive.UseOnBlock | Primitive.UseEntity | Primitive.WriteSign | Primitive.ReleaseUse;
            await Run(sb, g, "shoot_bow");
            g.World.Dimension = -1;
            await Run(sb, g, "milk_cow");
            g.World.Dimension = 0;
            var reg = SkillRegistry.CreateDefault();
            reg.AcquireMissing = (ctx, spec, group, ct) =>
            {
                if (group.Contains(Items.Bow)) g.Give(Items.Bow, 1);
                return Task.FromResult<string?>($"fetched for {spec.Name}: {string.Join("/", group)}");
            };
            await Run(sb, g, "shoot_bow", null, reg);
            g.World.Entities.SpawnMob(77, MobTypes.Zombie, new Vec3d(3.5, 64, 3.5), 0, 0, null);
            g.Give(Items.Arrow, 1);
            await Run(sb, g, "shoot_bow", null, reg);
            g.World.Time = 18000;
            await Run(sb, g, "expand_island", null, reg);
        }
    }

    [Fact]
    public async Task Door_traces_match_the_golden() => Same(SkillTraceGoldenData.Doors, await Area("doors", Doors));

    [Fact]
    public async Task Misc_traces_match_the_golden() => Same(SkillTraceGoldenData.Misc, await Area("misc", Misc));
}
