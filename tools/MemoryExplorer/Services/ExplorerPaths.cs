namespace MineClaw.MemoryExplorer.Services;

/// <summary>Repo-relative locations the explorer uses (the repo root is the folder holding MineClaw.sln).</summary>
public static class ExplorerPaths
{
    private static string? _root;

    /// <summary>Repo root above the exe or the working directory; else the working directory.</summary>
    public static string Root
    {
        get => _root ??= FindRoot(AppContext.BaseDirectory, Directory.GetCurrentDirectory());
        set => _root = value;
    }

    public static string FindRoot(params string[] starts)
    {
        foreach (var start in starts)
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "MineClaw.sln"))) return d.FullName;
        return Directory.GetCurrentDirectory();
    }

    public static string DataDir => Path.Combine(Root, "data");
    public static string SnapshotDir => Path.Combine(DataDir, "alice-snap");
    public static string LocalDevDb => Path.Combine(DataDir, "mineclaw.db");
    public static string SeedDb => Path.Combine(DataDir, "seed", "mineclaw-seed.db");
    public static string BotConfig => Path.Combine(Root, "src", "MineClaw.App", "appsettings.json");

    /// <summary>Local dbs worth offering in the "recent" list: the dev db, every snapshot (newest first), the seed.</summary>
    public static IReadOnlyList<string> KnownDatabases()
    {
        var list = new List<string>();
        if (File.Exists(LocalDevDb)) list.Add(LocalDevDb);
        if (Directory.Exists(SnapshotDir))
            list.AddRange(Directory.EnumerateFiles(SnapshotDir, "*.db").OrderByDescending(File.GetLastWriteTimeUtc));
        if (File.Exists(SeedDb)) list.Add(SeedDb);
        return list;
    }

    /// <summary>Path relative to the repo root when it lives under it (for display).</summary>
    public static string Display(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..].Replace('\\', '/') : full;
    }
}
