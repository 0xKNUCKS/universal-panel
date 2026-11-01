using System.Windows;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Terminal;

internal sealed class TerminalModule : IPanelModule
{
    private TerminalView? _view;

    public string Id => "terminal";
    public string Title => "Terminal";
    public SymbolRegular Icon => SymbolRegular.WindowConsole20;

    public FrameworkElement CreateView(IModuleHost host) => _view = new TerminalView(host);

    public void OnActivated() => _view?.OnShown();

    // Shells keep running while the panel is hidden (that's the point of a drop-down terminal).
    public void OnDeactivated() { }
}
