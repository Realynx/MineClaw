using System.Text.Json;
using MineClaw.Data;
using MineClaw.Skills.Inventory;
using MineClaw.World;

namespace MineClaw.Skills;

[Skill]
public sealed class SetRepeaterSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "set_repeater", Category = SkillCategory.Redstone, Summary = "set repeater delay 1-4 by clicking, verified", Args = "pos,delay",
        Schema = SkillJson.Schema("pos:pos,delay:int"), Estimate = TimeSpan.FromSeconds(5),
        Keywords = "repeater delay redstone tick timing circuit",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (args.Pos() is not { } p || ctx.Block(p) is not (Blocks.RepeaterOff or Blocks.RepeaterOn)) return Fail("no repeater there");
        if (ctx.Protected(p)) return Fail(ctx.ProtectedText(p, "repeater"));
        int want = Clamp(args.Int("delay"), 1, 1, 4);
        // b1.8.1 repeater meta: bits 0-1 facing, bits 2-3 = delay-1; each right-click adds one (wrapping).
        int Delay() => ((ctx.Meta(p) >> 2) & 3) + 1;
        var reach = await ctx.ReachAsync(p, ct);
        if (!reach.Ok) return Fail($"can't reach repeater: {reach.Message}");
        for (int i = 0; i < 4 && Delay() != want; i++)
        {
            int before = ctx.Meta(p);
            var r = await ctx.Game.ActivateAsync(p, ct);
            if (!r.Ok) return SkillResult.From(r);
            if (!await ctx.WaitBlockAsync(p, (_, m) => m != before, ct)) return Fail("repeater didn't change (protected?)");
        }
        return Delay() == want ? Ok($"repeater at {P(p)} delay {want}") : Fail($"repeater stuck at delay {Delay()}");
    }
}

[Skill]
public sealed class TuneNoteBlockSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "tune_noteblock", Category = SkillCategory.Redstone, Summary = "right-click note block N times (+1 pitch each)", Args = "pos,clicks",
        Schema = SkillJson.Schema("pos:pos,clicks:int"), Estimate = TimeSpan.FromSeconds(8),
        Keywords = "note block tune pitch music sound song",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (args.Pos() is not { } p || ctx.Block(p) != Blocks.NoteBlock) return Fail("no note block there");
        int n = Clamp(args.Int("clicks"), 1, 1, 24);
        var reach = await ctx.ReachAsync(p, ct);
        if (!reach.Ok) return Fail(reach.Message);
        for (int i = 0; i < n; i++)
        {
            var r = await ctx.Game.ActivateAsync(p, ct);
            if (!r.Ok) return Fail($"stopped after {i}: {r.Message}");
        }
        // Pitch lives in the tile entity; the client only hears the 0x36 note when it plays.
        return Ok($"clicked note block {n}x (pitch +{n} mod 25)");
    }
}

[Skill]
public sealed class LoadDispenserSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "load_dispenser", Category = SkillCategory.Redstone, Summary = "put items (arrows, snowballs...) in a dispenser", Args = "pos,item,count?",
        Schema = SkillJson.Schema("pos:pos,item:str,count:int?"), Estimate = TimeSpan.FromSeconds(10),
        Keywords = "dispenser load arrows trap fill ammo",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (args.Pos() is not { } p || ctx.Block(p) != Blocks.Dispenser) return Fail("no dispenser there");
        if (SkillJson.Item(args.Str("item")) is not { } it || !ctx.Has(it.Id)) return Fail($"no {args.Str("item")}");
        if (it.Id is Blocks.Tnt) return Fail("won't load TNT");
        int n = Clamp(args.Int("count"), ctx.Count(it.Id), 1, ctx.Count(it.Id));
        var run = new InventoryRun(ctx);
        if (await run.OpenAsync(p, ct) is { } err) return Fail(err);
        int moved = await run.TransferAsync(it.Id, null, n, w => w.SlotsOf(Region.Player).ToArray(), w => w.SlotsOf(Region.Container).ToArray(), ct);
        await run.CloseAsync(ct);
        return moved > 0 ? Ok($"loaded {moved} {Item(it.Id)} into dispenser at {P(p)} ({run.Clicks} clicks)") : Fail("dispenser full");
    }
}

[Skill]
public sealed class EatCakeSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "eat_cake", Category = SkillCategory.Survival, Summary = "eat a slice of a placed cake (right-click)", Args = "pos?",
        Schema = SkillJson.Schema("pos:pos?"), Estimate = TimeSpan.FromSeconds(5),
        Keywords = "cake eat slice food dessert",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (ctx.World.Player.Food >= 20) return Fail("not hungry");
        var p = args.Pos() ?? ctx.Find(Blocks.Cake, 12, 1).Select(f => (BlockPos?)f.Pos).FirstOrDefault();
        if (p is not { } c || ctx.Block(c) != Blocks.Cake) return Fail("no cake nearby");
        if (ctx.Protected(c)) return Fail(ctx.ProtectedText(c, "cake"));
        int bites = ctx.Meta(c);
        var reach = await ctx.ReachAsync(c, ct);
        if (!reach.Ok) return Fail(reach.Message);
        var r = await ctx.Game.ActivateAsync(c, ct);
        if (!r.Ok) return SkillResult.From(r);
        return await ctx.WaitBlockAsync(c, (id, m) => id != Blocks.Cake || m != bites, ct)
            ? Ok($"ate cake ({(ctx.Block(c) == Blocks.Cake ? 5 - ctx.Meta(c) : 0)} slices left)") : Fail("cake didn't change");
    }
}

[Skill]
public sealed class ReadSignSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "read_sign", Category = SkillCategory.Misc, Summary = "read sign text at pos or nearest", Args = "pos?",
        Schema = SkillJson.Schema("pos:pos?"), Estimate = TimeSpan.FromSeconds(1), Keywords = "read sign text label words",
    };

    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var signs = ctx.Game.Signs;
        var p = args.Pos() ?? signs.Keys.OrderBy(k => k.DistanceSquaredTo(ctx.Me)).Cast<BlockPos?>().FirstOrDefault();
        if (p is not { } s || !signs.TryGetValue(s, out var lines)) return Task.FromResult(Fail("no sign text known there"));
        return Task.FromResult(Ok($"sign {P(s)}: \"{string.Join(" | ", lines.Where(l => l.Length > 0))}\""));
    }
}

[Skill]
public sealed class WriteSignSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "write_sign", Category = SkillCategory.Misc, Summary = "place a sign and write up to 4 lines", Args = "pos,text",
        Schema = SkillJson.Schema("pos:pos,text:str"), Estimate = TimeSpan.FromSeconds(8),
        Pre = new() { Items = [[Items.Sign]], Needs = "sign", Primitives = Primitive.WriteSign },
        Keywords = "write sign text place label message",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (args.Pos() is not { } p) return Fail("pos required");
        var lines = LabelChestSkill.Wrap(args.Str("text") ?? "");
        if (ctx.Block(p) is not (Blocks.SignPost or Blocks.WallSign))
        {
            // A sign post on solid ground, else a wall sign on a solid neighbour (never on a chest/door).
            BlockFace? face = MineClaw.Client.FaceAim.IsWallSupport(ctx.World, p.Down()) ? BlockFace.Top : ctx.Game.WallFace(p);
            if (face is not { } f) return Fail($"nothing solid under or beside {P(p)} to hold a sign");
            var place = await ctx.Game.PlaceAsync(p, Items.Sign, f, ct);
            if (!place.Ok) return SkillResult.From(place);
        }
        return SkillResult.From(await ctx.Game.WriteSignAsync(p, lines, ct));
    }
}

[Skill]
public sealed class JukeboxSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "use_jukebox", Category = SkillCategory.Misc, Summary = "insert a disc (play) or eject it", Args = "pos?,eject?",
        Schema = SkillJson.Schema("pos:pos?,eject:bool?"), Estimate = TimeSpan.FromSeconds(6),
        Keywords = "jukebox music record disc play song eject 13 cat",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var p = args.Pos() ?? ctx.Find(Blocks.Jukebox, 12, 1).Select(f => (BlockPos?)f.Pos).FirstOrDefault();
        if (p is not { } j || ctx.Block(j) != Blocks.Jukebox) return Fail("no jukebox nearby");
        bool playing = ctx.Meta(j) != 0; // b1.8.1: meta 1 = has a record
        bool eject = args.Flag("eject", playing);
        if (eject && !playing) return Ok("jukebox already empty");
        if (!eject)
        {
            int? disc = ctx.FirstOwned(Items.Record13, Items.RecordCat);
            if (disc is null) return Fail("no music disc");
            var hold = await ctx.RunAsync("hold_item", new { item = Items.Get(disc.Value).Name }, ct);
            if (!hold.Ok) return hold;
        }
        var reach = await ctx.ReachAsync(j, ct);
        if (!reach.Ok) return Fail(reach.Message);
        var r = await ctx.Game.ActivateAsync(j, ct);
        if (!r.Ok) return SkillResult.From(r);
        return await ctx.WaitBlockAsync(j, (_, m) => (m != 0) == !eject, ct) ? Ok(eject ? "ejected the disc" : "playing") : Fail("jukebox didn't react");
    }
}

/// <summary>Throws snowballs/eggs with gravity-compensated aim at a hostile mob or a spot. Never at pigmen, never at
/// players (sparring has its own consent flow).</summary>
[Skill]
public sealed class ThrowProjectileSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "throw_projectile", Category = SkillCategory.Combat, Summary = "throw snowballs/eggs at a mob or spot", Args = "item?,target?,count?",
        Schema = SkillJson.Schema("item:str?,target:str?,count:int?"), Safety = SafetyClass.Combat, Estimate = TimeSpan.FromSeconds(6),
        Pre = new() { Items = [[Items.Snowball, Items.Egg]], Needs = "snowballs/eggs" },
        Keywords = "throw snowball egg ranged knockback hit mob distance",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        int item = args.Str("item")?.Contains("egg", StringComparison.OrdinalIgnoreCase) == true ? Items.Egg : ctx.FirstOwned(Items.Snowball, Items.Egg)!.Value;
        if (!ctx.Has(item)) return Fail($"no {Item(item)}");
        Vec3d aimAt;
        string what;
        var t = args.Str("target");
        if (t is not null && SkillJson.Parse($"{{\"p\":\"{t}\"}}").Pos("p") is { } pos) { aimAt = pos.Center; what = P(pos); }
        else
        {
            var mob = ctx.World.Entities.NearestHostile(ctx.Feet, 24);
            if (mob is null || NetherHazards.NeverAttack(mob)) return Fail("no hostile mob in range");
            aimAt = mob.Position + new Vec3d(0, 1, 0);
            what = mob.DisplayName;
        }
        int n = Clamp(args.Int("count"), 1, 1, 16), thrown = 0;
        for (int i = 0; i < n && ctx.Has(item); i++)
        {
            await ctx.Game.LookAtAsync(Ballistics.Aim(ctx.World.Player.EyePosition, aimAt, 1.5, 0.03), ct);
            if ((await ctx.Game.UseItemAsync(item, ct)).Ok) thrown++;
            await ctx.Game.WaitTicksAsync(4, ct);
        }
        return thrown > 0 ? Ok($"threw {thrown} {Item(item)} at {what}") : Fail("couldn't throw");
    }
}

/// <summary>Projectile aim: the point to look at so a projectile (speed v, gravity g per tick, 0.99 drag) lands on target.</summary>
public static class Ballistics
{
    public static Vec3d Aim(Vec3d eye, Vec3d target, double speed, double gravity)
    {
        var d = target - eye;
        double h = Math.Sqrt(d.X * d.X + d.Z * d.Z);
        if (h < 0.5) return target;
        double t = h / (speed * 0.97); // ticks of flight, rough drag compensation
        double drop = 0.5 * gravity * t * t;
        return new Vec3d(target.X, target.Y + drop, target.Z);
    }
}

[Skill]
public sealed class ShootBowSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "shoot_bow", Category = SkillCategory.Combat, Summary = "charge bow, aim w/ gravity, shoot hostile", Args = "target?,shots?",
        Schema = SkillJson.Schema("target:str?,shots:int?"), Safety = SafetyClass.Combat, Estimate = TimeSpan.FromSeconds(8),
        Pre = new() { Items = [[Items.Bow], [Items.Arrow]], Needs = "bow+arrows", Primitives = Primitive.ReleaseUse },
        Keywords = "bow arrow shoot ranged skeleton ghast archer snipe",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        Vec3d spot = default;
        MineClaw.World.Entity? mob = null;
        var t = args.Str("target");
        if (t is not null && SkillJson.Parse($"{{\"p\":\"{t}\"}}").Pos("p") is { } pos) spot = pos.Center;
        else
        {
            mob = ctx.World.Entities.NearestHostile(ctx.Feet, 32);
            if (mob is null || NetherHazards.NeverAttack(mob)) return Fail("no hostile mob in range");
        }
        int shots = Clamp(args.Int("shots"), 1, 1, 8), fired = 0;
        string? last = null;
        for (int i = 0; i < shots && ctx.Has(Items.Arrow); i++)
        {
            if (mob is not null && ctx.World.Entities.Get(mob.Id) is null) break; // dead or gone
            // Full charge (20 ticks, arrow speed 3.0); the client solves b1.8 arrow gravity/drag and leads the target.
            var r = await ctx.Game.ShootAsync(mob?.Position ?? spot, mob?.Id, 20, ct);
            last = r.Message;
            if (!r.Ok) return fired > 0 ? Ok($"shot {fired} arrow(s), then {r.Message}") : SkillResult.From(r);
            fired++;
        }
        return fired > 0 ? Ok($"shot {fired} arrow(s) at {mob?.DisplayName ?? P(spot.ToBlockPos())}") : Fail(last ?? "no arrows");
    }
}

/// <summary>Right-click-an-entity skills (shear, milk, tame, ride) share this: walk close, use the item on the mob.</summary>
public abstract class UseOnMobSkill : Skill
{
    protected abstract int MobType { get; }
    protected abstract int? ItemId { get; }
    protected abstract string Verb { get; }
    protected virtual bool Accept(MineClaw.World.Entity e) => true;
    protected virtual Func<SkillContext, int, bool>? Verify => null;
    /// <summary>Uses per mob until <see cref="Finished"/> (wolves take several bones).</summary>
    protected virtual int Attempts => 1;
    protected virtual bool Finished(MineClaw.World.Entity e) => true;

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        int n = Clamp(args.Int("count"), 1, 1, 16), done = 0;
        var used = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            var mob = ctx.World.Entities.Nearest(ctx.Feet, 16, e => e.Kind == EntityKind.Mob && e.TypeId == MobType && !used.Contains(e.Id) && Accept(e));
            if (mob is null) break;
            used.Add(mob.Id);
            var g = await ctx.Game.GotoAsync(new GoalNearPosition(mob.Position, 2), null, ct);
            if (!g.Ok) continue;
            bool ok = false;
            for (int k = 0; k < Attempts && !ok; k++)
            {
                if (ItemId is { } need && !ctx.Has(need)) break;
                int before = ItemId is { } it ? ctx.Count(it) : 0;
                var r = await ctx.Game.UseEntityAsync(mob.Id, ItemId, ct);
                if (!r.Ok) return done > 0 ? Ok($"{Verb} {done}, then {r.Message}") : SkillResult.From(r);
                if (Verify is { } v && !await ctx.Game.WaitUntilAsync(() => v(ctx, before), TimeSpan.FromSeconds(2), ct)) continue;
                ok = Attempts == 1 || (ctx.World.Entities.Get(mob.Id) is { } now && Finished(now));
                if (!ok) await ctx.Game.WaitTicksAsync(10, ct);
            }
            if (ok) done++;
        }
        await ctx.Game.CollectDropsAsync(6, ct);
        return done > 0 ? Ok($"{Verb} {done} {MobTypes.Name(MobType)}") : Fail($"no {MobTypes.Name(MobType)} to {Verb.TrimEnd('d', 'e')} nearby");
    }
}

[Skill]
public sealed class ShearSheepSkill : UseOnMobSkill
{
    protected override int MobType => MobTypes.Sheep;
    protected override int? ItemId => Items.Shears;
    protected override string Verb => "sheared";
    // Sheep metadata 16: bits 0-3 colour, 0x10 sheared.
    protected override bool Accept(MineClaw.World.Entity e) => ((e.MetaInt(16) ?? 0) & 0x10) == 0;
    public override SkillSpec Spec { get; } = new()
    {
        Name = "shear_sheep", Category = SkillCategory.Animals, Summary = "shear unsheared sheep, collect wool", Args = "count?",
        Schema = SkillJson.Schema("count:int?"), Estimate = TimeSpan.FromSeconds(20),
        Pre = new() { Items = [[Items.Shears]], Needs = "shears", Dimension = Dim.Overworld, Primitives = Primitive.UseEntity },
        Keywords = "shear sheep wool shears white",
    };
}

[Skill]
public sealed class MilkCowSkill : UseOnMobSkill
{
    protected override int MobType => MobTypes.Cow;
    protected override int? ItemId => Items.Bucket;
    protected override string Verb => "milked";
    protected override Func<SkillContext, int, bool>? Verify => (ctx, _) => ctx.Has(Items.MilkBucket);
    public override SkillSpec Spec { get; } = new()
    {
        Name = "milk_cow", Category = SkillCategory.Animals, Summary = "fill a bucket with milk from a cow", Args = "",
        Estimate = TimeSpan.FromSeconds(10),
        Pre = new() { Items = [[Items.Bucket]], Needs = "bucket", Dimension = Dim.Overworld, Primitives = Primitive.UseEntity },
        Keywords = "milk cow bucket cake",
    };
}

[Skill]
public sealed class TameWolfSkill : UseOnMobSkill
{
    protected override int MobType => MobTypes.Wolf;
    protected override int? ItemId => Items.Bone;
    protected override string Verb => "fed";
    // Wolf metadata 16: 0x4 tamed, 0x2 angry. Each bone tames with chance 1/3 (0x26 status 7 hearts, 6 smoke).
    protected override bool Accept(MineClaw.World.Entity e) => ((e.MetaInt(16) ?? 0) & 0x6) == 0;
    protected override int Attempts => 10;
    protected override bool Finished(MineClaw.World.Entity e) => ((e.MetaInt(16) ?? 0) & 0x4) != 0;
    public override SkillSpec Spec { get; } = new()
    {
        Name = "tame_wolf", Category = SkillCategory.Animals, Summary = "feed bones to a wild wolf until tamed", Args = "count?",
        Schema = SkillJson.Schema("count:int?"), Estimate = TimeSpan.FromSeconds(20),
        Pre = new() { Items = [[Items.Bone]], Needs = "bones", Dimension = Dim.Overworld, Primitives = Primitive.UseEntity },
        Keywords = "tame wolf dog bone pet",
    };
}

[Skill]
public sealed class DyeWoolSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "dye_wool", Category = SkillCategory.Animals, Summary = "craft coloured wool from wool + dye", Args = "color,count?",
        Schema = SkillJson.Schema("color:str,count:int?"), Estimate = TimeSpan.FromSeconds(10),
        Pre = new() { Items = [[Blocks.Wool], [Items.Dye]], Needs = "wool+dye" },
        Keywords = "dye wool colour color craft red blue green",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct) =>
        SkillResult.From(await ctx.Game.CraftAsync($"{args.Str("color") ?? "white"} wool", Clamp(args.Int("count"), 1, 1, 64), ct));
}

[Skill]
public sealed class PlaceBoatSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "place_boat", Category = SkillCategory.Vehicles, Summary = "put a boat on nearby water", Args = "pos?",
        Schema = SkillJson.Schema("pos:pos?"), Estimate = TimeSpan.FromSeconds(6),
        Pre = new() { Items = [[Items.Boat]], Needs = "boat" }, Keywords = "boat water place sail",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var water = args.Pos() ?? ctx.Find((id, _) => BlockShapes.IsWater(id), 4, 1).Select(f => (BlockPos?)f.Pos).FirstOrDefault();
        if (water is not { } w) return Fail("no water within reach");
        int boats = ctx.World.Entities.Nearby(ctx.Feet, 8, e => e.Kind == EntityKind.Object).Count;
        // ItemBoat raytraces from the player (like buckets): aim at the water surface, then use.
        await ctx.Game.LookAtAsync(new Vec3d(w.X + 0.5, w.Y + 0.9, w.Z + 0.5), ct);
        var r = await ctx.Game.UseItemAsync(Items.Boat, ct);
        if (!r.Ok) return SkillResult.From(r);
        return await ctx.Game.WaitUntilAsync(() => ctx.World.Entities.Nearby(ctx.Feet, 8, e => e.Kind == EntityKind.Object).Count > boats, TimeSpan.FromSeconds(2), ct)
            ? Ok($"boat placed at {P(w)}") : Fail("no boat appeared");
    }
}

[Skill]
public sealed class RideVehicleSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "ride_vehicle", Category = SkillCategory.Vehicles, Summary = "get in/out of the nearest boat or minecart", Args = "exit?",
        Schema = SkillJson.Schema("exit:bool?"), Estimate = TimeSpan.FromSeconds(6),
        Pre = new() { Primitives = Primitive.UseEntity }, Keywords = "ride boat minecart enter mount get in exit dismount vehicle cart",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        int riding = ctx.Game.RidingEntityId;
        if (args.Flag("exit") || riding >= 0)
        {
            if (!args.Flag("exit")) return Ok($"already riding the {ObjectTypes.Name(ctx.World.Entities.Get(riding)?.TypeId ?? 1)}");
            return SkillResult.From(await ctx.Game.DismountAsync(ct)); // sneak, else right-click the vehicle again (b1.8.1 toggles)
        }
        // Rideable: boat (1) and the plain minecart (10); storage (11) / powered (12) carts open or take fuel instead.
        var v = ctx.World.Entities.Nearest(ctx.Feet, 6, e => e.Kind == EntityKind.Object && e.TypeId is 1 or 10);
        if (v is null) return Fail("no boat/minecart within 6");
        var r = await ctx.Game.UseEntityAsync(v.Id, null, ct);
        if (!r.Ok) return SkillResult.From(r);
        return await ctx.Game.WaitUntilAsync(() => ctx.Game.RidingEntityId == v.Id, TimeSpan.FromSeconds(1), ct)
            ? Ok($"in the {ObjectTypes.Name(v.TypeId)}") : Fail($"the {ObjectTypes.Name(v.TypeId)} didn't take me ({r.Message})");
    }
}
