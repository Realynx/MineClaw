using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.World.Tests;

public class EntityEquipmentTests
{
    [Fact]
    public void SetEquipment_StoresArmorAndHeldItem_Immutably()
    {
        var t = new EntityTracker();
        t.SpawnPlayer(5, "kiwi", Vec3d.Zero, 0, 0, heldItem: 0);
        var before = t.Get(5)!;
        Assert.True(t.SetEquipment(5, 3, new ItemStack(307, 1, 12)));
        Assert.True(t.SetEquipment(5, 0, new ItemStack(276, 1, 0)));
        var e = t.Get(5)!;
        Assert.Equal(307, e.EquipmentAt(3).Id);
        Assert.Equal(12, e.EquipmentAt(3).Damage);
        Assert.Equal(276, e.HeldItem);
        Assert.True(e.EquipmentAt(1).IsEmpty);
        Assert.True(before.EquipmentAt(3).IsEmpty); // old snapshot untouched
        Assert.True(t.SetEquipment(5, 0, ItemStack.Empty));
        Assert.Equal(0, t.Get(5)!.HeldItem);
        Assert.False(t.SetEquipment(5, 7, new ItemStack(1, 1, 0)));
        Assert.False(t.SetEquipment(99, 1, new ItemStack(1, 1, 0)));
    }

    [Fact]
    public void MetaInt_WidensByteShortInt()
    {
        var e = new Entity(1, EntityKind.Mob, Vec3d.Zero, TypeId: 50,
            Metadata: new Dictionary<int, object> { [16] = (sbyte)-1, [17] = (short)300, [18] = 7, [19] = "owner" });
        Assert.Equal(-1, e.MetaInt(16));
        Assert.Equal(300, e.MetaInt(17));
        Assert.Equal(7, e.MetaInt(18));
        Assert.Null(e.MetaInt(19));
        Assert.Null(e.MetaInt(20));
    }
}
