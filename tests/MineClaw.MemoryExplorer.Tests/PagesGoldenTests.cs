using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Routines;
using MineClaw.Agent.Self;
using MineClaw.Data;
using MineClaw.MemoryExplorer.Services;
using MineClaw.MemoryExplorer.Tests.LiveHive;
using MineClaw.MemoryExplorer.ViewModels;
using MineClaw.World;
using MineClaw.World.Designs;
using Xunit.Abstractions;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>
/// Pins the Explorer's pages byte for byte (WP08 complexity refactor, checkpoint 4): the main window view model opened over the
/// fixture db in a headless session (open, the RAPTOR tree's detail pane / reveal / subtree export, search, the memory browser's
/// detail pane, the personality page), the Scripts and Designs pages over scripted sources, and every write flow of the edit
/// session and the shard import against a scripted admin target. Each dump is the pages' public state after each step (times,
/// temp paths and the user name masked), plus what the target and the dialogs were asked.
/// Set EXPLORER_GOLDEN_OUT to a directory to write the dumps out.
/// </summary>
[Collection("avalonia")]
public sealed partial class PagesGoldenTests(ITestOutputHelper output) : IAsyncLifetime
{
    private FixtureDb _fx = null!;

    public async Task InitializeAsync()
    {
        _fx = await FixtureDb.CreateAsync();
        Seed(_fx.Path);
    }

    public Task DisposeAsync() { _fx.Dispose(); return Task.CompletedTask; }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..20];

    private void Compare(List<(string Name, string Dump)> rows, string expected)
    {
        var outDir = Environment.GetEnvironmentVariable("EXPLORER_GOLDEN_OUT");
        var actual = new StringBuilder();
        foreach (var (n, dump) in rows)
        {
            if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, n + ".txt"), dump);
            actual.Append(n).Append(' ').Append(Hash(dump)).Append('\n');
        }
        var want = string.Join("\n", expected.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0)) + "\n";
        if (want != actual.ToString()) output.WriteLine(actual.ToString());
        Assert.Equal(want, actual.ToString());
    }

    // ------------------------------------------------------------------ fixture extras: claims, observations, the self-profile

    private static void Seed(string path)
    {
        using (var db = AgentDb.Open(path))
        {
            SelfSchema.Ensure(db);
            db.Exec("""
                INSERT INTO claims(id, claimant, text, norm, category, stakes, confidence, status, reason, created, updated, clone_id)
                    VALUES(1, 'PoofImaFox', 'the gen is at spawn', 'gen spawn', 'place', 'low', 0.8, 'verified', 'saw it', 1000, 2000, 'main');
                INSERT INTO claims(id, claimant, text, norm, category, stakes, confidence, status, created, updated, clone_id)
                    VALUES(2, 'kiwi', 'lava is safe', 'lava safe', 'fact', 'high', 0.1, 'refuted', 1000, 1500, 'main');
                INSERT INTO claim_evidence(claim_id, kind, ref, polarity, weight, note, at) VALUES(1, 'observation', 'obs:1', 1, 0.75, 'seen', 1100);
                INSERT INTO claim_evidence(claim_id, kind, ref, polarity, weight, note, at) VALUES(1, 'experiment', NULL, -1, 0.25, NULL, 1200);
                INSERT INTO claim_evidence(claim_id, kind, ref, polarity, weight, note, at) VALUES(1, 'seed', 'gd:x', 0, 0.5, 'n', 1300);
                INSERT INTO observations(id, kind, key, min_x, min_y, min_z, max_x, max_y, max_z, text, source, confidence, annotation, first_seen, last_seen, seen_count, removed)
                    VALUES(1, 'chest', '4,64,6', 4, 64, 6, 4, 64, 6, 'a chest by the gen', 'scan', 0.9, 'the old one', 1000, 3000, 4, NULL);
                INSERT INTO observations(id, kind, key, min_x, min_y, min_z, max_x, max_y, max_z, text, source, confidence, first_seen, last_seen, seen_count, removed)
                    VALUES(2, 'chest', '9,64,6', 9, 64, 6, 10, 64, 6, 'an empty double chest', 'scan', 0.5, 1000, 2000, 1, 2500);
                INSERT INTO observations(id, kind, key, min_x, min_y, min_z, max_x, max_y, max_z, text, source, confidence, first_seen, last_seen, seen_count)
                    VALUES(3, 'farm', 'wheat-1', 0, 63, 0, 5, 63, 5, 'a wheat farm', 'indexer', 0.6, 1000, 2000, 2);
                INSERT INTO container_items(obs_id, item_id, damage, name, count) VALUES(1, 4, 0, 'cobblestone', 64);
                INSERT INTO container_items(obs_id, item_id, damage, name, count) VALUES(1, 270, 12, 'wooden pickaxe', 1);
                INSERT INTO self_profile(id, section, player, text, strength, created, reinforced, reason, scope, observations)
                    VALUES(1, 'trait', NULL, 'patient', 0.8, 1000, 2000, 'waited a lot', 'main', 3);
                INSERT INTO self_profile(id, section, player, text, strength, created, reinforced, reason, scope, observations)
                    VALUES(2, 'like', 'PoofImaFox', 'fox''s farms', 1.4, 1000, 2000, NULL, 'main', 1);
                INSERT INTO self_profile(id, section, player, text, strength, created, reinforced, reason, scope, observations)
                    VALUES(3, 'quirks', NULL, 'counts cobble', -0.2, 1000, 2000, NULL, 'main', 2);
                INSERT INTO self_profile(id, section, player, text, strength, created, reinforced, reason, scope, observations)
                    VALUES(4, 'joke', 'kiwi', 'the lava thing', 0.5, 1000, 2000, 'funny', 'main', 5);
                INSERT INTO self_profile(id, section, player, text, strength, created, reinforced, reason, scope, observations)
                    VALUES(5, 'milestone', NULL, 'first gen', 0.9, 1000, 2000, NULL, 'hive', 1);
                INSERT INTO self_profile_history(at, op, section, entry_id, text, reason, source, episodes, snapshot, scope)
                    VALUES(1000, 'add', 'trait', 1, 'patient', 'waited', 'reflection', NULL, '{}', 'main');
                INSERT INTO self_profile_history(at, op, section, entry_id, text, reason, source, episodes, snapshot, scope)
                    VALUES(2000, 'reinforce', NULL, 1, NULL, NULL, 'episode', NULL, '{}', 'main');
                INSERT INTO self_acc(scope, family, key, dim, sum, n, updated) VALUES('main', 'mood', '', 'valence', 0.35, 4, 2000);
                INSERT INTO self_acc(scope, family, key, dim, sum, n, updated) VALUES('main', 'mood', '_', 'arousal', -0.5, 2, 2000);
                INSERT INTO self_acc(scope, family, key, dim, sum, n, updated) VALUES('main', 'mood', 'Fox', 'valence', 0.9, 1, 2000);
                INSERT INTO self_acc(scope, family, key, dim, sum, n, updated) VALUES('main', 'mood', '', 'trust', 1.5, 1, 2000);
                INSERT INTO self_abstractions(scope, layer, idx, text, leaves, built) VALUES('main', 1, 0, 'likes farms and patience', 3, 2000);
                INSERT INTO self_abstractions(scope, layer, idx, text, leaves, built) VALUES('main', 2, 0, 'a calm builder', 5, 2000);
                """);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    // ------------------------------------------------------------------ headless plumbing

    private static readonly HashSet<string> Skip =
    [
        "Shell", "Bank", "Graph", "Picture", "Spans", "Files", "FilesInfo", "DefaultPacks", "DefaultsInfo", "Folder", "DefaultsFolder",
        "Recent", "Pages", "Config", "Supervisor", "LiveHive", "TargetInfo", "SourceDetail", "SnapshotText", "Kinds", "Roots", "Children",
        "CurrentPage", "SelectedPage", "EmbeddingsStatus", "GroomSources", "TargetOptions", "SourceOptions", "ColorOptions", "DimensionOptions", "SelectedFile",
    ];

    /// <summary>The main window without its pages (each page has its own dump).</summary>
    private static readonly HashSet<string> MainSkip =
        [.. Skip, "Overview", "Tree", "Search", "Browser", "Personality", "Edit", "Shards", "Scripts", "Designs"];

    private string Mask(string s)
    {
        foreach (var d in new[] { _fx.Dir, _fx.Dir.Replace('\\', '/') }) s = s.Replace(d, "<fx>", StringComparison.OrdinalIgnoreCase);
        s = s.Replace("admin:" + Environment.UserName, "admin:<user>", StringComparison.Ordinal);
        s = DateLong().Replace(s, "<date>");
        s = DateShort().Replace(s, "<mdt>");
        s = Clock().Replace(s, "<hms>");
        return Millis().Replace(s, "<n> ms");
    }

    [GeneratedRegex(@"\d{4}-\d\d-\d\d[ T]\d\d:\d\d(:\d\d)?Z?")] private static partial Regex DateLong();
    [GeneratedRegex(@"\b\d\d-\d\d \d\d:\d\d(:\d\d)?\b")] private static partial Regex DateShort();
    [GeneratedRegex(@"\b\d\d:\d\d:\d\d\b")] private static partial Regex Clock();
    [GeneratedRegex(@"\b\d+ ms\b")] private static partial Regex Millis();

    private string D(object? o, int depth = 3) => Mask(StateDump.Of(o, depth, Skip));

    private static void Pump(Task t)
    {
        var sw = Stopwatch.StartNew();
        while (!t.IsCompleted)
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("the page never finished");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Dispatcher.UIThread.RunJobs();
        t.GetAwaiter().GetResult();
    }

    private static void Until(Func<bool> done)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("the page never settled");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Lets the fire-and-forget loads a property change started (and their dispatcher posts) land.</summary>
    private static void Settle(int ms = 150)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
    }

    private static MainWindowViewModel NewShell()
    {
        var vm = new MainWindowViewModel(new StartupOptions());
        typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.Config))!.SetValue(vm, FixtureDb.Config());
        return vm;
    }

    /// <summary>Waits for the live hive page's background texture load (a late post would hit the next test's headless app).</summary>
    private static void Close(MainWindowViewModel vm)
    {
        Until(() => vm.LiveHive.TextureStatus != "textures: loading…");
        vm.Dispose();
        Settle(50);
    }

    private static string Session(Func<string> body) => HeadlessUi.Run(body).GetAwaiter().GetResult();

    // ------------------------------------------------------------------ open, tree, search, browser, personality

    private string OpenDump(MainWindowViewModel vm, StringBuilder sb, string name)
    {
        sb.Append("== ").Append(name).Append('\n').Append(Mask(StateDump.Of(vm, 2, MainSkip))).Append('\n');
        return sb.ToString();
    }

    private string Main(MainWindowViewModel vm, out string tree, out string search, out string browser, out string personality)
    {
        var sb = new StringBuilder();
        Pump(vm.OpenAsync("   "));
        OpenDump(vm, sb, "blank path");
        Pump(vm.OpenAsync(Path.Combine(_fx.Dir, "missing.db")));
        OpenDump(vm, sb, "missing file");
        Pump(vm.OpenAsync(_fx.Path));
        Until(() => !vm.Browser.IsLoading && !vm.Personality.IsLoading && !vm.Scripts.IsLoading);
        Settle();
        OpenDump(vm, sb, "fixture");
        Pump(vm.ReloadAsync());
        Until(() => !vm.Browser.IsLoading && !vm.Personality.IsLoading);
        Settle();
        OpenDump(vm, sb, "reload");
        tree = Tree(vm);
        search = Search(vm);
        browser = Browser(vm);
        personality = Personality(vm);
        return sb.ToString();
    }

    private static readonly string[] TreeProps =
        ["DetailTitle", "DetailText", "DetailMeta", "DetailDoc", "HasSelection", "GraphSelected", "LeavesHeader", "Breadcrumb", "DetailChildren", "DetailParents"];

    private string TreeState(RaptorTreeViewModel t)
    {
        var sb = new StringBuilder();
        foreach (var name in TreeProps) sb.Append(name).Append(" = ").Append(D(typeof(RaptorTreeViewModel).GetProperty(name)!.GetValue(t), 2)).Append('\n');
        sb.Append("Selected = ").Append(t.Selected?.Index.ToString(CultureInfo.InvariantCulture) ?? "null").Append(" focus ").Append(t.GraphFocus)
            .Append(" graph ").Append(t.GraphMode).Append(" crumbs ").Append(string.Join(" › ", t.FocusCrumbs.Select(c => c.Label))).Append('\n');
        void Walk(IEnumerable<TreeNodeViewModel> level, string indent)
        {
            foreach (var n in level.Where(n => !n.IsPlaceholder))
            {
                if (!n.IsExpanded) continue;
                sb.Append(indent).Append('#').Append(n.Index).Append(" open ").Append(n.Children.Count).Append('\n');
                Walk(n.Children, indent + "  ");
            }
        }
        Walk(t.Roots, "  ");
        return Mask(sb.ToString());
    }

    private readonly StringBuilder _dialogs = new();

    private string Tree(MainWindowViewModel vm)
    {
        var t = vm.Tree;
        var sb = new StringBuilder();
        var indexes = t.Graph!.Nodes.Select(n => n.Index).OrderBy(i => i).ToList();
        foreach (var i in indexes.Append(99_999))
        {
            t.ShowNode(i);
            sb.Append("== show ").Append(i).Append('\n').Append(TreeState(t));
        }
        int leaf = indexes.First(i => t.Graph.TryGetSlot(i, out var s) && t.Graph.Nodes[s].IsLeaf);
        int top = t.Graph.TopLevel.Select(s => t.Graph.Nodes[s].Index).Max();
        int mid = indexes.FirstOrDefault(i => t.Graph.TryGetSlot(i, out var s) && !t.Graph.Nodes[s].IsLeaf && t.Graph.Nodes[s].Layer == 1);
        foreach (var (name, i, graph, focus) in new[] { ("leaf", leaf, false, -1), ("top", top, false, -1), ("mid", mid, false, -1), ("missing", 99_999, false, -1),
                     ("leaf in graph", leaf, true, -1), ("ancestor of focus", top, true, mid), ("focus itself", mid, true, mid) })
        {
            t.CollapseAllCommand.Execute(null);
            t.GraphMode = graph;
            t.GraphFocus = focus;
            t.Reveal(i);
            sb.Append("== reveal ").Append(name).Append('\n').Append(TreeState(t));
        }
        vm.Shards.PickSavePath = _ => Task.FromResult<string?>(null);
        vm.Shards.ShowExport = r =>
        {
            _dialogs.Append("export dialog: ").Append(r.Name).Append(" | ").Append(r.SubtreeLabel).Append(" | ").Append(r.Selection.Mode).Append(' ')
                .Append(r.Selection.Node).Append(' ').Append(r.Selection.LeafRefs?.Count).Append(" | ").Append(r.ActivePack).Append(" | ").Append(r.Source).Append('\n');
            return Task.FromResult<string?>(null);
        };
        t.GraphSelected = -1;
        foreach (object? p in new object?[] { null, 99_999, leaf, mid, top })
        {
            Pump(t.ExportSubtreeCommand.ExecuteAsync(p));
            sb.Append("== export ").Append(p ?? "selected").Append(": ").Append(vm.Status).Append('\n');
        }
        t.ShowNode(mid);
        Pump(t.ExportSubtreeCommand.ExecuteAsync(null));
        vm.Shards.ShowExport = r => Task.FromResult<string?>(Path.Combine(_fx.Dir, "picked.mcshard"));
        Pump(t.ExportSubtreeCommand.ExecuteAsync(top));
        Settle();
        sb.Append("== exported: ").Append(vm.Status).Append(" | ").Append(vm.Shards.LastResult).Append('\n').Append(_dialogs);
        _dialogs.Clear();
        return Mask(sb.ToString());
    }

    private string Search(MainWindowViewModel vm)
    {
        var s = vm.Search;
        var sb = new StringBuilder();
        void Dump(string name) => sb.Append("== ").Append(name).Append('\n').Append(D(s, 3)).Append("\nhits ").Append(D(vm.Tree.GraphHits, 2)).Append(' ')
            .Append(vm.Tree.GraphHitsInfo).Append('\n');
        vm.Navigate("search");
        Settle();
        foreach (var (q, kind, dim) in new[] { ("", "all kinds", "any dimension"), ("cobblestone generator", "all kinds", "any dimension"),
                     ("wheat farm", "episode", "any dimension"), ("portal obsidian", "all kinds", "nether"), ("cobble", "knowledge", "overworld"),
                     ("zzzz qqqq", "all kinds", "any dimension") })
        {
            s.Query = q;
            s.Kind = kind;
            s.Dimension = dim;
            Until(() => !s.IsLoading);
            Pump(s.RunSearchAsync());
            Until(() => !s.IsLoading);
            Dump($"{q} / {kind} / {dim}");
        }
        return sb.ToString();
    }

    private string Browser(MainWindowViewModel vm)
    {
        var b = vm.Browser;
        var sb = new StringBuilder();
        foreach (var k in b.Kinds)
        {
            b.SelectedKind = k;
            Until(() => !b.IsLoading);
            sb.Append("== kind ").Append(k.Info.Key).Append(' ').Append(b.RowInfo).Append(" | ").Append(b.EmptyText).Append('\n');
            foreach (var row in b.Rows.Take(8).ToList())
            {
                b.Selected = row;
                sb.Append("-- ").Append(row.Kind).Append(' ').Append(row.Id).Append(' ').Append(b.ExtraHeader).Append('\n')
                    .Append(D(b.Props, 2)).Append('\n').Append(D(b.Extra, 2)).Append('\n');
            }
            b.Selected = null;
            sb.Append("-- none ").Append(b.Props.Count).Append(b.Extra.Count).Append(b.ExtraHeader).Append('\n');
        }
        return Mask(sb.ToString());
    }

    private string Personality(MainWindowViewModel vm)
    {
        var p = vm.Personality;
        var sb = new StringBuilder();
        sb.Append("== loaded\n").Append(D(p, 4)).Append('\n');
        foreach (var scope in new[] { "hive", "nobody", "main" })
        {
            p.Scope = scope;
            Until(() => !p.IsLoading);
            sb.Append("== ").Append(scope).Append('\n').Append(D(p, 4)).Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void Main_window_tree_search_browser_and_personality_match_the_golden_hashes()
    {
        string tree = "", search = "", browser = "", personality = "";
        var open = Session(() =>
        {
            var vm = NewShell();
            try { return Main(vm, out tree, out search, out browser, out personality); }
            finally { Close(vm); }
        });
        Compare([("open", open), ("tree", tree), ("search", search), ("browser", browser), ("personality", personality)], MainGolden);
    }

    private const string MainGolden = """
        open 99E0B97CC3FB30C75B8C
        tree FDF0DDF86DE1C9017BE2
        search 7073F4791947843E7180
        browser CBEEEE46A8745F585231
        personality 1EA6435ECFD506429276
        """;
}
