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

    /// <summary>
    /// Add once, keep (incident 2026-11-12: the server saves its whitelist file synchronously on its tick thread, and a colony's add
    /// and remove on every login, logout and deploy stalled a slow-disk server past its 60 s watchdog). An account already known to
    /// be on the list (its managed record says so: a confirmed add, adopted, or the server answered "already whitelisted") sends
    /// nothing; an add already in flight for it is joined, not repeated. True = it is on the list (or the line went out, when no
    /// replies are fed). A removal queued or in flight for the account (a pool account given up for good and reused at once), or
    /// one sent that the server never confirmed, means the record can't be trusted: the add goes out after it, as before.
    /// </summary>
    public async Task<bool> AddAsync(string name, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        bool doubt = await AfterRemovalAsync(name, ct).ConfigureAwait(false);
        if (!doubt && IsListed(name)) return true;
        return await Coalesced("add", name, ct).ConfigureAwait(false);
    }

    /// <summary>Waits out a removal of the account queued or in flight. True = the record may say "listed" while the server's file
    /// no longer has it (a removal went out just now, or one sent was never confirmed).</summary>
    private async Task<bool> AfterRemovalAsync(string name, CancellationToken ct)
    {
        if (!_inflight.TryGetValue(Key("remove", name), out var removal)) return _removeUnconfirmed.ContainsKey(name);
        try { await removal.Value.WaitAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        { _log.LogDebug(ex, "hive: /whitelist remove {Name} failed before its add", name); }
        return true;
    }

    /// <summary>
    /// The server kicked the account "not white-listed": its list no longer has it (edited by hand, a reconcile of an older build),
    /// whatever the managed record says, so it is added again. At most once per <see cref="ReAddCooldown"/> after a confirmed add:
    /// a server that keeps refusing a name it just took isn't helped by more saves of its whitelist file.
    /// </summary>
    public async Task<bool> ReAddAsync(string name, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        // A removal since the last add (queued, in flight or unconfirmed) is why it was kicked: no cooldown then.
        bool doubt = await AfterRemovalAsync(name, ct).ConfigureAwait(false);
        if (!doubt && _addedAt.TryGetValue(name, out var at) && DateTimeOffset.UtcNow - at < ReAddCooldown && IsListed(name))
        {
            _log.LogInformation("hive: {Name} was added {S:0}s ago; not sending /whitelist add again", name, (DateTimeOffset.UtcNow - at).TotalSeconds);
            return true;
        }
        if (Accepts(name) && IsListed(name)) _managed.SetWhitelisted(name, false); // the server's word: it isn't on the list
        return await Coalesced("add", name, ct).ConfigureAwait(false);
    }

    /// <summary>The managed record says the account is on the server's whitelist.</summary>
    public bool IsListed(string name) => _managed.Get(name)?.Whitelisted == true;

    /// <summary>Least time between two re-adds of one account after "not white-listed" kicks (<see cref="ReAddAsync"/>).</summary>
    public TimeSpan ReAddCooldown { get; set; } = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _addedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _inflight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One line per account and op at a time: a second caller (a connect retry, a relaunch) joins the one in flight.</summary>
    private Task<bool> Coalesced(string op, string name, CancellationToken ct)
    {
        var key = Key(op, name);
        var mine = new Lazy<Task<bool>>(() => RunAsync(op, name, ct));
        var lazy = _inflight.GetOrAdd(key, mine);
        if (!ReferenceEquals(lazy, mine)) return lazy.Value.WaitAsync(ct);
        var task = mine.Value;
        _ = task.ContinueWith(_ => _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(key, mine)), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

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
        if (string.IsNullOrEmpty(text)) return;
        if (_pending.IsEmpty) { NoteLate(text); return; }
        foreach (var (reply, op, ok) in Replies)
            if (reply.Match(text) is { Success: true } m && _pending.TryGetValue(Key(op, m.Groups[1].Value), out var pending))
            {
                pending.TrySetResult(ok);
                return;
            }
        // Newer servers answer an add of a listed name "Player is already whitelisted" (no name in it): it is on the list. Taken
        // for the add it names, else for the only add waiting.
        if (Already().Match(text) is { Success: true } already)
        {
            if (_pending.TryGetValue(Key("add", already.Groups[1].Value), out var named)) { named.TrySetResult(true); return; }
            var adds = _pending.Where(p => p.Key.StartsWith("add ", StringComparison.Ordinal)).ToList();
            if (adds.Count == 1) { adds[0].Value.TrySetResult(true); return; }
        }
        if (NoteLate(text)) return;
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

    private readonly ConcurrentDictionary<string, DateTimeOffset> _unconfirmed = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Removals main sent that the server didn't confirm in time: it may have taken the name off its list (a lagging server),
    /// so the record's "listed" isn't trusted until an add is confirmed or a late "Removed" settles it. The flag stays set so the
    /// reconcile tries the removal again.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _removeUnconfirmed = new(StringComparer.OrdinalIgnoreCase);

    private bool NoteLate(string text) => NoteLateAdd(text) || NoteLateRemove(text);

    /// <summary>A lagging server confirms a removal main stopped waiting for: the account is off the list.</summary>
    private bool NoteLateRemove(string text)
    {
        if (_removeUnconfirmed.IsEmpty || Removed().Match(text) is not { Success: true } m) return false;
        var name = m.Groups[1].Value;
        if (!_removeUnconfirmed.TryRemove(name, out _) || !Accepts(name)) return false;
        _managed.SetWhitelisted(name, false);
        _log.LogInformation("hive: the server confirmed /whitelist remove {Name} late; it is off the list", name);
        return true;
    }

    /// <summary>A lagging server ("Can't keep up!") confirms an add after <see cref="ConfirmTimeout"/>: the account is on the list
    /// after all, so its next login sends no second add. Only for an add main sent and stopped waiting for (within 10 min).</summary>
    private bool NoteLateAdd(string text)
    {
        if (_unconfirmed.IsEmpty || Added().Match(text) is not { Success: true } m) return false;
        var name = m.Groups[1].Value;
        if (!_unconfirmed.TryRemove(name, out var at) || DateTimeOffset.UtcNow - at > TimeSpan.FromMinutes(10) || !Accepts(name)) return false;
        _managed.SetWhitelisted(name, true);
        _addedAt[name] = DateTimeOffset.UtcNow;
        _removeUnconfirmed.TryRemove(name, out _);
        _log.LogInformation("hive: the server confirmed /whitelist add {Name} late; it is on the list", name);
        return true;
    }

    [GeneratedRegex(@"Added (\S+?) to (?:the )?white-?list", RegexOptions.IgnoreCase)]
    private static partial Regex Added();
    [GeneratedRegex(@"Could not add (\S+?) to (?:the )?white-?list", RegexOptions.IgnoreCase)]
    private static partial Regex NotAdded();
    [GeneratedRegex(@"Removed (\S+?) from (?:the )?white-?list", RegexOptions.IgnoreCase)]
    private static partial Regex Removed();
    [GeneratedRegex(@"Could not remove (\S+?) from (?:the )?white-?list", RegexOptions.IgnoreCase)]
    private static partial Regex NotRemoved();
    [GeneratedRegex(@"(\S+?) is already (?:white-?listed|on the white-?list)", RegexOptions.IgnoreCase)]
    private static partial Regex Already();
    [GeneratedRegex(@"do not have permission|commands\.generic\.permission|^generic permission\b|unknown command", RegexOptions.IgnoreCase)]
    private static partial Regex Denied();
    /// <summary>Takes the account off the server's whitelist. One removal per account at a time (a second caller joins it); an add
    /// asked for meanwhile goes out after it (<see cref="AddAsync"/>).</summary>
    public Task<bool> RemoveAsync(string name, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        _addedAt.TryRemove(name, out _); // a re-add after this removal is never "just added"
        return Coalesced("remove", name, ct);
    }

    private readonly SemaphoreSlim _turn = new(1, 1);
    private int _queued;

    /// <summary>Whitelist lines waiting for their turn or going out now (a colony of 40 pawns logs in 10 at a time).</summary>
    public int Queued => Volatile.Read(ref _queued);

    /// <summary>About how long one queued line takes to go out (main's chat pacing, <c>Agent:Chat:LineDelayMs</c> 800 ms, plus slack):
    /// a launch waits this much longer for its add per line ahead of it (<see cref="HiveHierarchy.BeforeLaunchAsync"/>).</summary>
    public TimeSpan PerLine { get; set; } = TimeSpan.FromSeconds(1);

    private async Task<bool> RunAsync(string op, string name, CancellationToken ct)
    {
        name = (name ?? "").Trim();
        if (!Accepts(name)) { _log.LogWarning("hive: /whitelist {Op} {Name} refused: not a managed account", op, name); return false; }
        var line = $"/whitelist {op} {name}";
        Interlocked.Increment(ref _queued);
        TaskCompletionSource<bool>? confirm = null;
        bool sent;
        try
        {
            (sent, confirm) = await SendInTurnAsync(op, name, line, ct).ConfigureAwait(false);
            // An add counts once the server confirms it (when replies are fed): a gate that ate the line, a non-op main or a
            // typo'd name must not leave the account marked whitelisted (the clone would be kicked at login, forever).
            // A removal too: the reconcile counts only what the server took off its list.
            if (sent && confirm != null && !await ConfirmedAsync(confirm, line, ct).ConfigureAwait(false))
            {
                if (!confirm.Task.IsCompleted) // a late "Added" / "Removed" still counts
                    (op == "add" ? _unconfirmed : _removeUnconfirmed)[name] = DateTimeOffset.UtcNow;
                return false;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
            Forget(op, name, confirm);
        }
        if (sent)
        {
            _managed.SetWhitelisted(name, op == "add");
            if (op == "add") { _addedAt[name] = DateTimeOffset.UtcNow; _unconfirmed.TryRemove(name, out _); }
            else _addedAt.TryRemove(name, out _);
            _removeUnconfirmed.TryRemove(name, out _);
            _log.LogInformation("hive: {Line}", line);
        }
        return sent;
    }

    /// <summary>
    /// One whitelist line at a time: it is armed in the gate's scoped exception only when its turn comes, right before main sends it
    /// (paced like chat). Armed all at once, a burst of adds outlived the gate's 15 s arm in main's chat queue and was refused.
    /// → (sent, the server's answer to wait for).
    /// </summary>
    private async Task<(bool Sent, TaskCompletionSource<bool>? Confirm)> SendInTurnAsync(string op, string name, string line, CancellationToken ct)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_arm(line)) return (false, null);
            var confirm = Expect(op, name);
            try { return (await _send(line, ct).ConfigureAwait(false), confirm); }
            catch (OperationCanceledException) { Forget(op, name, confirm); throw; }
            catch (Exception ex) { _log.LogWarning(ex, "hive: {Line} failed", line); return (false, confirm); }
        }
        finally { _turn.Release(); }
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
    /// taken off the whitelist. Returns how many were removed. It never adds: an account it keeps (<paramref name="isRunning"/>:
    /// running, in the tree, claimed by an owner) keeps its whitelisting, and a confirmed removal clears the flag (no pass repeats it).
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
