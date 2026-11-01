using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Settings;

/// <summary>What the Settings page can ask the app to do (implemented in App / PanelWindow).</summary>
internal sealed record SettingsActions(
    Func<KeyGesture> CurrentHotkey,
    Func<KeyGesture, string?> ApplyHotkey,
    Action ResetPanelWidth,
    Action Redock,
    Action OpenLogFolder,
    Action OpenSettingsFolder);

internal sealed class SettingsModule(SettingsActions actions) : IPanelModule
{
    private SettingsView? _view;

    public string Id => "settings";
    public string Title => "Settings";
    public SymbolRegular Icon => SymbolRegular.Settings24;

    public FrameworkElement CreateView(IModuleHost host) => _view = new SettingsView(host, actions);

    public void OnActivated() => _view?.Refresh();

    public void OnDeactivated() => _view?.CancelCapture();
}
