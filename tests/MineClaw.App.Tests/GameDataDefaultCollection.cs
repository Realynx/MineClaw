namespace MineClaw.App.Tests;

/// <summary>
/// Tests that set or rely on the process-wide default game data (<see cref="MineClaw.Data.Versioning.GameData.SetDefault"/>, which
/// the app host and VersionSetup call) run in this one collection, never in parallel with each other, and restore the default.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GameDataDefaultCollection
{
    public const string Name = "Process-wide game data default";

    public static void Restore() => MineClaw.Data.Versioning.GameData.SetDefault(MineClaw.Data.Versioning.Beta181GameData.Instance);
}
