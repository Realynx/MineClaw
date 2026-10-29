using System.Collections.Concurrent;
using MineClaw.Render;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>
/// The textures and block models of the version the hive plays, for every Live Hive renderer (GPU freecam, CPU freecam, eye
/// tiles, compose). The version is the hive's own word (the index reply), else what the synced world looks like (256-high
/// columns are 1.12.2), else the local guess (<c>&lt;data&gt;/server-version.txt</c>). Before, the Explorer read only that local
/// file, so a viewer of a 1.12.2 hive on a machine whose bot never ran 1.12.2 drew 1.12.2 blocks with the b1.8.1 shapes: new ids
/// (wood doors, fences, gates, slabs, stairs…) as plain cubes and changed metadata (door tops, top slabs/trapdoors, upside-down
/// stairs, buttons, levers) the b1.8.1 way, while the server-rendered sheets used the models. Loading (the client jar) runs in
/// the background; until the new version's atlas is ready the previous one stays. Thread-safe.
/// </summary>
public sealed class VersionedAtlas(Func<string?> hiveVersion, Func<string, TextureAtlas?> load, string fallbackVersion)
{
    private readonly ConcurrentDictionary<string, TextureAtlas?> _loaded = new(StringComparer.Ordinal);
    private volatile TextureAtlas? _current;
    private volatile string? _currentVersion;
    private int _loading;
    private Task _inFlight = Task.CompletedTask;

    /// <summary>Raised (on a pool thread) when a version's atlas finished loading (null = no textures for it).</summary>
    public event Action<string, TextureAtlas?>? Loaded;

    /// <summary>The version the renderers should draw.</summary>
    public string Version => hiveVersion() ?? fallbackVersion;

    /// <summary>The version of <see cref="Current"/> (null before the first load).</summary>
    public string? CurrentVersion => _currentVersion;

    /// <summary>The atlas for <see cref="Version"/>, starting its load if needed (the previous one meanwhile; null before any).</summary>
    public TextureAtlas? Current
    {
        get
        {
            string v = Version;
            if (v != _currentVersion)
            {
                if (_loaded.TryGetValue(v, out var a)) { _current = a; _currentVersion = v; }
                else Start(v);
            }
            return _current;
        }
    }

    /// <summary>Loads <paramref name="version"/>'s atlas in the background (one load at a time: while another runs, its task).</summary>
    public Task Start(string version)
    {
        if (_loaded.ContainsKey(version)) return Task.CompletedTask;
        if (Interlocked.CompareExchange(ref _loading, 1, 0) != 0) return _inFlight;
        return _inFlight = Task.Run(() =>
        {
            TextureAtlas? a = null;
            try { a = load(version); }
            catch (Exception) { }
            finally
            {
                foreach (var k in _loaded.Keys) if (k != version) _loaded.TryRemove(k, out _); // keep one version's textures
                _loaded[version] = a;
                if (Version == version || _currentVersion == null) { _current = a; _currentVersion = version; }
                Interlocked.Exchange(ref _loading, 0);
            }
            try { Loaded?.Invoke(version, a); } catch (Exception) { }
        });
    }

    /// <summary>A status line ("textures: 1.12.2 models from the client jar").</summary>
    public static string Describe(string version, TextureAtlas? a) => a == null
        ? $"textures: none found locally for {version} (flat colours)"
        : a.Models != null ? $"textures: {version} block models from the local client jar" : $"textures: local {version} atlas";
}
