using System.Reflection;

namespace MineClaw.App.Hosting;

/// <summary>
/// This build's version as the release stamped it (<c>dotnet publish -p:Version=1.2.3 -p:InformationalVersion=1.2.3+&lt;sha&gt;</c>):
/// <c>--version</c> prints it and the Administration page's node info shows it. An unstamped build reads the SDK's default.
/// </summary>
public static class AppVersion
{
    private static readonly Lazy<string> Value = new(() =>
    {
        var asm = typeof(AppVersion).Assembly;
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } v
            ? v
            : asm.GetName().Version?.ToString() ?? "0.0.0";
    });

    /// <summary>The informational version (e.g. <c>1.2.3+abc1234</c>).</summary>
    public static string Informational => Value.Value;
}
