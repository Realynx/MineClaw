using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Hierarchy;

/// <summary>
/// Main's whitelist hands. Every <c>/whitelist add|remove</c> the hive ever sends goes through here, for a managed account only,
/// armed one line at a time in the command gate's scoped exception (<c>CommandGate.ArmManagedWhitelist</c>) and sent on main's
/// own connection. Clones and other hives never whitelist: they ask main over the bus.
/// </summary>
public sealed class WhitelistManager
{
    private readonly ManagedAccounts _managed;
    private readonly HiveAccountsConfig _cfg;
    private readonly string _main;
    private readonly Func<string, bool> _arm;
    private readonly Func<string, CancellationToken, Task<bool>> _send;
    private readonly ILogger _log;

    /// <param name="arm">The gate's scoped exception (one exact line).</param>
    /// <param name="send">Main's command sender (paced like chat; the gated sender re-checks the line).</param>
    public WhitelistManager(ManagedAccounts managed, HiveAccountsConfig cfg, string main, Func<string, bool> arm,
        Func<string, CancellationToken, Task<bool>> send, ILogger? log = null)
    {
        _managed = managed;
        _cfg = cfg;
        _main = main;
        _arm = arm;
        _send = send;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>The predicate the command gate uses: managed, managed-shaped, never main.</summary>
    public bool Accepts(string name) => _managed.IsManaged(name) && AccountNames.IsManagedShape(name, _cfg, _main);

    public Task<bool> AddAsync(string name, CancellationToken ct = default) => RunAsync("add", name, ct);
    public Task<bool> RemoveAsync(string name, CancellationToken ct = default) => RunAsync("remove", name, ct);

    private async Task<bool> RunAsync(string op, string name, CancellationToken ct)
    {
        name = (name ?? "").Trim();
        if (!Accepts(name)) { _log.LogWarning("hive: /whitelist {Op} {Name} refused: not a managed account", op, name); return false; }
        var line = $"/whitelist {op} {name}";
        if (!_arm(line)) return false;
        bool sent;
        try { sent = await _send(line, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "hive: {Line} failed", line); return false; }
        if (sent)
        {
            _managed.SetWhitelisted(name, op == "add");
            _log.LogInformation("hive: {Line}", line);
        }
        return sent;
    }

    /// <summary>
    /// Crash cleanup: every managed account that main left whitelisted (or active) but that isn't running now is released and
    /// taken off the whitelist. Returns how many were removed.
    /// </summary>
    public async Task<int> ReconcileAsync(Func<string, bool> isRunning, CancellationToken ct = default)
    {
        int n = 0;
        foreach (var a in _managed.All())
        {
            if (isRunning(a.Name)) continue;
            if (a.Active) _managed.MarkReleased(a.Name);
            if (a.Whitelisted && await RemoveAsync(a.Name, ct).ConfigureAwait(false)) n++;
        }
        if (n > 0) _log.LogInformation("hive: reconcile removed {N} stale clone account(s) from the whitelist", n);
        return n;
    }

    /// <summary>Old fixed-roster names (they were whitelisted by hand): adopted once so the reconcile can clear them and the
    /// allocator can reuse them. Names that don't fit the managed shape are left alone.</summary>
    public int Adopt(IEnumerable<string> legacy)
    {
        int n = 0;
        foreach (var raw in legacy)
        {
            var name = (raw ?? "").Trim();
            if (!AccountNames.IsManagedShape(name, _cfg, _main) || _managed.IsManaged(name)) continue;
            _managed.Upsert(name, a => a with { Adopted = true, Whitelisted = true, Active = false });
            n++;
        }
        return n;
    }
}
