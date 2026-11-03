using MineClaw.App.Supervisor;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.App.Tests;

/// <summary>The compact <c>look</c> string and the number <c>variant</c> per kind, from canonical metadata (b1.8.1 16/17, 1.12.2 32+/50+).</summary>
public class EntityLooksTests
{
    private static Entity Mob(int type, params (int Index, object Value)[] meta) =>
        new(1, EntityKind.Mob, default, TypeId: type, Metadata: meta.ToDictionary(m => m.Index, m => m.Value))
        { TypeName = MineClaw.World.Versioning.Release1122EntityRules.Known(type)?.Name };

    private static Entity Obj(int type, int data = 0, params (int Index, object Value)[] meta) =>
        new(2, EntityKind.Object, default, TypeId: type, Metadata: meta.ToDictionary(m => m.Index, m => m.Value))
        { TypeName = new MineClaw.World.Versioning.Release1122EntityRules().ObjectName(type), Data = data };

    [Fact]
    public void Looks_per_kind()
    {
        Assert.Equal("tame,sitting,collar=blue", EntityLooks.Look(Mob(MobTypes.Wolf, (EntityMeta.Mob16, (sbyte)5), (EntityMeta.CollarColor, 11))));
        Assert.Equal("angry", EntityLooks.Look(Mob(MobTypes.Wolf, (EntityMeta.Mob16, (sbyte)2))));
        Assert.Equal("powered,fusing", EntityLooks.Look(Mob(MobTypes.Creeper, (EntityMeta.Mob16, 1), (EntityMeta.Mob17, (sbyte)1))));
        Assert.Equal("size=4", EntityLooks.Look(Mob(MobTypes.Slime, (EntityMeta.Mob16, 4))));
        Assert.Equal("profession=librarian,converting", EntityLooks.Look(Mob(MobTypes.ZombieVillager, (EntityMeta.Profession, 1), (EntityMeta.Converting, (sbyte)1))));
        Assert.Equal("brown,chest,carpet=red", EntityLooks.Look(Mob(MobTypes.Llama, (EntityMeta.LlamaVariant, 2), (EntityMeta.HasChest, (sbyte)1), (EntityMeta.CarpetColor, 14))));
        Assert.Equal("killer", EntityLooks.Look(Mob(MobTypes.Rabbit, (EntityMeta.RabbitType, 99))));
        Assert.Equal("siamese,tame", EntityLooks.Look(Mob(MobTypes.Ocelot, (EntityMeta.OcelotType, 3), (EntityMeta.Mob16, (sbyte)4))));
        Assert.Equal("cyan", EntityLooks.Look(Mob(MobTypes.Parrot, (EntityMeta.ParrotVariant, 3))));
        Assert.Null(EntityLooks.Look(Mob(MobTypes.Cow)));
        Assert.Equal(3, EntityLooks.Variant(Mob(MobTypes.Ocelot, (EntityMeta.OcelotType, 3))));
        Assert.Equal(2, EntityLooks.Variant(Mob(MobTypes.Llama, (EntityMeta.LlamaVariant, 2))));

        Assert.Equal("birch", EntityLooks.Look(Obj(1, 0, (EntityMeta.BoatType, 2))));
        Assert.Equal(2, EntityLooks.Variant(Obj(1, 0, (EntityMeta.BoatType, 2))));
        Assert.Equal("hopper", EntityLooks.Look(Obj(10, 5)));
        Assert.Equal(new WireItem(Blocks.Sand, 1), EntityLooks.FallingBlock(Obj(70, Blocks.Sand | 1 << 12)));
        Assert.Equal("east", EntityLooks.Facing(Obj(71, 3)));
        Assert.Equal("ender_crystal", EntityLooks.ObjectKind(Obj(51)));
        Assert.Equal("firework", EntityLooks.ObjectKind(Obj(76)));
        Assert.Equal("object", EntityLooks.ObjectKind(Obj(65))); // an ender pearl: drawn by its name
    }
}
