namespace MineClaw.Client.Tests;

/// <summary>The pinned station traces of <see cref="ClientStationGoldenTests"/> (taken before the WP05 refactor reshaped them).</summary>
internal static class ClientStationGoldenData
{
    public static readonly Dictionary<string, string> Traces = new()
    {
        ["anvil-bad-mode"] = """
            fail: anvil mode is repair|combine|book
            ----

            """,
        ["anvil-combine-one"] = """
            fail: needs two diamond pickaxes to combine
            ----

            """,
        ["anvil-combine-opens"] = """
            fail: anvil at 9,64,8 didn't open (blocked above, or protected?)
            ----
            PlayerBlockPlacementPacket Cursor=(0, 0.5, 0.5) Direction=4 EffectiveCursor=(0, 0.5, 0.5) Hand=0 HeldItem=278:900 x1 Id=15 X=9 Y=64 Z=8
            AnimationPacket Animation=1 EntityId=0 Hand=0 Id=18
            """,
        ["anvil-no-book"] = """
            fail: no enchanted book that fits a diamond pickaxe
            ----

            """,
        ["anvil-no-diamond"] = """
            fail: no diamond to repair the diamond pickaxe with
            ----

            """,
        ["anvil-no-item"] = """
            fail: no diamond pickaxe
            ----

            """,
        ["anvil-no-material-kind"] = """
            fail: nothing repairs a stick at an anvil
            ----

            """,
        ["anvil-none"] = """
            fail: no anvil at 9,64,8
            ----

            """,
        ["anvil-repair-no-xp"] = """
            fail: needs 3 XP levels (has 0)
            ----

            """,
        ["anvil-repair-opens"] = """
            fail: anvil at 9,64,8 didn't open (blocked above, or protected?)
            ----
            PlayerBlockPlacementPacket Cursor=(0, 0.5, 0.5) Direction=4 EffectiveCursor=(0, 0.5, 0.5) Hand=0 HeldItem=278:900 x1 Id=15 X=9 Y=64 Z=8
            AnimationPacket Animation=1 EntityId=0 Hand=0 Id=18
            """,
        ["anvil-undamaged"] = """
            fail: the diamond pickaxe isn't damaged
            ----

            """,
        ["bottles-cauldron"] = """
            fail: the bottle didn't fill at 9,64,8 (can't see the water from here?)
            ----
            HoldingChangePacket Id=16 Slot=0
            PlayerBlockPlacementPacket Cursor=(0.5, 1, 0.5) Direction=1 EffectiveCursor=(0.5, 1, 0.5) Hand=0 HeldItem=374:0 x2 Id=15 X=9 Y=64 Z=8
            AnimationPacket Animation=1 EntityId=0 Hand=0 Id=18
            """,
        ["bottles-dry"] = """
            fail: no water at 9,64,8 (air: a water source block or a filled cauldron)
            ----

            """,
        ["bottles-none"] = """
            fail: no glass bottles
            ----

            """,
        ["brew-no-base"] = """
            fail: no water bottle to brew from
            ----

            """,
        ["brew-no-mix"] = """
            fail: the stand won't brew sugar into water bottle
            ----
            PlayerBlockPlacementPacket Cursor=(0.5, 1, 0.5) Direction=1 EffectiveCursor=(0.5, 1, 0.5) Hand=0 HeldItem=373:0 x1 +nbt Id=15 X=9 Y=64 Z=8
            AnimationPacket Animation=1 EntityId=0 Hand=0 Id=18
            WindowClickPacket ActionNumber=1 Button=0 ClickedItem=377:0 x1 EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=34 WindowId=1
            WindowClickPacket ActionNumber=2 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=4 WindowId=1
            WindowClickPacket ActionNumber=3 Button=0 ClickedItem=373:0 x1 +nbt EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=32 WindowId=1
            WindowClickPacket ActionNumber=4 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=0 WindowId=1
            WindowClickPacket ActionNumber=5 Button=0 ClickedItem=353:0 x1 EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=33 WindowId=1
            WindowClickPacket ActionNumber=6 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=3 WindowId=1
            WindowClickPacket ActionNumber=7 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=1 Id=102 Mode=1 RightClick=False Shift=True Slot=0 WindowId=1
            WindowClickPacket ActionNumber=8 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=1 Id=102 Mode=1 RightClick=False Shift=True Slot=3 WindowId=1
            CloseWindowPacket Id=101 WindowId=1
            """,
        ["brew-swiftness"] = """
            ok: brewed 3 Potion of Swiftness
            ----
            PlayerBlockPlacementPacket Cursor=(0.5, 1, 0.5) Direction=1 EffectiveCursor=(0.5, 1, 0.5) Hand=0 HeldItem=373:0 x1 +nbt Id=15 X=9 Y=64 Z=8
            AnimationPacket Animation=1 EntityId=0 Hand=0 Id=18
            WindowClickPacket ActionNumber=1 Button=0 ClickedItem=377:0 x1 EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=37 WindowId=1
            WindowClickPacket ActionNumber=2 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=4 WindowId=1
            WindowClickPacket ActionNumber=3 Button=0 ClickedItem=373:0 x1 +nbt EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=32 WindowId=1
            WindowClickPacket ActionNumber=4 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=0 WindowId=1
            WindowClickPacket ActionNumber=5 Button=0 ClickedItem=373:0 x1 +nbt EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=33 WindowId=1
            WindowClickPacket ActionNumber=6 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=1 WindowId=1
            WindowClickPacket ActionNumber=7 Button=0 ClickedItem=373:0 x1 +nbt EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=34 WindowId=1
            WindowClickPacket ActionNumber=8 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=2 WindowId=1
            WindowClickPacket ActionNumber=9 Button=0 ClickedItem=372:0 x4 EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=35 WindowId=1
            WindowClickPacket ActionNumber=10 Button=1 ClickedItem=(empty) EffectiveButton=1 EffectiveMode=0 Id=102 Mode=0 RightClick=True Shift=False Slot=3 WindowId=1
            WindowClickPacket ActionNumber=11 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=35 WindowId=1
            WindowClickPacket ActionNumber=12 Button=0 ClickedItem=353:0 x2 EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=36 WindowId=1
            WindowClickPacket ActionNumber=13 Button=1 ClickedItem=(empty) EffectiveButton=1 EffectiveMode=0 Id=102 Mode=0 RightClick=True Shift=False Slot=3 WindowId=1
            WindowClickPacket ActionNumber=14 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=36 WindowId=1
            WindowClickPacket ActionNumber=15 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=1 Id=102 Mode=1 RightClick=False Shift=True Slot=0 WindowId=1
            WindowClickPacket ActionNumber=16 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=1 Id=102 Mode=1 RightClick=False Shift=True Slot=1 WindowId=1
            WindowClickPacket ActionNumber=17 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=1 Id=102 Mode=1 RightClick=False Shift=True Slot=2 WindowId=1
            CloseWindowPacket Id=101 WindowId=1
            """,
        ["enchant-fortune"] = """
            ok: enchanted diamond pickaxe: Fortune II (offer #3, level 18)
            ----
            PlayerBlockPlacementPacket Cursor=(0, 0.5, 0.5) Direction=4 EffectiveCursor=(0, 0.5, 0.5) Hand=0 HeldItem=278:0 x1 Id=15 X=9 Y=64 Z=8
            AnimationPacket Animation=1 EntityId=0 Hand=0 Id=18
            WindowClickPacket ActionNumber=1 Button=0 ClickedItem=278:0 x1 EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=29 WindowId=1
            WindowClickPacket ActionNumber=2 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=0 WindowId=1
            WindowClickPacket ActionNumber=3 Button=0 ClickedItem=351:4 x5 EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=30 WindowId=1
            WindowClickPacket ActionNumber=4 Button=1 ClickedItem=(empty) EffectiveButton=1 EffectiveMode=0 Id=102 Mode=0 RightClick=True Shift=False Slot=1 WindowId=1
            WindowClickPacket ActionNumber=5 Button=1 ClickedItem=351:4 x1 EffectiveButton=1 EffectiveMode=0 Id=102 Mode=0 RightClick=True Shift=False Slot=1 WindowId=1
            WindowClickPacket ActionNumber=6 Button=1 ClickedItem=351:4 x2 EffectiveButton=1 EffectiveMode=0 Id=102 Mode=0 RightClick=True Shift=False Slot=1 WindowId=1
            WindowClickPacket ActionNumber=7 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=30 WindowId=1
            EnchantItemPacket Enchantment=2 Id=108 WindowId=1
            WindowClickPacket ActionNumber=8 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=1 Id=102 Mode=1 RightClick=False Shift=True Slot=0 WindowId=1
            CloseWindowPacket Id=101 WindowId=1
            """,
        ["enchant-no-xp"] = """
            fail: needs 1 XP levels (has 0)
            ----

            """,
        ["enchant-silk-only"] = """
            fail: no offer shows Silk Touch (#1: level 3 (Unbreaking I?), #2: level 9 (Efficiency II?), #3: level 18 (Fortune II?))
            ----
            PlayerBlockPlacementPacket Cursor=(0, 0.5, 0.5) Direction=4 EffectiveCursor=(0, 0.5, 0.5) Hand=0 HeldItem=278:0 x1 Id=15 X=9 Y=64 Z=8
            AnimationPacket Animation=1 EntityId=0 Hand=0 Id=18
            WindowClickPacket ActionNumber=1 Button=0 ClickedItem=278:0 x1 EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=29 WindowId=1
            WindowClickPacket ActionNumber=2 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=0 WindowId=1
            WindowClickPacket ActionNumber=3 Button=0 ClickedItem=351:4 x5 EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=30 WindowId=1
            WindowClickPacket ActionNumber=4 Button=1 ClickedItem=(empty) EffectiveButton=1 EffectiveMode=0 Id=102 Mode=0 RightClick=True Shift=False Slot=1 WindowId=1
            WindowClickPacket ActionNumber=5 Button=1 ClickedItem=351:4 x1 EffectiveButton=1 EffectiveMode=0 Id=102 Mode=0 RightClick=True Shift=False Slot=1 WindowId=1
            WindowClickPacket ActionNumber=6 Button=1 ClickedItem=351:4 x2 EffectiveButton=1 EffectiveMode=0 Id=102 Mode=0 RightClick=True Shift=False Slot=1 WindowId=1
            WindowClickPacket ActionNumber=7 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=30 WindowId=1
            WindowClickPacket ActionNumber=8 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=1 Id=102 Mode=1 RightClick=False Shift=True Slot=0 WindowId=1
            WindowClickPacket ActionNumber=9 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=1 Id=102 Mode=1 RightClick=False Shift=True Slot=1 WindowId=1
            CloseWindowPacket Id=101 WindowId=1
            """,
    };
}
