namespace MineClaw.Data.Versioning;

/// <summary>
/// The process's game data (docs/design/multi-version.md 3.3): the static tables (<see cref="Items"/>, <see cref="Recipes"/>,
/// <see cref="Smelting"/>, <see cref="Drops"/>, <see cref="Tools"/>.CanHarvest/DigTicks) are facades over <see cref="Current"/>.
/// One process speaks one version, so the composition root sets the default once (<see cref="SetDefault"/>, from the resolved
/// profile) before anything reads a table; b1.8.1 is the default and keeps the hand-transcribed tables on a direct path.
/// Tests scope another version with <see cref="Override"/> (an AsyncLocal: it flows into awaits, <c>Task.Run</c> and new
/// threads; the process default lies underneath).
/// <see cref="Blocks"/>.Get/Exists/ByName joined in P6 (physics and collision read the profile's shapes since P3). Not behind
/// the facade: <see cref="Items.All"/>/<see cref="Blocks.All"/>, which list b1.8.1's records.
/// </summary>
public static class GameData
{
    private static IGameData _default = Beta181GameData.Instance;
    private static readonly AsyncLocal<IGameData?> _override = new();

    /// <summary>The game data every static table answers from in this flow.</summary>
    public static IGameData Current => _override.Value ?? _default;

    /// <summary>True when <see cref="Current"/> is b1.8.1 (the static tables' own path).</summary>
    internal static bool IsBeta(out IGameData current)
    {
        current = Current;
        return current is Beta181GameData;
    }

    /// <summary>Sets the process default (the composition root, once the version is resolved).</summary>
    public static void SetDefault(IGameData data) => _default = data ?? throw new ArgumentNullException(nameof(data));

    /// <summary>Scopes <paramref name="data"/> to this async flow until the result is disposed (tests).</summary>
    public static IDisposable Override(IGameData data)
    {
        var prev = _override.Value;
        _override.Value = data ?? throw new ArgumentNullException(nameof(data));
        return new Scope(prev);
    }

    private sealed class Scope(IGameData? prev) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _override.Value = prev;
        }
    }
}
