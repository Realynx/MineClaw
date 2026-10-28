using MineClaw.Agent.Rag.Beliefs;
using MineClaw.Bot.Game;
using MineClaw.World;

namespace MineClaw.Bot.Beliefs;

/// <summary>Read-only world view for the belief verifier, straight off the client's world copy (no network, no LLM).</summary>
public sealed class GameBeliefWorld(IGameClient client) : IBeliefWorld
{
    public int? BlockAt(int x, int y, int z)
    {
        if (y < 0 || y >= client.World.WorldHeight || !client.World.IsLoaded(x, z)) return null;
        return client.World.GetBlock(x, y, z);
    }

    public ClaimPos? Self
    {
        get
        {
            if (!client.IsPlaying) return null;
            var p = client.World.Player.Position.ToBlockPos();
            return new ClaimPos(p.X, p.Y, p.Z);
        }
    }

    public ClaimPos? FindNearest(IReadOnlyCollection<int> ids, int radius)
    {
        if (Self is not { } me || ids.Count == 0) return null;
        radius = Math.Clamp(radius, 1, 8);
        ClaimPos? best = null;
        double bestD = double.MaxValue;
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int x = me.X + dx, y = me.Y + dy, z = me.Z + dz;
                    if (BlockAt(x, y, z) is not { } b || !ids.Contains(b)) continue;
                    double d = dx * dx + dy * dy + dz * dz;
                    if (d < bestD) { bestD = d; best = new ClaimPos(x, y, z); }
                }
        return best;
    }
}
