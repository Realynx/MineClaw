using System.Security.Cryptography;
using MineClaw.Agent;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Rag;
using MineClaw.Protocol.Versioning;
using Microsoft.Extensions.Configuration;

namespace MineClaw.App.Hosting;

/// <summary>
/// <c>MineClaw.App --reset-memory --pack &lt;version pack&gt; [--modes none|a.mcshard,b.mcshard] [--no-sdk] [--backup-dir dir] --yes</c>:
/// the one-shot fresh-world reset (e.g. the move from the b1.8.1 skyblock to a default 1.12.2 world). With the service STOPPED:
/// <list type="number">
/// <item>backs up the whole data dir (db + wal/shm + every data file) to a timestamped sibling (<c>/var/lib/mineclaw-backup-YYYYMMDDTHHMMSSZ</c>)
/// and verifies every file's size and SHA-256; any failure stops here with nothing changed;</item>
/// <item>removes the db files and the per-world state files (<see cref="WorldStateFiles"/>), plus a staged seed of another version;</item>
/// <item>creates a fresh db (current schema: no memories, beliefs, directives, players, structures, blueprints, scripts, activity, world
/// index, placed blocks, protection marks, standing orders, prefixes, chat or self-profile growth), swaps in the version pack, sets
/// the modes (none unless given) and adds the <c>sdk-js</c> pack found beside the version pack (unless <c>--no-sdk</c>);</item>
/// <item>writes <c>server-version.txt</c> = the pack's version and prints the counts.</item>
/// </list>
/// usage.json, hive-ledger.json (budget accounting), assets, backups and everything in /etc (config, credentials) stay as they are.
/// Without <c>--yes</c> it prints the plan and refuses. No network, no API key, no embedding calls: docs without a vector in the
/// pack are embedded by the bot on its next start (a few cents at most for a version pack).
/// </summary>
public static class MemoryReset
{
    /// <summary>Per-world state outside the db, removed by a reset (backed up first): the legacy memory import and hunger latches.</summary>
    public static readonly string[] WorldStateFiles = ["memory.json", "hunger-latch-*.json"];

    /// <summary>Kept untouched (budget accounting, textures, install-time db backups...): every other file is kept too.</summary>
    public static readonly string[] Kept = ["usage.json", "hive-ledger.json"];

    public sealed record Request
    {
        public required string DataDirectory { get; init; }
        public required string PackPath { get; init; }
        /// <summary>Mode packs to add (empty = a plain world).</summary>
        public IReadOnlyList<string> ModePacks { get; init; } = [];
        /// <summary>Add <c>sdk-js.mcshard</c> from beside the version pack (or <see cref="SdkPack"/>).</summary>
        public bool Sdk { get; init; } = true;
        public string? SdkPack { get; init; }
        public string? BackupDirectory { get; init; }
        public bool Yes { get; init; }
        public AgentOptions Options { get; init; } = new();
    }

    public sealed record Outcome(int Exit, string? Backup = null, IReadOnlyDictionary<string, long>? Counts = null);

    /// <summary>The CLI entry: config (data dir, db file, knowledge dir) from the usual layers, then <see cref="RunAsync(Request, TextWriter, DateTimeOffset?)"/>.</summary>
    public static async Task<int> RunAsync(AppCli cli, TextWriter output)
    {
        var root = AppPaths.FindRoot();
        AgentOptions options;
        try
        {
            var config = AppConfig.Build(cli, root);
            options = new AgentOptions();
            config.GetSection("Agent").Bind(options);
        }
        catch (Exception ex)
        {
            output.WriteLine($"reset: can't read the configuration: {ex.Message}");
            return ExitCodes.Config;
        }
        options.DataDirectory = AppPaths.Under(root, options.DataDirectory);
        if (options.KnowledgeDirectory != null) options.KnowledgeDirectory = AppPaths.UnderOrBeside(root, options.KnowledgeDirectory);
        var req = new Request
        {
            DataDirectory = options.DataDirectory,
            PackPath = Path.GetFullPath(cli.ResetPack!),
            ModePacks = cli.ResetModes.Select(Path.GetFullPath).ToList(),
            Sdk = !cli.ResetNoSdk,
            BackupDirectory = cli.ResetBackupDir is { } b ? Path.GetFullPath(b) : null,
            Yes = cli.Yes,
            Options = options,
        };
        return (await RunAsync(req, output).ConfigureAwait(false)).Exit;
    }

    public static async Task<Outcome> RunAsync(Request r, TextWriter o, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var data = Path.GetFullPath(r.DataDirectory);
        var dbPath = Path.IsPathRooted(r.Options.Rag.DbFile) ? r.Options.Rag.DbFile : Path.Combine(data, r.Options.Rag.DbFile);

        // 1) The packs, read and verified before anything is touched.
        var (packs, refused) = ReadPacks(r, o);
        if (packs == null) return refused!;
        var plan = new Plan(r, data, dbPath, packs.Value.Version, packs.Value.Modes, BackupPath(r, data, at));
        PrintPlan(plan, o);
        if (!r.Yes)
        {
            o.WriteLine("reset: refusing without --yes (nothing changed)");
            return new Outcome(ExitCodes.Usage);
        }

        // 2) Backup, verified; any failure stops before a single file is removed.
        if (BackUp(plan, o) is { } backupFailed) return backupFailed;
        // 3) Out with the old world.
        RemoveOldWorld(plan, o);
        // 4) A fresh db with only the packs.
        var (counts, importFailed) = await ImportFreshAsync(plan, o).ConfigureAwait(false);
        if (counts == null) return importFailed!;
        // 5) The version the next start speaks while the server is down.
        if (GameVersion.TryParse(plan.PackVersion, out var gv))
            File.WriteAllText(Path.Combine(data, VersionSetup.LastKnownFile), gv.Id + "\n");
        else o.WriteLine($"note: {plan.PackVersion} isn't a game version this build speaks; {VersionSetup.LastKnownFile} left as it is");
        PrintSummary(plan, counts, o);
        return new Outcome(ExitCodes.Ok, Directory.Exists(plan.Backup) ? plan.Backup : null, counts);
    }

    /// <summary>A reset in progress: the request, the data dir and db, the packs and where the backup goes.</summary>
    private sealed record Plan(Request R, string Data, string DbPath, Shard Version, List<(string Path, Shard Shard)> Modes, string Backup)
    {
        public string PackVersion => Version.Manifest.PackVersion!;
    }

    /// <summary>The version pack and the mode packs (with the sdk-js pack beside the version pack unless --no-sdk), each verified.</summary>
    private static ((Shard Version, List<(string Path, Shard Shard)> Modes)? Packs, Outcome? Refused) ReadPacks(Request r, TextWriter o)
    {
        try
        {
            var version = ShardFile.Read(r.PackPath);
            if (version.Manifest.Kind != "pack" || version.Manifest.Pack is not { Kind: ShardPackInfo.VersionKind })
                return (null, Fail(o, ExitCodes.Usage, $"{r.PackPath} is not a version pack (a {version.Manifest.Pack?.Kind ?? version.Manifest.Kind} shard)"));
            var (modePaths, sdkMissing) = ModePaths(r, o);
            if (sdkMissing != null) return (null, sdkMissing);
            var modes = new List<(string Path, Shard Shard)>();
            foreach (var p in modePaths)
            {
                var s = ShardFile.Read(p);
                if (s.Manifest.Pack is not { Kind: ShardPackInfo.ModeKind }) return (null, Fail(o, ExitCodes.Usage, $"{p} is not a mode pack"));
                modes.Add((p, s));
            }
            return ((version, modes), null);
        }
        catch (Exception ex) when (ex is IOException or ShardFormatException or UnauthorizedAccessException or InvalidDataException)
        {
            return (null, Fail(o, ExitCodes.Usage, "can't read a pack: " + ex.Message));
        }
    }

    /// <summary>The mode packs to add: the asked ones, plus the sdk-js pack (given, or beside the version pack) unless --no-sdk.</summary>
    private static (List<string> Paths, Outcome? SdkMissing) ModePaths(Request r, TextWriter o)
    {
        var modePaths = r.ModePacks.ToList();
        if (!r.Sdk) return (modePaths, null);
        var sdk = r.SdkPack ?? Path.Combine(Path.GetDirectoryName(r.PackPath)!, "sdk-js" + ShardFile.Extension);
        bool exists = File.Exists(sdk);
        if (exists && !modePaths.Any(p => Path.GetFullPath(p).Equals(Path.GetFullPath(sdk), StringComparison.OrdinalIgnoreCase)))
            modePaths.Add(sdk);
        else if (r.SdkPack != null && !exists) return (modePaths, Fail(o, ExitCodes.Usage, $"no sdk pack at {sdk}"));
        else if (!exists) o.WriteLine($"reset: note: no sdk-js pack beside the version pack ({sdk}); the db gets none");
        return (modePaths, null);
    }

    private static string BackupPath(Request r, string data, DateTimeOffset at) =>
        r.BackupDirectory ?? Path.Combine(Path.GetDirectoryName(data.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!,
            Path.GetFileName(data.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + "-backup-" + at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'"));

    private static void PrintPlan(Plan p, TextWriter o)
    {
        o.WriteLine("reset-memory plan:");
        o.WriteLine($"  data dir:     {p.Data}{(Directory.Exists(p.Data) ? "" : " (doesn't exist yet)")}");
        o.WriteLine($"  backup to:    {p.Backup} (whole data dir, verified by size + sha256)");
        o.WriteLine($"  fresh db:     {p.DbPath} (schema v{AgentDb.LatestSchemaVersion}), every memory, belief, rule, player, place, script and growth gone");
        o.WriteLine($"  version pack: {p.Version.Manifest.Pack!.Name} ({p.PackVersion}, {p.Version.Records.Count} records)");
        o.WriteLine($"  modes:        {(p.Modes.Count == 0 ? "none" : string.Join(", ", p.Modes.Select(m => $"{m.Shard.Manifest.Pack!.Mode} ({m.Shard.Records.Count} records)")))}");
        o.WriteLine($"  kept:         {string.Join(", ", Kept)}, assets, backups and every other file; config and credentials (/etc) are never touched");
        o.WriteLine($"  server-version.txt := {p.PackVersion}");
    }

    /// <summary>The whole data dir copied and verified (or created when there's none); a failure outcome when the copy failed.</summary>
    private static Outcome? BackUp(Plan p, TextWriter o)
    {
        if (!Directory.Exists(p.Data))
        {
            Directory.CreateDirectory(p.Data);
            o.WriteLine("backup: no data dir yet, nothing to back up");
            return null;
        }
        try
        {
            var (files, bytes) = Backup(p.Data, p.Backup);
            o.WriteLine($"backup: {files} file(s), {bytes / 1024.0 / 1024.0:F1} MB → {p.Backup} (verified)");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Fail(o, ExitCodes.Error, $"backup to {p.Backup} failed, nothing was changed: {ex.Message}");
        }
    }

    /// <summary>The db (and its journals), the world-state files, and a staged b1.8.1 seed when the pack is another version.</summary>
    private static void RemoveOldWorld(Plan p, TextWriter o)
    {
        var removed = new List<string>();
        foreach (var f in new[] { p.DbPath, p.DbPath + "-wal", p.DbPath + "-shm", p.DbPath + "-journal" })
            if (File.Exists(f)) { File.Delete(f); removed.Add(Path.GetFileName(f)); }
        foreach (var pattern in WorldStateFiles)
            foreach (var f in Directory.GetFiles(p.Data, pattern))
            { File.Delete(f); removed.Add(Path.GetFileName(f)); }
        var seed = Path.IsPathRooted(p.R.Options.Rag.SeedFile) ? p.R.Options.Rag.SeedFile : Path.Combine(p.Data, p.R.Options.Rag.SeedFile);
        if (!string.IsNullOrWhiteSpace(p.R.Options.Rag.SeedFile) && File.Exists(seed) && !p.PackVersion.Equals(KnowledgePack.DefaultVersion, StringComparison.OrdinalIgnoreCase))
        {
            // The staged clean seed is b1.8.1 game data: a lost db must never bootstrap back into the old version.
            File.Delete(seed);
            removed.Add(Path.GetRelativePath(p.Data, seed).Replace('\\', '/'));
        }
        o.WriteLine("removed: " + (removed.Count == 0 ? "nothing (no db yet)" : string.Join(", ", removed)));
    }

    /// <summary>The fresh db: the version pack swapped in, the modes added, then its counts; a failure outcome when an import failed.</summary>
    private static async Task<(SortedDictionary<string, long>? Counts, Outcome? Failed)> ImportFreshAsync(Plan p, TextWriter o)
    {
        var audit = new AdminAudit(Path.Combine(p.Data, "admin-audit.jsonl"));
        using var ctx = MemoryAdminContext.OpenLocal(p.DbPath, p.R.Options, NullEmbeddingProvider.Instance, llm: null);
        var importer = new ShardImporter(ctx, audit, "reset:" + p.DbPath);
        var name = p.Version.Manifest.Pack!.Name;
        var res = await importer.ApplyAsync(p.Version, new ShardImportOptions { Mode = ShardImportMode.SwapPack, Who = "reset-memory" }).ConfigureAwait(false);
        if (!res.Ok) return (null, Fail(o, ExitCodes.Error, $"importing {name} failed: {res.Message} (the backup is at {p.Backup})"));
        o.WriteLine($"import: {name}: {res.Message}");
        KnowledgePack.SetActive(ctx.Db, KnowledgePack.Active(ctx.Db) with { Modes = [] }); // a plain world unless modes follow
        foreach (var (path, shard) in p.Modes)
        {
            var mr = await importer.ApplyAsync(shard, new ShardImportOptions { Mode = ShardImportMode.AddMode, Who = "reset-memory" }).ConfigureAwait(false);
            if (!mr.Ok) return (null, Fail(o, ExitCodes.Error, $"adding {path} failed: {mr.Message} (the backup is at {p.Backup})"));
            o.WriteLine($"import: {shard.Manifest.Pack!.Name}: {mr.Message}");
        }
        var counts = Counts(ctx.Db);
        o.WriteLine($"knowledge_pack: {KnowledgePack.Active(ctx.Db).Label}");
        return (counts, null);
    }

    private static SortedDictionary<string, long> Counts(AgentDb db)
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (kind, n) in db.Query("SELECT kind, count(*) FROM docs GROUP BY kind ORDER BY kind", rd => (rd.GetString(0), rd.GetInt64(1))))
            counts["docs." + kind] = n;
        counts["vectors"] = db.ScalarLong("SELECT count(*) FROM vectors");
        counts["needs_embedding"] = db.ScalarLong("SELECT count(*) FROM docs d LEFT JOIN vectors v ON v.doc_id = d.id WHERE d.embed = 1 AND v.doc_id IS NULL");
        foreach (var t in LivedInTables)
            if (db.TableExists(t)) counts["rows." + t] = db.ScalarLong($"SELECT count(*) FROM {t}");
        return counts;
    }

    private static void PrintSummary(Plan p, SortedDictionary<string, long> counts, TextWriter o)
    {
        o.WriteLine("counts:");
        foreach (var (k, v) in counts.Where(c => !c.Key.StartsWith("rows.", StringComparison.Ordinal) || c.Value > 0)) o.WriteLine($"  {k,-28} {v}");
        var lived = counts.Where(c => c.Key.StartsWith("rows.", StringComparison.Ordinal)).Sum(c => c.Value);
        o.WriteLine($"  lived-in rows (memories, beliefs, rules, places, scripts, activity, world, protection, chat, self): {lived}");
        o.WriteLine($"kept: {string.Join(", ", Kept.Where(k => File.Exists(Path.Combine(p.Data, k))).DefaultIfEmpty("(none present)"))}");
        o.WriteLine($"done. Backup: {p.Backup}. The service is NOT started by the reset.");
    }

    /// <summary>Tables that hold a lived-in world (a fresh db has none of their rows; the summary shows any that do).</summary>
    public static readonly string[] LivedInTables =
    [
        "notes", "waypoints", "structures", "routines", "identity", "chat_log", "observations", "container_items", "claims", "claim_evidence",
        "claim_sources", "directives", "standing_orders", "command_prefixes", "blueprints", "blueprint_builds", "build_plans", "build_lanes",
        "activity_log", "activity_rollups", "activity_episodes", "world_chunks", "placed_blocks", "protection_marks", "portal_links", "scenery",
        "self_profile", "self_profile_history", "self_signals", "self_acc", "self_abstractions", "self_phrases", "hive_events", "hive_inventory",
        "crew_board", "crew_inbox", "crew_locks", "doc_corrections",
    ];

    private static Outcome Fail(TextWriter o, int exit, string message)
    {
        o.WriteLine("reset: " + message);
        return new Outcome(exit);
    }

    /// <summary>Copies every file under <paramref name="from"/> into a new (or empty) <paramref name="to"/>, then checks each copy's
    /// size and SHA-256 against its source. Throws on any problem (the caller changes nothing then).</summary>
    internal static (int Files, long Bytes) Backup(string from, string to)
    {
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
            throw new IOException($"{to} already exists and isn't empty");
        var full = Path.GetFullPath(to);
        if (full.StartsWith(Path.GetFullPath(from).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("the backup can't live inside the data dir");
        Directory.CreateDirectory(to);
        int files = 0;
        long bytes = 0;
        foreach (var src in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(from, src);
            var dst = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: false);
            var a = new FileInfo(src);
            var b = new FileInfo(dst);
            if (a.Length != b.Length || !Hash(src).AsSpan().SequenceEqual(Hash(dst)))
                throw new InvalidDataException($"backup copy of {rel} doesn't match its source");
            files++;
            bytes += a.Length;
        }
        return (files, bytes);
    }

    private static byte[] Hash(string path)
    {
        using var s = File.OpenRead(path);
        return SHA256.HashData(s);
    }
}
