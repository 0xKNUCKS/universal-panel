using System.Windows;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Terminal;

internal sealed class TerminalModule : IPanelModule
{
    private TerminalView? _view;

    // Shells are found in the background at startup, so "Terminal ›" menus elsewhere are ready.
    public TerminalModule() => _ = TerminalProfiles.GetAsync();

    public const string ModuleId = "terminal";

    public string Id => ModuleId;
    public string Title => "Terminal";
    public SymbolRegular Icon => SymbolRegular.WindowConsole20;

    public FrameworkElement CreateView(IModuleHost host) => _view = new TerminalView(host);

    public void OnActivated() => _view?.Conceal();

    public void OnShown() => _view?.Reveal();

    // Shells keep running while the panel is hidden (that's the point of a drop-down terminal).
    public void OnDeactivated() { }
}
