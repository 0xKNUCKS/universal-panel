using System.Windows;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Tools;

internal sealed class ToolsModule : IPanelModule
{
    private ToolsView? _view;

    public string Id => "launcher";
    public string Title => "Tools";
    public SymbolRegular Icon => SymbolRegular.Apps24;

    public FrameworkElement CreateView(IModuleHost host) => _view = new ToolsView(host);

    public void OnActivated() => _view?.Refresh();

    public void OnDeactivated() { }
}
