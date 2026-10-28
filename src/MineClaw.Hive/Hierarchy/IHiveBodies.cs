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
/// Subhive bodies: characters the owning hive's own brain drives (one brain, many bodies; <c>Bodies:Mode=inproc</c>). Null on
/// the hierarchy = not available in this mode, and <c>hive spawn kind=subhive</c> says so.
/// </summary>
public interface ISubhiveHost
{
    string Spawn(string owner, string account, string? task, string? script);
    string Release(string account);
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
