namespace MineClaw.Render;

/// <summary>What falls from the sky in one column.</summary>
public enum Precip : byte { None, Rain, Snow }

/// <summary>How a view shows precipitation: streaks (perspective cameras), a tint (iso / the flat maps), nothing.</summary>
public enum PrecipStyle : byte { Auto, Streaks, Tint, None }

/// <summary>
/// The weather a picture is rendered under (live 10-29, Fox: "I also didn't see the weather on the viewer": the client tracked
/// rain — the "overworld 16:20 day rain" line — but no renderer knew). Strengths are 0..1: b1.8.1's 0x46 reasons 1/2 switch rain
/// on/off (thunder only shows as lightning bolts), 1.12.2's Change Game State fades both with reasons 7 (rain) and 8 (thunder).
/// Ported in our own words from the 1.12.2 client (World.getSkyColorBody, getCloudColour, EntityRenderer.updateFogColor and
/// renderRainSnow, World.calculateSkylightSubtracted, Biome.getTemperature):
/// <list type="bullet">
/// <item>the sky blends toward its own grey × 0.6 by rain × 0.75, then toward grey × 0.2 by thunder × 0.75 (a storm is dark);</item>
/// <item>fog: red and green × (1 − rain/2), blue × (1 − rain × 0.4); thunder × (1 − thunder/2) on all three;</item>
/// <item>sky light: the day brightness × (1 − rain × 5/16) × (1 − thunder × 5/16) before World.k (<see cref="Lighting.SkylightSubtracted(long, float, float)"/>);</item>
/// <item>thunder only counts while it rains (World.getThunderStrength multiplies by the rain strength);</item>
/// <item>snow instead of rain where the biome's temperature is below 0.15 (cold biomes; above y 64 it drops 0.05 per 30
/// blocks), none in deserts, savannas, mesas, the Nether and the End; unknown biomes (b1.8.1, remembered columns) rain.</item>
/// </list>
/// </summary>
public readonly record struct Weather(float Rain, float Thunder)
{
    public static readonly Weather Clear = default;

    /// <summary>Rain strength 0..1.</summary>
    public float R => Math.Clamp(Rain, 0f, 1f);
    /// <summary>Effective thunder 0..1 (× rain, as vanilla).</summary>
    public float T => Math.Clamp(Thunder, 0f, 1f) * R;
    public bool Wet => R > 0.01f;
    public bool Storm => T > 0.2f;

    /// <summary>The sky colour under this weather (getSkyColorBody's desaturation).</summary>
    public uint Sky(uint c) => Grey(c, R * 0.75f, 0.6f, T * 0.75f, 0.2f);

    /// <summary>The fog / horizon colour under this weather (updateFogColor).</summary>
    public uint Fog(uint c)
    {
        if (!Wet) return c;
        float r = R, t = T;
        float kr = (1f - r * 0.5f) * (1f - t * 0.5f), kb = (1f - r * 0.4f) * (1f - t * 0.5f);
        return (uint)((int)(((c >> 16) & 0xff) * kr) << 16 | (int)(((c >> 8) & 0xff) * kr) << 8 | (int)((c & 0xff) * kb));
    }

    private static uint Grey(uint c, float a, float ka, float b, float kb)
    {
        if (a <= 0 && b <= 0) return c;
        float r = (c >> 16) & 0xff, g = (c >> 8) & 0xff, bl = c & 0xff;
        void Mix(float k, float amount)
        {
            if (amount <= 0) return;
            float grey = (r * 0.3f + g * 0.59f + bl * 0.11f) * k;
            r = r * (1 - amount) + grey * amount; g = g * (1 - amount) + grey * amount; bl = bl * (1 - amount) + grey * amount;
        }
        Mix(ka, a);
        Mix(kb, b);
        return (uint)Math.Clamp((int)r, 0, 255) << 16 | (uint)Math.Clamp((int)g, 0, 255) << 8 | (uint)Math.Clamp((int)bl, 0, 255);
    }

    /// <summary>1.12.2 biome ids that never rain (deserts, savannas, mesas, hell, the End, the void).</summary>
    public static bool Dry(int biome) => DryBiomes.Contains(biome);

    private static readonly HashSet<int> DryBiomes = [2, 17, 130, 35, 36, 163, 164, 37, 38, 39, 165, 166, 167, 8, 9, 127];

    /// <summary>A 1.12.2 biome's base temperature for the rain/snow choice (only the cold ones matter; −1 unknown = mild).</summary>
    public static float Temperature(int biome) => Temperatures.TryGetValue(biome, out var t) ? t : 0.8f;

    private static readonly Dictionary<int, float> Temperatures = new()
    {
        [10] = 0f, [11] = 0f, [12] = 0f, [13] = 0f, [140] = 0f,
        [26] = 0.05f,
        [30] = -0.5f, [31] = -0.5f, [158] = -0.5f,
        [3] = 0.2f, [20] = 0.2f, [25] = 0.2f, [34] = 0.2f, [131] = 0.2f, [162] = 0.2f,
        [5] = 0.25f, [19] = 0.25f, [133] = 0.25f, [160] = 0.25f, [161] = 0.25f,
    };

    /// <summary>What falls in a column of <paramref name="biome"/> at height <paramref name="y"/> (no wind, no noise).</summary>
    public static Precip At(int biome, double y)
    {
        if (biome >= 0 && Dry(biome)) return Precip.None;
        float t = Temperature(biome);
        if (y > 64) t -= (float)((y - 64) * 0.05 / 30);
        return t < 0.15f ? Precip.Snow : Precip.Rain;
    }

    /// <summary>"rain" / "snow" / "thunderstorm" / "light rain" for a caption (null = clear or nothing falls there).</summary>
    public string? Text(Precip p = Precip.Rain)
    {
        if (!Wet || p == Precip.None) return null;
        string what = p == Precip.Snow ? "snow" : "rain";
        if (Storm) return p == Precip.Snow ? "snowstorm" : "thunderstorm";
        return R < 0.5f ? "light " + what : what;
    }
}
