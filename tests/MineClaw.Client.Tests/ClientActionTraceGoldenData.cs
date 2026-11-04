namespace MineClaw.Client.Tests;

/// <summary>The pinned action traces of <see cref="ClientActionTraceGoldenTests"/> (taken before the WP05 refactor).</summary>
internal static class ClientActionTraceGoldenData
{
    public const string Beta = """
        ok: dug cobblestone at 9,64,9 (15 ticks, stone pickaxe)
        ok: placed dirt at 9,64,9
        ok: placed furnace at 7,64,7 facing east
        fail: chest is full
        fail: no sand in the chest at 10,64,6: it is empty
        ok: crafted 4 wooden planks (have 4)
        ok: holding dirt
        ok: jumped 1x
        ok: waved (2 swings)
        ----
        HandshakePacket ConnectionHash=maid_chan Id=2 IsOfflineMode=False Text=maid_chan Username=maid_chan
        LoginRequestPacket Difficulty=0 Dimension=0 EntityId=17 Id=1 MapSeed=0 MaxPlayers=0 ProtocolVersion=17 ProtocolVersionOrEntityId=17 ServerMode=0 Username=maid_chan WorldHeight=0
        HoldingChangePacket Id=16 Slot=0
        AnimationPacket Animation=1 EntityId=42 Hand=0 Id=18
        PlayerDiggingPacket Face=2 Id=14 Status=0 X=9 Y=64 Z=9
        AnimationPacket Animation=1 EntityId=42 Hand=0 Id=18
        PlayerDiggingPacket Face=2 Id=14 Status=2 X=9 Y=64 Z=9
        HoldingChangePacket Id=16 Slot=1
        EntityActionPacket Action=1 EntityId=42 Id=19
        PlayerBlockPlacementPacket Cursor=null Direction=1 EffectiveCursor=(0.5, 1, 0.5) Hand=0 HeldItem=3:0 x8 Id=15 X=9 Y=63 Z=9
        AnimationPacket Animation=1 EntityId=42 Hand=0 Id=18
        HoldingChangePacket Id=16 Slot=2
        PlayerBlockPlacementPacket Cursor=null Direction=1 EffectiveCursor=(0.5, 1, 0.5) Hand=0 HeldItem=61:0 x2 Id=15 X=7 Y=63 Z=7
        AnimationPacket Animation=1 EntityId=42 Hand=0 Id=18
        EntityActionPacket Action=2 EntityId=42 Id=19
        PlayerBlockPlacementPacket Cursor=null Direction=4 EffectiveCursor=(0, 0.5, 0.5) Hand=0 HeldItem=61:0 x2 Id=15 X=10 Y=64 Z=6
        AnimationPacket Animation=1 EntityId=42 Hand=0 Id=18
        WindowClickPacket ActionNumber=1 Button=null ClickedItem=4:0 x7 EffectiveButton=0 EffectiveMode=1 Id=102 Mode=0 RightClick=False Shift=True Slot=30 WindowId=1
        TransactionPacket Accepted=True ActionNumber=1 Id=106 WindowId=1
        CloseWindowPacket Id=101 WindowId=1
        PlayerBlockPlacementPacket Cursor=null Direction=4 EffectiveCursor=(0, 0.5, 0.5) Hand=0 HeldItem=61:0 x2 Id=15 X=10 Y=64 Z=6
        AnimationPacket Animation=1 EntityId=42 Hand=0 Id=18
        WindowClickPacket ActionNumber=2 Button=null ClickedItem=12:0 x5 EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=3 WindowId=2
        TransactionPacket Accepted=True ActionNumber=2 Id=106 WindowId=2
        CloseWindowPacket Id=101 WindowId=2
        WindowClickPacket ActionNumber=3 Button=null ClickedItem=17:0 x3 EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=39 WindowId=0
        WindowClickPacket ActionNumber=4 Button=null ClickedItem=(empty) EffectiveButton=1 EffectiveMode=0 Id=102 Mode=0 RightClick=True Shift=False Slot=1 WindowId=0
        WindowClickPacket ActionNumber=5 Button=null ClickedItem=(empty) EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=39 WindowId=0
        WindowClickPacket ActionNumber=6 Button=null ClickedItem=5:0 x4 EffectiveButton=0 EffectiveMode=1 Id=102 Mode=0 RightClick=False Shift=True Slot=0 WindowId=0
        HoldingChangePacket Id=16 Slot=1
        AnimationPacket Animation=1 EntityId=42 Hand=0 Id=18
        """;

    public const string Modern = """
        ok: dug cobblestone at 9,64,9 (15 ticks, stone pickaxe)
        ok: placed dirt at 9,64,9
        ok: placed furnace at 7,64,7 facing east
        ok: deposited 7 cobblestone
        ok: took 2 sand (have 2)
        ok: crafted 4 oak wood planks (have 4)
        ok: holding dirt
        ok: jumped 1x
        ok: waved (2 swings)
        ----
        HandshakePacket ConnectionHash=maid_chan Id=2 IsOfflineMode=False Text=maid_chan Username=maid_chan
        LoginRequestPacket Difficulty=0 Dimension=0 EntityId=17 Id=1 MapSeed=0 MaxPlayers=0 ProtocolVersion=17 ProtocolVersionOrEntityId=17 ServerMode=0 Username=maid_chan WorldHeight=0
        HoldingChangePacket Id=16 Slot=0
        AnimationPacket Animation=1 EntityId=0 Hand=0 Id=18
        PlayerDiggingPacket Face=2 Id=14 Status=0 X=9 Y=64 Z=9
        AnimationPacket Animation=1 EntityId=0 Hand=0 Id=18
        PlayerDiggingPacket Face=2 Id=14 Status=2 X=9 Y=64 Z=9
        HoldingChangePacket Id=16 Slot=1
        EntityActionPacket Action=1 EntityId=42 Id=19
        PlayerBlockPlacementPacket Cursor=(0.5, 1, 0.5) Direction=1 EffectiveCursor=(0.5, 1, 0.5) Hand=0 HeldItem=3:0 x8 Id=15 X=9 Y=63 Z=9
        AnimationPacket Animation=1 EntityId=0 Hand=0 Id=18
        HoldingChangePacket Id=16 Slot=2
        PlayerBlockPlacementPacket Cursor=(0.5, 1, 0.5) Direction=1 EffectiveCursor=(0.5, 1, 0.5) Hand=0 HeldItem=61:0 x2 Id=15 X=7 Y=63 Z=7
        AnimationPacket Animation=1 EntityId=0 Hand=0 Id=18
        EntityActionPacket Action=2 EntityId=42 Id=19
        PlayerBlockPlacementPacket Cursor=(0, 0.5, 0.5) Direction=4 EffectiveCursor=(0, 0.5, 0.5) Hand=0 HeldItem=61:0 x2 Id=15 X=10 Y=64 Z=6
        AnimationPacket Animation=1 EntityId=0 Hand=0 Id=18
        WindowClickPacket ActionNumber=1 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=1 Id=102 Mode=1 RightClick=False Shift=True Slot=30 WindowId=1
        CloseWindowPacket Id=101 WindowId=1
        PlayerBlockPlacementPacket Cursor=(0, 0.5, 0.5) Direction=4 EffectiveCursor=(0, 0.5, 0.5) Hand=0 HeldItem=61:0 x2 Id=15 X=10 Y=64 Z=6
        AnimationPacket Animation=1 EntityId=0 Hand=0 Id=18
        WindowClickPacket ActionNumber=2 Button=0 ClickedItem=12:0 x5 EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=3 WindowId=2
        WindowClickPacket ActionNumber=3 Button=1 ClickedItem=(empty) EffectiveButton=1 EffectiveMode=0 Id=102 Mode=0 RightClick=True Shift=False Slot=58 WindowId=2
        WindowClickPacket ActionNumber=4 Button=1 ClickedItem=12:0 x1 EffectiveButton=1 EffectiveMode=0 Id=102 Mode=0 RightClick=True Shift=False Slot=58 WindowId=2
        WindowClickPacket ActionNumber=5 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=3 WindowId=2
        CloseWindowPacket Id=101 WindowId=2
        WindowClickPacket ActionNumber=6 Button=0 ClickedItem=17:0 x3 EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=39 WindowId=0
        WindowClickPacket ActionNumber=7 Button=1 ClickedItem=(empty) EffectiveButton=1 EffectiveMode=0 Id=102 Mode=0 RightClick=True Shift=False Slot=1 WindowId=0
        WindowClickPacket ActionNumber=8 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=0 Id=102 Mode=0 RightClick=False Shift=False Slot=39 WindowId=0
        WindowClickPacket ActionNumber=9 Button=0 ClickedItem=(empty) EffectiveButton=0 EffectiveMode=1 Id=102 Mode=1 RightClick=False Shift=True Slot=0 WindowId=0
        HoldingChangePacket Id=16 Slot=1
        AnimationPacket Animation=1 EntityId=0 Hand=0 Id=18
        """;
}
