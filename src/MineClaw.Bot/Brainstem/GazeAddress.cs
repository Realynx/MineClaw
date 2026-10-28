using MineClaw.World;

namespace MineClaw.Bot.Brainstem;

/// <summary>
/// Addressee by body language (zero tokens): whom a speaker is looking at (their look ray within <c>maxDegrees</c> of someone's
/// body, within <c>maxBlocks</c>; nearer targets get a wider cone) or, failing that, the one player/bot standing right next to
/// them. Feeds <see cref="Agent.Chat.ChatRouter.LookingAt"/>: Robin looking at maid_chan saying "follow me" is for us.
/// </summary>
public static class GazeAddress
{
    public static string? Target(HandoverWatch.PlayerView speaker, IEnumerable<(string Name, Vec3d Feet)> others,
        double maxDegrees = 10, double maxBlocks = 8, double nextTo = 2.0)
    {
        double yaw = speaker.Yaw * Math.PI / 180, pitch = speaker.Pitch * Math.PI / 180;
        // Minecraft: yaw 0 = +Z (south), 90 = -X (west); pitch +90 = straight down.
        double lx = -Math.Sin(yaw) * Math.Cos(pitch), ly = -Math.Sin(pitch), lz = Math.Cos(yaw) * Math.Cos(pitch);
        double ex = speaker.Feet.X, ey = speaker.Feet.Y + 1.62, ez = speaker.Feet.Z;
        string? best = null;
        double bestAngle = double.MaxValue;
        var list = others.Where(o => !o.Name.Equals(speaker.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var (name, feet) in list)
        {
            double dx = feet.X - ex, dy = feet.Y + 1.1 - ey, dz = feet.Z - ez;
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist < 0.05 || dist > maxBlocks) continue;
            double cos = Math.Clamp((dx * lx + dy * ly + dz * lz) / dist, -1, 1);
            double angle = Math.Acos(cos) * 180 / Math.PI;
            double tol = Math.Max(maxDegrees, Math.Atan(0.6 / dist) * 180 / Math.PI); // a body is ~0.6 wide, ~1.8 tall
            if (angle <= tol && angle < bestAngle) { bestAngle = angle; best = name; }
        }
        if (best != null) return best;
        var near = list.Where(o =>
        {
            double dx = o.Feet.X - speaker.Feet.X, dz = o.Feet.Z - speaker.Feet.Z;
            return Math.Sqrt(dx * dx + dz * dz) <= nextTo && Math.Abs(o.Feet.Y - speaker.Feet.Y) <= 1.5;
        }).ToList();
        return near.Count == 1 ? near[0].Name : null;
    }
}
