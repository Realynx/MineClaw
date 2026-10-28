using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MineClaw.MemoryExplorer.Services;

namespace MineClaw.MemoryExplorer.ViewModels;

/// <summary>A page of the main area. <see cref="LoadAsync"/> runs whenever a (new) memory bank is opened.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    protected PageViewModel(MainWindowViewModel shell) => Shell = shell;

    public MainWindowViewModel Shell { get; }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _emptyText;

    public MemoryBank? Bank => Shell.Bank;

    public virtual Task LoadAsync(MemoryBank? bank, CancellationToken ct) => Task.CompletedTask;

    /// <summary>The page became visible.</summary>
    public virtual void Activate() { }
    public virtual void Deactivate() { }

    [RelayCommand]
    private Task Copy(string? text) => Shell.CopyAsync(text);
}

/// <summary>One entry of the left navigation.</summary>
public sealed partial class NavItem(string key, string label, string iconKey, object page) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string IconKey { get; } = iconKey;
    public object Page { get; } = page;
    [ObservableProperty] private string? _badge;
}
