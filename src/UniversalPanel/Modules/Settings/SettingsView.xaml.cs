using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UniversalPanel.Diagnostics;
using UniversalPanel.Modules.Processes;
using UniversalPanel.Native;
using UniversalPanel.Shell;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Settings;

internal sealed partial class SettingsView : UserControl
{
    private readonly IModuleHost _host;
    private readonly SettingsActions _actions;
    private bool _loading;
    private bool _capturing;

    public SettingsView(IModuleHost host, SettingsActions actions)
    {
        _host = host;
        _actions = actions;
        InitializeComponent();
        Refresh();
    }

    private static PanelSettings Settings => PanelSettings.Current;

    /// <summary>Re-reads the current values (selection events are ignored while doing so).</summary>
    public void Refresh()
    {
        _loading = true;
        try
        {
            CancelCapture();
            Select(StartupBox, Settings.StartWithWindows);
            Select(RefreshBox, Settings.ProcessRefreshSeconds.ToString(CultureInfo.InvariantCulture));
            FillMonitors();
        }
        finally
        {
            _loading = false;
        }
    }

    private static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[0];

    private static string? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    // --- Hotkey ------------------------------------------------------------------------------

    private void OnHotkeyClick(object sender, RoutedEventArgs e)
    {
        _capturing = true;
        HotkeyButton.Content = "Press keys…";
        HotkeyHint.Text = "Press the new combination (with Ctrl, Alt, Shift or Win; F-keys work alone).";
    }

    public void CancelCapture()
    {
        _capturing = false;
        HotkeyButton.Content = _actions.CurrentHotkey().DisplayString;
        HotkeyHint.Text = "Shows or hides the panel from anywhere.";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key; // Alt combos arrive as Key.System
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return; // Wait for the actual key.
        }

        if (!HotkeyFormat.TryCreate(key, Keyboard.Modifiers, out var gesture))
        {
            HotkeyHint.Text = "That can't be a global hotkey. Add Ctrl, Alt, Shift or Win.";
            return;
        }

        var error = _actions.ApplyHotkey(gesture);
        CancelCapture();
        if (error is not null)
        {
            _host.ShowNotice("Hotkey not changed", error, InfoBarSeverity.Warning);
        }
        else
        {
            _host.ShowNotice($"Hotkey is now {gesture.DisplayString}", string.Empty, InfoBarSeverity.Success);
        }
    }

    // --- Start with Windows ------------------------------------------------------------------

    private void OnStartupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && TagOf(StartupBox) is { } mode && mode != Settings.StartWithWindows)
        {
            _ = Guard.RunAsync("Settings.Startup", () => ApplyStartupAsync(mode));
        }
    }

    private async Task ApplyStartupAsync(string mode)
    {
        StartupBox.IsEnabled = false;
        // A UAC prompt takes focus; without this the panel would auto-hide mid-change.
        using var keepOpen = _host.SuppressAutoHide();
        try
        {
            var error = await Task.Run(() => StartupRegistration.Apply(mode));
            if (error is null)
            {
                Settings.StartWithWindows = mode;
                Settings.SaveSoon();
                _host.ShowNotice("Startup setting saved", string.Empty, InfoBarSeverity.Success);
            }
            else
            {
                _host.ShowNotice("Startup setting not changed", error, InfoBarSeverity.Warning);
                Refresh(); // Back to what's actually in effect.
            }
        }
        finally
        {
            StartupBox.IsEnabled = true;
        }
    }

    // --- Monitor ------------------------------------------------------------------------------

    /// <summary>Cursor / primary, then every connected display; a saved display that isn't
    /// connected right now stays listed so the choice isn't silently lost.</summary>
    private void FillMonitors()
    {
        MonitorBox.Items.Clear();
        MonitorBox.Items.Add(new ComboBoxItem { Content = "Monitor with the mouse cursor", Tag = MonitorPlacement.UnderCursor });
        MonitorBox.Items.Add(new ComboBoxItem { Content = "Primary monitor", Tag = MonitorPlacement.Primary });
        var displays = MonitorPlacement.List();
        foreach (var display in displays)
        {
            MonitorBox.Items.Add(new ComboBoxItem { Content = display.Label, Tag = display.Device });
        }

        string saved = Settings.PanelMonitor;
        if (saved is not (MonitorPlacement.UnderCursor or MonitorPlacement.Primary)
            && !displays.Any(d => d.Device.Equals(saved, StringComparison.OrdinalIgnoreCase)))
        {
            MonitorBox.Items.Add(new ComboBoxItem { Content = $"{saved} (not connected: using primary)", Tag = saved });
        }

        Select(MonitorBox, saved);
    }

    private void OnMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && TagOf(MonitorBox) is { } choice && choice != Settings.PanelMonitor)
        {
            Settings.PanelMonitor = choice;
            Settings.SaveSoon();
            _actions.Redock(); // move there now, not on the next hotkey press
        }
    }

    // --- Refresh, width, logs ---------------------------------------------------------

    private void OnRefreshChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && TagOf(RefreshBox) is { } tag && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            Settings.ProcessRefreshSeconds = seconds;
            Settings.SaveSoon();
            ProcessSampler.Interval = TimeSpan.FromSeconds(seconds);
        }
    }

    private void OnResetWidthClick(object sender, RoutedEventArgs e) => _actions.ResetPanelWidth();

    private void OnOpenLogsClick(object sender, RoutedEventArgs e) => Guard.Run("Settings.OpenLogs", _actions.OpenLogFolder);
}
