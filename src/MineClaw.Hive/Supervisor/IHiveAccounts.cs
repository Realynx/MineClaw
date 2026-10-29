using MineClaw.Hive.Ipc;

namespace MineClaw.Hive.Supervisor;

/// <summary>
/// The supervisor's seam to dynamic accounts (<see cref="Hierarchy.HiveHierarchy"/>): no fixed roster, every clone account is
/// allocated by main, whitelisted right before its login and taken off the whitelist once it is offline for good.
/// Null = the supervisor runs only the slots it was given (tests, old configs).
/// </summary>
public interface IHiveAccounts
{
    /// <summary>A new account for an unnamed spawn when no existing slot is free: (name, null) or (null, why not).</summary>
    (string? Name, string? Why) Allocate(HiveSpawnOptions? options);
    /// <summary>A slot took a task (fresh launch, reassignment or queued relaunch).</summary>
    void Assigned(string account, TaskMsg task, HiveSpawnOptions? options);
    /// <summary>Right before the clone logs in (main whitelists the account).</summary>
    void BeforeLaunch(string account);
    /// <summary>Right before the clone logs in, awaited (bounded by the implementation): main whitelists the account and the
    /// launch waits for the server's confirmation, so the login isn't refused "not white-listed". Default: <see cref="BeforeLaunch"/>.</summary>
    Task BeforeLaunchAsync(string account, CancellationToken ct = default) { BeforeLaunch(account); return Task.CompletedTask; }
    /// <summary>The slot is offline for good (logged out, kicked or retired; not restarting, not relaunching).</summary>
    void Released(string account, string reason);
}
