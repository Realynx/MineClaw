namespace MineClaw.Render;

/// <summary>
/// Vanilla b1.8 lighting maths, checked against mc-dev 218a2a71:
/// <list type="bullet">
/// <item><c>WorldProvider.c()</c>: brightness table <c>f[i] = (1 − f1)/(f1·3 + 1)·(1 − base) + base</c>, <c>f1 = 1 − i/15</c>; base 0
/// overworld, 0.1 in the Nether (<c>WorldProviderHell.c()</c>).</item>
/// <item><c>WorldProvider.a(time, partial)</c>: celestial angle; <c>World.a(float)</c> → <c>World.k</c> (skylightSubtracted)
/// = <c>(int)((1 − clamp(1 − (cos(angle·2π)·2 + 0.5)))·11)</c> with rain/thunder scaling the brightness first (<see cref="Weather"/>).</item>
/// <item>A face's light level is <c>max(blockLight, skyLight − k)</c> of the air cell in front of it.</item>
/// </list>
/// Client-only (not in mc-dev, from the b1.8 client's RenderBlocks): the per-face shade factors top 1.0, N/S 0.8, E/W 0.6,
/// bottom 0.5, and smooth lighting's per-vertex averaging (our version: the 4 cells around each corner, bilinear).
/// </summary>
public static class Lighting
{
    private static readonly float[] Overworld = Table(0f), Nether = Table(0.1f);

    private static float[] Table(float b)
    {
        var t = new float[16];
        for (int i = 0; i <= 15; i++)
        {
            float f1 = 1f - i / 15f;
            t[i] = (1f - f1) / (f1 * 3f + 1f) * (1f - b) + b;
        }
        return t;
    }

    /// <summary>Brightness (0..1) of a light level 0..15.</summary>
    public static float Brightness(int level, bool nether = false) => (nether ? Nether : Overworld)[Math.Clamp(level, 0, 15)];

    /// <summary>World.k: how much sky light is taken off at this time of day (0 at noon, 11 at midnight).</summary>
    public static int SkylightSubtracted(long time) => SkylightSubtracted(time, 0f, 0f);

    /// <summary>World.k under weather: the day brightness × (1 − rain·5/16) × (1 − thunder·rain·5/16) first (rain at noon
    /// takes 3 off the sky light, a thunderstorm 5, a clear noon 0).</summary>
    public static int SkylightSubtracted(long time, float rain, float thunder)
    {
        int j = (int)(((time % 24000) + 24000) % 24000);
        float f1 = j / 24000f - 0.25f;
        if (f1 < 0f) f1 += 1f;
        if (f1 > 1f) f1 -= 1f;
        float f2 = f1;
        f1 = 1f - (float)((Math.Cos(f1 * Math.PI) + 1.0) / 2.0);
        float angle = f2 + (f1 - f2) / 3f;
        float k = 1f - (MathF.Cos(angle * MathF.PI * 2f) * 2f + 0.5f);
        k = Math.Clamp(k, 0f, 1f);
        k = 1f - k;
        rain = Math.Clamp(rain, 0f, 1f);
        k *= 1f - rain * 5f / 16f;
        k *= 1f - Math.Clamp(thunder, 0f, 1f) * rain * 5f / 16f;
        k = 1f - k;
        return (int)(k * 11f);
    }

    /// <summary>Light level of a cell: max(block, sky − subtracted).</summary>
    public static int Level(int sky, int block, int subtracted) => Math.Max(block, sky - subtracted);

    /// <summary>Client RenderBlocks face shade: top 1.0, north/south 0.8, east/west 0.6, bottom 0.5.</summary>
    public static float FaceShade(Face f) => f switch
    {
        Face.Up => 1f,
        Face.Down => 0.5f,
        Face.North or Face.South => 0.8f,
        _ => 0.6f,
    };

    /// <summary>Face of a unit step (dx, dy, dz) pointing out of the block toward the viewer's side.</summary>
    public static Face FaceOf(int dx, int dy, int dz) => dy > 0 ? Face.Up : dy < 0 ? Face.Down : dz < 0 ? Face.North : dz > 0 ? Face.South : dx < 0 ? Face.West : Face.East;

    public static (int Dx, int Dy, int Dz) Normal(Face f) => f switch
    {
        Face.Up => (0, 1, 0), Face.Down => (0, -1, 0), Face.North => (0, 0, -1), Face.South => (0, 0, 1), Face.West => (-1, 0, 0), _ => (1, 0, 0),
    };

    public static uint Scale(uint argb, float k)
    {
        if (k >= 0.999f) return argb & 0xffffff;
        int r = (int)(((argb >> 16) & 0xff) * k), g = (int)(((argb >> 8) & 0xff) * k), b = (int)((argb & 0xff) * k);
        return (uint)(r << 16 | g << 8 | b);
    }
}
