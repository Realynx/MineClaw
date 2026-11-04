using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MineClaw.Agent.Admin;
using MineClaw.MemoryExplorer.ViewModels;

namespace MineClaw.MemoryExplorer.Views;

/// <summary>
/// The generic memory edit form: one input per <see cref="Agent.Admin.EditField"/> of the kind (text, multiline, number, enum),
/// pre-filled from the selected row, plus a reason that goes into the audit trail. Returns the values (key "__reason" = reason).
/// </summary>
public static class EditDialog
{
    public static async Task<Dictionary<string, string?>?> ShowAsync(Window owner, EditRequest req)
    {
        Dictionary<string, string?>? result = null;
        var dlg = new Window
        {
            Title = req.Title,
            Width = 620,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 860,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        dlg.Bind(Window.BackgroundProperty, dlg.GetResourceObservable("CardBrush"));
        var inputs = new Dictionary<string, Func<string?>>();
        var form = new StackPanel { Spacing = 14 };
        foreach (var f in req.Fields)
        {
            if (req.Op == "update" && f.Help?.StartsWith("add only", StringComparison.Ordinal) == true) continue;
            form.Children.Add(FieldBlock(f, req, inputs));
        }
        var reason = new TextBox { Watermark = "Why (kept in the audit trail)" };
        form.Children.Add(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Reason", FontWeight = FontWeight.SemiBold, FontSize = 12.5 }, reason } });

        var save = new Button { Content = req.Op == "add" ? "Add" : "Save", Classes = { "accent" }, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Classes = { "ghost" }, IsCancel = true };
        save.Click += (_, _) =>
        {
            result = inputs.ToDictionary(kv => kv.Key, kv => kv.Value());
            result["__reason"] = string.IsNullOrWhiteSpace(reason.Text) ? null : reason.Text.Trim();
            dlg.Close();
        };
        cancel.Click += (_, _) => dlg.Close();
        dlg.Content = Layout(req, form, cancel, save);
        await dlg.ShowDialog(owner);
        return result;
    }

    /// <summary>One field: its label (required on add: " *"), its input (registered in <paramref name="inputs"/>) and its help.</summary>
    private static StackPanel FieldBlock(EditField f, EditRequest req, Dictionary<string, Func<string?>> inputs)
    {
        var start = req.Values.TryGetValue(f.Name, out var v) ? v : null;
        var input = Input(f, req.Op, start, inputs);
        var label = new TextBlock { Text = f.Label + (f.Required && req.Op == "add" ? " *" : ""), FontWeight = FontWeight.SemiBold, FontSize = 12.5 };
        var block = new StackPanel { Spacing = 4, Children = { label, input } };
        if (f.Help is { Length: > 0 } help)
            block.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Classes = { "muted" } });
        return block;
    }

    private static Control Input(EditField f, string op, string? start, Dictionary<string, Func<string?>> inputs) => f.Type switch
    {
        "enum" when f.Options is { Count: > 0 } opts => EnumInput(f, opts, op, start, inputs),
        "multiline" => MultilineInput(f, start, inputs),
        _ => TextInput(f, start, inputs),
    };

    /// <summary>A pick from the options (matched ignoring case); locked on update when the field is fixed after add.</summary>
    private static ComboBox EnumInput(EditField f, IReadOnlyList<string> opts, string op, string? start, Dictionary<string, Func<string?>> inputs)
    {
        var cb = new ComboBox { ItemsSource = opts, SelectedItem = opts.FirstOrDefault(o => string.Equals(o, start, StringComparison.OrdinalIgnoreCase)), MinWidth = 220 };
        if (op == "update" && f.Help?.Contains("fixed after add", StringComparison.Ordinal) == true) cb.IsEnabled = false;
        inputs[f.Name] = () => cb.SelectedItem as string;
        return cb;
    }

    private static TextBox MultilineInput(EditField f, string? start, Dictionary<string, Func<string?>> inputs)
    {
        var tb = new TextBox { Text = start ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 76, MaxHeight = 220 };
        inputs[f.Name] = () => tb.Text;
        return tb;
    }

    private static TextBox TextInput(EditField f, string? start, Dictionary<string, Func<string?>> inputs)
    {
        var tb = new TextBox { Text = start ?? "", Watermark = f.Type is "int" or "number" ? f.Type : null };
        inputs[f.Name] = () => tb.Text;
        return tb;
    }

    /// <summary>Title and note on top, Cancel / Save at the bottom, the form scrolling between.</summary>
    private static DockPanel Layout(EditRequest req, Control form, Button cancel, Button save) => new()
    {
        Margin = new Thickness(24),
        Children =
        {
            Dock(new StackPanel
            {
                Spacing = 4, Margin = new Thickness(0, 0, 0, 16),
                Children =
                {
                    new TextBlock { Text = req.Title, Classes = { "h2" }, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = req.Note ?? "", Classes = { "muted" }, FontSize = 12 },
                },
            }, Avalonia.Controls.Dock.Top),
            Dock(new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 18, 0, 0),
                Children = { cancel, save },
            }, Avalonia.Controls.Dock.Bottom),
            new ScrollViewer { Content = form },
        },
    };

    private static Control Dock(Control c, Dock d)
    {
        DockPanel.SetDock(c, d);
        return c;
    }
}
