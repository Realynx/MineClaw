using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace MineClaw.Render;

/// <summary>Where to find the b1.8.1 textures (bind "Bot:Vision"). Mojang's textures are copyrighted: they are read at runtime
/// from a client jar on this machine (or downloaded from Mojang into a gitignored cache), never committed or embedded.</summary>
public sealed class TextureOptions
{
    /// <summary>Explicit client jar path (the "Render:ClientJar" setting); tried first.</summary>
    public string? ClientJar { get; set; }
    /// <summary>Directory holding extracted <c>terrain.png</c>/<c>items.png</c> (and the cached jar); default "&lt;data&gt;/assets".</summary>
    public string? AssetsDir { get; set; }
    /// <summary>Last resort: download the official client jar listed in Mojang's version manifest (SHA-1 verified).</summary>
    public bool AllowDownload { get; set; } = true;
    /// <summary>Also look in the usual launcher folders (PrismLauncher libraries, vanilla .minecraft/versions).</summary>
    public bool SearchLauncherDirs { get; set; } = true;
    public string ManifestUrl { get; set; } = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public string Version { get; set; } = "b1.8.1";
}

/// <summary>
/// b1.8.1 block atlas (<c>terrain.png</c>, 16×16 tiles of 16 px) and item atlas (<c>gui/items.png</c>), plus the player skin
/// (<c>mob/char.png</c>) when present. Pixels are 0xAARRGGBB. Use <see cref="TextureLocator"/> to load one; null = none on
/// this machine, and every renderer falls back to the flat palette.
/// </summary>
public sealed class TextureAtlas
{
    public const int Tiles = 16;
    public int Size { get; }
    public int Tile { get; }
    private readonly uint[] _terrain;
    private readonly uint[] _avg = new uint[256];
    private readonly float[] _opaque = new float[256];
    public uint[]? Items { get; }
    public (int W, int H, uint[] Px)? Skin { get; }
    /// <summary>Where it came from (for logs): a jar path or the assets dir.</summary>
    public string Source { get; init; } = "";

    public TextureAtlas(int size, uint[] terrain, uint[]? items = null, (int, int, uint[])? skin = null)
    {
        if (size < 16 || terrain.Length < size * size) throw new ArgumentException("bad atlas");
        Size = size;
        Tile = size / Tiles;
        _terrain = terrain;
        Items = items;
        Skin = skin;
        for (int t = 0; t < 256; t++)
        {
            long r = 0, g = 0, b = 0, n = 0;
            int tx = t % Tiles * Tile, ty = t / Tiles * Tile;
            for (int y = 0; y < Tile; y++)
                for (int x = 0; x < Tile; x++)
                {
                    uint p = terrain[(ty + y) * size + tx + x];
                    if (p >> 24 < 128) continue;
                    r += (p >> 16) & 0xff; g += (p >> 8) & 0xff; b += p & 0xff; n++;
                }
            _opaque[t] = n / (float)(Tile * Tile);
            _avg[t] = n == 0 ? 0 : (uint)(r / n) << 16 | (uint)(g / n) << 8 | (uint)(b / n);
        }
    }

    /// <summary>Texel of a tile at (u, v) in [0,1) (u right, v down), 0xAARRGGBB.</summary>
    public uint Sample(int tile, float u, float v)
    {
        int x = (int)(u * Tile), y = (int)(v * Tile);
        x = x < 0 ? 0 : x >= Tile ? Tile - 1 : x;
        y = y < 0 ? 0 : y >= Tile ? Tile - 1 : y;
        return _terrain[(tile / Tiles * Tile + y) * Size + tile % Tiles * Tile + x];
    }

    /// <summary>Texel by integer tile coordinates (0..Tile-1).</summary>
    public uint Texel(int tile, int x, int y) => _terrain[(tile / Tiles * Tile + y) * Size + tile % Tiles * Tile + x];

    /// <summary>Average opaque colour of a tile (0xRRGGBB; 0 = fully transparent tile).</summary>
    public uint Average(int tile) => _avg[tile & 255];

    /// <summary>Fraction of a tile's texels that are opaque (animated tiles such as water and lava are blank in the file).</summary>
    public float Coverage(int tile) => _opaque[tile & 255];

    public static TextureAtlas FromPng(byte[] terrainPng, byte[]? itemsPng = null, byte[]? skinPng = null, string source = "")
    {
        var (w, h, px) = Png.Decode(terrainPng);
        if (w != h || w % 16 != 0) throw new InvalidDataException($"terrain.png is {w}x{h}, expected a square of 16 tiles");
        uint[]? items = null;
        if (itemsPng != null) { var (iw, ih, ip) = Png.Decode(itemsPng); if (iw == w && ih == h) items = ip; }
        (int, int, uint[])? skin = null;
        if (skinPng != null) skin = Png.Decode(skinPng);
        return new TextureAtlas(w, px, items, skin) { Source = source };
    }
}

/// <summary>
/// Finds the textures: an explicit jar, then already-extracted files in the assets dir, then the usual launcher locations
/// (PrismLauncher libraries/instances, vanilla .minecraft/versions), then a cached or freshly downloaded official jar
/// (verified against the manifest's SHA-1). Only <c>terrain.png</c>, <c>gui/items.png</c> and <c>mob/char.png</c> are ever
/// read from a jar; nothing in it is executed. Whatever is found is extracted into the assets dir (gitignored) so a server
/// without a jar can be given just the two PNGs.
/// </summary>
public static class TextureLocator
{
    public const string TerrainFile = "terrain.png", ItemsFile = "items.png", SkinFile = "char.png", JarFile = "b1.8.1-client.jar";

    /// <summary>Candidate jar paths on this machine, most specific first.</summary>
    public static IEnumerable<string> CandidateJars(TextureOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.ClientJar)) yield return o.ClientJar!;
        if (o.SearchLauncherDirs && Environment.GetEnvironmentVariable("MINECLAW_CLIENT_JAR") is { Length: > 0 } env) yield return env;
        if (o.AssetsDir is { Length: > 0 } cached && !o.SearchLauncherDirs) { yield return Path.Combine(cached, JarFile); yield break; }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[] { appData, Path.Combine(home, ".local", "share"), home }.Where(r => r.Length > 0).Distinct())
        {
            yield return Path.Combine(root, "PrismLauncher", "libraries", "com", "mojang", "minecraft", o.Version, $"minecraft-{o.Version}-client.jar");
            yield return Path.Combine(root, ".minecraft", "versions", o.Version, $"{o.Version}.jar");
        }
        if (o.AssetsDir is { Length: > 0 } dir) yield return Path.Combine(dir, JarFile);
    }

    /// <summary>Loads the atlas, or null when none is available (renderers then use the flat palette).</summary>
    public static TextureAtlas? Load(TextureOptions o, Action<string>? log = null, HttpClient? http = null)
    {
        try
        {
            if (o.AssetsDir is { Length: > 0 } dir && File.Exists(Path.Combine(dir, TerrainFile)))
            {
                var items = Path.Combine(dir, ItemsFile);
                var skin = Path.Combine(dir, SkinFile);
                return TextureAtlas.FromPng(File.ReadAllBytes(Path.Combine(dir, TerrainFile)), File.Exists(items) ? File.ReadAllBytes(items) : null,
                    File.Exists(skin) ? File.ReadAllBytes(skin) : null, dir);
            }
        }
        catch (Exception ex) { log?.Invoke($"extracted textures unreadable: {ex.Message}"); }

        foreach (var jar in CandidateJars(o))
        {
            if (!File.Exists(jar)) continue;
            try
            {
                var atlas = FromJar(jar, o.AssetsDir);
                if (atlas != null) { log?.Invoke($"textures from {jar}"); return atlas; }
            }
            catch (Exception ex) { log?.Invoke($"textures: {jar} unreadable ({ex.Message})"); }
        }

        if (!o.AllowDownload || string.IsNullOrWhiteSpace(o.AssetsDir)) return null;
        try
        {
            var jar = DownloadAsync(o, http, CancellationToken.None).GetAwaiter().GetResult();
            return jar == null ? null : FromJar(jar, o.AssetsDir);
        }
        catch (Exception ex)
        {
            log?.Invoke($"textures: download failed ({ex.Message}); using flat colours");
            return null;
        }
    }

    /// <summary>Reads the three PNGs out of a client jar (zip) and, when <paramref name="extractTo"/> is set, saves them there.</summary>
    public static TextureAtlas? FromJar(string jarPath, string? extractTo = null)
    {
        using var zip = ZipFile.OpenRead(jarPath);
        byte[]? Read(string name)
        {
            var e = zip.GetEntry(name);
            if (e == null || e.Length > 4 * 1024 * 1024) return null;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        var terrain = Read("terrain.png");
        if (terrain == null) return null;
        var items = Read("gui/items.png");
        var skin = Read("mob/char.png");
        var atlas = TextureAtlas.FromPng(terrain, items, skin, jarPath);
        if (!string.IsNullOrWhiteSpace(extractTo))
        {
            try
            {
                Directory.CreateDirectory(extractTo);
                File.WriteAllBytes(Path.Combine(extractTo, TerrainFile), terrain);
                if (items != null) File.WriteAllBytes(Path.Combine(extractTo, ItemsFile), items);
                if (skin != null) File.WriteAllBytes(Path.Combine(extractTo, SkinFile), skin);
            }
            catch (IOException) { /* read-only data dir: fine, the jar is still there next time */ }
            catch (UnauthorizedAccessException) { }
        }
        return atlas;
    }

    /// <summary>Downloads the official client jar (manifest → version json → downloads.client) into the assets dir, verifying
    /// its SHA-1. Returns the jar path (an existing verified copy is reused).</summary>
    public static async Task<string?> DownloadAsync(TextureOptions o, HttpClient? http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.AssetsDir)) return null;
        bool own = http == null;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        try
        {
            using var manifest = JsonDocument.Parse(await http.GetStringAsync(o.ManifestUrl, ct).ConfigureAwait(false));
            string? versionUrl = null;
            foreach (var v in manifest.RootElement.GetProperty("versions").EnumerateArray())
                if (v.GetProperty("id").GetString() == o.Version) { versionUrl = v.GetProperty("url").GetString(); break; }
            if (versionUrl == null) return null;
            using var ver = JsonDocument.Parse(await http.GetStringAsync(versionUrl, ct).ConfigureAwait(false));
            var client = ver.RootElement.GetProperty("downloads").GetProperty("client");
            string url = client.GetProperty("url").GetString()!, sha1 = client.GetProperty("sha1").GetString()!;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
            var path = Path.Combine(o.AssetsDir!, JarFile);
            if (File.Exists(path) && Sha1(File.ReadAllBytes(path)) == sha1.ToLowerInvariant()) return path;
            var bytes = await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            if (Sha1(bytes) != sha1.ToLowerInvariant()) throw new InvalidDataException("client jar SHA-1 mismatch");
            Directory.CreateDirectory(o.AssetsDir!);
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            return path;
        }
        finally
        {
            if (own) http.Dispose();
        }
    }

    public static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
}
