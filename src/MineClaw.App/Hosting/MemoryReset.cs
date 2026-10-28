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
        Shard version;
        var modes = new List<(string Path, Shard Shard)>();
        try
        {
            version = ShardFile.Read(r.PackPath);
            if (version.Manifest.Kind != "pack" || version.Manifest.Pack is not { Kind: ShardPackInfo.VersionKind })
                return Fail(o, ExitCodes.Usage, $"{r.PackPath} is not a version pack (a {version.Manifest.Pack?.Kind ?? version.Manifest.Kind} shard)");
            var modePaths = r.ModePacks.ToList();
            var sdk = r.SdkPack ?? Path.Combine(Path.GetDirectoryName(r.PackPath)!, "sdk-js" + ShardFile.Extension);
            if (r.Sdk && File.Exists(sdk) && !modePaths.Any(p => Path.GetFullPath(p).Equals(Path.GetFullPath(sdk), StringComparison.OrdinalIgnoreCase)))
                modePaths.Add(sdk);
            else if (r.Sdk && r.SdkPack != null && !File.Exists(sdk)) return Fail(o, ExitCodes.Usage, $"no sdk pack at {sdk}");
            else if (r.Sdk && !File.Exists(sdk)) o.WriteLine($"reset: note: no sdk-js pack beside the version pack ({sdk}); the db gets none");
            foreach (var p in modePaths)
            {
                var s = ShardFile.Read(p);
                if (s.Manifest.Pack is not { Kind: ShardPackInfo.ModeKind }) return Fail(o, ExitCodes.Usage, $"{p} is not a mode pack");
                modes.Add((p, s));
            }
        }
        catch (Exception ex) when (ex is IOException or ShardFormatException or UnauthorizedAccessException or InvalidDataException)
        {
            return Fail(o, ExitCodes.Usage, "can't read a pack: " + ex.Message);
        }
        var packVersion = version.Manifest.PackVersion!;
        var backup = r.BackupDirectory ?? Path.Combine(Path.GetDirectoryName(data.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!,
            Path.GetFileName(data.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + "-backup-" + at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'"));

        o.WriteLine("reset-memory plan:");
        o.WriteLine($"  data dir:     {data}{(Directory.Exists(data) ? "" : " (doesn't exist yet)")}");
        o.WriteLine($"  backup to:    {backup} (whole data dir, verified by size + sha256)");
        o.WriteLine($"  fresh db:     {dbPath} (schema v{AgentDb.LatestSchemaVersion}), every memory, belief, rule, player, place, script and growth gone");
        o.WriteLine($"  version pack: {version.Manifest.Pack.Name} ({packVersion}, {version.Records.Count} records)");
        o.WriteLine($"  modes:        {(modes.Count == 0 ? "none" : string.Join(", ", modes.Select(m => $"{m.Shard.Manifest.Pack!.Mode} ({m.Shard.Records.Count} records)")))}");
        o.WriteLine($"  kept:         {string.Join(", ", Kept)}, assets, backups and every other file; config and credentials (/etc) are never touched");
        o.WriteLine($"  server-version.txt := {packVersion}");
        if (!r.Yes)
        {
            o.WriteLine("reset: refusing without --yes (nothing changed)");
            return new Outcome(ExitCodes.Usage);
        }

        // 2) Backup, verified; any failure stops before a single file is removed.
        int files = 0;
        long bytes = 0;
        if (Directory.Exists(data))
        {
            try { (files, bytes) = Backup(data, backup); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return Fail(o, ExitCodes.Error, $"backup to {backup} failed, nothing was changed: {ex.Message}");
            }
            o.WriteLine($"backup: {files} file(s), {bytes / 1024.0 / 1024.0:F1} MB → {backup} (verified)");
        }
        else
        {
            Directory.CreateDirectory(data);
            o.WriteLine("backup: no data dir yet, nothing to back up");
        }

        // 3) Out with the old world.
        var removed = new List<string>();
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm", dbPath + "-journal" })
            if (File.Exists(f)) { File.Delete(f); removed.Add(Path.GetFileName(f)); }
        foreach (var pattern in WorldStateFiles)
            foreach (var f in Directory.GetFiles(data, pattern))
            { File.Delete(f); removed.Add(Path.GetFileName(f)); }
        var seed = Path.IsPathRooted(r.Options.Rag.SeedFile) ? r.Options.Rag.SeedFile : Path.Combine(data, r.Options.Rag.SeedFile);
        if (!string.IsNullOrWhiteSpace(r.Options.Rag.SeedFile) && File.Exists(seed) && !packVersion.Equals(KnowledgePack.DefaultVersion, StringComparison.OrdinalIgnoreCase))
        {
            // The staged clean seed is b1.8.1 game data: a lost db must never bootstrap back into the old version.
            File.Delete(seed);
            removed.Add(Path.GetRelativePath(data, seed).Replace('\\', '/'));
        }
        o.WriteLine("removed: " + (removed.Count == 0 ? "nothing (no db yet)" : string.Join(", ", removed)));

        // 4) A fresh db with only the packs.
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var audit = new AdminAudit(Path.Combine(data, "admin-audit.jsonl"));
        using (var ctx = MemoryAdminContext.OpenLocal(dbPath, r.Options, NullEmbeddingProvider.Instance, llm: null))
        {
            var importer = new ShardImporter(ctx, audit, "reset:" + dbPath);
            var res = await importer.ApplyAsync(version, new ShardImportOptions { Mode = ShardImportMode.SwapPack, Who = "reset-memory" }).ConfigureAwait(false);
            if (!res.Ok) return Fail(o, ExitCodes.Error, $"importing {version.Manifest.Pack.Name} failed: {res.Message} (the backup is at {backup})");
            o.WriteLine($"import: {version.Manifest.Pack.Name}: {res.Message}");
            KnowledgePack.SetActive(ctx.Db, KnowledgePack.Active(ctx.Db) with { Modes = [] }); // a plain world unless modes follow
            foreach (var (path, shard) in modes)
            {
                var mr = await importer.ApplyAsync(shard, new ShardImportOptions { Mode = ShardImportMode.AddMode, Who = "reset-memory" }).ConfigureAwait(false);
                if (!mr.Ok) return Fail(o, ExitCodes.Error, $"adding {path} failed: {mr.Message} (the backup is at {backup})");
                o.WriteLine($"import: {shard.Manifest.Pack!.Name}: {mr.Message}");
            }
            foreach (var (kind, n) in ctx.Db.Query("SELECT kind, count(*) FROM docs GROUP BY kind ORDER BY kind", rd => (rd.GetString(0), rd.GetInt64(1))))
                counts["docs." + kind] = n;
            counts["vectors"] = ctx.Db.ScalarLong("SELECT count(*) FROM vectors");
            counts["needs_embedding"] = ctx.Db.ScalarLong("SELECT count(*) FROM docs d LEFT JOIN vectors v ON v.doc_id = d.id WHERE d.embed = 1 AND v.doc_id IS NULL");
            foreach (var t in LivedInTables)
                if (ctx.Db.TableExists(t)) counts["rows." + t] = ctx.Db.ScalarLong($"SELECT count(*) FROM {t}");
            var active = KnowledgePack.Active(ctx.Db);
            o.WriteLine($"knowledge_pack: {active.Label}");
        }

        // 5) The version the next start speaks while the server is down.
        if (GameVersion.TryParse(packVersion, out var gv))
            File.WriteAllText(Path.Combine(data, VersionSetup.LastKnownFile), gv.Id + "\n");
        else o.WriteLine($"note: {packVersion} isn't a game version this build speaks; {VersionSetup.LastKnownFile} left as it is");

        o.WriteLine("counts:");
        foreach (var (k, v) in counts.Where(c => !c.Key.StartsWith("rows.", StringComparison.Ordinal) || c.Value > 0)) o.WriteLine($"  {k,-28} {v}");
        var lived = counts.Where(c => c.Key.StartsWith("rows.", StringComparison.Ordinal)).Sum(c => c.Value);
        o.WriteLine($"  lived-in rows (memories, beliefs, rules, places, scripts, activity, world, protection, chat, self): {lived}");
        o.WriteLine($"kept: {string.Join(", ", Kept.Where(k => File.Exists(Path.Combine(data, k))).DefaultIfEmpty("(none present)"))}");
        o.WriteLine($"done. Backup: {backup}. The service is NOT started by the reset.");
        return new Outcome(ExitCodes.Ok, Directory.Exists(backup) ? backup : null, counts);
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
