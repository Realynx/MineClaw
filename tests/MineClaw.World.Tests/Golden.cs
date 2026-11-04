using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MineClaw.World.Tests;

/// <summary>
/// Digest goldens for the complexity refactor (WP04): a test writes a text dump of everything a function computes over a corpus,
/// and the SHA-256 of that dump must match the digest taken from the implementation before it was reshaped. On a mismatch the
/// dump is written to the temp folder so the change can be diffed against a dump of the old code. Doubles are written round-trip
/// ("R", invariant), so a match is bit for bit. The dump never contains line endings from files, so it is checkout-independent.
/// </summary>
internal static class Golden
{
    public static void Check(string name, string expected, Action<StringBuilder> dump)
    {
        var prev = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var sb = new StringBuilder();
            dump(sb);
            var text = sb.ToString();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            if (hash == expected) return;
            var path = Path.Combine(Path.GetTempPath(), $"mineclaw-golden-{name}.txt");
            File.WriteAllText(path, text);
            Assert.Fail($"{name}: digest {hash} != {expected} (dump: {path})");
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    public static string R(double d) => d.ToString("R", CultureInfo.InvariantCulture);
    public static string R(float f) => f.ToString("R", CultureInfo.InvariantCulture);
    public static string R(Vec3d v) => $"{R(v.X)},{R(v.Y)},{R(v.Z)}";
}
