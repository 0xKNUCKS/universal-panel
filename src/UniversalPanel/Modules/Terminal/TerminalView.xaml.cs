using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;
using UniversalPanel.Shell;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Terminal;

// Terminal tab: shells in tabs. They run in the terminal host with the user's normal rights and
// keep running while the panel is hidden; the panel only draws them.
internal sealed partial class TerminalView : UserControl
{
    private readonly IModuleHost _host;
    private readonly ObservableCollection<TerminalTab> _tabs = [];
    private List<TerminalProfile>? _profiles;

    public TerminalView(IModuleHost host)
    {
        _host = host;
        InitializeComponent();
        Tabs.ItemsSource = _tabs;
        UpdateEmpty();
        _ = Guard.RunAsync("Terminal.Profiles", LoadProfilesAsync);
    }

    private TerminalTab? Selected => Tabs.SelectedItem as TerminalTab;

    // The terminal is a native child window (HwndHost), which can't follow the panel's slide-in
    // animation: hidden while the panel slides in, shown and focused once it's done.
    public void Conceal() => Surfaces.Visibility = Visibility.Hidden;

    public void Reveal()
    {
        Surfaces.Visibility = Visibility.Visible;
        Selected?.Focus();
    }

    private async Task LoadProfilesAsync()
    {
        _profiles = await Task.Run(TerminalProfiles.Detect);
        QuickStart.ItemsSource = _profiles;
        RestoreTabs();
    }

    // ---- opening and closing -------------------------------------------------------------------

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (NewButton.ContextMenu is not { } menu)
        {
            return;
        }

        menu.Items.Clear();
        if (_profiles is null)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Looking for shells…", IsEnabled = false });
        }
        else
        {
            foreach (var profile in _profiles)
            {
                menu.Items.Add(ProfileItem(profile, admin: null));
            }

            // Admin shells only from an elevated panel (see TerminalHost.GetAsync).
            var asAdmin = new System.Windows.Controls.MenuItem
            {
                Header = "Run as administrator",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Shield16 },
                IsEnabled = Elevation.IsElevated,
                ToolTip = Elevation.IsElevated ? null : "Restart the panel as administrator first",
            };
            ToolTipService.SetShowOnDisabled(asAdmin, true);
            foreach (var profile in _profiles)
            {
                asAdmin.Items.Add(ProfileItem(profile, admin: true));
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(asAdmin);
        }

        menu.PlacementTarget = NewButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <param name="admin">null: Shift+click opens it as administrator.</param>
    private System.Windows.Controls.MenuItem ProfileItem(TerminalProfile profile, bool? admin)
    {
        var item = new System.Windows.Controls.MenuItem
        {
            Header = profile.Name,
            Icon = new System.Windows.Controls.Image { Source = profile.Icon, Width = 16, Height = 16 },
        };
        item.Click += (_, _) => Open(profile, admin ?? ShiftHeld());
        return item;
    }

    private void OnQuickStart(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TerminalProfile profile })
        {
            Open(profile, ShiftHeld());
        }
    }

    private static bool ShiftHeld() => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

    private void Open(TerminalProfile profile, bool admin, string? name = null)
    {
        if (TerminalRenderer.Problem() is { } problem)
        {
            _host.ShowNotice("The terminal can't start", problem, InfoBarSeverity.Error);
            return;
        }

        if (admin && !Elevation.IsElevated)
        {
            _host.ShowNotice("Panel isn't elevated", "Restart it as administrator from the tray to open admin terminals.", InfoBarSeverity.Warning);
            return;
        }

        Guard.Run("Terminal.Open", () =>
        {
            var tab = TerminalTab.Create(profile, Dispatcher, admin);
            if (name is not null)
            {
                tab.Name = name;
            }

            tab.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TerminalTab.Name))
                {
                    SaveTabs();
                }
            };
            _tabs.Add(tab);
            Surfaces.Children.Add(tab.Surface);
            Tabs.SelectedItem = tab;
            UpdateEmpty();
            SaveTabs();
        });
    }

    /// <summary>Reopens the tabs saved last time (shells start fresh; admin tabs aren't saved).</summary>
    private void RestoreTabs()
    {
        if (_profiles is null || _tabs.Count > 0)
        {
            return;
        }

        foreach (var saved in PanelSettings.Current.TerminalTabs.ToList())
        {
            if (_profiles.FirstOrDefault(p => p.Name == saved.Profile) is { } profile)
            {
                Open(profile, admin: false, saved.Name);
            }
        }
    }

    private void SaveTabs()
    {
        PanelSettings.Current.TerminalTabs = _tabs
            .Where(t => !t.Admin)
            .Select(t => new SavedTerminalTab(t.Session.Profile.Name, t.Name))
            .ToList();
        PanelSettings.Current.SaveSoon();
    }

    private void OnCloseTabButton(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TerminalTab tab })
        {
            Close(tab);
        }
    }

    private void OnCloseTab(object sender, RoutedEventArgs e)
    {
        if (Selected is { } tab)
        {
            Close(tab);
        }
    }

    private void Close(TerminalTab tab)
    {
        int index = _tabs.IndexOf(tab);
        tab.Session.Close();
        Surfaces.Children.Remove(tab.Surface);
        _tabs.Remove(tab);
        if (_tabs.Count > 0)
        {
            Tabs.SelectedIndex = Math.Clamp(index, 0, _tabs.Count - 1);
        }

        UpdateEmpty();
        SaveTabs();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var tab in _tabs)
        {
            tab.Surface.Visibility = tab == Selected ? Visibility.Visible : Visibility.Collapsed;
        }

        Selected?.Focus();
    }

    private void UpdateEmpty()
    {
        bool empty = _tabs.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Tabs.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- renaming ------------------------------------------------------------------------------

    private void OnTabDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: TerminalTab tab })
        {
            StartRename(tab);
            e.Handled = true;
        }
    }

    private void OnRenameTab(object sender, RoutedEventArgs e)
    {
        if (Selected is { } tab)
        {
            StartRename(tab);
        }
    }

    private void StartRename(TerminalTab tab)
    {
        tab.IsRenaming = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (Tabs.ItemContainerGenerator.ContainerFromItem(tab) is ListBoxItem item && FindRenameBox(item) is { } box)
            {
                box.Text = tab.Name;
                box.Focus();
                box.SelectAll();
            }
        }, DispatcherPriority.Input);
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox { DataContext: TerminalTab tab } box && e.Key is Key.Enter or Key.Escape)
        {
            if (e.Key == Key.Enter)
            {
                tab.Name = box.Text;
            }

            tab.IsRenaming = false;
            e.Handled = true;
            tab.Focus();
        }
    }

    private void OnRenameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox { DataContext: TerminalTab { IsRenaming: true } tab } box)
        {
            tab.Name = box.Text;
            tab.IsRenaming = false;
        }
    }

    private static System.Windows.Controls.TextBox? FindRenameBox(DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.TextBox { Name: "RenameBox" } box)
            {
                return box;
            }

            if (FindRenameBox(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
