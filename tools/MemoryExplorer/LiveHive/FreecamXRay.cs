using MineClaw.Render;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>
/// The free camera's x-ray, only when the user turns it on (the freecam stays exact otherwise; live 10-29, Fox: "when the
/// bot's in the tunnel… you just see blocks in your face"): the same camera cone as the AI's orbit/iso panels
/// (<see cref="ViewCut"/>), toward the bodies in front of the camera, nearest first (at most <see cref="MaxSubjects"/>, within
/// <see cref="Range"/> blocks). The CPU freecam renders it with <see cref="EyeScene.XRay"/>; the GPU one discards the same cone
/// per fragment (world geometry only: the bodies themselves, drops and overlay boxes always draw).
/// </summary>
public static class FreecamXRay
{
    public const int MaxSubjects = 2;
    public const double Range = 48;

    /// <summary>"auto" / "on" / "off": what the Explorer's x-ray box offers. Auto = the AI's rule for the server pictures,
    /// exact geometry in the freecam.</summary>
    public static readonly IReadOnlyList<string> Modes = ["auto", "on", "off"];

    /// <summary>Feet positions of the bodies the cut keeps in sight: same dimension, in front of the camera, nearest first.</summary>
    public static List<(double X, double Y, double Z)> Subjects(CamPose pose, IEnumerable<BotDto> bots)
    {
        var cam = new ViewProjector(pose, 16, 16);
        return bots.Where(b => b.Dim == pose.Dim)
            .Select(b => (b, d: Math.Sqrt((b.X - pose.X) * (b.X - pose.X) + (b.Y + 0.9 - pose.Y) * (b.Y + 0.9 - pose.Y) + (b.Z - pose.Z) * (b.Z - pose.Z))))
            .Where(t => t.d is > 1.5 and <= Range && cam.Forward(t.b.X, t.b.Y + 0.9, t.b.Z) > 1)
            .OrderBy(t => t.d).Take(MaxSubjects).Select(t => (t.b.X, t.b.Y, t.b.Z)).ToList();
    }

    /// <summary>The cut for the CPU freecam, or null when no body is in front of the camera.</summary>
    public static ViewCut? Cut(CamPose pose, IEnumerable<BotDto> bots)
    {
        var s = Subjects(pose, bots);
        if (s.Count == 0) return null;
        int floor = (int)Math.Floor(s.Min(p => p.Y) + 0.01);
        return new ViewCut(pose.X, pose.Y, pose.Z, s.Select(p => (p.X, p.Y + 0.9, p.Z)), PerspectivePanel.XRayRadius, floor);
    }
}
