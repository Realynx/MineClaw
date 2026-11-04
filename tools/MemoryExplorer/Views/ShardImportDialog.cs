using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MineClaw.Agent.Admin.Shards;
using MineClaw.MemoryExplorer.Services;

namespace MineClaw.MemoryExplorer.Views;

/// <param name="Target">"local: data/alice-snap/…db" / "live: http://…" (where the import runs).</param>
/// <param name="Preset">The mode to open on (the Default packs buttons: swap / add mode / remove mode); null = the usual default.</param>
public sealed record ShardImportDialogRequest(ShardFileInfo File, string Target, bool IsLive, string ActivePack,
    Func<ShardImportOptions, Task<ShardDiff>> Preview, ShardImportMode? Preset = null);

/// <summary>
/// "Import shard": the manifest, the mode (merge / replace-section / swap knowledge pack), the conflict policy, the opt-ins (rules,
/// world memory), then the diff first: added / changed / conflicts / removed / unchanged / skipped, the vectors it reuses, and the
/// scoped RAPTOR rebuild with its cost estimate. Import is the confirmation, enabled only for the diff on screen. Returns the
/// options, or null.
/// </summary>
public static class ShardImportDialog
{
    public static async Task<ShardImportOptions?> ShowAsync(Window owner, ShardImportDialogRequest req)
    {
        var dlg = new Window
        {
            Title = "Import shard",
            Width = 760,
            Height = 820,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        dlg.Bind(Window.BackgroundProperty, dlg.GetResourceObservable("CardBrush"));
        var form = new ImportForm(dlg, req);
        dlg.Opened += async (_, _) => await form.RunPreview();
        await dlg.ShowDialog(owner);
        return form.Result;
    }

    /// <summary>The dialog's controls and what they do: the mode and policy, the diff preview, and Import for the diff on screen.</summary>
    private sealed class ImportForm
    {
        private readonly Window _dlg;
        private readonly ShardImportDialogRequest _req;
        private readonly ShardFileInfo _f;
        private readonly RadioButton _merge, _replace, _swap, _addMode, _removeMode;
        private readonly TextBlock _modeHint, _refused;
        private readonly ComboBox _conflict;
        private readonly CheckBox _rules, _world, _showUnchanged;
        private readonly SelectableTextBlock _summary;
        private readonly ListBox _items;
        private readonly Button _previewBtn, _importBtn, _cancel;
        private ShardDiff? _diff;
        private ShardImportOptions? _diffFor;

        /// <summary>The options of the diff the user imported (null = cancelled).</summary>
        public ShardImportOptions? Result { get; private set; }

        public ImportForm(Window dlg, ShardImportDialogRequest req)
        {
            _dlg = dlg;
            _req = req;
            _f = req.File;
            bool pack = _f.Kind == "pack", modePack = _f.IsModePack;
            var preset = req.Preset;
            var group = "imode-" + dlg.GetHashCode();
            _merge = ModeRadio("Merge", group, !modePack, MergeAtStart(req, pack, modePack));
            _replace = ModeRadio("Replace section", group, !pack, preset == ShardImportMode.ReplaceSection);
            _swap = ModeRadio("Swap version pack", group, pack && !modePack, SwapAtStart(req, pack, modePack));
            _addMode = ModeRadio("Add mode", group, modePack, preset == null ? modePack : preset == ShardImportMode.AddMode);
            _removeMode = ModeRadio("Remove mode", group, modePack, preset == ShardImportMode.RemoveMode, spaced: false);
            _modeHint = ShardExportDialog.Hint("");
            _conflict = new ComboBox
            {
                ItemsSource = new[] { "Keep the newer copy", "Prefer the shard", "Keep mine" }, SelectedIndex = 0, MinWidth = 200,
            };
            _rules = new CheckBox { Content = "Include standing rules (only from players trusted here)" };
            _world = new CheckBox { Content = "Include world memory (places, structures)" };

            _summary = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "body" }, Text = "Preview the diff to see what changes; nothing is written until Import." };
            _refused = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold, IsVisible = false };
            _refused.Bind(TextBlock.ForegroundProperty, _refused.GetResourceObservable("BadBrush"));
            _showUnchanged = new CheckBox { Content = "Show unchanged", FontSize = 12 };
            _items = new ListBox { Classes = { "plain" }, MinHeight = 200 };
            _items.ItemTemplate = new FuncDataTemplate<ShardDiffItem>((it, _) => ItemView(it), supportsRecycling: false);

            _previewBtn = new Button { Content = "Preview diff", Classes = { "ghost" } };
            _importBtn = new Button { Content = "Import", Classes = { "accent" }, IsEnabled = false, IsDefault = true };
            _cancel = new Button { Content = "Cancel", Classes = { "ghost" }, IsCancel = true };
            Wire();
            Invalidate();
            dlg.Content = Layout();
        }

        /// <summary>No preset: merge unless it's a version pack for another version (or a mode pack).</summary>
        private static bool MergeAtStart(ShardImportDialogRequest req, bool pack, bool modePack) =>
            req.Preset == null ? !pack || (!modePack && req.File.PackVersion == req.ActivePack) : req.Preset == ShardImportMode.Merge;

        /// <summary>No preset: swap for a version pack of another version.</summary>
        private static bool SwapAtStart(ShardImportDialogRequest req, bool pack, bool modePack) =>
            req.Preset == null ? pack && !modePack && req.File.PackVersion != req.ActivePack : req.Preset == ShardImportMode.SwapPack;

        private static RadioButton ModeRadio(string text, string group, bool enabled, bool isChecked, bool spaced = true)
        {
            var r = new RadioButton { Content = text, GroupName = group, IsEnabled = enabled };
            if (spaced) r.Margin = new Thickness(0, 0, 14, 0);
            r.IsChecked = isChecked;
            return r;
        }

        /// <summary>One diff row: the change chip ("(kept)" when the policy keeps this side), the kind and label, the note.</summary>
        private static Control ItemView(ShardDiffItem? it)
        {
            if (it == null) return new TextBlock();
            var chip = new Border { Classes = { "chip" }, Padding = new Thickness(7, 1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top, MinWidth = 76,
                Child = new TextBlock { Text = it.Change + (it.Apply || it.Change is ShardChange.Unchanged or ShardChange.Skipped ? "" : " (kept)"), HorizontalAlignment = HorizontalAlignment.Center } };
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = $"{it.Kind} · {One(it.Label, 140)}", TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
            if (it.Note != null) text.Children.Add(new TextBlock { Text = One(it.Note, 160), Classes = { "muted" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 2) };
            g.Children.Add(chip);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            return g;
        }

        private void Wire()
        {
            foreach (var r in new[] { _merge, _replace, _swap, _addMode, _removeMode }) r.IsCheckedChanged += (_, _) => Invalidate();
            _conflict.SelectionChanged += (_, _) => Invalidate();
            _rules.IsCheckedChanged += (_, _) => Invalidate();
            _world.IsCheckedChanged += (_, _) => Invalidate();
            _showUnchanged.IsCheckedChanged += (_, _) => ShowItems();
            _previewBtn.Click += async (_, _) => await RunPreview();
            _importBtn.Click += (_, _) =>
            {
                if (_diff == null || _diffFor == null || _diff.Refused != null) return;
                Result = _diffFor;
                _dlg.Close();
            };
            _cancel.Click += (_, _) => _dlg.Close();
        }

        private ShardImportOptions Options() => new()
        {
            Mode = CheckedMode(),
            Conflict = _conflict.SelectedIndex switch { 1 => ShardConflictPolicy.PreferShard, 2 => ShardConflictPolicy.KeepMine, _ => ShardConflictPolicy.KeepNewer },
            IncludeRules = _rules.IsChecked == true, IncludeWorld = _world.IsChecked == true, Who = "admin:" + Environment.UserName,
        };

        private ShardImportMode CheckedMode() =>
            _swap.IsChecked == true ? ShardImportMode.SwapPack : _replace.IsChecked == true ? ShardImportMode.ReplaceSection
            : _addMode.IsChecked == true ? ShardImportMode.AddMode : _removeMode.IsChecked == true ? ShardImportMode.RemoveMode : ShardImportMode.Merge;

        /// <summary>The diff's items, worst first (removed, conflicts, changed, added, skipped), unchanged only when asked.</summary>
        private void ShowItems()
        {
            if (_diff == null) { _items.ItemsSource = null; return; }
            _items.ItemsSource = _diff.Items.Where(i => _showUnchanged.IsChecked == true || i.Change != ShardChange.Unchanged)
                .OrderBy(i => i.Change switch { ShardChange.Removed => 0, ShardChange.Conflict => 1, ShardChange.Changed => 2, ShardChange.Added => 3, ShardChange.Skipped => 4, _ => 5 })
                .ThenBy(i => i.Kind).ToList();
        }

        /// <summary>Any change to the options drops the diff on screen (Import needs a fresh preview) and explains the mode.</summary>
        private void Invalidate()
        {
            _diff = null;
            _importBtn.IsEnabled = false;
            _modeHint.Text = (_swap.IsChecked, _replace.IsChecked, _addMode.IsChecked, _removeMode.IsChecked) switch
            {
                (true, _, _, _) => $"Unloads this db's game data and version docs ({_req.ActivePack}) and loads the pack's ({_f.PackVersion}); the active version follows. Mode packs and everything else stay.",
                (_, true, _, _) => "Removes the records here that match the shard's selection (e.g. everything about that person), then inserts the shard's.",
                (_, _, true, _) => $"Turns the {_f.PackMode} mode on: its docs join retrieval and its core lines join the cached prompt (at the bot's next start).",
                (_, _, _, true) => $"Turns the {_f.PackMode} mode off: its chunks leave retrieval and its core lines leave the cached prompt. Nothing else is touched.",
                _ => "Adds what's new, dedupes by stable id; records on both sides follow the conflict policy.",
            };
            ShowItems();
        }

        public async Task RunPreview()
        {
            var o = Options();
            _previewBtn.IsEnabled = _importBtn.IsEnabled = false;
            _summary.Text = "Computing the diff…";
            _refused.IsVisible = false;
            try
            {
                _diff = await _req.Preview(o);
                _diffFor = o;
                ShowDiff(_diff);
            }
            catch (Exception ex) { _summary.Text = "Can't import: " + ex.Message; _diff = null; }
            finally { _previewBtn.IsEnabled = true; }
        }

        private void ShowDiff(ShardDiff diff)
        {
            _summary.Text = Describe(diff);
            _refused.Text = diff.Refused != null ? "Refused: " + diff.Refused : "";
            _refused.IsVisible = diff.Refused != null;
            _importBtn.IsEnabled = diff.Refused == null && !diff.Empty;
            _importBtn.Content = diff.Rebuild.Count > 0 ? "Import and rebuild" : "Import";
            ShowItems();
        }

        private DockPanel Layout()
        {
            var options = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    ShardExportDialog.Label("Mode"),
                    new WrapPanel { Children = { _merge, _replace, _swap, _addMode, _removeMode } },
                    _modeHint,
                    ShardExportDialog.Row(ShardExportDialog.Label("On conflict"), _conflict),
                    _rules, _world,
                },
            };
            var diffHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            diffHeader.Children.Add(ShardExportDialog.Label("Diff"));
            Grid.SetColumn(_showUnchanged, 1);
            diffHeader.Children.Add(_showUnchanged);
            var top = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    Manifest(),
                    new TextBlock { Text = "Into " + _req.Target + (_req.IsLive ? " · the bot applies it with its own stores and rebuilds in the background under its budget" : " · a backup is taken first"),
                        Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    options,
                    new Border { Classes = { "card" }, Child = new StackPanel { Spacing = 6, Children = { _summary, _refused } } },
                    diffHeader,
                },
            };
            return new DockPanel
            {
                Margin = new Thickness(24),
                Children =
                {
                    ShardExportDialog.Dock(top, Avalonia.Controls.Dock.Top),
                    ShardExportDialog.Dock(new StackPanel
                    {
                        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 14, 0, 0),
                        Children = { _cancel, _previewBtn, _importBtn },
                    }, Avalonia.Controls.Dock.Bottom),
                    new Border { Classes = { "card" }, Padding = new Thickness(4), Margin = new Thickness(0, 6, 0, 0), Child = _items },
                },
            };
        }

        /// <summary>The shard's name, kind, size, export time, vectors, counts and description.</summary>
        private StackPanel Manifest()
        {
            var manifest = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = _f.Name, Classes = { "h2" }, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"{_f.KindLabel} · {_f.Records:N0} records · {_f.SizeText} · exported {_f.CreatedText}" + (_f.EmbeddingModel != null ? $" · vectors {_f.EmbeddingModel}" : " · no vectors"),
                        Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = _f.Counts, Classes = { "faint" }, TextWrapping = TextWrapping.Wrap },
                },
            };
            if (_f.Description is { Length: > 0 } d) manifest.Children.Add(new TextBlock { Text = d, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) });
            return manifest;
        }
    }

    private static string Modes(IReadOnlyList<string>? m) => m == null ? "every mode doc on disk" : m.Count == 0 ? "none" : string.Join(", ", m);

    private static string One(string s, int n)
    {
        var t = s.ReplaceLineEndings(" ");
        return t.Length > n ? t[..n] + "…" : t;
    }

    internal static string Describe(ShardDiff d)
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            $"{d.Added:N0} added · {d.Changed:N0} changed · {d.Conflicts:N0} conflicts · {d.Removed:N0} removed · {d.Unchanged:N0} unchanged · {d.Skipped:N0} skipped",
            $"{d.Writes:N0} writes ({ShardImportOptions.Name(d.Mode)}, {ShardImportOptions.Name(d.Conflict)}) · vectors: {d.ReusableVectors:N0} reused, {d.ToEmbed:N0} to embed",
        };
        if (d.PackTo != null) lines.Add($"knowledge pack: {d.PackFrom} → {(d.Mode == ShardImportMode.SwapPack ? d.PackTo : d.PackFrom)}");
        if (d.Mode is ShardImportMode.AddMode or ShardImportMode.RemoveMode)
            lines.Add($"modes: {Modes(d.ModesFrom)} → {Modes(d.ModesTo)}");
        if (d.Rebuild.Count > 0)
        {
            var e = d.Estimate;
            lines.Add($"RAPTOR rebuild: {string.Join(", ", d.Rebuild.Select(r => r.Kind))} (re-cluster + re-summarize the affected nodes)" +
                (e != null ? string.Create(inv, $" · ~{e.Summaries} summaries with {e.Model}, ~{e.InputTokens:N0}+{e.OutputTokens:N0} tokens, ~{e.EmbeddingTokens:N0} embedding tokens, est. ${e.Usd:0.0000}") : ""));
        }
        else if (!d.Empty) lines.Add("no RAPTOR rebuild needed");
        foreach (var n in d.Notes) lines.Add("• " + n);
        return string.Join("\n", lines);
    }
}
