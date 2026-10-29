using MineClaw.Data;

namespace MineClaw.Skills;

// Wave 2: documented contracts, discoverable through find_skill, honest "not implemented yet" at run time.
// CATALOG.md lists what each one needs.

[Skill]
public sealed class FishingSkill : StubSkill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "go_fishing", Category = SkillCategory.Animals, Summary = "cast rod, reel on bite (bobber dip), repeat", Args = "minutes?",
        Status = SkillStatus.Stub, Pre = new() { Items = [[Items.FishingRod]], Needs = "fishing rod", Dimension = Dim.Overworld },
        Keywords = "fish fishing rod catch raw fish food",
    };
}

[Skill]
public sealed class BuildCobbleGenSkill : StubSkill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "build_cobble_gen", Category = SkillCategory.Building, Summary = "build a lava+water cobblestone generator", Args = "pos?",
        Status = SkillStatus.Stub, Safety = SafetyClass.Destructive,
        Pre = new() { Items = [[Items.LavaBucket], [Items.WaterBucket]], Needs = "lava+water bucket", Dimension = Dim.Overworld, Requires = Requirement.GeneratorStone },
        Keywords = "cobblestone generator cobble gen build make infinite stone",
    };
}

[Skill]
public sealed class MobFarmPlatformSkill : StubSkill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "build_mob_farm", Category = SkillCategory.Building, Summary = "dark spawn platform + water channels + drop", Args = "pos?,size?",
        Status = SkillStatus.Stub, Safety = SafetyClass.Edge, Pre = new() { Needs = "blocks+water", Dimension = Dim.Overworld },
        Keywords = "mob farm grinder spawner platform xp drops bones string",
    };
}

[Skill]
public sealed class BlueprintSkill : StubSkill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "build_blueprint", Category = SkillCategory.Building, Summary = "build from a compact layered schematic", Args = "name|layers,pos",
        Status = SkillStatus.Stub, Safety = SafetyClass.Edge, Pre = new() { Needs = "blocks" },
        Keywords = "blueprint schematic build house structure plan copy",
    };
}

[Skill]
public sealed class RailsSkill : StubSkill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "ride_rails", Category = SkillCategory.Vehicles, Summary = "lay rails, place minecart, ride to the end", Args = "to",
        Status = SkillStatus.Stub, Pre = new() { Needs = "rails+minecart", Primitives = Primitive.UseOnBlock | Primitive.UseEntity },
        Keywords = "rail rails minecart track ride powered detector",
    };
}

[Skill]
public sealed class MakeMapSkill : StubSkill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "make_map", Category = SkillCategory.Misc, Summary = "craft a map (compass+paper) and fill it", Args = "",
        Status = SkillStatus.Stub, Pre = new() { Needs = "compass+8 paper" }, Keywords = "map paper compass explore chart",
    };
}

[Skill]
public sealed class PlaySongSkill : StubSkill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "play_song", Category = SkillCategory.Redstone, Summary = "tune a row of note blocks to a melody", Args = "notes",
        Status = SkillStatus.Stub, Keywords = "song music melody note blocks tune play",
    };
}

[Skill]
public sealed class PlacePaintingSkill : StubSkill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "place_painting", Category = SkillCategory.Misc, Summary = "hang a painting on a wall face", Args = "pos",
        Status = SkillStatus.Stub, Pre = new() { Items = [[Items.Painting]], Needs = "painting", Primitives = Primitive.UseOnBlock },
        Keywords = "painting art decorate wall picture",
    };
}

/// <summary>Never lights TNT on its own: in b1.8.1 even punching (digging) TNT primes it. Needs an explicit,
/// supervised design (confirm flag, no builds within 8, retreat path) before it is implemented.</summary>
[Skill]
public sealed class IgniteTntSkill : StubSkill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "ignite_tnt", Category = SkillCategory.Redstone, Summary = "light TNT only when explicitly asked, far from builds", Args = "pos,confirm",
        Status = SkillStatus.Stub, Safety = SafetyClass.Destructive, Keywords = "tnt explode blast dynamite ignite boom",
    };
}
