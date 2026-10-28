using System.ComponentModel;
using Avalonia.Controls;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>The live script debugger panel; keeps the current line in view while the script steps.</summary>
public partial class ScriptDebugView : UserControl
{
    private ScriptDebugViewModel? _vm;

    public ScriptDebugView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as ScriptDebugViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ScriptDebugViewModel.CurrentIndex) || _vm is not { CurrentIndex: >= 0 } vm || vm.CurrentIndex >= vm.Lines.Count) return;
        Source.ScrollIntoView(vm.CurrentIndex);
    }
}
