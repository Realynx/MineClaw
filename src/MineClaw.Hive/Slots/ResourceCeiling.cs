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
    /// <summary>What one more hive inside main's process costs (<c>Hive:Isolation=inproc</c>): its brain (runtime, caches, its own db
    /// connection, tools) and one client at the default view distance, without a second CLR, JIT and GC heap. The kernel's
    /// measurement still wins when it is higher (used = max(estimate, measured)).</summary>
    public double InProcHiveMb { get; set; } = 140;
    /// <summary>Main's own process with no clones (used as the floor of the estimate).</summary>
    public double MainProcessMb { get; set; } = 650;
    /// <summary>Kept free for GC spikes, the OS and everything else in the container.</summary>
    public double ReserveMb { get; set; } = 400;
    /// <summary>Kept free in main's managed heap (its <c>GCHeapHardLimit</c>) when in-process hives or bodies are added.</summary>
    public double HeapReserveMb { get; set; } = 512;
    /// <summary>The memory limit; 0 = detect it (cgroup v2 <c>memory.max</c>, v1 <c>memory.limit_in_bytes</c>, else the machine).</summary>
    public double LimitMb { get; set; }
    /// <summary>Off = no resource ceiling (only the player cap and the optional hard cap).</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>What the hive runs now: extra hives (<paramref name="HiveProcesses"/>: every clone out, whatever runs it), subhive bodies
/// (in main's or a hive's process), and how many of those hives run inside main's process (<paramref name="InProcHives"/>), which
/// <see cref="ResourceCeiling"/> prices at <see cref="HiveResourceConfig.InProcHiveMb"/> instead of a whole process.</summary>
public readonly record struct ResourceUse(int HiveProcesses, int Bodies, int InProcHives = 0);

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

    /// <summary>
    /// This process's managed heap: its hard limit (<c>System.GC.HeapHardLimit</c>, else what the GC may use) and its size now.
    /// In-process hives and their bodies all live in main's one heap, so a spawn that would fit the container but not the heap is
    /// refused too (an OutOfMemoryException there would take every hive at once). Null = not checked (tests, process isolation).
    /// </summary>
    public Func<MemoryProbe>? Heap { get; init; }

    /// <summary>The GC's own numbers for <see cref="Heap"/>.</summary>
    public static MemoryProbe GcHeap()
    {
        const double Mb = 1024 * 1024;
        var gc = GC.GetGCMemoryInfo();
        return new MemoryProbe(gc.TotalAvailableMemoryBytes / Mb, GC.GetTotalMemory(false) / Mb, "managed heap");
    }

    /// <summary>Free MB in the managed heap after <see cref="HiveResourceConfig.HeapReserveMb"/> (double.MaxValue = unbounded).</summary>
    private double HeapFree()
    {
        if (Heap == null) return double.MaxValue;
        try
        {
            var h = Heap();
            return h.LimitMb <= 0 ? double.MaxValue : h.LimitMb - _cfg.HeapReserveMb - h.UsedMb;
        }
        catch (Exception) { return double.MaxValue; }
    }

    /// <summary>Limit, used (max of estimate and measurement) and free MB after the reserve.</summary>
    public (double Limit, double Used, double Free, string Source) Snapshot()
    {
        var p = SafeProbe();
        var u = _use();
        double limit = _cfg.LimitMb > 0 ? _cfg.LimitMb : p.LimitMb;
        int inproc = Math.Clamp(u.InProcHives, 0, Math.Max(0, u.HiveProcesses));
        double est = _cfg.MainProcessMb + (Math.Max(0, u.HiveProcesses) - inproc) * _cfg.HiveProcessMb + inproc * _cfg.InProcHiveMb
            + Math.Max(0, u.Bodies) * _cfg.BodyMb;
        double used = Math.Max(est, p.UsedMb);
        return (limit, used, limit - _cfg.ReserveMb - used, _cfg.LimitMb > 0 ? "Hive:Resources:LimitMb" : p.Source);
    }

    /// <summary>One more hive's price: inside main's process (a brain + one client) or a process of its own.</summary>
    public double HiveMb(bool inProcess) => inProcess ? _cfg.InProcHiveMb : _cfg.HiveProcessMb;

    /// <summary>May <paramref name="hives"/> more hives (inside main's process when <paramref name="inProcess"/>, else processes of
    /// their own) and <paramref name="bodies"/> more bodies start now?</summary>
    public (bool Ok, string? Why) CanAdd(int hives, int bodies, bool inProcess = false)
    {
        if (!_cfg.Enabled) return (true, null);
        var s = Snapshot();
        if (s.Limit <= 0) return (true, null);
        double need = Math.Max(0, hives) * HiveMb(inProcess) + Math.Max(0, bodies) * _cfg.BodyMb;
        // Main's own bodies and in-process hives share this heap; a process of its own has its own.
        double heap = inProcess || hives <= 0 ? HeapFree() : double.MaxValue;
        var hive = hives <= 0 ? "" : inProcess ? "a hive + " : "a hive process + ";
        if (need > heap && heap < s.Free)
            return (false, string.Create(CultureInfo.InvariantCulture,
                $"memory: {Math.Max(0, heap):0} MB free in the managed heap ({_cfg.HeapReserveMb:0} kept), {hive}{bodies} bod{(bodies == 1 ? "y" : "ies")} need {need:0} MB"));
        if (need <= s.Free) return (true, null);
        return (false, string.Create(CultureInfo.InvariantCulture,
            $"memory: {Math.Max(0, s.Free):0} MB free of {s.Limit:0} MB ({s.Used:0} used, {_cfg.ReserveMb:0} kept), {hive}{bodies} bod{(bodies == 1 ? "y" : "ies")} need {need:0} MB"));
    }

    /// <summary>How many bodies fit after <paramref name="withHives"/> more hives (int.MaxValue when unlimited).</summary>
    public int BodiesThatFit(int withHives = 0, bool inProcess = false)
    {
        if (!_cfg.Enabled) return int.MaxValue;
        var s = Snapshot();
        if (s.Limit <= 0) return int.MaxValue;
        double free = Math.Min(s.Free, withHives <= 0 || inProcess ? HeapFree() : double.MaxValue) - Math.Max(0, withHives) * HiveMb(inProcess);
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
