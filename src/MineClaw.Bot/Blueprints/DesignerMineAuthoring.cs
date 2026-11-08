using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace MineClaw.Bot.Blueprints;

/// <summary>Editor-only layout envelope. The browser validates geometry against the actual saved canvas before restoring it.</summary>
public static class DesignerMineAuthoring
{
    public const int MaxLength = 2_000_000;

    /// <summary>A short DSL marker makes metadata-only edits distinct history revisions without copying editor data into the plan.</summary>
    public static string Marker(string data) => "# editor layout " + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data))) + "\n";

    public static bool Valid(string? data)
    {
        if (data == null) return true;
        if (data.Length > MaxLength) return false;
        try
        {
            using var doc = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("size", out var size) && size.TryGetInt32(out var n) && n is 64 or 256
                && root.TryGetProperty("modules", out var modules) && modules.ValueKind == JsonValueKind.Array && modules.GetArrayLength() <= 100
                && root.TryGetProperty("base", out var baseline) && baseline.ValueKind == JsonValueKind.Array
                && root.TryGetProperty("overrides", out var overrides) && overrides.ValueKind == JsonValueKind.Array;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return false; }
    }
}
