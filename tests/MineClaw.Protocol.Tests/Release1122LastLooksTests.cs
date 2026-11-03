using MineClaw.Data;
using MineClaw.Protocol.Release1122;

namespace MineClaw.Protocol.Tests;

/// <summary>
/// The 1.12.2 facts behind the viewer's last looks, on the wire both ways: an enderman's carried block (optional block state,
/// serializer 12), a player's shoulder parrots (NBT, serializer 13), the glint facts of an item's NBT, and a sign's styled
/// JSON lines as legacy § text.
/// </summary>
public class Release1122LastLooksTests
{
    private static List<Meta1122> RoundTrip(List<Meta1122> entries)
    {
        var bytes = new WireWriter().WriteMetadata(entries).ToArray();
        int pos = 0;
        var back = EntityMeta1122.Read(bytes, ref pos);
        Assert.Equal(bytes.Length, pos);
        return back;
    }

    [Fact]
    public void An_endermans_carried_block_is_an_optional_block_state()
    {
        var md = new EntityMetadata([MetadataEntry.FromInt(EntityMeta.CarriedBlock, 12 | 1 << 12)]);
        var wire = EntityMeta1122.FromCanonical(EntityWireKind.Mob, 58, md);
        Assert.Contains(wire, e => e.Index == 12 && e.Serializer == 12 && Equals(e.Value, 12 | 1 << 12));
        int flags = 0, hands = 0;
        var back = EntityMeta1122.ToCanonical(EntityWireKind.Mob, 58, RoundTrip(wire), ref flags, ref hands);
        Assert.Equal(12 | 1 << 12, back.Get(EntityMeta.CarriedBlock)?.AsInt());
        // Another mob's index 12 is not a carried block (a zombie's is the baby flag).
        var zombie = EntityMeta1122.ToCanonical(EntityWireKind.Mob, 54, [new Meta1122(12, 6, true)], ref flags, ref hands);
        Assert.Null(zombie.Get(EntityMeta.CarriedBlock));
    }

    [Fact]
    public void Shoulder_parrots_are_nbt_compounds()
    {
        var md = new EntityMetadata([MetadataEntry.FromInt(EntityMeta.ShoulderLeft, 3), MetadataEntry.FromInt(EntityMeta.ShoulderRight, -1)]);
        var wire = EntityMeta1122.FromCanonical(EntityWireKind.Player, 0, md);
        Assert.Contains(wire, e => e.Index == 15 && e.Serializer == 13 && e.Value is NbtCompound c && c.GetString("id") == "minecraft:parrot" && c.GetInt("Variant") == 3);
        Assert.Contains(wire, e => e.Index == 16 && e.Serializer == 13 && e.Value is NbtCompound { Count: 0 });
        int flags = 0, hands = 0;
        var back = EntityMeta1122.ToCanonical(EntityWireKind.Player, 0, RoundTrip(wire), ref flags, ref hands);
        Assert.Equal(3, back.Get(EntityMeta.ShoulderLeft)?.AsInt());
        Assert.Equal(-1, back.Get(EntityMeta.ShoulderRight)?.AsInt());
        // Not a parrot (a future shoulder entity): no parrot.
        Assert.Equal(-1, EntityMeta1122.ShoulderParrot(new NbtCompound { ["id"] = "minecraft:chicken" }));
    }

    [Fact]
    public void Item_nbt_says_when_it_glints()
    {
        Assert.True(Codec1122.TagOf(new NbtCompound { ["ench"] = new NbtList(NbtType.Compound, []) }).Enchanted); // even an empty list (isItemEnchanted)
        Assert.False(Codec1122.TagOf(new NbtCompound { ["display"] = new NbtCompound { ["Name"] = "Bob" } }).Enchanted);
        Assert.True(Codec1122.TagOf(new NbtCompound { ["Potion"] = "minecraft:strong_healing" }).PotionEffects);
        Assert.False(Codec1122.TagOf(new NbtCompound { ["Potion"] = "minecraft:awkward" }).PotionEffects);
        Assert.True(Codec1122.TagOf(new NbtCompound
        {
            ["Potion"] = "minecraft:water",
            ["CustomPotionEffects"] = new NbtList(NbtType.Compound, [new NbtCompound { ["Id"] = (sbyte)1 }]),
        }).PotionEffects);
    }

    [Fact]
    public void Sign_json_styles_become_legacy_codes_and_plain_signs_none()
    {
        var styled = Codec1122.SignTag(1, 2, 3, ["", "", "", ""],
            ["""{"text":"Fox","color":"red","extra":[{"text":"!","bold":true}]}""", """{"text":"plain"}""", """{"text":"§kx"}""", "\"\""]);
        Assert.Equal(["§cFox§r§c§l!§r", "plain", "§kx", ""], Codec1122.SignLegacy(styled));
        Assert.Equal(["Fox!", "plain", "x", ""], Codec1122.SignLines(styled));
        Assert.Null(Codec1122.SignLegacy(Codec1122.SignTag(1, 2, 3, ["a", "b", "c", "d"])));
        Assert.Null(Codec1122.SignLegacy(new NbtCompound { ["id"] = "minecraft:bed", ["Text1"] = "x" }));
    }
}
