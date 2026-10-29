using MineClaw.Hive.Hierarchy;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 2026-10-29 (1.12.2, main not op yet): the start's reconcile sent four "/whitelist remove maid_chan_N", each answered
/// "commands.generic.permission", and still logged "removed 4 stale clone accounts". A removal counts only once the server
/// confirms it ("Removed X from the whitelist"; "Could not remove X" = not on the list, so off it already); the rest stay marked
/// whitelisted and the reconcile tries them again later.
/// </summary>
public class WhitelistReconcileTests
{
    private static (HierarchyKit K, WhitelistManager W, List<string> Sent) Rig(Func<string, string?> reply)
    {
        var k = new HierarchyKit();
        var sent = new List<string>();
        WhitelistManager? w = null;
        w = new WhitelistManager(k.Managed, k.Cfg, HierarchyKit.Main, k.Gate.ArmManagedWhitelist, (line, ct) =>
        {
            bool ok = k.Gate.AllowOutgoing(line);
            if (!ok) return Task.FromResult(false);
            lock (sent) sent.Add(line);
            // The server's answer comes back on main's chat a moment later.
            if (reply(line) is { } answer) _ = Task.Run(async () => { await Task.Delay(20); w!.NoteServerLine(answer); });
            return Task.FromResult(true);
        })
        { ConfirmTimeout = TimeSpan.FromSeconds(2), RetryDelay = TimeSpan.FromMilliseconds(200) };
        k.Gate.ManagedAccount = w.Accepts;
        w.NoteServerLine(""); // main's chat feed is wired: confirmations are expected
        foreach (var n in new[] { "maid_chan_1337", "maid_chan_420", "maid_chan_67", "maid_chan_69" })
            k.Managed.Upsert(n, a => a with { Whitelisted = true, Active = false });
        return (k, w, sent);
    }

    [Fact]
    public async Task Permission_errors_count_nothing_and_the_retry_removes_them_once_main_is_op()
    {
        bool op = false;
        var (k, w, sent) = Rig(line => op
            ? "Removed " + line.Split(' ')[2] + " from the whitelist"
            : "You do not have permission to use this command");
        int removed = await w.ReconcileAsync(_ => false);
        Assert.Equal(0, removed);
        Assert.Equal(4, sent.Count);
        Assert.All(k.Managed.All().Where(a => a.Name.StartsWith("maid_chan_")), a => Assert.True(a.Whitelisted, a.Name));

        // Main gets op; the scheduled retry clears all four.
        op = true;
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && k.Managed.All().Any(a => a.Whitelisted)) await Task.Delay(20);
        Assert.All(k.Managed.All(), a => Assert.False(a.Whitelisted, a.Name));
    }

    [Fact]
    public async Task Confirmed_removals_count_and_not_on_the_list_clears_the_flag()
    {
        var (k, w, _) = Rig(line =>
        {
            var n = line.Split(' ')[2];
            return n == "maid_chan_69" ? $"Could not remove {n} from the whitelist" : $"Removed {n} from the whitelist";
        });
        Assert.Equal(4, await w.ReconcileAsync(_ => false));
        Assert.All(k.Managed.All(), a => Assert.False(a.Whitelisted, a.Name));
    }
}
