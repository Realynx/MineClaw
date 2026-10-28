// ShardPacks — builds the default knowledge packs (shards/defaults/*.mcshard + index.json) from PRISTINE sources, never a live db.
//
//   dotnet run --project tools/ShardPacks -- build                         b1.8.1: MineClaw.Data tables + docs/knowledge, vectors from the seed
//   dotnet run --project tools/ShardPacks -- build --version 1.8.9 --source minecraft-data --data <minecraft-data checkout> [--data-key 1.8]
//   dotnet run --project tools/ShardPacks -- verify                        rebuild into a temp folder and compare byte for byte
//   dotnet run --project tools/ShardPacks -- list                          the index
//
// Options: --out <dir> (shards/defaults) · --knowledge <dir> (docs/knowledge) · --seed <db> (data/seed/mineclaw-seed.db) · --no-vectors ·
//          --model <name> --dims <n> (which seed vectors) · --only <pack,...> · --max-mb <n> (20: bigger packs drop their vectors).
// No network, no API calls, no keys: it reads files and the seed db read-only. It never downloads minecraft-data.

using System.Globalization;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Admin.Shards.Packs;

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
        "build" => Build(outDir, print: true),
        "verify" => Verify(),
        "list" => List(),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or DirectoryNotFoundException or ShardFormatException or InvalidOperationException)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 2;
}

int Usage()
{
    Console.Error.WriteLine("usage: ShardPacks build|verify|list [--version v] [--source mineclaw|minecraft-data --data dir] [--out dir] [--no-vectors]");
    return 1;
}

PackBuildOptions Options(string output)
{
    var version = Opt("--version") ?? KnowledgePack.DefaultVersion;
    var sourceKind = Opt("--source") ?? (version == KnowledgePack.DefaultVersion ? "mineclaw" : "minecraft-data");
    IGameDataSource source = sourceKind.ToLowerInvariant() switch
    {
        "mineclaw" when version == KnowledgePack.DefaultVersion => new MineClawDataSource(),
        "mineclaw" => throw new ArgumentException($"MineClaw.Data holds b1.8.1 only; {version} needs --source minecraft-data --data <dir>"),
        "minecraft-data" or "prismarine" => new MinecraftDataSource(Abs(Opt("--data") ?? throw new ArgumentException("--data <minecraft-data checkout> is required")),
            version, Opt("--edition") ?? "pc", Opt("--data-key")),
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
        Dims = Opt("--dims") is { } d ? int.Parse(d, CultureInfo.InvariantCulture) : null,
        MaxPackBytes = (long)(double.Parse(Opt("--max-mb") ?? "20", CultureInfo.InvariantCulture) * 1024 * 1024),
        Only = Opt("--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    };
}

int Build(string output, bool print)
{
    var o = Options(output);
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
        Build(temp, print: false);
        int bad = 0;
        foreach (var f in Directory.GetFiles(temp).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal))
        {
            var mine = Path.Combine(outDir, f);
            bool same = File.Exists(mine) && File.ReadAllBytes(mine).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(temp, f)));
            if (f != PackIndex.FileName && same) ShardFile.Read(mine);
            Console.WriteLine($"  {(same ? "same" : "DIFFERS"),-8} {f}");
            if (!same) bad++;
        }
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
