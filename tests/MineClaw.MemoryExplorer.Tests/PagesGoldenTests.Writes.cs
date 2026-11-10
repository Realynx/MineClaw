using System.Reflection;
using System.Text;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using MineClaw.MemoryExplorer.Services;
using MineClaw.MemoryExplorer.ViewModels;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>The edit session (audit, edits, deletes, rebuilds, compaction) and the shard import against a scripted target.</summary>
public sealed partial class PagesGoldenTests
{
    /// <summary>An admin target that logs every call and answers from its scripted fields.</summary>
    private sealed class FakeTarget(StringBuilder log, string backup) : IAdminTarget
    {
        public string Name => "fake";
        public string Describe => "fake: scripted";
        public bool BackupFails;
        public Queue<object> Applies = new();
        public RebuildEstimate Estimate = new(0, 0, 0, 0, 0m, "m", "nothing dirty");
        public object Rebuild = new RebuildStatus("done", null, null);
        public TaskCompletionSource? Gate;
        public List<RebuildStatus> Progress = [];
        public object AuditAnswer = new List<AuditEntry>();
        public object Import = new ShardImportResult { Ok = true, Message = "imported" };
        public RebuildStatus? ImportRebuild;
        public object Compact = new ActivityCompactionReport(true, true, 1, 1, 1, 1, 0, 0, 0, 0, 0, "nothing to compact", []);

        private static T Answer<T>(object o) => o is Exception ex ? throw ex : (T)o;

        public Task<string> BackupAsync(CancellationToken ct)
        {
            log.Append("backup\n");
            return BackupFails ? throw new IOException("disk full") : Task.FromResult(backup);
        }

        public Task<MemoryEditResult> ApplyAsync(MemoryEdit edit, CancellationToken ct)
        {
            log.Append("apply ").Append(edit.Op).Append(' ').Append(edit.Kind).Append(' ').Append(edit.Id ?? "null").Append(' ')
                .Append(string.Join(",", edit.Fields.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + (kv.Value ?? "null"))))
                .Append(' ').Append(edit.Who).Append(' ').Append(edit.Reason ?? "null").Append('\n');
            return Task.FromResult(Answer<MemoryEditResult>(Applies.Dequeue()));
        }

        public Task<RebuildEstimate> EstimateAsync(RebuildRequest r, CancellationToken ct)
        {
            log.Append("estimate ").Append(r).Append('\n');
            return Task.FromResult(Estimate);
        }

        public async Task<RebuildStatus> RebuildAsync(RebuildRequest r, IProgress<RebuildStatus> progress, CancellationToken ct)
        {
            log.Append("rebuild ").Append(r).Append('\n');
            foreach (var p in Progress) progress.Report(p);
            if (Gate is { } g) await g.Task;
            return Answer<RebuildStatus>(Rebuild);
        }

        public Task<IReadOnlyList<AuditEntry>> AuditAsync(int max, CancellationToken ct)
        {
            log.Append("audit ").Append(max).Append('\n');
            return Task.FromResult(Answer<IReadOnlyList<AuditEntry>>(AuditAnswer));
        }

        public Task<ShardDiff> PreviewShardAsync(string shardPath, ShardImportOptions options, CancellationToken ct) =>
            Task.FromResult(new ShardDiff { Shard = Path.GetFileName(shardPath) });

        public async Task<ShardImportResult> ImportShardAsync(string shardPath, ShardImportOptions options, CancellationToken ct)
        {
            log.Append("import ").Append(Path.GetFileName(shardPath)).Append(' ').Append(options).Append('\n');
            if (Gate is { } g) await g.Task;
            return Answer<ShardImportResult>(Import);
        }

        public Task<RebuildStatus?> RunImportRebuildAsync(ShardImportResult result, IProgress<RebuildStatus> progress, CancellationToken ct)
        {
            log.Append("import rebuild ").Append(result.Rebuild.Count).Append('\n');
            foreach (var p in Progress) progress.Report(p);
            return Task.FromResult(ImportRebuild);
        }

        public Task<ActivityCompactionReport> CompactActivityAsync(bool dryRun, string who, CancellationToken ct)
        {
            log.Append("compact ").Append(dryRun).Append(' ').Append(who).Append('\n');
            return Task.FromResult(Answer<ActivityCompactionReport>(Compact));
        }

        public void Dispose() => log.Append("dispose\n");
    }

    private static readonly FieldInfo SessionF = typeof(EditSessionViewModel).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo SessionKeyF = typeof(EditSessionViewModel).GetField("_sessionKey", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Makes <paramref name="t"/> the edit session's open target (what its first use would have opened).</summary>
    private static FakeTarget Inject(MainWindowViewModel vm, FakeTarget t)
    {
        var e = vm.Edit;
        SessionF.SetValue(e, t);
        SessionKeyF.SetValue(e, e.IsLive ? "live:" + vm.Supervisor.BaseUrl : "local:" + vm.Bank?.Path);
        return t;
    }

    private static readonly string[] EditProps =
        ["EditMode", "BackupInfo", "LastResult", "RebuildRunning", "RebuildPercent", "RebuildStage", "RebuildSummary", "AuditInfo", "CompactInfo", "CompactRunning", "Audit", "Diffs"];

    private string EditState(MainWindowViewModel vm, StringBuilder log)
    {
        var sb = new StringBuilder();
        foreach (var n in EditProps) sb.Append(n).Append(" = ").Append(StateDump.Of(typeof(EditSessionViewModel).GetProperty(n)!.GetValue(vm.Edit), 3, Skip)).Append('\n');
        sb.Append("Status = ").Append(vm.Status).Append(" | ").Append(vm.ModeBadge).Append(" | page ").Append(vm.SelectedPage.Key).Append('\n');
        sb.Append(log).Append(_dialogs);
        log.Clear();
        _dialogs.Clear();
        return Mask(sb.ToString());
    }

    private Queue<bool> _answers = new();

    private void Dialogs(MainWindowViewModel vm)
    {
        vm.Confirm = (title, message) =>
        {
            var yes = _answers.Count > 0 && _answers.Dequeue();
            _dialogs.Append("confirm ").Append(title).Append(" | ").Append(message.Replace("\n", "\\n")).Append(" => ").Append(yes).Append('\n');
            return Task.FromResult(yes);
        };
    }

    private static readonly MemoryRow FactRow = new()
    {
        Kind = "facts", Id = "1", Name = "cobble gen", Text = "Fox likes the gen", Type = "note", Dimension = 0, Confidence = 0.5,
        Props = [new("subject", "PoofimaFox"), new("x", "1")],
    };

    private static readonly MemoryRow PlaceRow = new()
    {
        Kind = "places", Id = "spawn", Name = "spawn", Text = "main island", Dimension = -1, Props = [new("x", "4"), new("y", "64"), new("z", "6"), new("note", "main island")],
    };

    private string EditSession(MainWindowViewModel vm)
    {
        var e = vm.Edit;
        var sb = new StringBuilder();
        var log = new StringBuilder();
        void Dump(string name) => sb.Append("== ").Append(name).Append('\n').Append(EditState(vm, log));
        void Run(Func<Task> a) { Pump(a()); Settle(30); }
        Dialogs(vm);
        vm.Navigate("audit");
        Settle();

        // audit: local without and with a trail, live off and on
        Run(e.RefreshAuditAsync);
        Dump("local audit, no file");
        var audit = new AdminAudit(LocalAdminTarget.AuditPathFor(_fx.Path));
        audit.Write(new AuditEntry(S0, "admin:x", "local:" + _fx.Path, "update", "fact", "1", "{\"a\":1}", "{\"a\":2}", "typo", true, "updated"));
        audit.Write(new AuditEntry(S0.AddMinutes(1), "admin:y", "local:" + _fx.Path, "delete", "place", null, null, null, null, false, "refused"));
        audit.Write(new AuditEntry(S0.AddMinutes(2), "admin:y", "live:http://bot", "import", "shard", "fox pack", null, null, null, true, "imported 3"));
        Run(e.RefreshAuditAsync);
        Dump("local audit");
        e.Target = "Live alice";
        Run(e.RefreshAuditAsync);
        Dump("live audit, edit mode off");
        var t = Inject(vm, new FakeTarget(log, Path.Combine(_fx.Dir, "backups", "fixture-1.db"))
        {
            AuditAnswer = new List<AuditEntry>
            {
                new(S0, "admin:z", "live:x", "rebuild", "node", "7", null, null, null, true, "rebuilt"),
                new(S0.AddHours(1), "admin:z", "live:x", "add", "fact", "9", null, "{}", "r", true, "added"),
            },
        });
        e.EditMode = true;
        Settle();
        Dump("live edit mode on");
        t.AuditAnswer = new InvalidOperationException("supervisor down");
        Run(e.RefreshAuditAsync);
        Dump("live audit fails");
        t.AuditAnswer = new List<AuditEntry>();

        // edits
        Run(() => e.BeginEditAsync("fact", FactRow));
        Dump("edit without an editor");
        var edits = new Queue<Dictionary<string, string?>?>();
        e.ShowEditor = r =>
        {
            _dialogs.Append("editor ").Append(r.Title).Append(" | ").Append(r.Kind).Append(' ').Append(r.Op).Append(" | ").Append(string.Join(",", r.Fields.Select(f => f.Name)))
                .Append(" | ").Append(string.Join(",", r.Values.Select(kv => kv.Key + "=" + (kv.Value ?? "null")))).Append(" | ").Append(r.Note).Append('\n');
            return Task.FromResult(edits.Count > 0 ? edits.Dequeue() : null);
        };
        Run(() => e.BeginAddAsync("observation"));
        Dump("add an observation (unsupported)");
        Run(() => e.BeginAddAsync("bogus"));
        Dump("add a bogus kind");
        Run(() => e.BeginEditAsync("fact", FactRow));
        Dump("editor cancelled");
        edits.Enqueue(new Dictionary<string, string?> { ["subject"] = "cobble gen", ["text"] = "Fox likes the gen" });
        Run(() => e.BeginEditAsync("fact", FactRow));
        Dump("nothing changed");
        edits.Enqueue(new Dictionary<string, string?> { ["subject"] = "kiwi", ["text"] = "Fox likes the gen", ["__reason"] = "wrong player" });
        t.Applies.Enqueue(new MemoryEditResult(true, "updated", "1", Reembedded: true));
        Run(() => e.BeginEditAsync("fact", FactRow));
        Dump("update ok");
        edits.Enqueue(new Dictionary<string, string?> { ["name"] = "nether hub", ["x"] = "1", ["y"] = "", ["z"] = null });
        t.Applies.Enqueue(new MemoryEditResult(false, "no such place"));
        Run(() => e.BeginAddAsync("place"));
        Dump("add refused");
        edits.Enqueue(new Dictionary<string, string?> { ["x"] = "5" });
        t.Applies.Enqueue(new InvalidOperationException("locked"));
        Run(() => e.BeginEditAsync("place", PlaceRow));
        Dump("update throws");
        Run(() => e.DeleteAsync("place", PlaceRow));
        Dump("delete declined");
        _answers.Enqueue(true);
        t.Applies.Enqueue(new MemoryEditResult(true, "deleted", "spawn"));
        Run(() => e.DeleteAsync("place", PlaceRow));
        Dump("delete ok");

        // rebuilds
        Run(() => e.StartRebuildAsync(3));
        Dump("rebuild, nothing to do");
        t.Estimate = new RebuildEstimate(4, 12_000, 800, 3000, 0.0123m, "claude-haiku-5-5", "4 dirty summaries");
        Run(() => e.StartKindRebuildAsync("episodes"));
        Dump("rebuild declined");
        _answers.Enqueue(true);
        t.Progress = [new RebuildStatus("running", new RebuildProgress("cluster", 1, 4), null), new RebuildStatus("running", null, null),
            new RebuildStatus("running", new RebuildProgress("summarize", 3, 4, "L1"), null), new RebuildStatus("running", new RebuildProgress("embed", 0, 0), null)];
        t.Rebuild = new RebuildStatus("done", null, new RebuildResult(true, "4 summaries", [new SummaryDiff(5, 1, "old", "new"), new SummaryDiff(9, 2, null, "fresh"),
            new SummaryDiff(2, 1, "gone", null), new SummaryDiff(4, 2, "a", "b")], 0.0101m, TimeSpan.FromSeconds(12.34)));
        Run(() => e.StartKindRebuildAsync("recipes"));
        Dump("rebuild done (live)");
        _answers.Enqueue(true);
        t.Rebuild = new RebuildStatus("failed", null, null, "budget");
        Run(() => e.StartKindRebuildAsync("facts"));
        Dump("rebuild failed with an error");
        _answers.Enqueue(true);
        t.Rebuild = new RebuildStatus("cancelled", null, null);
        Run(() => e.StartKindRebuildAsync("weird"));
        Dump("rebuild cancelled state");
        _answers.Enqueue(true);
        t.Rebuild = new OperationCanceledException();
        Run(() => e.StartRebuildAsync(5));
        Dump("rebuild cancelled");
        _answers.Enqueue(true);
        t.Rebuild = new InvalidOperationException("boom");
        t.Progress = [];
        Run(() => e.StartRebuildAsync(5));
        Dump("rebuild throws");
        _answers.Enqueue(true);
        t.Gate = new TaskCompletionSource();
        t.Rebuild = new RebuildStatus("done", null, new RebuildResult(false, "half", [], 0m, TimeSpan.Zero));
        var running = e.StartRebuildAsync(6);
        Settle(30);
        Run(() => e.StartRebuildAsync(7));
        Dump("rebuild while running");
        t.Gate.SetResult();
        t.Gate = null;
        Pump(running);
        Settle(30);
        Dump("rebuild not ok");

        // compaction
        t.Compact = new ActivityCompactionReport(true, true, 10, 4, 8, 2, 8, 6, 2, 2, 3, "would coalesce 8 into 2", ["a 1-5", "b 2-9", "c", "d"]);
        Run(() => e.PreviewCompactionCommand.ExecuteAsync(null));
        Dump("compaction preview (live)");
        Run(() => e.RunCompactionCommand.ExecuteAsync(null));
        Dump("compaction declined");
        _answers.Enqueue(true);
        t.Compact = new ActivityCompactionReport(true, false, 10, 4, 8, 2, 8, 6, 2, 2, 3, "coalesced 8 into 2", []);
        Run(() => e.RunCompactionCommand.ExecuteAsync(null));
        Dump("compaction run (live)");
        t.Compact = new InvalidOperationException("no activity table");
        Run(() => e.PreviewCompactionCommand.ExecuteAsync(null));
        Dump("compaction throws");

        // the local file: refused rebuild problems, backups, a write that reloads the bank
        e.Target = "Local file";
        Settle();
        Dump("back to the local file");
        t = Inject(vm, new FakeTarget(log, Path.Combine(_fx.Dir, "backups", "fixture-1.db")) { BackupFails = true });
        _answers.Enqueue(true);
        t.Compact = new ActivityCompactionReport(true, false, 10, 4, 8, 2, 8, 6, 2, 2, 3, "coalesced", ["x"]);
        Run(() => e.RunCompactionCommand.ExecuteAsync(null));
        Dump("local compaction, backup fails");
        t.Estimate = new RebuildEstimate(2, 100, 10, 10, 0m, "m", "two");
        _answers.Enqueue(true);
        Run(() => e.StartRebuildAsync(3));
        Dump("local rebuild, backup fails");
        t.BackupFails = false;
        _answers.Enqueue(true);
        Run(() => e.RunCompactionCommand.ExecuteAsync(null));
        Until(() => !vm.IsBusy && !vm.Browser.IsLoading && !vm.Personality.IsLoading);
        Settle();
        Dump("local compaction ok, reloaded");
        edits.Enqueue(new Dictionary<string, string?> { ["subject"] = "world" });
        t.Applies.Enqueue(new MemoryEditResult(true, "updated", "2"));
        Run(() => e.BeginEditAsync("fact", FactRow));
        Until(() => !vm.IsBusy && !vm.Browser.IsLoading && !vm.Personality.IsLoading);
        Settle();
        Dump("local edit ok, reloaded");
        _answers.Enqueue(true);
        t.Rebuild = new RebuildStatus("done", null, new RebuildResult(true, "ok", [], 0m, TimeSpan.FromSeconds(1)));
        Run(() => e.StartRebuildAsync(3));
        Until(() => !vm.IsBusy && !vm.Browser.IsLoading && !vm.Personality.IsLoading);
        Settle();
        Dump("local rebuild ok, reloaded");
        Run(() => e.BackupNowCommand.ExecuteAsync(null));
        Dump("backup now");
        e.EditMode = false;
        Settle();
        Run(() => e.StartRebuildAsync(3));
        Run(() => e.PreviewCompactionCommand.ExecuteAsync(null));
        Run(() => e.BeginAddAsync("fact"));
        Dump("edit mode off");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ shard import

    private string ShardsState(MainWindowViewModel vm, StringBuilder log)
    {
        var s = vm.Shards;
        var sb = new StringBuilder();
        sb.Append("LastResult = ").Append(s.LastResult).Append("\nStage = ").Append(s.Stage).Append("\nPercent = ").Append(s.Percent).Append("\nRunning = ").Append(s.Running)
            .Append("\nActivePack = ").Append(s.ActivePack).Append("\nHistoryInfo = ").Append(s.HistoryInfo).Append("\nHistory = ").Append(StateDump.Of(s.History, 2, Skip))
            .Append("\nStatus = ").Append(vm.Status).Append(" | ").Append(vm.ModeBadge).Append(" | page ").Append(vm.SelectedPage.Key).Append('\n');
        sb.Append(log).Append(_dialogs);
        log.Clear();
        _dialogs.Clear();
        return Mask(sb.ToString());
    }

    private string ShardImport(MainWindowViewModel vm)
    {
        var s = vm.Shards;
        var e = vm.Edit;
        var sb = new StringBuilder();
        var log = new StringBuilder();
        void Dump(string name) => sb.Append("== ").Append(name).Append('\n').Append(ShardsState(vm, log));
        void Run(Func<Task> a)
        {
            Pump(a());
            Until(() => !vm.IsBusy && !vm.Browser.IsLoading && !vm.Personality.IsLoading);
            Settle(60);
        }
        Dialogs(vm);
        vm.Navigate("shards");
        Settle(300);
        Until(() => s.HistoryInfo.Length > 0);

        // a real shard of the fixture's top node
        var shard = Path.Combine(_fx.Dir, "top.mcshard");
        var bank = vm.Bank!;
        int root = bank.ReadTree(t => t.Roots.OrderByDescending(n => n.Layer).ThenBy(n => n.Index).First().Index, -1);
        var refs = bank.ReadTree(t => ShardCollector.LeafRefsUnder(t, root), []);
        Pump(ShardService.ExportAsync(bank, new ShardExportRequest("top subtree", new ShardSelection { Mode = "subtree", Node = root, LeafRefs = refs }, "test"), shard));
        var junk = Path.Combine(_fx.Dir, "junk.mcshard");
        File.WriteAllText(junk, "not a shard");

        Run(() => s.ImportAsync(junk));
        Dump("not a shard");
        Run(() => s.ImportAsync(shard));
        Dump("no import dialog");
        var options = new Queue<ShardImportOptions?>();
        s.ShowImport = r =>
        {
            _dialogs.Append("import dialog ").Append(r.File.Name).Append(" | ").Append(r.File.Kind).Append(' ').Append(r.File.Records).Append(" | ").Append(r.Target)
                .Append(" | ").Append(r.IsLive).Append(' ').Append(r.ActivePack).Append(' ').Append(r.Preset?.ToString() ?? "null").Append('\n');
            return Task.FromResult(options.Count > 0 ? options.Dequeue() : null);
        };
        var confirm = vm.Confirm;
        vm.Confirm = null;
        Run(() => s.ImportAsync(shard));
        Dump("edit mode off, no confirm");
        vm.Confirm = confirm;
        Run(() => s.ImportAsync(shard));
        Dump("edit mode declined");
        var t = Inject(vm, new FakeTarget(log, Path.Combine(_fx.Dir, "backups", "fixture-1.db")));
        _answers.Enqueue(true);
        Run(() => s.ImportAsync(shard));
        Dump("edit mode on, dialog cancelled");
        t.BackupFails = true;
        options.Enqueue(new ShardImportOptions { Mode = ShardImportMode.ReplaceSection, IncludeRules = true });
        Run(() => s.ImportAsync(shard, ShardImportMode.ReplaceSection));
        Dump("backup fails");
        t.BackupFails = false;
        options.Enqueue(new ShardImportOptions());
        t.Import = new ShardImportResult { Ok = false, Message = "manifest mismatch" };
        Run(() => s.ImportAsync(shard));
        Dump("import refused");
        options.Enqueue(new ShardImportOptions { Mode = ShardImportMode.SwapPack });
        t.Import = new ShardImportResult
        {
            Ok = true, Message = "imported 12",
            Rebuild = [new RebuildRequest("kind", Kind: "episode"), new RebuildRequest("node", Node: 4)],
        };
        t.Progress = [new RebuildStatus("running", new RebuildProgress("cluster", 1, 2), null), new RebuildStatus("running", new RebuildProgress("embed", 0, 0, "vectors"), null)];
        t.ImportRebuild = new RebuildStatus("done", null, new RebuildResult(true, "2 sections", [], 0.0042m, TimeSpan.FromSeconds(3)));
        Run(() => s.ImportAsync(shard, ShardImportMode.SwapPack));
        Dump("import ok with a rebuild, local reload");
        t = Inject(vm, new FakeTarget(log, Path.Combine(_fx.Dir, "backups", "fixture-1.db")) { Progress = t.Progress, Import = t.Import });
        options.Enqueue(new ShardImportOptions());
        t.ImportRebuild = new RebuildStatus("failed", null, null, "over budget");
        Run(() => s.ImportAsync(shard));
        Dump("import ok, rebuild error");
        options.Enqueue(new ShardImportOptions());
        t.ImportRebuild = new RebuildStatus("queued", null, null);
        Run(() => s.ImportAsync(shard));
        Dump("import ok, rebuild state");

        e.Target = "Live alice";
        Settle();
        t = Inject(vm, new FakeTarget(log, Path.Combine(_fx.Dir, "backups", "fixture-1.db")) { Import = new ShardImportResult { Ok = true, Message = "imported live" } });
        t.AuditAnswer = new List<AuditEntry> { new(S0, "admin:z", "live:alice", "export", "shard", "x", null, null, null, true, "exported"),
            new(S0, "admin:z", "live:alice", "update", "fact", "1", null, null, null, true, "other kind") };
        options.Enqueue(new ShardImportOptions());
        Run(() => s.ImportAsync(shard));
        Dump("live import, no rebuild");
        options.Enqueue(new ShardImportOptions());
        t.Import = new OperationCanceledException();
        Run(() => s.ImportAsync(shard));
        Dump("live import cancelled");
        options.Enqueue(new ShardImportOptions());
        t.Import = new InvalidOperationException("supervisor refused");
        t.AuditAnswer = new InvalidOperationException("audit down");
        Run(() => s.ImportAsync(shard));
        Dump("live import throws, history fails");
        t.AuditAnswer = new List<AuditEntry>();
        options.Enqueue(new ShardImportOptions());
        t.Import = new ShardImportResult { Ok = true, Message = "slow" };
        t.Gate = new TaskCompletionSource();
        var running = s.ImportAsync(shard);
        Settle(30);
        Run(() => s.ImportAsync(shard));
        Dump("import while running");
        t.Gate.SetResult();
        Run(() => running);
        Dump("slow import done");
        return sb.ToString();
    }

    [Fact]
    public void Edit_session_and_shard_import_match_the_golden_hashes()
    {
        string shards = "";
        var edit = Session(() =>
        {
            var vm = NewShell();
            try
            {
                Pump(vm.OpenAsync(_fx.Path));
                Until(() => !vm.Browser.IsLoading && !vm.Personality.IsLoading && !vm.Scripts.IsLoading);
                Settle();
                var dump = EditSession(vm);
                vm.Edit.EditMode = false;
                vm.Edit.Target = "Local file";
                Settle();
                shards = ShardImport(vm);
                return dump;
            }
            finally { Close(vm); }
        });
        Compare([("edit", edit), ("shards", shards)], WritesGolden);
    }

    private const string WritesGolden = """
        edit DD3F54ED930D45725259
        shards 7F7538A09EDAF509545D
        """;
}
