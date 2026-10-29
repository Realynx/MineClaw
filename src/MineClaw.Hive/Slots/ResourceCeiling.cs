using System.Globalization;

namespace MineClaw.Hive.Slots;

/// <summary>
/// "Hive:Resources": the memory model behind the hive's resource-aware ceiling (replaces the fixed <c>Hive:MaxClones</c>, which is
/// now only an optional hard cap). Sizes are MB of resident memory, measured on the 1.12.2 normal world (see ARCHITECTURE.md,
/// "Hive", the per-body benchmark).
/// </summary>
public sealed class HiveResourceConfig
{
    /// <summary>What one more subhive body adds to the process hosting it (its connection, world view, wiring).</summary>
    public double BodyMb { get; set; } = 40;
    /// <summary>What one more hive process costs before its bodies (runtime, brain, db connection, its own body).</summary>
    public double HiveProcessMb { get; set; } = 380;
    /// <summary>Main's own process with no clones (used as the floor of the estimate).</summary>
    public double MainProcessMb { get; set; } = 650;
    /// <summary>Kept free for GC spikes, the OS and everything else in the container.</summary>
    public double ReserveMb { get; set; } = 400;
    /// <summary>The memory limit; 0 = detect it (cgroup v2 <c>memory.max</c>, v1 <c>memory.limit_in_bytes</c>, else the machine).</summary>
    public double LimitMb { get; set; }
    /// <summary>Off = no resource ceiling (only the player cap and the optional hard cap).</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>What the hive runs now: extra hive processes (clone processes) and subhive bodies (in main's or a hive's process).</summary>
public readonly record struct ResourceUse(int HiveProcesses, int Bodies);

/// <summary>The container's memory as the kernel reports it. <paramref name="UsedMb"/> excludes reclaimable page cache.</summary>
public readonly record struct MemoryProbe(double LimitMb, double UsedMb, string Source);

/// <summary>
/// The resource-aware ceiling: does one more hive process and/or N more bodies fit in the container's memory? Used memory is the
/// larger of the estimate (main + hives × <see cref="HiveResourceConfig.HiveProcessMb"/> + bodies × <see cref="HiveResourceConfig.BodyMb"/>)
/// and the kernel's measurement, so a burst of spawns that hasn't grown the heap yet is still counted.
/// </summary>
public sealed class ResourceCeiling
{
    private readonly HiveResourceConfig _cfg;
    private readonly Func<ResourceUse> _use;
    private readonly Func<MemoryProbe> _probe;

    public ResourceCeiling(HiveResourceConfig cfg, Func<ResourceUse> use, Func<MemoryProbe>? probe = null)
    {
        _cfg = cfg;
        _use = use;
        _probe = probe ?? Probe;
    }

    public HiveResourceConfig Config => _cfg;

    /// <summary>Limit, used (max of estimate and measurement) and free MB after the reserve.</summary>
    public (double Limit, double Used, double Free, string Source) Snapshot()
    {
        var p = SafeProbe();
        var u = _use();
        double limit = _cfg.LimitMb > 0 ? _cfg.LimitMb : p.LimitMb;
        double est = _cfg.MainProcessMb + Math.Max(0, u.HiveProcesses) * _cfg.HiveProcessMb + Math.Max(0, u.Bodies) * _cfg.BodyMb;
        double used = Math.Max(est, p.UsedMb);
        return (limit, used, limit - _cfg.ReserveMb - used, _cfg.LimitMb > 0 ? "Hive:Resources:LimitMb" : p.Source);
    }

    /// <summary>May <paramref name="hives"/> more hive processes and <paramref name="bodies"/> more bodies start now?</summary>
    public (bool Ok, string? Why) CanAdd(int hives, int bodies)
    {
        if (!_cfg.Enabled) return (true, null);
        var s = Snapshot();
        if (s.Limit <= 0) return (true, null);
        double need = Math.Max(0, hives) * _cfg.HiveProcessMb + Math.Max(0, bodies) * _cfg.BodyMb;
        if (need <= s.Free) return (true, null);
        return (false, string.Create(CultureInfo.InvariantCulture,
            $"memory: {Math.Max(0, s.Free):0} MB free of {s.Limit:0} MB ({s.Used:0} used, {_cfg.ReserveMb:0} kept), {(hives > 0 ? "a hive process + " : "")}{bodies} bod{(bodies == 1 ? "y" : "ies")} need {need:0} MB"));
    }

    /// <summary>How many bodies fit after <paramref name="withHives"/> more hive processes (int.MaxValue when unlimited).</summary>
    public int BodiesThatFit(int withHives = 0)
    {
        if (!_cfg.Enabled) return int.MaxValue;
        var s = Snapshot();
        if (s.Limit <= 0) return int.MaxValue;
        double free = s.Free - Math.Max(0, withHives) * _cfg.HiveProcessMb;
        return free <= 0 || _cfg.BodyMb <= 0 ? (free > 0 ? int.MaxValue : 0) : (int)Math.Floor(free / _cfg.BodyMb);
    }

    public string Describe()
    {
        var s = Snapshot();
        return string.Create(CultureInfo.InvariantCulture, $"memory {s.Used:0}/{s.Limit:0} MB ({s.Source}), room for {BodiesThatFit()} more bod(ies)");
    }

    private MemoryProbe SafeProbe()
    {
        try { return _probe(); }
        catch (Exception) { return new MemoryProbe(0, 0, "unknown"); }
    }

    // ================================================================ the kernel's numbers

    /// <summary>
    /// cgroup v2, then v1, then <c>/proc/meminfo</c> (an LXC container such as alice's CT 114 sees <c>memory.max = max</c> inside, while
    /// lxcfs shows its real limit in meminfo), then the machine (GC's view; used = this process).
    /// </summary>
    public static MemoryProbe Probe() => ProbeFrom("/sys/fs/cgroup", File.Exists, File.ReadAllText);

    /// <summary>Testable probe over a fake file system.</summary>
    public static MemoryProbe ProbeFrom(string root, Func<string, bool> exists, Func<string, string> read, string meminfo = "/proc/meminfo")
    {
        const double Mb = 1024 * 1024;
        string v2Max = Path.Combine(root, "memory.max").Replace('\\', '/');
        if (exists(v2Max))
        {
            var max = read(v2Max).Trim();
            double limit = max == "max" ? 0 : ParseBytes(max) / Mb;
            double cur = ParseBytes(TryRead(Path.Combine(root, "memory.current").Replace('\\', '/'), exists, read)) / Mb;
            double cache = StatField(TryRead(Path.Combine(root, "memory.stat").Replace('\\', '/'), exists, read), "inactive_file") / Mb;
            if (limit > 0) return new MemoryProbe(limit, Math.Max(0, cur - cache), "cgroup v2");
        }
        string v1 = Path.Combine(root, "memory").Replace('\\', '/');
        string v1Limit = v1 + "/memory.limit_in_bytes";
        if (exists(v1Limit))
        {
            double limit = ParseBytes(read(v1Limit)) / Mb;
            double cur = ParseBytes(TryRead(v1 + "/memory.usage_in_bytes", exists, read)) / Mb;
            double cache = StatField(TryRead(v1 + "/memory.stat", exists, read), "total_inactive_file") / Mb;
            if (limit > 0 && limit < 1024 * 1024 * 64) return new MemoryProbe(limit, Math.Max(0, cur - cache), "cgroup v1"); // "unlimited" is ~8 EiB
        }
        if (TryRead(meminfo, exists, read) is { Length: > 0 } mi)
        {
            double total = MemInfo(mi, "MemTotal") / 1024, avail = MemInfo(mi, "MemAvailable") / 1024;
            if (total > 0) return new MemoryProbe(total, Math.Max(0, total - avail), "meminfo");
        }
        try
        {
            var gc = GC.GetGCMemoryInfo();
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            return new MemoryProbe(gc.TotalAvailableMemoryBytes / Mb, me.WorkingSet64 / Mb, "machine");
        }
        catch (Exception) { return new MemoryProbe(0, 0, "unknown"); }
    }

    private static string TryRead(string path, Func<string, bool> exists, Func<string, string> read)
    {
        try { return exists(path) ? read(path) : ""; }
        catch (Exception) { return ""; }
    }

    private static double ParseBytes(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>"MemTotal:        4194304 kB" → 4194304 (kB).</summary>
    private static double MemInfo(string text, string key)
    {
        foreach (var line in text.Split('\n'))
            if (line.StartsWith(key + ":", StringComparison.Ordinal))
                return ParseBytes(line[(key.Length + 1)..].Replace("kB", "").Trim());
        return 0;
    }

    private static double StatField(string stat, string key)
    {
        foreach (var line in stat.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == key) return ParseBytes(parts[1]);
        }
        return 0;
    }
}
