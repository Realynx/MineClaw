using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MineClaw.Agent.Admin.Shards;
using MineClaw.MemoryExplorer.Services;

namespace MineClaw.MemoryExplorer.Views;

/// <summary>What the export dialog starts from and how it reaches the exporter (the view model supplies the callbacks).</summary>
/// <param name="SubtreeLabel">"#123 · L3 · furnace, smelting (48 leaves)" when the dialog was opened on a RAPTOR node.</param>
public sealed record ShardExportDialogRequest(
    string Name, ShardSelection Selection, string? SubtreeLabel, IReadOnlyList<PersonChoice> People, string ActivePack, string Source,
    Func<ShardExportRequest, Task<ShardPreview>> Preview, Func<ShardExportRequest, string, Task<string>> Export, Func<string, Task<string?>> PickSavePath);

/// <summary>
/// "Export shard…": name + description, what goes in (the RAPTOR subtree it was opened on, a filter, everything about a person, or the
/// knowledge pack), the opt-ins (standing rules, world memory, vectors, summaries cache), a preview of counts and the exact size,
/// then Export… to a .mcshard. Returns the written path, or null.
/// </summary>
public static class ShardExportDialog
{
    private static readonly (string Kind, string Label)[] KindLabels =
    [
        (ShardKinds.Fact, "Facts"), (ShardKinds.Claim, "Claims & beliefs"), (ShardKinds.Trait, "Self-profile"), (ShardKinds.Rule, "Standing rules"),
        (ShardKinds.Lesson, "Lessons"), (ShardKinds.Episode, "Episodes"), (ShardKinds.Knowledge, "Knowledge chunks"), (ShardKinds.GameData, "Recipes & game data"),
        (ShardKinds.Place, "Places"), (ShardKinds.Structure, "Structures"),
    ];

    public static async Task<string?> ShowAsync(Window owner, ShardExportDialogRequest req)
    {
        string? written = null;
        var dlg = new Window
        {
            Title = "Export shard",
            Width = 700,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 920,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        dlg.Bind(Window.BackgroundProperty, dlg.GetResourceObservable("CardBrush"));
        var sel = req.Selection;

        var name = new TextBox { Text = req.Name };
        var description = new TextBox { Watermark = "What this shard holds and why (travels in the manifest)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 48 };

        // ---- what goes in
        var modes = new List<(string Key, RadioButton Radio, Control Panel)>();
        RadioButton Radio(string text) => new() { Content = text, GroupName = "mode-" + dlg.GetHashCode(), Margin = new Thickness(0, 0, 14, 0) };

        var subtreeInfo = new TextBlock { Text = req.SubtreeLabel ?? "", TextWrapping = TextWrapping.Wrap, Classes = { "muted" }, FontSize = 12 };
        var subtreePanel = Block("Every leaf under the node, mapped back to its memory record (notes, claims, episodes, knowledge…).", subtreeInfo);

        var kindBoxes = KindLabels.Select(k => (k.Kind, Box: new CheckBox
        {
            Content = k.Label, Margin = new Thickness(0, 0, 12, 0),
            IsChecked = sel.Mode == "filter" && sel.Kinds is { Count: > 0 } ks ? ks.Contains(k.Kind) : !ShardKinds.IsWorld(k.Kind) && k.Kind is not (ShardKinds.GameData or ShardKinds.Knowledge or ShardKinds.Rule),
        })).ToList();
        var dimension = new ComboBox { ItemsSource = new[] { "any dimension", "overworld", "nether" }, SelectedIndex = sel.Dimension switch { 0 => 1, -1 => 2, _ => 0 }, MinWidth = 160 };
        var query = new TextBox { Text = sel.Mode == "filter" ? sel.Query ?? "" : "", Watermark = "Text every record must contain (or a semantic query)" };
        var semantic = new CheckBox { Content = "Semantic (the bot's own hybrid search; needs embeddings configured)", IsChecked = sel.Semantic };
        var kindsWrap = new WrapPanel { ItemSpacing = 0, LineSpacing = 4 };
        foreach (var (_, box) in kindBoxes) kindsWrap.Children.Add(box);
        var filterPanel = new StackPanel
        {
            Spacing = 8,
            Children = { Label("Memory kinds"), kindsWrap, Row(Label("Dimension"), dimension), Label("Query"), query, semantic },
        };

        var people = new ComboBox { ItemsSource = req.People, MinWidth = 260, PlaceholderText = "Pick a player" };
        var names = new TextBox { Watermark = "Login and every name used for them, comma separated", Text = sel.People is { Count: > 0 } p ? string.Join(", ", p) : "" };
        people.SelectionChanged += (_, _) => { if (people.SelectedItem is PersonChoice pc) names.Text = string.Join(", ", pc.Names); };
        if (sel.People is { Count: > 0 } given && req.People.FirstOrDefault(x => x.Names.Any(n => given.Contains(n, StringComparer.OrdinalIgnoreCase))) is { } match)
            people.SelectedItem = match;
        var personPanel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Hint("Beliefs and claims about them, facts, relationship entries in the self-profile, episodes with them (and their standing rules when ticked below)."),
                people, names,
            },
        };

        var version = new TextBox { Text = sel.PackVersion ?? req.ActivePack, Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        var withKb = new CheckBox { Content = "Also carry the docs/knowledge chunks", IsChecked = sel.IncludeKnowledgeDocs };
        var packPanel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Hint($"The version-tagged seed game data (recipes, smelting, items, blocks, dig facts). This db's active pack: {req.ActivePack}."),
                Row(Label("Minecraft version"), version), withKb,
            },
        };

        var rules = new CheckBox { Content = "Include standing rules (directives from trusted players)", IsChecked = sel.IncludeRules };
        var world = new CheckBox { Content = "Include world memory (places, structures)", IsChecked = sel.IncludeWorld };
        var vectors = new CheckBox { Content = "Include embeddings (reused on import when the model and dims match)", IsChecked = true };
        var summaries = new CheckBox { Content = "Include RAPTOR summaries as a cache (never as truth)", IsChecked = true };
        if (req.SubtreeLabel != null) modes.Add(("subtree", Radio("RAPTOR subtree"), subtreePanel));
        modes.Add(("filter", Radio("Filter"), filterPanel));
        modes.Add(("person", Radio("About a person"), personPanel));
        modes.Add(("pack", Radio("Knowledge pack"), packPanel));
        var modeRow = new WrapPanel();
        var panels = new Panel();
        foreach (var m in modes)
        {
            modeRow.Children.Add(m.Radio);
            panels.Children.Add(m.Panel);
            m.Radio.IsCheckedChanged += (_, _) => Sync();
        }
        (modes.FirstOrDefault(m => m.Key == sel.Mode).Radio ?? modes[0].Radio).IsChecked = true;

        var flags = new StackPanel { Spacing = 4, Children = { rules, world, vectors, summaries } };

        var previewText = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "body" }, Text = "Preview shows the counts and the exact file size before anything is written." };
        var previewCard = new Border { Classes = { "card" }, Child = previewText };

        string Mode() => modes.First(m => m.Radio.IsChecked == true).Key;

        void Sync()
        {
            var mode = modes.FirstOrDefault(m => m.Radio.IsChecked == true).Key;
            foreach (var m in modes) m.Panel.IsVisible = m.Key == mode;
            bool pack = mode == "pack";
            rules.IsEnabled = world.IsEnabled = !pack;
            if (pack && string.IsNullOrWhiteSpace(name.Text)) name.Text = "recipes " + version.Text;
        }
        Sync();

        ShardExportRequest Build()
        {
            var mode = Mode();
            var s = new ShardSelection
            {
                Mode = mode,
                Node = mode == "subtree" ? sel.Node : null,
                LeafRefs = mode == "subtree" ? sel.LeafRefs : null,
                Kinds = mode == "filter" ? kindBoxes.Where(k => k.Box.IsChecked == true).Select(k => k.Kind).ToList() : null,
                Dimension = mode == "filter" ? dimension.SelectedIndex switch { 1 => 0, 2 => -1, _ => null } : null,
                Query = mode == "filter" && !string.IsNullOrWhiteSpace(query.Text) ? query.Text.Trim() : null,
                Semantic = mode == "filter" && semantic.IsChecked == true,
                People = mode == "person" ? (names.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() : null,
                PackVersion = mode == "pack" ? version.Text?.Trim() : null,
                IncludeKnowledgeDocs = mode == "pack" && withKb.IsChecked == true,
                IncludeRules = mode != "pack" && rules.IsChecked == true,
                IncludeWorld = mode != "pack" && world.IsChecked == true,
            };
            // Ticking a world/rule kind in the filter implies the opt-in for it.
            if (mode == "filter" && s.Kinds!.Any(ShardKinds.IsWorld)) s = s with { IncludeWorld = true };
            if (mode == "filter" && s.Kinds!.Contains(ShardKinds.Rule)) s = s with { IncludeRules = true };
            return new ShardExportRequest(string.IsNullOrWhiteSpace(name.Text) ? "shard" : name.Text.Trim(), s,
                string.IsNullOrWhiteSpace(description.Text) ? null : description.Text.Trim(), vectors.IsChecked == true, summaries.IsChecked == true,
                "admin:" + Environment.UserName, req.Source);
        }

        var preview = new Button { Content = "Preview", Classes = { "ghost" } };
        var export = new Button { Content = "Export…", Classes = { "accent" }, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Classes = { "ghost" }, IsCancel = true };
        async Task<ShardPreview?> RunPreview()
        {
            preview.IsEnabled = export.IsEnabled = false;
            previewText.Text = "Counting…";
            try
            {
                var p = await req.Preview(Build());
                previewText.Text = Describe(p);
                return p;
            }
            catch (Exception ex) { previewText.Text = "Can't export: " + ex.Message; return null; }
            finally { preview.IsEnabled = export.IsEnabled = true; }
        }
        preview.Click += async (_, _) => await RunPreview();
        export.Click += async (_, _) =>
        {
            var p = await RunPreview();
            if (p == null || p.Total == 0) return;
            var r = Build();
            var path = await req.PickSavePath(ShardService.SuggestPath(r.Name));
            if (path == null) return;
            export.IsEnabled = false;
            try
            {
                previewText.Text = await req.Export(r, path);
                written = path;
                dlg.Close();
            }
            catch (Exception ex) { previewText.Text = "Export failed: " + ex.Message; }
            finally { export.IsEnabled = true; }
        };
        cancel.Click += (_, _) => dlg.Close();

        var form = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                new StackPanel { Spacing = 4, Children = { Label("Name"), name } },
                new StackPanel { Spacing = 4, Children = { Label("Description"), description } },
                new StackPanel { Spacing = 8, Children = { Label("What goes in"), modeRow, panels } },
                new StackPanel { Spacing = 6, Children = { Label("Options"), flags,
                    Hint("Secrets never travel: records about keys or passwords are left out and token-shaped strings are redacted. World observations, containers, the inventory ledger, chunks and build plans are never part of a shard.") } },
                previewCard,
            },
        };
        dlg.Content = new DockPanel
        {
            Margin = new Thickness(24),
            Children =
            {
                Dock(new StackPanel
                {
                    Spacing = 4, Margin = new Thickness(0, 0, 0, 16),
                    Children =
                    {
                        new TextBlock { Text = "Export a memory shard", Classes = { "h2" } },
                        new TextBlock { Text = "From " + req.Source + " (read-only). A portable .mcshard you can import into any db or the live bot.", Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    },
                }, Avalonia.Controls.Dock.Top),
                Dock(new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 18, 0, 0),
                    Children = { cancel, preview, export },
                }, Avalonia.Controls.Dock.Bottom),
                new ScrollViewer { Content = form },
            },
        };
        await dlg.ShowDialog(owner);
        return written;
    }

    internal static string Describe(ShardPreview p)
    {
        var lines = new List<string>
        {
            p.Total == 0 ? "Nothing matches." : $"{p.Total:N0} records · {MemoryBank.FormatBytes(p.Bytes)}" + (p.PackVersion != null ? $" · pack {p.PackVersion}" : ""),
        };
        if (p.Counts.Count > 0) lines.Add(string.Join(" · ", p.Counts.OrderByDescending(c => c.Value).Select(c => $"{c.Value:N0} {c.Key}")));
        lines.Add(p.Vectors > 0 ? $"{p.Vectors:N0} vectors ({p.EmbeddingModel}@{p.Dims.ToString(CultureInfo.InvariantCulture)})" : "no vectors");
        if (p.Summaries > 0) lines.Add($"{p.Summaries:N0} cached RAPTOR summaries");
        foreach (var n in p.Notes) lines.Add("• " + n);
        if (p.Samples.Count > 0)
        {
            lines.Add("");
            lines.AddRange(p.Samples);
        }
        return string.Join("\n", lines);
    }

    internal static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 12.5 };

    internal static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Classes = { "muted" } };

    private static StackPanel Block(string hint, Control c) => new() { Spacing = 6, Children = { Hint(hint), c } };

    internal static StackPanel Row(Control a, Control b)
    {
        a.VerticalAlignment = VerticalAlignment.Center;
        a.Width = 130;
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { a, b } };
    }

    internal static Control Dock(Control c, Dock d)
    {
        DockPanel.SetDock(c, d);
        return c;
    }
}
