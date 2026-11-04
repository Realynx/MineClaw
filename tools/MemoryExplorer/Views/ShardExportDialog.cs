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
        var form = new ExportForm(dlg, req);
        await dlg.ShowDialog(owner);
        return form.Written;
    }

    /// <summary>The dialog's controls and what they do: one panel per mode, the opt-ins, preview and export.</summary>
    private sealed class ExportForm
    {
        private readonly Window _dlg;
        private readonly ShardExportDialogRequest _req;
        private readonly ShardSelection _sel;
        private readonly TextBox _name, _description, _query, _names, _version;
        private readonly List<(string Kind, CheckBox Box)> _kindBoxes;
        private readonly ComboBox _dimension, _people;
        private readonly CheckBox _semantic, _withKb, _rules, _world, _vectors, _summaries;
        private readonly List<(string Key, RadioButton Radio, Control Panel)> _modes = [];
        private readonly WrapPanel _modeRow = new();
        private readonly Panel _panels = new();
        private readonly SelectableTextBlock _previewText;
        private readonly Button _preview, _export, _cancel;

        /// <summary>The written path (null until an export succeeds).</summary>
        public string? Written { get; private set; }

        public ExportForm(Window dlg, ShardExportDialogRequest req)
        {
            _dlg = dlg;
            _req = req;
            _sel = req.Selection;
            _name = new TextBox { Text = req.Name };
            _description = new TextBox { Watermark = "What this shard holds and why (travels in the manifest)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 48 };

            // ---- what goes in
            var subtreeInfo = new TextBlock { Text = req.SubtreeLabel ?? "", TextWrapping = TextWrapping.Wrap, Classes = { "muted" }, FontSize = 12 };
            var subtreePanel = Block("Every leaf under the node, mapped back to its memory record (notes, claims, episodes, knowledge…).", subtreeInfo);
            _kindBoxes = KindLabels.Select(k => (k.Kind, Box: new CheckBox { Content = k.Label, Margin = new Thickness(0, 0, 12, 0), IsChecked = CheckedAtStart(_sel, k.Kind) })).ToList();
            _dimension = new ComboBox { ItemsSource = new[] { "any dimension", "overworld", "nether" }, SelectedIndex = _sel.Dimension switch { 0 => 1, -1 => 2, _ => 0 }, MinWidth = 160 };
            _query = new TextBox { Text = _sel.Mode == "filter" ? _sel.Query ?? "" : "", Watermark = "Text every record must contain (or a semantic query)" };
            _semantic = new CheckBox { Content = "Semantic (the bot's own hybrid search; needs embeddings configured)", IsChecked = _sel.Semantic };
            var filterPanel = FilterPanel();

            _people = new ComboBox { ItemsSource = req.People, MinWidth = 260, PlaceholderText = "Pick a player" };
            _names = new TextBox { Watermark = "Login and every name used for them, comma separated", Text = _sel.People is { Count: > 0 } p ? string.Join(", ", p) : "" };
            _people.SelectionChanged += (_, _) => { if (_people.SelectedItem is PersonChoice pc) _names.Text = string.Join(", ", pc.Names); };
            if (PersonOf(_sel, req.People) is { } match) _people.SelectedItem = match;
            var personPanel = PersonPanel();

            _version = new TextBox { Text = _sel.PackVersion ?? req.ActivePack, Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
            _withKb = new CheckBox { Content = "Also carry the docs/knowledge chunks", IsChecked = _sel.IncludeKnowledgeDocs };
            var packPanel = PackPanel();

            _rules = new CheckBox { Content = "Include standing rules (directives from trusted players)", IsChecked = _sel.IncludeRules };
            _world = new CheckBox { Content = "Include world memory (places, structures)", IsChecked = _sel.IncludeWorld };
            _vectors = new CheckBox { Content = "Include embeddings (reused on import when the model and dims match)", IsChecked = true };
            _summaries = new CheckBox { Content = "Include RAPTOR summaries as a cache (never as truth)", IsChecked = true };
            AddModes(subtreePanel, filterPanel, personPanel, packPanel);

            var flags = new StackPanel { Spacing = 4, Children = { _rules, _world, _vectors, _summaries } };
            _previewText = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "body" }, Text = "Preview shows the counts and the exact file size before anything is written." };
            var previewCard = new Border { Classes = { "card" }, Child = _previewText };
            Sync();

            _preview = new Button { Content = "Preview", Classes = { "ghost" } };
            _export = new Button { Content = "Export…", Classes = { "accent" }, IsDefault = true };
            _cancel = new Button { Content = "Cancel", Classes = { "ghost" }, IsCancel = true };
            _preview.Click += async (_, _) => await RunPreview();
            _export.Click += async (_, _) => await ExportAsync();
            _cancel.Click += (_, _) => _dlg.Close();
            _dlg.Content = Layout(flags, previewCard);
        }

        /// <summary>A kind box starts ticked when the filter named it, else for every kind but world memory, game data, knowledge and rules.</summary>
        private static bool CheckedAtStart(ShardSelection sel, string kind) =>
            sel.Mode == "filter" && sel.Kinds is { Count: > 0 } ks ? ks.Contains(kind)
                : !ShardKinds.IsWorld(kind) && kind is not (ShardKinds.GameData or ShardKinds.Knowledge or ShardKinds.Rule);

        /// <summary>The player the selection's names point at (any of their names, ignoring case).</summary>
        private static PersonChoice? PersonOf(ShardSelection sel, IReadOnlyList<PersonChoice> people) =>
            sel.People is { Count: > 0 } given ? people.FirstOrDefault(x => x.Names.Any(n => given.Contains(n, StringComparer.OrdinalIgnoreCase))) : null;

        private StackPanel FilterPanel()
        {
            var kindsWrap = new WrapPanel { ItemSpacing = 0, LineSpacing = 4 };
            foreach (var (_, box) in _kindBoxes) kindsWrap.Children.Add(box);
            return new StackPanel
            {
                Spacing = 8,
                Children = { Label("Memory kinds"), kindsWrap, Row(Label("Dimension"), _dimension), Label("Query"), _query, _semantic },
            };
        }

        private StackPanel PersonPanel() => new()
        {
            Spacing = 8,
            Children =
            {
                Hint("Beliefs and claims about them, facts, relationship entries in the self-profile, episodes with them (and their standing rules when ticked below)."),
                _people, _names,
            },
        };

        private StackPanel PackPanel() => new()
        {
            Spacing = 8,
            Children =
            {
                Hint($"The version-tagged seed game data (recipes, smelting, items, blocks, dig facts). This db's active pack: {_req.ActivePack}."),
                Row(Label("Minecraft version"), _version), _withKb,
            },
        };

        /// <summary>One radio per mode (the subtree only when opened on a node); the selection's mode starts checked.</summary>
        private void AddModes(Control subtreePanel, Control filterPanel, Control personPanel, Control packPanel)
        {
            if (_req.SubtreeLabel != null) _modes.Add(("subtree", Radio("RAPTOR subtree"), subtreePanel));
            _modes.Add(("filter", Radio("Filter"), filterPanel));
            _modes.Add(("person", Radio("About a person"), personPanel));
            _modes.Add(("pack", Radio("Knowledge pack"), packPanel));
            foreach (var m in _modes)
            {
                _modeRow.Children.Add(m.Radio);
                _panels.Children.Add(m.Panel);
                m.Radio.IsCheckedChanged += (_, _) => Sync();
            }
            (_modes.FirstOrDefault(m => m.Key == _sel.Mode).Radio ?? _modes[0].Radio).IsChecked = true;
        }

        private RadioButton Radio(string text) => new() { Content = text, GroupName = "mode-" + _dlg.GetHashCode(), Margin = new Thickness(0, 0, 14, 0) };

        private string Mode() => _modes.First(m => m.Radio.IsChecked == true).Key;

        /// <summary>Shows the checked mode's panel; a pack has no rules or world opt-ins and names itself after its version.</summary>
        private void Sync()
        {
            var mode = _modes.FirstOrDefault(m => m.Radio.IsChecked == true).Key;
            foreach (var m in _modes) m.Panel.IsVisible = m.Key == mode;
            bool pack = mode == "pack";
            _rules.IsEnabled = _world.IsEnabled = !pack;
            if (pack && string.IsNullOrWhiteSpace(_name.Text)) _name.Text = "recipes " + _version.Text;
        }

        private ShardExportRequest Build() =>
            new(Trimmed(_name.Text) ?? "shard", SelectionFor(Mode()), Trimmed(_description.Text), _vectors.IsChecked == true, _summaries.IsChecked == true,
                "admin:" + Environment.UserName, _req.Source);

        private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        /// <summary>The selection the form describes: the mode's own fields, and the opt-ins (never for a pack).</summary>
        private ShardSelection SelectionFor(string mode)
        {
            bool pack = mode == "pack";
            var s = new ShardSelection { Mode = mode, IncludeRules = !pack && _rules.IsChecked == true, IncludeWorld = !pack && _world.IsChecked == true };
            return mode switch
            {
                "subtree" => s with { Node = _sel.Node, LeafRefs = _sel.LeafRefs },
                "filter" => FilterSelection(s),
                "person" => s with { People = (_names.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() },
                "pack" => s with { PackVersion = _version.Text?.Trim(), IncludeKnowledgeDocs = _withKb.IsChecked == true },
                _ => s,
            };
        }

        private ShardSelection FilterSelection(ShardSelection s)
        {
            var kinds = _kindBoxes.Where(k => k.Box.IsChecked == true).Select(k => k.Kind).ToList();
            s = s with
            {
                Kinds = kinds, Dimension = _dimension.SelectedIndex switch { 1 => 0, 2 => -1, _ => null }, Query = Trimmed(_query.Text), Semantic = _semantic.IsChecked == true,
            };
            // Ticking a world/rule kind in the filter implies the opt-in for it.
            if (kinds.Any(ShardKinds.IsWorld)) s = s with { IncludeWorld = true };
            if (kinds.Contains(ShardKinds.Rule)) s = s with { IncludeRules = true };
            return s;
        }

        private async Task<ShardPreview?> RunPreview()
        {
            _preview.IsEnabled = _export.IsEnabled = false;
            _previewText.Text = "Counting…";
            try
            {
                var p = await _req.Preview(Build());
                _previewText.Text = Describe(p);
                return p;
            }
            catch (Exception ex) { _previewText.Text = "Can't export: " + ex.Message; return null; }
            finally { _preview.IsEnabled = _export.IsEnabled = true; }
        }

        /// <summary>Preview first (nothing to export: stop), then the save path, then the write; closes on success.</summary>
        private async Task ExportAsync()
        {
            var p = await RunPreview();
            if (p == null || p.Total == 0) return;
            var r = Build();
            var path = await _req.PickSavePath(ShardService.SuggestPath(r.Name));
            if (path == null) return;
            _export.IsEnabled = false;
            try
            {
                _previewText.Text = await _req.Export(r, path);
                Written = path;
                _dlg.Close();
            }
            catch (Exception ex) { _previewText.Text = "Export failed: " + ex.Message; }
            finally { _export.IsEnabled = true; }
        }

        private DockPanel Layout(Control flags, Control previewCard)
        {
            var form = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new StackPanel { Spacing = 4, Children = { Label("Name"), _name } },
                    new StackPanel { Spacing = 4, Children = { Label("Description"), _description } },
                    new StackPanel { Spacing = 8, Children = { Label("What goes in"), _modeRow, _panels } },
                    new StackPanel { Spacing = 6, Children = { Label("Options"), flags,
                        Hint("Secrets never travel: records about keys or passwords are left out and token-shaped strings are redacted. World observations, containers, the inventory ledger, chunks and build plans are never part of a shard.") } },
                    previewCard,
                },
            };
            return new DockPanel
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
                            new TextBlock { Text = "From " + _req.Source + " (read-only). A portable .mcshard you can import into any db or the live bot.", Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                        },
                    }, Avalonia.Controls.Dock.Top),
                    Dock(new StackPanel
                    {
                        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 18, 0, 0),
                        Children = { _cancel, _preview, _export },
                    }, Avalonia.Controls.Dock.Bottom),
                    new ScrollViewer { Content = form },
                },
            };
        }
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
