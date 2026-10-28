// ShardPacks — builds the default knowledge packs (shards/defaults/*.mcshard + index.json) from PRISTINE sources, never a live db.
//
//   dotnet run --project tools/ShardPacks -- build                         b1.8.1: MineClaw.Data tables + docs/knowledge, vectors from the seed
//   dotnet run --project tools/ShardPacks -- build --version 1.12.2        the vendored minecraft-data (third_party/minecraft-data/pc/1.12.2)
//                                                                          + shards/sources/1.12.2 facts + docs/knowledge marked 1.12.2
//   dotnet run --project tools/ShardPacks -- build --version 1.8.9 --source minecraft-data --data <minecraft-data checkout> [--data-key 1.8]
//   dotnet run --project tools/ShardPacks -- verify                        rebuild every version in index.json (+ sdk-js) into a temp
//                                                                          folder and compare byte for byte (exit 4 when anything is stale)
//   dotnet run --project tools/ShardPacks -- list                          the index
//
// Every build also writes sdk-js.mcshard (the JS scripting SDK, generated from src/MineClaw.Scripting/Sdk/js and the bot's tools);
// --no-sdk skips it, --sdk <dir> reads the SDK modules from another folder.
// Options: --out <dir> (shards/defaults) · --knowledge <dir> (docs/knowledge) · --seed <db> (data/seed/mineclaw-seed.db) · --no-vectors ·
//          --data <dir> · --facts <dir> (shards/sources/<version>) · --model <name> --dims <n> (which seed vectors) · --only <pack,...> ·
//          --max-mb <n> (20: bigger packs drop their vectors).
// No network, no API calls, no keys: it reads files and the seed db read-only. It never downloads minecraft-data.

using System.Globalization;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Admin.Shards.Packs;
using MineClaw.Bot.GameTools;
using MineClaw.Scripting.Sdk;

var root = FindRoot();
string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool Flag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);
string Abs(string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(root, p));

var cmd = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "build";
var outDir = Abs(Opt("--out") ?? Path.Combine("shards", "defaults"));
try
{
    return cmd.ToLowerInvariant() switch
    {
        "build" => Build(outDir, Opt("--version") ?? KnowledgePack.DefaultVersion, print: true, sdk: !Flag("--no-sdk")),
        "verify" => Verify(),
        "list" => List(),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or DirectoryNotFoundException or ShardFormatException
                               or InvalidOperationException or InvalidDataException)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 2;
}

int Usage()
{
    Console.Error.WriteLine("usage: ShardPacks build|verify|list [--version v] [--source mineclaw|minecraft-data --data dir] [--facts dir] [--out dir] [--no-vectors] [--no-sdk]");
    return 1;
}

PackBuildOptions Options(string output, string version, bool sdk, bool versionPacks = true)
{
    var sourceKind = Opt("--source") ?? (version == KnowledgePack.DefaultVersion ? "mineclaw" : "minecraft-data");
    var vendored = Path.Combine(root, "third_party", "minecraft-data", "pc", version);
    var facts = Opt("--facts") is { } fo ? Abs(fo) : Path.Combine(root, "shards", "sources", version);
    IGameDataSource source = sourceKind.ToLowerInvariant() switch
    {
        "mineclaw" when version == KnowledgePack.DefaultVersion => new MineClawDataSource(),
        "mineclaw" => throw new ArgumentException($"MineClaw.Data holds b1.8.1 only; {version} needs --source minecraft-data [--data <dir>]"),
        "minecraft-data" or "prismarine" => new MinecraftDataSource(
            Opt("--data") is { } d ? Abs(d) : Directory.Exists(vendored) ? vendored
                : throw new ArgumentException($"--data <minecraft-data checkout> is required (no vendored {vendored})"),
            version, Opt("--edition") ?? "pc", Opt("--data-key"), Directory.Exists(facts) ? facts : null,
            // The bot's own tables, when MineClaw.Data has this version: the pack's drop facts are what its planners use.
            version == MineClaw.Data.Versioning.Release1122GameData.Instance.VersionId ? MineClaw.Data.Versioning.Release1122GameData.Instance : null),
        _ => throw new ArgumentException($"unknown --source {sourceKind} (mineclaw | minecraft-data)"),
    };
    return new PackBuildOptions
    {
        Source = source,
        KnowledgeDirectory = Abs(Opt("--knowledge") ?? Path.Combine("docs", "knowledge")),
        OutputDirectory = output,
        SeedPath = Flag("--no-vectors") ? null : Abs(Opt("--seed") ?? Path.Combine("data", "seed", "mineclaw-seed.db")),
        IncludeVectors = !Flag("--no-vectors"),
        EmbeddingModel = Opt("--model"),
        Dims = Opt("--dims") is { } dm ? int.Parse(dm, CultureInfo.InvariantCulture) : null,
        MaxPackBytes = (long)(double.Parse(Opt("--max-mb") ?? "20", CultureInfo.InvariantCulture) * 1024 * 1024),
        Only = Opt("--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        Generated = sdk ? [ScriptSdkPack.Build(SdkModules.FromDirectory(Abs(Opt("--sdk") ?? Path.Combine("src", "MineClaw.Scripting", "Sdk", "js"))))] : [],
        IncludeVersionPacks = versionPacks,
    };
}

int Build(string output, string version, bool print, bool sdk, bool versionPacks = true)
{
    var o = Options(output, version, sdk, versionPacks);
    var result = DefaultPackBuilder.Build(o);
    if (!print) return 0;
    Console.WriteLine($"source: {o.Source.Description}");
    foreach (var p in result.Packs)
        Console.WriteLine($"  {p.File,-28} {p.Kind,-7} {p.Records,5} records ({string.Join(", ", p.Counts.Select(c => $"{c.Value} {c.Key}"))}), " +
                          $"{p.Bytes / 1024.0:F0} KB, vectors {p.Vectors} {p.EmbeddingModel}@{p.Dims}, needs embedding {p.NeedsEmbedding}");
    foreach (var n in result.Notes) Console.WriteLine("  note: " + n);
    Console.WriteLine("index: " + result.IndexPath);
    return 0;
}

int Verify()
{
    var temp = Path.Combine(Path.GetTempPath(), "mineclaw-shardpacks-" + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        // Every version the committed index lists (b1.8.1 first), the version-independent packs (sdk-js) once.
        var versions = PackIndex.Load(outDir).Packs.Select(p => p.Version).Where(KnowledgePack.ValidVersion)
            .Where(v => !v.Equals(KnowledgePack.AnyVersion, StringComparison.OrdinalIgnoreCase))
            .Append(KnowledgePack.DefaultVersion).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v == KnowledgePack.DefaultVersion ? 0 : 1).ThenBy(v => v, StringComparer.Ordinal).ToList();
        if (Opt("--version") is { } only) versions = [only];
        bool first = true;
        foreach (var v in versions) { Build(temp, v, print: false, sdk: first && !Flag("--no-sdk")); first = false; }
        var checks = PackVerifier.Compare(outDir, temp);
        if (Opt("--version") != null) checks = checks.Where(c => File.Exists(Path.Combine(temp, c.File))).ToList();
        foreach (var c in checks) Console.WriteLine($"  {c.Status.ToUpperInvariant(),-8} {c.File}{(c.Note != null ? "  (" + c.Note + ")" : "")}");
        int bad = checks.Count(c => !c.Ok);
        Console.WriteLine(bad == 0 ? "the committed packs are exactly what the sources build" : $"{bad} file(s) differ: run `build` and commit");
        return bad == 0 ? 0 : 4;
    }
    finally
    {
        try { Directory.Delete(temp, true); } catch (IOException) { }
    }
}

int List()
{
    var index = PackIndex.Load(outDir);
    foreach (var p in index.Packs)
        Console.WriteLine($"{p.File,-28} {p.Kind,-7} {p.Version,-8} {p.Records,5} records {p.Bytes / 1024.0,8:F0} KB  {p.Description}");
    return 0;
}

static string FindRoot()
{
    foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ARCHITECTURE.md"))) return dir.FullName;
    return Directory.GetCurrentDirectory();
}
