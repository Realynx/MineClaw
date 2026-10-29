namespace MineClaw.Data.Versioning;

/// <summary>
/// What kind of world the bot plays in, beside the version (multi-version P6): <b>skyblock</b> (an island over the void, a cobble
/// generator for stone, scarce dirt and saplings, one lava source and one bucket) or a <b>normal</b> generated world (ground
/// under everything, natural stone, caves, villages, plentiful trees and dirt). It is a world fact, not a version fact: the
/// active knowledge packs decide it (the <c>skyblock</c> mode pack; a legacy db without pack metadata counts as skyblock, so
/// every b1.8.1 install behaves as before).
/// <para>Like <see cref="GameData"/>: one process plays one world, so the composition root sets the default once
/// (<see cref="SetDefault"/>); tests scope another with <see cref="Override"/> (an AsyncLocal that flows into awaits, tasks and
/// new threads). The default is skyblock.</para>
/// <para>Game code reads facts, never the mode name: <see cref="HasVoidBelow"/>, <see cref="GeneratorStone"/>,
/// <see cref="ScarceTerrain"/>, <see cref="Islands"/>.</para>
/// </summary>
public sealed record WorldMode(string Name, bool Skyblock)
{
    public static readonly WorldMode SkyblockWorld = new("skyblock", true);
    public static readonly WorldMode Normal = new("normal", false);

    private static WorldMode _default = SkyblockWorld;
    private static readonly AsyncLocal<WorldMode?> _override = new();

    /// <summary>The world mode in this flow.</summary>
    public static WorldMode Current => _override.Value ?? _default;

    /// <summary>The island stands over the void: an edge is a deadly fall, "void" language and void-reset moves apply.</summary>
    public bool HasVoidBelow => Skyblock;

    /// <summary>Stone and cobblestone come from a cobble generator (not natural terrain): "mine stone" means the gen, stone
    /// beside lava + water is a gen cell, and every stone/cobble block outside one was placed by someone.</summary>
    public bool GeneratorStone => Skyblock;

    /// <summary>Dirt, saplings, sand, clay and the lava source can't be replaced: keep the last trees, the dirt under them,
    /// replant every sapling.</summary>
    public bool ScarceTerrain => Skyblock;

    /// <summary>Land comes in islands (clusters of ground over the void) worth naming ("main island").</summary>
    public bool Islands => Skyblock;

    public static void SetDefault(WorldMode mode) => _default = mode ?? throw new ArgumentNullException(nameof(mode));

    /// <summary>Skyblock when the active modes name it (null = a legacy db: every mode doc counts, i.e. skyblock).</summary>
    public static WorldMode FromModes(IEnumerable<string>? modes) =>
        modes == null || modes.Any(m => m.Equals("skyblock", StringComparison.OrdinalIgnoreCase)) ? SkyblockWorld : Normal;

    public static IDisposable Override(WorldMode mode)
    {
        var prev = _override.Value;
        _override.Value = mode ?? throw new ArgumentNullException(nameof(mode));
        return new Scope(prev);
    }

    private sealed class Scope(WorldMode? prev) : IDisposable
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
