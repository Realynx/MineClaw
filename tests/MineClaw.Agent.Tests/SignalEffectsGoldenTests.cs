using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MineClaw.Agent.Self;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins <see cref="SignalEffects.Of"/> (WP06 refactor) for every signal kind (all <see cref="SignalKinds"/> constants plus an
/// unknown one) × with/without player and subject × intensities, weights and valences.
/// </summary>
public class SignalEffectsGoldenTests
{
    private const string Expected = "44D3D6562C135D9C45FE48F3CCE6C197A7B76E9AB7CA5940E2A83A1D9FD5D55F";

    [Fact]
    public void Every_kind_maps_to_the_same_effects()
    {
        var kinds = typeof(SignalKinds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).Append("unknown_kind").ToList();
        var sb = new StringBuilder();
        foreach (var kind in kinds)
            foreach (var player in new[] { null, "Fox" })
                foreach (var subject in new[] { null, "", "cobble" })
                    foreach (var (intensity, weight, valence) in new[] { (0.6, 1.0, 0.5), (1.5, 0.5, -0.8), (0.1, 2.0, 0.0) })
                    {
                        var s = new SelfSignal { Kind = kind, Player = player, Subject = subject, Intensity = intensity, Weight = weight, Valence = valence };
                        sb.Append(kind).Append('|').Append(player).Append('|').Append(subject ?? "null").Append('|').Append(intensity.ToString(CultureInfo.InvariantCulture))
                          .Append(" => ").Append(string.Join(", ", SignalEffects.Of(s).Select(e =>
                              $"{e.Family}/{e.Key}/{e.Dim}={e.Amount.ToString("R", CultureInfo.InvariantCulture)}"))).Append('\n');
                    }
        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "signal-effects.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
