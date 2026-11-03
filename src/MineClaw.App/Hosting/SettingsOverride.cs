using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace MineClaw.App.Hosting;

/// <summary>
/// The settings override file written by the web's Administration page (<c>Supervisor/Admin</c>): a plain JSON object of config
/// keys layered on top of the base config (appsettings, <c>--config</c>, env), so the deployed base file is never touched.
/// Default path <c>&lt;Agent:DataDirectory&gt;/settings.override.json</c> (on CT 114 <c>/var/lib/mineclaw/settings.override.json</c>);
/// <c>Host:SettingsOverride</c> or env <c>MINECLAW_SETTINGS_OVERRIDE</c> move it, <c>off</c> disables it.
/// </summary>
public static class SettingsOverrideFile
{
    public const string EnvVar = "MINECLAW_SETTINGS_OVERRIDE";
    public const string ConfigKey = "Host:SettingsOverride";
    public const string FileName = "settings.override.json";

    /// <summary>
    /// The override path for a config being built (<paramref name="partial"/> = the layers below it), or null = off.
    /// <paramref name="testSeam"/>: a fake environment (tests) gets an override only when it names one.
    /// </summary>
    public static string? ResolvePath(string? envValue, string? cliDataDir, IConfiguration partial, string root, bool testSeam = false)
    {
        if (envValue is { } e)
            return e.Trim() is "" or "off" or "none" ? null : AppPaths.Under(root, e.Trim());
        if (testSeam) return null;
        var configured = partial[ConfigKey];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim() is "off" or "none" ? null : AppPaths.Under(root, configured.Trim());
        var dataDir = cliDataDir ?? partial["Agent:DataDirectory"];
        return Path.Combine(AppPaths.Under(root, string.IsNullOrWhiteSpace(dataDir) ? "data" : dataDir), FileName);
    }

    /// <summary>The override layer of a built config (null = no override layer).</summary>
    public static SettingsOverrideProvider? Of(IConfigurationRoot config) => config.Providers.OfType<SettingsOverrideProvider>().LastOrDefault();
}

public sealed class SettingsOverrideSource(string path) : IConfigurationSource
{
    public string Path { get; } = path;
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new SettingsOverrideProvider(Path);
}

/// <summary>
/// Reads the override file into flat config keys. A list in it (a JSON array) REPLACES the lower layers' list instead of merging
/// index by index (the stock JSON provider would leave a longer base list's tail behind): <see cref="GetChildKeys"/> hides the
/// lower layers' children of every list path it owns. A missing or broken file is an empty layer (the bot always starts).
/// </summary>
public sealed class SettingsOverrideProvider(string path) : ConfigurationProvider
{
    private HashSet<string> _lists = new(StringComparer.OrdinalIgnoreCase);

    public string FilePath { get; } = path;
    /// <summary>Why the last load found nothing usable (a parse error), or null.</summary>
    public string? LoadError { get; private set; }

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var lists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LoadError = null;
        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.ValueKind == JsonValueKind.Object) Flatten(doc.RootElement, null, data, lists);
                else LoadError = "the override file is not a JSON object";
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            data.Clear();
            lists.Clear();
            LoadError = ex.GetType().Name; // never the content
        }
        Data = data;
        _lists = lists;
    }

    /// <summary>Flat key → value (lists as <c>key:0</c>, <c>key:1</c>…) and the list paths, from a JSON object.</summary>
    public static void Flatten(JsonElement e, string? prefix, IDictionary<string, string?> data, ISet<string> lists)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) Flatten(p.Value, prefix == null ? p.Name : prefix + ":" + p.Name, data, lists);
                break;
            case JsonValueKind.Array:
                if (prefix != null) lists.Add(prefix);
                int i = 0;
                foreach (var item in e.EnumerateArray()) Flatten(item, prefix + ":" + i++.ToString(System.Globalization.CultureInfo.InvariantCulture), data, lists);
                break;
            case JsonValueKind.Null:
                if (prefix != null) data[prefix] = null;
                break;
            case JsonValueKind.String:
                if (prefix != null) data[prefix] = e.GetString();
                break;
            default:
                if (prefix != null) data[prefix] = e.GetRawText();
                break;
        }
    }

    /// <summary>A list this layer owns shows only its own items (the lower layers' items under it are hidden).</summary>
    public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
    {
        var mine = base.GetChildKeys([], parentPath);
        if (parentPath != null && _lists.Contains(parentPath)) return mine;
        return base.GetChildKeys(earlierKeys, parentPath);
    }

    /// <summary>The list paths this layer replaces.</summary>
    public IReadOnlyCollection<string> Lists => _lists;
}
