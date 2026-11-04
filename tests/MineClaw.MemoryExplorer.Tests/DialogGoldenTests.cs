using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using MineClaw.MemoryExplorer.Services;
using MineClaw.MemoryExplorer.Tests.LiveHive;
using MineClaw.MemoryExplorer.ViewModels;
using MineClaw.MemoryExplorer.Views;
using Xunit.Abstractions;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>
/// Pins the Explorer's dialogs byte for byte (WP08 complexity refactor, checkpoint 4): the edit form, the shard export dialog and the
/// shard import dialog shown headless over an owner window. Each dump is the dialog's whole logical tree (every locally set or
/// styled property, the classes) after each scripted interaction, what its callbacks were asked, and what it returned.
/// Set EXPLORER_GOLDEN_OUT to a directory to write the dumps out.
/// </summary>
[Collection("avalonia")]
public sealed partial class DialogGoldenTests(ITestOutputHelper output)
{
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

    // ------------------------------------------------------------------ plumbing

    private static void Pump(Func<bool> done)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("the dialog never got there");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static readonly AvaloniaProperty[] Attached = [DockPanel.DockProperty, Grid.ColumnProperty];

    [GeneratedRegex(@"\b(i?mode)-\d+")] private static partial Regex Group();
    [GeneratedRegex(@"\d{4}-\d\d-\d\d \d\d:\d\d")] private static partial Regex Date();
    [GeneratedRegex(@"-\d{8}-\d{4}\.mcshard")] private static partial Regex Stamp();

    private static string Mask(string s) =>
        Stamp().Replace(Date().Replace(Group().Replace(s, "$1-#"), "<date>"), "-<stamp>.mcshard").Replace("admin:" + Environment.UserName, "admin:<user>");

    private static string Val(object? v) => v switch
    {
        null => "null",
        string s => '"' + s.Replace("\n", "\\n") + '"',
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        Control c => c.GetType().Name,
        IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Val)) + "]",
        _ => v.ToString() ?? "",
    };

    /// <summary>The logical tree with every non-direct property that has a value set (locally, by a binding or a style).</summary>
    private static string Ui(ILogical root)
    {
        var sb = new StringBuilder();
        void Walk(ILogical n, string indent)
        {
            sb.Append(indent).Append(n.GetType().Name);
            // Style classes (sorted: their order means nothing); pseudo-classes repeat state the properties already show.
            if (n is StyledElement se && se.Classes.Where(c => !c.StartsWith(':')).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } cls)
                sb.Append(" .").Append(string.Join(".", cls));
            if (n is AvaloniaObject o)
            {
                foreach (var p in AvaloniaPropertyRegistry.Instance.GetRegistered(o.GetType()).Concat(Attached).Where(p => !p.IsDirect && o.IsSet(p))
                             .OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.OwnerType.Name, StringComparer.Ordinal))
                    sb.Append(' ').Append(p.Name).Append('=').Append(Val(o.GetValue(p)));
                if (o is SelectingItemsControl sic) sb.Append(" SelectedIndex=").Append(sic.SelectedIndex);
            }
            sb.Append('\n');
            foreach (var c in n.LogicalChildren) Walk(c, indent + "  ");
        }
        Walk(root, "");
        return Mask(sb.ToString());
    }

    private static T Find<T>(Window dlg, Func<T, bool> pick) where T : class => dlg.GetLogicalDescendants().OfType<T>().First(pick);

    private static Button ButtonOf(Window dlg, string text) => Find<Button>(dlg, b => b.Content as string == text);

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Button Default(Window dlg) => Find<Button>(dlg, b => b.IsDefault);

    /// <summary>Shows the dialog over a fresh owner, runs <paramref name="script"/> on it, and returns what it returned.</summary>
    private static T Drive<T>(Func<Window, Task<T>> show, Action<Window> script)
    {
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        var task = show(owner);
        Pump(() => owner.OwnedWindows.Count > 0 || task.IsCompleted);
        var dlg = owner.OwnedWindows[0];
        Pump(() => dlg.IsVisible);
        script(dlg);
        if (!task.IsCompleted) dlg.Close();
        Pump(() => task.IsCompleted);
        owner.Close();
        Dispatcher.UIThread.RunJobs();
        return task.Result;
    }

    // ------------------------------------------------------------------ edit form

    private static readonly EditField[] Fields =
    [
        new("name", "Name", "text", Required: true, Help: "add only: the key"),
        new("kind", "Kind", "enum", Required: true, Options: ["farm", "Generator", "tree"], Help: "fixed after add"),
        new("text", "Text", "multiline"),
        new("x", "X", "int"),
        new("conf", "Confidence", "number", Help: "0..1"),
        new("state", "State", "enum", Options: ["a", "b"]),
        new("empty", "Empty enum", "enum", Options: []),
        new("note", "Note", "text", Help: ""),
    ];

    private static string EditDump()
    {
        var sb = new StringBuilder();
        var values = new Dictionary<string, string?> { ["name"] = "gen", ["kind"] = "generator", ["text"] = "line one\nline two", ["x"] = "4", ["state"] = "c" };
        foreach (var (name, op, values2, note, act) in new (string, string, Dictionary<string, string?>, string?, int)[]
                 {
                     ("update, save", "update", values, "Applied to the local db file.", 1),
                     ("add, save edited", "add", values.Keys.ToDictionary(k => k, _ => (string?)""), null, 2),
                     ("update, cancel", "update", values, "x", 0),
                     ("update, close", "update", [], null, -1),
                 })
        {
            var result = Drive(o => EditDialog.ShowAsync(o, new EditRequest("Edit structure gen", "structure", op, Fields, values2, note)), dlg =>
            {
                sb.Append("== ").Append(name).Append('\n').Append(Ui(dlg));
                if (act == 2)
                {
                    foreach (var tb in dlg.GetLogicalDescendants().OfType<TextBox>()) tb.Text = (tb.Watermark ?? "plain") + " typed";
                    foreach (var cb in dlg.GetLogicalDescendants().OfType<ComboBox>()) cb.SelectedIndex = cb.ItemCount - 1;
                }
                if (act > 0) Click(ButtonOf(dlg, op == "add" ? "Add" : "Save"));
                if (act == 0) Click(ButtonOf(dlg, "Cancel"));
            });
            sb.Append("=> ").Append(result == null ? "null" : string.Join(" | ", result.Select(kv => kv.Key + "=" + Val(kv.Value)))).Append('\n');
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ export dialog

    private static readonly PersonChoice[] People = [new("PoofImaFox", ["PoofImaFox", "Fox", "foxy"]), new("kiwi", ["kiwi", "Robin"])];

    private static ShardPreview PreviewOf(int total) => new(new Dictionary<string, int> { ["fact"] = 3, ["episode"] = 9 }, total, total > 0 ? 7 : 0, "fake-hash", 48,
        total > 0 ? 2 : 0, 12_345, 1, total > 0 ? ["sample one", "sample two"] : [], ["redacted 1 token"], total > 5 ? "b1.8.1" : null);

    private static string ExportDump()
    {
        var sb = new StringBuilder();
        var scenarios = new (string Name, ShardSelection Sel, string? Subtree, string NameText, Action<Window, StringBuilder> Script)[]
        {
            ("subtree", new ShardSelection { Mode = "subtree", Node = 12, LeafRefs = ["ep:1", "ep:2"] }, "#12 · layer 2 · cobble gen (2 leaves)", "subtree cobble", (dlg, log) =>
            {
                Click(ButtonOf(dlg, "Preview"));
                Pump(() => ButtonOf(dlg, "Preview").IsEnabled);
                log.Append(Ui(dlg));
                Click(ButtonOf(dlg, "Export…"));
                Pump(() => ButtonOf(dlg, "Export…").IsEnabled);
            }),
            ("filter", new ShardSelection { Mode = "filter", Kinds = [ShardKinds.Place, ShardKinds.Rule, ShardKinds.Fact], Dimension = -1, Query = "cobble", Semantic = true },
                null, "memory", (dlg, log) =>
            {
                Find<TextBox>(dlg, t => t.Text == "cobble").Text = "  gen  ";
                Click(ButtonOf(dlg, "Preview"));
                Pump(() => ButtonOf(dlg, "Preview").IsEnabled);
                foreach (var cb in dlg.GetLogicalDescendants().OfType<CheckBox>().Where(c => c.Content is "Places" or "Standing rules" or "Semantic (the bot's own hybrid search; needs embeddings configured)"))
                    cb.IsChecked = false;
                Find<ComboBox>(dlg, c => c.SelectedIndex == 2).SelectedIndex = 1;
                Find<TextBox>(dlg, t => t.Text == "memory").Text = " ";
                Find<TextBox>(dlg, t => t.Watermark?.StartsWith("What this shard") == true).Text = "  why  ";
                Click(ButtonOf(dlg, "Preview"));
                Pump(() => ButtonOf(dlg, "Preview").IsEnabled);
                log.Append(Ui(dlg));
            }),
            ("filter default kinds, zero", new ShardSelection { Mode = "filter" }, null, "memory", (dlg, log) =>
            {
                Click(ButtonOf(dlg, "Export…"));
                Pump(() => ButtonOf(dlg, "Export…").IsEnabled);
                log.Append(Ui(dlg));
            }),
            ("person", new ShardSelection { Mode = "person", People = ["fox"] }, null, "about", (dlg, log) =>
            {
                Click(ButtonOf(dlg, "Preview"));
                Pump(() => ButtonOf(dlg, "Preview").IsEnabled);
                Find<ComboBox>(dlg, c => c.PlaceholderText == "Pick a player").SelectedIndex = 1;
                log.Append(Ui(dlg));
                Click(ButtonOf(dlg, "Export…"));
                Pump(() => ButtonOf(dlg, "Export…").IsEnabled);
            }),
            ("person unknown", new ShardSelection { Mode = "person", People = ["nobody", "x"] }, null, "about", (dlg, log) =>
            {
                Find<RadioButton>(dlg, r => r.Content as string == "Knowledge pack").IsChecked = true;
                log.Append(Ui(dlg));
                Find<RadioButton>(dlg, r => r.Content as string == "Filter").IsChecked = true;
                Click(ButtonOf(dlg, "Preview"));
                Pump(() => ButtonOf(dlg, "Preview").IsEnabled);
                log.Append(Ui(dlg));
            }),
            ("pack", new ShardSelection { Mode = "pack", PackVersion = "1.12.2", IncludeKnowledgeDocs = true, IncludeRules = true, IncludeWorld = true }, null, "", (dlg, log) =>
            {
                Click(ButtonOf(dlg, "Preview"));
                Pump(() => ButtonOf(dlg, "Preview").IsEnabled);
                log.Append(Ui(dlg));
                Click(ButtonOf(dlg, "Export…"));
                Pump(() => ButtonOf(dlg, "Export…").IsEnabled);
            }),
            ("unknown mode", new ShardSelection { Mode = "weird" }, "#1", "x", (dlg, log) => Click(ButtonOf(dlg, "Cancel"))),
        };
        int n = 0;
        foreach (var (name, sel, subtree, nameText, script) in scenarios)
        {
            int at = n++;
            var log = new StringBuilder();
            var req = new ShardExportDialogRequest(nameText, sel, subtree, People, "b1.8.1", "data/x.db",
                r =>
                {
                    log.Append("preview ").Append(StateDump.Of(r, 3)).Append('\n');
                    return at == 5 && log.ToString().Split("preview ").Length > 2 ? throw new InvalidOperationException("no tree")
                        : Task.FromResult(PreviewOf(name.Contains("zero") ? 0 : 12 - at));
                },
                (r, path) =>
                {
                    log.Append("export ").Append(r.Name).Append(" to ").Append(path).Append('\n');
                    return at == 3 ? throw new IOException("disk full") : Task.FromResult("exported " + r.Name);
                },
                suggested =>
                {
                    log.Append("pick ").Append(suggested.Replace('\\', '/').Split('/')[^1]).Append('\n');
                    return Task.FromResult<string?>(at == 5 ? null : "out/" + at + ".mcshard");
                });
            var result = Drive(o => ShardExportDialog.ShowAsync(o, req), dlg =>
            {
                log.Append("-- opened\n").Append(Ui(dlg));
                script(dlg, log);
            });
            sb.Append("== ").Append(name).Append('\n').Append(Mask(log.ToString())).Append("=> ").Append(result ?? "null").Append('\n');
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ import dialog

    private static ShardFileInfo FileOf(string kind, string? version, string? mode, string? description) =>
        new("C:/shards/x.mcshard", "fox pack", kind, 42, version, new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero), 2048, description, null,
            "12 fact · 30 episode", kind == "pack" ? null : "fake-hash", mode);

    private static readonly ShardDiffItem[] Items =
    [
        new("1", "fact", ShardChange.Added, "a new fact", true), new("2", "fact", ShardChange.Unchanged, "same", false),
        new("3", "episode", ShardChange.Conflict, "both sides\nchanged " + new string('x', 200), false, "kept mine"), new("4", "claim", ShardChange.Removed, "gone", true),
        new("5", "trait", ShardChange.Changed, "edited", true, new string('n', 200)), new("6", "rule", ShardChange.Skipped, "untrusted", false), new("7", "fact", "other", "?", true),
    ];

    private static string ImportDump()
    {
        var sb = new StringBuilder();
        var diffs = new ShardDiff[]
        {
            new() { Shard = "x", Items = Items, Added = 1, Changed = 1, Conflicts = 1, Removed = 1, Unchanged = 1, Skipped = 1, Writes = 3, ReusableVectors = 4, ToEmbed = 2,
                Rebuild = [new RebuildRequest("kind", Kind: "episode"), new RebuildRequest("kind", Kind: "fact")],
                Estimate = new RebuildEstimate(3, 1200, 300, 900, 0.0042m, "claude-haiku-5-5", "n"), Notes = ["note a", "note b"] },
            new() { Shard = "x", Mode = ShardImportMode.SwapPack, PackFrom = "b1.8.1", PackTo = "1.12.2", Writes = 5, Rebuild = [new RebuildRequest("kind", Kind: "gamedata")] },
            new() { Shard = "x", Mode = ShardImportMode.AddMode, PackFrom = "b1.8.1", PackTo = "b1.8.1", ModesFrom = null, ModesTo = ["skyblock"], Writes = 2 },
            new() { Shard = "x", Mode = ShardImportMode.RemoveMode, ModesFrom = ["skyblock"], ModesTo = [], Removed = 2 },
            new() { Shard = "x", Refused = "the pack is for another version", Writes = 3 },
            new() { Shard = "x" },
        };
        var scenarios = new (string Name, ShardFileInfo File, ShardImportMode? Preset, bool Live, Action<Window, StringBuilder, Func<int>> Script)[]
        {
            ("memory shard", FileOf("subtree", null, null, "the gen subtree"), null, false, (dlg, log, calls) =>
            {
                Find<CheckBox>(dlg, c => c.Content as string == "Show unchanged").IsChecked = true;
                log.Append(Ui(dlg));
                Find<ComboBox>(dlg, c => c.ItemCount == 3).SelectedIndex = 2;
                Find<CheckBox>(dlg, c => (c.Content as string)?.StartsWith("Include standing") == true).IsChecked = true;
                Find<CheckBox>(dlg, c => (c.Content as string)?.StartsWith("Include world") == true).IsChecked = true;
                Find<RadioButton>(dlg, r => r.Content as string == "Replace section").IsChecked = true;
                log.Append(Ui(dlg));
                Click(Default(dlg));
                int before = calls();
                Click(ButtonOf(dlg, "Preview diff"));
                Pump(() => calls() > before && ButtonOf(dlg, "Preview diff").IsEnabled);
                log.Append(Ui(dlg));
                Click(Default(dlg));
            }),
            ("version pack, other version", FileOf("pack", "1.12.2", null, null), null, true, (dlg, log, _) => Click(Default(dlg))),
            ("version pack, same version", FileOf("pack", "b1.8.1", null, ""), null, false, (dlg, log, _) => log.Append(Ui(dlg))),
            ("mode pack", FileOf("pack", "b1.8.1", "skyblock", null), null, false, (dlg, log, calls) =>
            {
                Find<RadioButton>(dlg, r => r.Content as string == "Remove mode").IsChecked = true;
                int before = calls();
                Click(ButtonOf(dlg, "Preview diff"));
                Pump(() => calls() > before && ButtonOf(dlg, "Preview diff").IsEnabled);
                log.Append(Ui(dlg));
                Click(Default(dlg));
            }),
            ("preset swap, refused", FileOf("pack", "1.12.2", null, null), ShardImportMode.SwapPack, false, (dlg, log, _) => Click(Default(dlg))),
            ("preset merge, empty", FileOf("subtree", null, null, null), ShardImportMode.Merge, false, (dlg, log, _) => Click(ButtonOf(dlg, "Cancel"))),
            ("preset add mode, preview throws", FileOf("pack", "b1.8.1", "skyblock", null), ShardImportMode.AddMode, false, (dlg, log, _) => log.Append(Ui(dlg))),
            ("preset replace", FileOf("person", null, null, null), ShardImportMode.ReplaceSection, true, (dlg, log, _) => log.Append(Ui(dlg))),
            ("preset remove mode", FileOf("pack", "b1.8.1", "skyblock", null), ShardImportMode.RemoveMode, false, (dlg, log, _) => log.Append(Ui(dlg))),
        };
        int n = 0;
        foreach (var (name, file, preset, live, script) in scenarios)
        {
            int at = n++;
            var log = new StringBuilder();
            int calls = 0;
            var req = new ShardImportDialogRequest(file, "local: data/x.db", live, "b1.8.1", o =>
            {
                log.Append("preview ").Append(o).Append('\n');
                int call = calls++;
                if (at == 6) throw new InvalidOperationException("bad manifest");
                return Task.FromResult(diffs[Math.Min(at + call, diffs.Length - 1)] with { Mode = o.Mode, Conflict = o.Conflict });
            }, preset);
            var result = Drive(o => ShardImportDialog.ShowAsync(o, req), dlg =>
            {
                Pump(() => calls > 0 && ButtonOf(dlg, "Preview diff").IsEnabled);
                log.Append("-- opened\n").Append(Ui(dlg));
                script(dlg, log, () => calls);
            });
            sb.Append("== ").Append(name).Append('\n').Append(Mask(log.ToString())).Append("=> ").Append(Mask(result?.ToString() ?? "null")).Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void Dialogs_match_the_golden_hashes()
    {
        var rows = HeadlessUi.Run(() => new List<(string, string)> { ("edit-dialog", EditDump()), ("export-dialog", ExportDump()), ("import-dialog", ImportDump()) })
            .GetAwaiter().GetResult();
        Compare(rows, Golden);
    }

    private const string Golden = """
        edit-dialog B840FF8E5EA82758C6B5
        export-dialog F0D476C3F010915A6D73
        import-dialog 6CCC6DDD07B2FFDEDAC2
        """;
}
