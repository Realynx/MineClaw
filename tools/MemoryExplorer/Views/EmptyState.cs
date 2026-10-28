using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MineClaw.MemoryExplorer.Views;

/// <summary>Centered empty/loading state overlay: a spinner while <see cref="IsBusy"/>, else <see cref="Text"/> when set.</summary>
public sealed class EmptyState : ContentControl
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<EmptyState, string?>(nameof(Text));
    public static readonly StyledProperty<bool> IsBusyProperty = AvaloniaProperty.Register<EmptyState, bool>(nameof(IsBusy));

    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 420, Classes = { "muted" } };
    private readonly ProgressBar _bar = new() { IsIndeterminate = true, Width = 160, Height = 3, MinHeight = 3 };

    public EmptyState()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Content = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(28, 22),
            Child = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, Children = { _bar, _text } },
        };
        Update();
    }

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsBusy { get => GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == IsBusyProperty) Update();
    }

    private void Update()
    {
        _bar.IsVisible = IsBusy;
        _text.Text = IsBusy ? "Loading…" : Text;
        IsVisible = IsBusy || !string.IsNullOrEmpty(Text);
    }
}
