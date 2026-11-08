using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    private static void ReceiveWorldSound(SoundEffectPacket effect, WorldState world)
    {
        // Block-break particles (2001) carry the old state. Their sound is client-side in both supported versions.
        if (effect.EffectId != 2001) return;
        int id = effect.Data & 4095;
        string material = id switch
        {
            5 or 17 or 25 or 47 or 53 or 54 or 58 or 64 or 68 or 85 or 96 or 107 or 125 or 126 or 134 or 135 or 136 or 146 or 162 or 163 or 164 => "wood",
            2 or 3 or 6 or 18 or 31 or 32 or 37 or 38 or 39 or 40 or 59 or 60 or 86 or 91 or 103 or 110 or 111 or 161 or 175 => "grass",
            12 or 88 => "sand", 13 => "gravel", 20 or 79 or 95 or 102 or 160 => "glass", 35 or 171 => "cloth", 78 or 80 => "snow",
            _ => "stone",
        };
        world.Entities.Events.Add(new EntityEvent(0, "sound", DateTime.UtcNow,
            new(effect.X + 0.5, effect.Y + 0.5, effect.Z + 0.5), $"block.{material}.break", 1, 0.8f));
    }
}
