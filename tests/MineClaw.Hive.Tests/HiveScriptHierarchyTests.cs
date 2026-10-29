using MineClaw.Agent.Events;
using MineClaw.Hive.Hierarchy;
using Xunit.Abstractions;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Hive scripts (Fox, 10-29: "one script to control all the bodies in the Hive, like a Hive script"): <c>hive spawn kind=script
/// bodies=N script=X</c> allocates N subhive bodies in main's process exactly like <c>bodies=N</c> (whitelisted before login, the
/// caps), starts ONE coordinator over them with no brain, and when it ends — or <c>release X</c> — its bodies deposit, log out and
/// leave the whitelist once each; main's brain hears the one result line.
/// </summary>
public class HiveScriptHierarchyTests(ITestOutputHelper output)
{
    /// <summary>Main's in-process body host as the hierarchy sees it (logs in after the whitelist add; deposits before logout).</summary>
    private sealed class FakeLocalHost(HierarchyKit kit) : ISubhiveHost
    {
        public List<(string Account, string? Task, string? Script)> Spawned { get; } = [];

        public string Spawn(string owner, string account, string? task, string? script)
        {
            lock (Spawned) Spawned.Add((account, task, script));
            _ = Task.Run(async () =>
            {
                await kit.H.BeforeLaunchAsync(account);
                kit.Note("login " + account);
            });
            return $"ok: {HiveNode.ShortOf(account)} spawning";
        }

        public string Release(string account)
        {
            _ = Task.Run(async () =>
            {
                kit.Note("deposit " + account);
                await Task.Delay(20);
                kit.Note("logout " + account);
                kit.H.Released(account, "released");
            });
            return "ok: logging out";
        }
    }

    /// <summary>The coordinator host: records what it was asked to run; the test ends it.</summary>
    private sealed class FakeScripts : IHiveScriptHost
    {
        public List<(string Id, string Script, string? Args, Func<IReadOnlyCollection<string>> Members, Action<bool, string> Ended)> Started { get; } = [];
        public List<string> Stopped { get; } = [];
        public string? Refuse { get; set; }

        public (string? Name, string? Error) Prepare(string? script, string? code) =>
            Refuse != null ? (null, Refuse) : (script ?? "hive_" + code!.Length, null);

        public string Start(string id, string script, string? args, Func<IReadOnlyCollection<string>> members, Action<bool, string> ended)
        {
            lock (Started) Started.Add((id, script, args, members, ended));
            return "ok: running";
        }

        public bool Stop(string id, string reason)
        {
            lock (Stopped) Stopped.Add(id);
            var s = Started.Single(x => x.Id == id);
            _ = Task.Run(() => s.Ended(false, "cancelled: " + reason)); // a stopped coordinator still reports its end
            return true;
        }

        public string? Status(string id) => null;
    }

    private static (HierarchyKit Kit, FakeLocalHost Host, FakeScripts Scripts, List<GameEvent> Notices) Kit()
    {
        var k = new HierarchyKit();
        var host = new FakeLocalHost(k);
        var scripts = new FakeScripts();
        var notices = new List<GameEvent>();
        k.H.Subhives = host;
        k.H.ScriptHives = scripts;
        k.H.Notice = e => { lock (notices) notices.Add(e); };
        return (k, host, scripts, notices);
    }

    [Fact]
    public async Task A_hive_script_gets_N_bodies_one_coordinator_and_releases_them_when_it_ends()
    {
        var (k, host, scripts, notices) = Kit();
        var r = k.H.Spawn(new SpawnRequest(Kind: "script", Script: "deforest", Args: """{"r":64}""", Bodies: 3));
        output.WriteLine(r);
        Assert.StartsWith("ok: hive script deforest: ONE script drives 3 bodies (101-103), no brain", r);
        Assert.Contains("none in game yet", r);
        Assert.Empty(k.Bodies.Spawns); // no clone process (no extra account, no brain)
        Assert.All(host.Spawned, s => Assert.True(s.Task == null && s.Script == null)); // no per-body job: the coordinator hands out lanes
        var (id, script, args, members, ended) = Assert.Single(scripts.Started);
        Assert.Equal(("deforest", "deforest", """{"r":64}"""), (id, script, args));
        Assert.Equal(["maid_chan_101", "maid_chan_102", "maid_chan_103"], members().Order());
        Assert.All(k.H.Tree.All(), n => Assert.Equal((CloneKind.Subhive, HiveTree.Root, "deforest"), (n.Kind, n.Owner, n.Crew)));
        Assert.StartsWith("hive script deforest: 3 bodies 101-103", k.H.Tree.Render());
        await Wait.Until(() => k.LogCopy().Count(l => l.StartsWith("login")) == 3, 10, "3 logins");
        var log = k.LogCopy();
        foreach (var n in new[] { "101", "102", "103" })
            Assert.True(log.IndexOf($"/whitelist add maid_chan_{n}") is >= 0 and var i && i < log.IndexOf($"login maid_chan_{n}"), n + " whitelisted first");

        // The script ends: one line to main's brain, every body deposits, logs out and leaves the whitelist.
        ended(true, "deforest: 3/3 bodies done (101 6, 102 5, 103 7)");
        await Wait.Until(() => k.H.Tree.Count == 0, 10, "released");
        await Wait.Until(() => k.LogCopy().Count(l => l.StartsWith("/whitelist remove")) == 3, 10);
        log = k.LogCopy();
        output.WriteLine(string.Join("\n", log));
        foreach (var n in new[] { "101", "102", "103" })
        {
            Assert.Equal(1, log.Count(l => l == $"deposit maid_chan_{n}"));
            Assert.True(log.IndexOf($"deposit maid_chan_{n}") < log.IndexOf($"logout maid_chan_{n}"), n + " deposits before logout");
            Assert.True(log.IndexOf($"logout maid_chan_{n}") < log.IndexOf($"/whitelist remove maid_chan_{n}"), n + " off the whitelist after logout");
        }
        var note = Assert.Single(notices);
        Assert.StartsWith("[hive] hive script deforest done: deforest: 3/3 bodies done", note.Text);
        Assert.True(note.MustWake);
        Assert.Empty(k.H.HiveScripts);
        Assert.All(k.Managed.All(), a => Assert.False(a.Active || a.Whitelisted));
    }

    [Fact]
    public async Task Release_by_its_name_stops_the_coordinator_and_each_body_leaves_once()
    {
        var (k, _, scripts, notices) = Kit();
        // script + bodies with no kind is a hive script too; a second one of the same script gets its own id.
        Assert.StartsWith("ok: hive script farm:", k.H.Spawn(new SpawnRequest(Script: "farm", Bodies: 2)));
        Assert.StartsWith("ok: hive script farm#2:", k.H.Spawn(new SpawnRequest(Script: "farm", Bodies: 1)));
        await Wait.Until(() => k.LogCopy().Count(l => l.StartsWith("login")) == 3, 10, "3 logins");
        var r = k.H.Release("farm");
        Assert.Equal("ok: stopping hive script farm; its 2 bodies deposit and log out", r);
        Assert.Equal(["farm"], scripts.Stopped);
        await Wait.Until(() => k.H.Tree.Count == 1, 10, "farm's bodies gone");
        Assert.Equal("farm#2", k.H.Tree.All().Single().Crew); // the other hive script keeps its body
        await Wait.Until(() => notices.Count == 1, 5);
        Assert.Contains("hive script farm failed: cancelled", notices[0].Text);
        // release all ends the rest the same way; nothing is released twice.
        Assert.StartsWith("ok: releasing", k.H.Release("all"));
        await Wait.Until(() => k.H.Tree.Count == 0, 10, "all released");
        await Wait.Until(() => notices.Count == 2, 5);
        var log = k.LogCopy();
        foreach (var n in new[] { "101", "102", "103" }) Assert.Equal(1, log.Count(l => l == $"deposit maid_chan_{n}"));
        Assert.Equal(["farm", "farm#2"], scripts.Stopped);
    }

    [Fact]
    public void Hive_scripts_need_main_a_body_host_and_a_saved_script()
    {
        var (k, host, scripts, _) = Kit();
        scripts.Refuse = "no saved script 'nope'";
        Assert.Equal("error: no saved script 'nope'", k.H.Spawn(new SpawnRequest(Kind: "script", Script: "nope", Bodies: 2)));
        Assert.Empty(host.Spawned);
        Assert.Equal(0, k.H.Tree.Count); // nothing allocated for a script that can't run

        // A hive clone runs a hive script over its own bodies (its process hosts them), with script{name}.
        k.H.Tree.Put(new HiveNode("maid_chan_150", CloneKind.Hive, BrainKind.DeepSeek, HiveTree.Root) { State = "running" });
        scripts.Refuse = null;
        Assert.StartsWith("no: run the hive script over your own bodies: script{name}", k.H.Spawn(new SpawnRequest(Kind: "script", Script: "x", Bodies: 2), "maid_chan_150"));

        var off = new HierarchyKit();
        off.H.Subhives = new FakeLocalHost(off);
        Assert.StartsWith("no: hive scripts need subhive bodies here", off.H.Spawn(new SpawnRequest(Kind: "script", Script: "x", Bodies: 2)));
        // One body without bodies= stays the old script-only clone.
        Assert.StartsWith("ok", off.H.Spawn(new SpawnRequest(Kind: "script", Script: "x")));
        Assert.Single(off.Bodies.Spawns);
    }

    [Fact]
    public void The_kind_hint_teaches_the_hive_script_within_its_line()
    {
        Assert.Contains("script=X bodies=N: X alone drives N", HiveTools.KindHint);
        Assert.True(HiveTools.KindHint.Length <= 220, HiveTools.KindHint.Length.ToString());
    }
}
