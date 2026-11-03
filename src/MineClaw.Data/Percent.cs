using System.Globalization;

namespace MineClaw.Data;

/// <summary>
/// Whole-number percentages that read the same on every host: "91%". The "P0" format follows the current culture
/// ("91%" for en-US, "91 %" for the invariant culture a Linux service usually runs under), so chat, prompts and tests
/// changed with the machine.
/// </summary>
public static class Percent
{
    public static string Of(double ratio) => (ratio * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
