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
public sealed record ShardImportDialogRequest(ShardFileInfo File, string Target, bool IsLive, string ActivePack,
    Func<ShardImportOptions, Task<ShardDiff>> Preview);

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
        ShardImportOptions? result = null;
        var f = req.File;
        bool pack = f.Kind == "pack";
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

        var group = "imode-" + dlg.GetHashCode();
        var merge = new RadioButton { Content = "Merge", GroupName = group, IsChecked = !pack || f.PackVersion == req.ActivePack, Margin = new Thickness(0, 0, 14, 0) };
        var replace = new RadioButton { Content = "Replace section", GroupName = group, IsEnabled = !pack, Margin = new Thickness(0, 0, 14, 0) };
        var swap = new RadioButton { Content = "Swap knowledge pack", GroupName = group, IsEnabled = pack, IsChecked = pack && f.PackVersion != req.ActivePack };
        var modeHint = ShardExportDialog.Hint("");
        var conflict = new ComboBox
        {
            ItemsSource = new[] { "Keep the newer copy", "Prefer the shard", "Keep mine" }, SelectedIndex = 0, MinWidth = 200,
        };
        var rules = new CheckBox { Content = "Include standing rules (only from players trusted here)" };
        var world = new CheckBox { Content = "Include world memory (places, structures)" };

        var summary = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "body" }, Text = "Preview the diff to see what changes; nothing is written until Import." };
        var refused = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold, IsVisible = false };
        refused.Bind(TextBlock.ForegroundProperty, refused.GetResourceObservable("BadBrush"));
        var showUnchanged = new CheckBox { Content = "Show unchanged", FontSize = 12 };
        var items = new ListBox { Classes = { "plain" }, MinHeight = 200 };
        items.ItemTemplate = new FuncDataTemplate<ShardDiffItem>((it, _) =>
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
        }, supportsRecycling: false);

        var previewBtn = new Button { Content = "Preview diff", Classes = { "ghost" } };
        var importBtn = new Button { Content = "Import", Classes = { "accent" }, IsEnabled = false, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Classes = { "ghost" }, IsCancel = true };
        ShardDiff? diff = null;
        ShardImportOptions? diffFor = null;

        ShardImportOptions Options() => new()
        {
            Mode = swap.IsChecked == true ? ShardImportMode.SwapPack : replace.IsChecked == true ? ShardImportMode.ReplaceSection : ShardImportMode.Merge,
            Conflict = conflict.SelectedIndex switch { 1 => ShardConflictPolicy.PreferShard, 2 => ShardConflictPolicy.KeepMine, _ => ShardConflictPolicy.KeepNewer },
            IncludeRules = rules.IsChecked == true, IncludeWorld = world.IsChecked == true, Who = "admin:" + Environment.UserName,
        };

        void ShowItems()
        {
            if (diff == null) { items.ItemsSource = null; return; }
            items.ItemsSource = diff.Items.Where(i => showUnchanged.IsChecked == true || i.Change != ShardChange.Unchanged)
                .OrderBy(i => i.Change switch { ShardChange.Removed => 0, ShardChange.Conflict => 1, ShardChange.Changed => 2, ShardChange.Added => 3, ShardChange.Skipped => 4, _ => 5 })
                .ThenBy(i => i.Kind).ToList();
        }

        void Invalidate()
        {
            diff = null;
            importBtn.IsEnabled = false;
            modeHint.Text = (swap.IsChecked, replace.IsChecked) switch
            {
                (true, _) => $"Unloads this db's game data ({req.ActivePack}) and loads the shard's ({f.PackVersion}); the active version follows. Nothing else is touched.",
                (_, true) => "Removes the records here that match the shard's selection (e.g. everything about that person), then inserts the shard's.",
                _ => "Adds what's new, dedupes by stable id; records on both sides follow the conflict policy.",
            };
            ShowItems();
        }

        async Task RunPreview()
        {
            var o = Options();
            previewBtn.IsEnabled = importBtn.IsEnabled = false;
            summary.Text = "Computing the diff…";
            refused.IsVisible = false;
            try
            {
                diff = await req.Preview(o);
                diffFor = o;
                summary.Text = Describe(diff);
                refused.Text = diff.Refused != null ? "Refused: " + diff.Refused : "";
                refused.IsVisible = diff.Refused != null;
                importBtn.IsEnabled = diff.Refused == null && !diff.Empty;
                importBtn.Content = diff.Rebuild.Count > 0 ? "Import and rebuild" : "Import";
                ShowItems();
            }
            catch (Exception ex) { summary.Text = "Can't import: " + ex.Message; diff = null; }
            finally { previewBtn.IsEnabled = true; }
        }

        foreach (var r in new[] { merge, replace, swap }) r.IsCheckedChanged += (_, _) => Invalidate();
        conflict.SelectionChanged += (_, _) => Invalidate();
        rules.IsCheckedChanged += (_, _) => Invalidate();
        world.IsCheckedChanged += (_, _) => Invalidate();
        showUnchanged.IsCheckedChanged += (_, _) => ShowItems();
        previewBtn.Click += async (_, _) => await RunPreview();
        importBtn.Click += (_, _) =>
        {
            if (diff == null || diffFor == null || diff.Refused != null) return;
            result = diffFor;
            dlg.Close();
        };
        cancel.Click += (_, _) => dlg.Close();
        Invalidate();

        var manifest = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = f.Name, Classes = { "h2" }, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"{f.KindLabel} · {f.Records:N0} records · {f.SizeText} · exported {f.CreatedText}" + (f.EmbeddingModel != null ? $" · vectors {f.EmbeddingModel}" : " · no vectors"),
                    Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = f.Counts, Classes = { "faint" }, TextWrapping = TextWrapping.Wrap },
            },
        };
        if (f.Description is { Length: > 0 } d) manifest.Children.Add(new TextBlock { Text = d, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) });

        var options = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                ShardExportDialog.Label("Mode"),
                new WrapPanel { Children = { merge, replace, swap } },
                modeHint,
                ShardExportDialog.Row(ShardExportDialog.Label("On conflict"), conflict),
                rules, world,
            },
        };
        var diffHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        diffHeader.Children.Add(ShardExportDialog.Label("Diff"));
        Grid.SetColumn(showUnchanged, 1);
        diffHeader.Children.Add(showUnchanged);

        var top = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                manifest,
                new TextBlock { Text = "Into " + req.Target + (req.IsLive ? " · the bot applies it with its own stores and rebuilds in the background under its budget" : " · a backup is taken first"),
                    Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                options,
                new Border { Classes = { "card" }, Child = new StackPanel { Spacing = 6, Children = { summary, refused } } },
                diffHeader,
            },
        };
        dlg.Content = new DockPanel
        {
            Margin = new Thickness(24),
            Children =
            {
                ShardExportDialog.Dock(top, Avalonia.Controls.Dock.Top),
                ShardExportDialog.Dock(new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 14, 0, 0),
                    Children = { cancel, previewBtn, importBtn },
                }, Avalonia.Controls.Dock.Bottom),
                new Border { Classes = { "card" }, Padding = new Thickness(4), Margin = new Thickness(0, 6, 0, 0), Child = items },
            },
        };
        dlg.Opened += async (_, _) => await RunPreview();
        await dlg.ShowDialog(owner);
        return result;
    }

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
