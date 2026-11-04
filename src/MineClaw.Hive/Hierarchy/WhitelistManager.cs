using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Hierarchy;

/// <summary>
/// Main's whitelist hands. Every <c>/whitelist add|remove</c> the hive ever sends goes through here, for a managed account only,
/// armed one line at a time in the command gate's scoped exception (<c>CommandGate.ArmManagedWhitelist</c>) and sent on main's
/// own connection. Clones and other hives never whitelist: they ask main over the bus.
/// </summary>
public sealed partial class WhitelistManager
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

    /// <summary>The server refused a login because the account isn't on the whitelist ("You are not white-listed on this server!").</summary>
    public static bool IsNotWhitelistedKick(string? reason) => reason != null
        && (reason.Contains("not white-listed", StringComparison.OrdinalIgnoreCase) || reason.Contains("not whitelisted", StringComparison.OrdinalIgnoreCase));

    /// <summary>How long an add waits for the server's confirmation line once replies are fed (<see cref="NoteServerLine"/>).</summary>
    public TimeSpan ConfirmTimeout { get; set; } = TimeSpan.FromSeconds(8);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _repliesFed;

    /// <summary>
    /// Every server chat/console line main sees (wire it to main's chat feed). Once lines are fed, an add counts only after the
    /// server's confirmation: "Added X to the whitelist" (1.12.2 <c>commands.whitelist.add.success</c>; b1.8.1 "Added X to
    /// white-list"), or fails on "Could not add X". Without a feed, an add counts once it is sent (the old behaviour).
    /// </summary>
    public void NoteServerLine(string? text)
    {
        _repliesFed = true;
        if (string.IsNullOrEmpty(text) || _pending.IsEmpty) return;
        foreach (var (reply, op, ok) in Replies)
            if (reply.Match(text) is { Success: true } m && _pending.TryGetValue(Key(op, m.Groups[1].Value), out var pending))
            {
                pending.TrySetResult(ok);
                return;
            }
        // The permission error names no account: every whitelist line in flight failed (live 10-29: main not op yet, four
        // "/whitelist remove" each answered "commands.generic.permission", and the reconcile still said "removed 4").
        if (Denied().IsMatch(text))
            foreach (var p in _pending.Values) p.TrySetResult(false);
    }

    /// <summary>The server's answers to a whitelist line, in the order they are tried: the op they answer and whether it took. "Could
    /// not remove X": X isn't on the server's list (CommandWhitelist: no profile in it), so it is off it already.</summary>
    private static readonly (Regex Reply, string Op, bool Ok)[] Replies =
    [
        (Added(), "add", true), (NotAdded(), "add", false), (Removed(), "remove", true), (NotRemoved(), "remove", true),
    ];

    private static string Key(string op, string name) => op + " " + name;

    [GeneratedRegex(@"Added (\S+?) to (?:the )?white-?list", RegexOptions.IgnoreCase)]
    private static partial Regex Added();
    [GeneratedRegex(@"Could not add (\S+?) to (?:the )?white-?list", RegexOptions.IgnoreCase)]
    private static partial Regex NotAdded();
    [GeneratedRegex(@"Removed (\S+?) from (?:the )?white-?list", RegexOptions.IgnoreCase)]
    private static partial Regex Removed();
    [GeneratedRegex(@"Could not remove (\S+?) from (?:the )?white-?list", RegexOptions.IgnoreCase)]
    private static partial Regex NotRemoved();
    [GeneratedRegex(@"do not have permission|commands\.generic\.permission|^generic permission\b|unknown command", RegexOptions.IgnoreCase)]
    private static partial Regex Denied();
    public Task<bool> RemoveAsync(string name, CancellationToken ct = default) => RunAsync("remove", name, ct);

    private async Task<bool> RunAsync(string op, string name, CancellationToken ct)
    {
        name = (name ?? "").Trim();
        if (!Accepts(name)) { _log.LogWarning("hive: /whitelist {Op} {Name} refused: not a managed account", op, name); return false; }
        var line = $"/whitelist {op} {name}";
        if (!_arm(line)) return false;
        // An add counts once the server confirms it (when replies are fed): a gate that ate the line, a non-op main or a
        // typo'd name must not leave the account marked whitelisted (the clone would be kicked at login, forever).
        // A removal too: the reconcile counts only what the server took off its list.
        var confirm = Expect(op, name);
        bool sent;
        try
        {
            try { sent = await _send(line, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "hive: {Line} failed", line); return false; }
            if (sent && confirm != null && !await ConfirmedAsync(confirm, line, ct).ConfigureAwait(false)) return false;
        }
        finally { Forget(op, name, confirm); }
        if (sent)
        {
            _managed.SetWhitelisted(name, op == "add");
            _log.LogInformation("hive: {Line}", line);
        }
        return sent;
    }

    /// <summary>The server confirmed the line within <see cref="ConfirmTimeout"/>.</summary>
    private async Task<bool> ConfirmedAsync(TaskCompletionSource<bool> confirm, string line, CancellationToken ct)
    {
        try { return await confirm.Task.WaitAsync(ConfirmTimeout, ct).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _log.LogWarning("hive: {Line}: no confirmation from the server within {S:0}s", line, ConfirmTimeout.TotalSeconds);
            return false;
        }
    }

    /// <summary>The server's answer to wait for (only when its replies are fed); null = none.</summary>
    private TaskCompletionSource<bool>? Expect(string op, string name)
    {
        TaskCompletionSource<bool>? confirm = _repliesFed ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        if (confirm != null) _pending[Key(op, name)] = confirm;
        return confirm;
    }

    private void Forget(string op, string name, TaskCompletionSource<bool>? confirm)
    {
        if (confirm != null) _pending.TryRemove(new KeyValuePair<string, TaskCompletionSource<bool>>(Key(op, name), confirm));
    }

    /// <summary>
    /// Crash cleanup: every managed account that main left whitelisted (or active) but that isn't running now is released and
    /// taken off the whitelist. Returns how many were removed.
    /// </summary>
    public async Task<int> ReconcileAsync(Func<string, bool> isRunning, CancellationToken ct = default)
    {
        int n = 0, failed = 0;
        foreach (var a in _managed.All())
        {
            if (isRunning(a.Name)) continue;
            if (a.Active) _managed.MarkReleased(a.Name);
            if (!a.Whitelisted) continue;
            if (await RemoveAsync(a.Name, ct).ConfigureAwait(false)) n++;
            else failed++; // still marked whitelisted: the retry (and the next start) tries it again
        }
        if (n > 0) _log.LogInformation("hive: reconcile removed {N} stale clone account(s) from the whitelist", n);
        if (failed > 0)
        {
            int attempt = Interlocked.Increment(ref _reconcileRetries);
            _log.LogWarning("hive: reconcile: {N} whitelist removal(s) not confirmed by the server{Retry}", failed,
                attempt <= MaxReconcileRetries ? $"; retrying in {RetryDelay.TotalSeconds:0}s" : "; giving up until the next start");
            if (attempt <= MaxReconcileRetries)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                        await ReconcileAsync(isRunning, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _log.LogDebug(ex, "hive: reconcile retry failed"); }
                }, CancellationToken.None);
        }
        else Interlocked.Exchange(ref _reconcileRetries, 0);
        return n;
    }

    private int _reconcileRetries;

    /// <summary>Wait before a reconcile retries the removals the server didn't confirm (main may be opped by then).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Retries of unconfirmed removals before waiting for the next start.</summary>
    public int MaxReconcileRetries { get; set; } = 5;

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
