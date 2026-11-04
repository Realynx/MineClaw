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
    /// <summary>Last resort: download the official client jar listed in Mojang's version manifest (SHA-1 verified). Off by
    /// default: nothing is fetched unless asked for (a jar cached in <see cref="AssetsDir"/> by an earlier download still loads).</summary>
    public bool AllowDownload { get; set; }
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
    /// <summary>Mob textures (<c>mob/zombie.png</c> ... by name without extension) when the jar or the assets dir has them;
    /// null = none (entity models use procedural stand-ins, see <see cref="EntityTextures"/>).</summary>
    public IReadOnlyDictionary<string, (int W, int H, uint[] Px)>? Mobs { get; init; }
    /// <summary>Data-driven block models (1.12.2: blockstates → models → textures from the client jar); null = b1.8.1, whose
    /// blocks are <see cref="BlockGeometry"/> + <see cref="BlockTextures"/> over this atlas's terrain tiles.</summary>
    public IBlockModels? Models { get; init; }

    /// <summary>The mob textures the renderer asks for (read from a jar's or the assets dir's <c>mob/</c> folder; never fetched).</summary>
    public static readonly string[] MobNames = ["zombie", "pigzombie", "skeleton", "creeper", "spider", "pig", "cow", "sheep", "sheep_fur", "chicken", "enderman", "slime", "wolf", "squid", "ghast", "silverfish"];

    internal static IReadOnlyDictionary<string, (int, int, uint[])>? DecodeMobs(Func<string, byte[]?> read)
    {
        var d = new Dictionary<string, (int, int, uint[])>();
        foreach (var n in MobNames)
        {
            try { if (read(n) is { } png) d[n] = Png.Decode(png); }
            catch (Exception) { /* a bad PNG: that mob uses its stand-in */ }
        }
        return d.Count == 0 ? null : d;
    }

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

    /// <summary>The raw terrain.png pixels (0xAARRGGBB, Size×Size).</summary>
    public uint[] TerrainPixels => _terrain;

    /// <summary>Texel of an items.png tile at (u, v) in [0,1); null without an items atlas.</summary>
    public uint? SampleItem(int tile, float u, float v)
    {
        if (Items is not { } it) return null;
        int x = (int)(u * Tile), y = (int)(v * Tile);
        x = x < 0 ? 0 : x >= Tile ? Tile - 1 : x;
        y = y < 0 ? 0 : y >= Tile ? Tile - 1 : y;
        return it[(tile / Tiles * Tile + y) * Size + tile % Tiles * Tile + x];
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
        if (o.Version == Models.Release1122Assets.Version) return LoadModern(o, log, http);
        if (LoadExtracted(o, log) is { } extracted) return extracted;
        if (FromCandidateJars(o, log) is { } fromJar) return fromJar;
        if (!o.AllowDownload || string.IsNullOrWhiteSpace(o.AssetsDir))
        {
            log?.Invoke("no b1.8.1 textures found; using flat colours (set Bot:Vision:ClientJar, put terrain.png/items.png in the " +
                "assets folder, or allow the one-time download with Bot:Vision:AllowDownload=true)");
            return null;
        }
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

    /// <summary>The PNGs extracted into the assets dir earlier (mob textures read from the cached jar when missing), or null.</summary>
    private static TextureAtlas? LoadExtracted(TextureOptions o, Action<string>? log)
    {
        try
        {
            if (o.AssetsDir is not { Length: > 0 } dir || !File.Exists(Path.Combine(dir, TerrainFile))) return null;
            var items = Path.Combine(dir, ItemsFile);
            var skin = Path.Combine(dir, SkinFile);
            var mobDir = Path.Combine(dir, "mob");
            var mobs = Directory.Exists(mobDir) ? TextureAtlas.DecodeMobs(n => File.Exists(Path.Combine(mobDir, n + ".png")) ? File.ReadAllBytes(Path.Combine(mobDir, n + ".png")) : null) : null;
            var loaded = TextureAtlas.FromPng(File.ReadAllBytes(Path.Combine(dir, TerrainFile)), File.Exists(items) ? File.ReadAllBytes(items) : null,
                File.Exists(skin) ? File.ReadAllBytes(skin) : null, dir);
            if (mobs == null && CandidateJars(o).FirstOrDefault(File.Exists) is { } cachedJar) mobs = MobsFromJar(cachedJar, mobDir, log);
            return mobs == null ? loaded : new TextureAtlas(loaded.Size, loaded.TerrainPixels, loaded.Items, loaded.Skin) { Source = dir, Mobs = mobs };
        }
        catch (Exception ex) { log?.Invoke($"extracted textures unreadable: {ex.Message}"); }
        return null;
    }

    /// <summary>The PNGs were extracted earlier without the mob textures: read just those from the jar (a zip; nothing runs).</summary>
    private static IReadOnlyDictionary<string, (int, int, uint[])>? MobsFromJar(string cachedJar, string mobDir, Action<string>? log)
    {
        IReadOnlyDictionary<string, (int, int, uint[])>? mobs = null;
        try
        {
            using var zip = ZipFile.OpenRead(cachedJar);
            var raw = new Dictionary<string, byte[]>();
            mobs = TextureAtlas.DecodeMobs(n => zip.GetEntry($"mob/{n}.png") is { Length: < 1 << 20 } e ? raw[n] = ReadEntry(e) : null);
            KeepMobPngs(raw, mobDir);
        }
        catch (Exception ex) { log?.Invoke($"mob textures: {cachedJar} unreadable ({ex.Message})"); }
        return mobs;
    }

    /// <summary>Keep them next to terrain.png, so a host given only the assets dir (no jar) has them too.</summary>
    private static void KeepMobPngs(Dictionary<string, byte[]> raw, string mobDir)
    {
        try
        {
            if (raw.Count > 0) Directory.CreateDirectory(mobDir);
            foreach (var (n, b) in raw) File.WriteAllBytes(Path.Combine(mobDir, n + ".png"), b);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The first readable b1.8.1 jar among the candidates, or null.</summary>
    private static TextureAtlas? FromCandidateJars(TextureOptions o, Action<string>? log)
    {
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
        return null;
    }

    /// <summary>The cached download of the 1.12.2 client jar in the assets dir.</summary>
    public const string ModernJarFile = "1.12.2-client.jar";

    /// <summary>1.12.2: blockstates, models and textures read straight from a 1.12.2 client jar (the explicit one, the launcher
    /// folders, a cached or downloaded official jar); nothing is extracted. A configured jar that isn't a 1.12.2 jar (e.g. the
    /// b1.8.1 one) is skipped.</summary>
    private static TextureAtlas? LoadModern(TextureOptions o, Action<string>? log, HttpClient? http)
    {
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = CandidateJars(o).Where(j => !j.EndsWith(JarFile, StringComparison.OrdinalIgnoreCase)).ToList();
        if (o.AssetsDir is { Length: > 0 } dir) candidates.Add(Path.Combine(dir, ModernJarFile));
        foreach (var jar in candidates)
        {
            if (!tried.Add(jar) || !File.Exists(jar) || !Models.Release1122Assets.IsModernJar(jar)) continue;
            try
            {
                if (Models.Release1122Assets.FromJar(jar, log) is { } atlas) return atlas;
            }
            catch (Exception ex) { log?.Invoke($"textures: {jar} unreadable ({ex.Message})"); }
        }
        if (!o.AllowDownload || string.IsNullOrWhiteSpace(o.AssetsDir)) return null;
        try
        {
            var jar = DownloadAsync(o, http, CancellationToken.None, ModernJarFile).GetAwaiter().GetResult();
            return jar == null ? null : Models.Release1122Assets.FromJar(jar, log);
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
        var atlas0 = TextureAtlas.FromPng(terrain, items, skin, jarPath);
        var mobPngs = new Dictionary<string, byte[]>();
        var mobs = TextureAtlas.DecodeMobs(n => { var b = Read($"mob/{n}.png"); if (b != null) mobPngs[n] = b; return b; });
        var atlas = mobs == null ? atlas0 : new TextureAtlas(atlas0.Size, atlas0.TerrainPixels, atlas0.Items, atlas0.Skin) { Source = jarPath, Mobs = mobs };
        if (!string.IsNullOrWhiteSpace(extractTo))
        {
            try
            {
                Directory.CreateDirectory(extractTo);
                File.WriteAllBytes(Path.Combine(extractTo, TerrainFile), terrain);
                if (items != null) File.WriteAllBytes(Path.Combine(extractTo, ItemsFile), items);
                if (skin != null) File.WriteAllBytes(Path.Combine(extractTo, SkinFile), skin);
                if (mobPngs.Count > 0)
                {
                    Directory.CreateDirectory(Path.Combine(extractTo, "mob"));
                    foreach (var (n, b) in mobPngs) File.WriteAllBytes(Path.Combine(extractTo, "mob", n + ".png"), b);
                }
            }
            catch (IOException) { /* read-only data dir: fine, the jar is still there next time */ }
            catch (UnauthorizedAccessException) { }
        }
        return atlas;
    }

    private static byte[] ReadEntry(ZipArchiveEntry e)
    {
        using var st = e.Open();
        using var ms = new MemoryStream();
        st.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Downloads the official client jar (manifest → version json → downloads.client) into the assets dir, verifying
    /// its SHA-1. Returns the jar path (an existing verified copy is reused).</summary>
    public static async Task<string?> DownloadAsync(TextureOptions o, HttpClient? http, CancellationToken ct, string jarFile = JarFile)
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
            var path = Path.Combine(o.AssetsDir!, jarFile);
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
