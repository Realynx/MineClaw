using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;
using MineClaw.Hive.Supervisor;

namespace MineClaw.Hive.Hierarchy;

/// <summary>What the hierarchy needs from whatever runs clone characters (the process supervisor today; fakes in tests).</summary>
public interface IHiveBodies
{
    string Spawn(string task, string? name, bool handover, HiveSpawnOptions options);
    /// <summary>Planned logout: the clone deposits its whole inventory first (the existing logout gate), then disconnects.</summary>
    string Recall(string name, string reason);
    /// <summary>Completes when the clone is offline (or after <paramref name="timeout"/>).</summary>
    Task WaitGoneAsync(string name, TimeSpan timeout);
    IReadOnlyCollection<string> Running();
    SlotFacts Slots();
    int Pending();
    string List();
}

/// <summary>
/// Subhive bodies in this process: characters the hive's own brain drives (one brain, many bodies), each with its own login and
/// world view. Main's host is <see cref="HiveHierarchy.Subhives"/> (null = <c>hive spawn kind=subhive</c> says not available); a
/// hive-kind clone hosts its own the same way and hears about each account over the bus. The host whitelists nothing: the account
/// is allocated (and whitelisted before its login) by main.
/// </summary>
public interface ISubhiveHost
{
    /// <summary>Logs the (allocated) account in as a body and starts its job (task/script), or gives an online body a new job.</summary>
    string Spawn(string owner, string account, string? task, string? script);
    /// <summary>Planned logout: deposit first, then disconnect; the host reports it gone (main: <see cref="HiveHierarchy.Released"/>).</summary>
    string Release(string account);
    /// <summary>A hosted body whose login is looping (a kick, a backoff) tries again now. False = not hosted here or not waiting.</summary>
    bool RetryNow(string account) => false;
    /// <summary>The owner chose to let this account use the seats kept for offline regulars (until it logs out for good).</summary>
    void AllowOverCap(string account) { }
}

/// <summary>
/// Hive scripts (<c>hive spawn kind=script bodies=N script=X</c>): ONE coordinator script and no brain, owning N subhive bodies of
/// this process (live 10-29: a brain issuing per-body jobs turn after turn left bodies idle, sent several to one tree and dropped
/// jobs at the first interruption). The coordinator runs bodiless in the hosting process; its <c>bodies()</c> are exactly the accounts
/// <see cref="Start"/>'s member list names that are online now, so late logins join and leavers drop out.
/// </summary>
public interface IHiveScriptHost
{
    /// <summary>A saved name (or inline code the hive tool already authorised as the Opus brain's: linted, saved) → (name, error).</summary>
    (string? Name, string? Error) Prepare(string? script, string? code);
    /// <summary>Starts the coordinator; <paramref name="ended"/>(ok, line) once it ends however it ends. "ok: …" or why not.</summary>
    string Start(string id, string script, string? args, Func<IReadOnlyCollection<string>> members, Action<bool, string> ended);
    /// <summary>Stops it at its next safe point (its lanes are cancelled with it); <c>ended</c> still reports.</summary>
    bool Stop(string id, string reason);
    /// <summary>The running coordinator's state line, or null.</summary>
    string? Status(string id);
}

/// <summary><see cref="IHiveBodies"/> over the process supervisor.</summary>
public sealed class SupervisorBodies(HiveSupervisor sup) : IHiveBodies
{
    public string Spawn(string task, string? name, bool handover, HiveSpawnOptions options) => sup.Spawn(task, name, handover, options: options);
    public string Recall(string name, string reason) => sup.Recall(name, reason);
    public Task WaitGoneAsync(string name, TimeSpan timeout) => sup.WaitGoneAsync(name, timeout);
    public IReadOnlyCollection<string> Running() =>
        sup.Slots.Where(s => s.State is not (CloneState.Idle or CloneState.Retired)).Select(s => s.Name).ToList();
    public SlotFacts Slots() => sup.SlotFacts();
    public int Pending()
    {
        var f = sup.SlotFacts();
        return sup.Slots.Count(s => s.State is CloneState.Starting && !f.Online.Contains(s.Name, StringComparer.OrdinalIgnoreCase));
    }
    public string List() => sup.List();
}
