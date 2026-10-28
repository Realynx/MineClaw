using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>The Controller panel of a tile (its "Controller" button): the brain's context, the owner's brain, or the script.</summary>
public sealed partial class LiveHiveViewModel
{
    /// <summary>The open Controller panel, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasController))]
    private ControllerPanelViewModel? _controller;
    public bool HasController => Controller != null;

    /// <summary>Opens the Controller panel on that body (polls ≈ 1.4 Hz while open and the tab is shown).</summary>
    public void OpenController(BotTileViewModel t) => OpenController(t.Key, t.Title);

    /// <summary>Opens it on a body by name ("main", "67"...): also the subhive panel's "Full hive view".</summary>
    public void OpenController(string body, string title)
    {
        if (_api is not { } api) return;
        Controller?.Dispose();
        var c = new ControllerPanelViewModel(new HiveControllerSource(api), body, title, () => new HiveScriptDebugSource(api));
        c.OpenOwnerRequested += owner => OpenController(owner, owner == "main" ? "main · the owning hive" : owner);
        Controller = c;
        if (_active) c.Start();
    }

    [RelayCommand]
    private void CloseController()
    {
        Controller?.Dispose();
        Controller = null;
    }
}
