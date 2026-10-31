using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UniversalPanel.Diagnostics;
using UniversalPanel.Modules.Processes;
using UniversalPanel.Native;
using UniversalPanel.Shell;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Tools;

// Launcher: double-click opens a tool with the user's normal rights (even when the panel is elevated);
// "Run as administrator" is per click and confirms first when no UAC prompt would appear.
internal sealed partial class ToolsView : UserControl
{
    private readonly IModuleHost _host;
    private readonly ObservableCollection<ToolRow> _rows = [];

    public ToolsView(IModuleHost host)
    {
        _host = host;
        InitializeComponent();
        ToolList.ItemsSource = _rows;
        foreach (var path in PanelSettings.Current.Tools)
        {
            _rows.Add(new ToolRow(path));
        }

        UpdateEmpty();
    }

    /// <summary>Re-reads identities and icons (tools get updated or moved while we're hidden).</summary>
    public void Refresh() => _ = Guard.RunAsync("Tools.Inspect", () => InspectAsync(_rows.ToList()));

    private static async Task InspectAsync(IEnumerable<ToolRow> rows)
    {
        foreach (var row in rows)
        {
            row.Apply(await Task.Run(row.Inspect));
        }
    }

    private ToolRow? Selected => ToolList.SelectedItem as ToolRow;

    // ---- launching ---------------------------------------------------------------------------

    // One click selects (ListBox), two open.
    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is ListBoxItem { DataContext: ToolRow row })
        {
            e.Handled = true;
            _ = Guard.RunAsync("Tools.Open", () => OpenAsync(row));
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (Selected is { } row)
        {
            if (e.Key == Key.Enter)
            {
                _ = Guard.RunAsync("Tools.Open", () => OpenAsync(row));
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                Remove(row);
                e.Handled = true;
            }
        }
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
        {
            _ = Guard.RunAsync("Tools.Open", () => OpenAsync(row));
        }
    }

    private void OnRunAsAdmin(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
        {
            _ = Guard.RunAsync("Tools.RunAsAdmin", () => RunAsAdminAsync(row));
        }
    }

    private async Task OpenAsync(ToolRow row)
    {
        if (!await CanStartAsync(row))
        {
            return;
        }

        try
        {
            ToolLauncher.Open(row.Path);
        }
        catch (ProcessActionException ex)
        {
            _host.ShowNotice($"Couldn't open {row.Name}", ex.Message, InfoBarSeverity.Warning);
        }
    }

    private async Task RunAsAdminAsync(ToolRow row)
    {
        if (!await CanStartAsync(row))
        {
            return;
        }

        // An elevated panel gets no UAC prompt, so this dialog is the only check left.
        if (Elevation.IsElevated && !await _host.ConfirmAsync(
                $"Run {row.Name} as administrator?",
                $"{row.Path}\n\n{(string.IsNullOrEmpty(row.Identity.Detail) ? "Not a recognized tool." : row.Identity.Detail)}\n\nThe panel is elevated, so Windows won't ask again.",
                "Run as administrator", destructive: false))
        {
            return;
        }

        try
        {
            using (_host.SuppressAutoHide())
            {
                ToolLauncher.RunAsAdministrator(row.Path);
            }
        }
        catch (ProcessActionException ex)
        {
            _host.ShowNotice($"Couldn't start {row.Name}", ex.Message, InfoBarSeverity.Warning);
        }
    }

    /// <summary>The file must still be there; a broken or foreign signature needs a yes first.</summary>
    private async Task<bool> CanStartAsync(ToolRow row)
    {
        row.Apply(await Task.Run(row.Inspect)); // the file may have changed since it was listed
        if (!row.Exists)
        {
            _host.ShowNotice($"{row.Name} isn't there anymore", row.Path, InfoBarSeverity.Warning);
            return false;
        }

        return row.Identity.Trust != ToolTrust.Warning || await _host.ConfirmAsync(
            $"{row.Name} doesn't look official",
            $"{row.Identity.Detail}\n\n{row.Path}",
            "Open anyway", destructive: true);
    }

    // ---- managing the list -------------------------------------------------------------------

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        string? path;
        using (_host.SuppressAutoHide())
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Add a tool",
                Filter = "Programs and scripts (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            };
            path = dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
        }

        if (path is not null)
        {
            Add([path]);
        }
    }

    // ---- find installed: scan, then the user picks what to add ------------------------------

    private readonly ObservableCollection<FoundTool> _found = [];

    private async void OnFindClick(object sender, RoutedEventArgs e)
    {
        FindButton.IsEnabled = false;
        await LoadingRing.ShowAsync(Spinner);
        try
        {
            await Guard.RunAsync("Tools.Find", async () =>
            {
                var paths = await Task.Run(() => ToolFinder.Find(CancellationToken.None));
                if (paths.Count == 0)
                {
                    _host.ShowNotice("No tools found", "Looked in Program Files, Documents, Desktop and Downloads. Use Add for anything else.", InfoBarSeverity.Informational);
                    return;
                }

                _found.Clear();
                foreach (var path in paths)
                {
                    bool listed = _rows.Any(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                    var found = new FoundTool(new ToolRow(path), listed);
                    found.PropertyChanged += (_, _) => UpdateAddFound();
                    _found.Add(found);
                }

                FoundList.ItemsSource = _found;
                FindTitle.Text = $"Found {paths.Count} tool{(paths.Count == 1 ? string.Empty : "s")}";
                UpdateAddFound();
                MainPage.Visibility = Visibility.Collapsed;
                FindPage.Visibility = Visibility.Visible;
                await InspectAsync(_found.Select(f => f.Tool).ToList());
            });
        }
        finally
        {
            LoadingRing.Set(Spinner, false);
            FindButton.IsEnabled = true;
        }
    }

    private void OnFoundClick(object sender, MouseButtonEventArgs e)
    {
        // A click anywhere on the row toggles it; the checkbox handles its own clicks.
        if (sender is ListBoxItem { DataContext: FoundTool found } && e.OriginalSource is not System.Windows.Controls.Primitives.ToggleButton
            && !IsInside<CheckBox>(e.OriginalSource as DependencyObject))
        {
            found.IsChecked = !found.IsChecked;
        }
    }

    private static bool IsInside<T>(DependencyObject? element)
        where T : DependencyObject
    {
        for (; element is not null; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is T)
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateAddFound()
    {
        int count = _found.Count(f => f.IsChecked);
        AddFoundButton.Content = count > 0 ? $"Add {count}" : "Add";
        AddFoundButton.IsEnabled = count > 0;
    }

    private void OnAddFound(object sender, RoutedEventArgs e)
    {
        Add(_found.Where(f => f.IsChecked).Select(f => f.Tool.Path));
        CloseFind();
    }

    private void OnFindBack(object sender, RoutedEventArgs e) => CloseFind();

    private void CloseFind()
    {
        FindPage.Visibility = Visibility.Collapsed;
        MainPage.Visibility = Visibility.Visible;
        _found.Clear();
    }

    /// <returns>How many were new.</returns>
    private int Add(IEnumerable<string> paths)
    {
        var added = new List<ToolRow>();
        foreach (var path in paths)
        {
            if (Path.IsPathFullyQualified(path) && !path.Contains('"')
                && !_rows.Any(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                var row = new ToolRow(path);
                _rows.Add(row);
                added.Add(row);
            }
        }

        if (added.Count > 0)
        {
            Save();
            _ = Guard.RunAsync("Tools.Inspect", () => InspectAsync(added));
        }

        return added.Count;
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
        {
            Remove(row);
        }
    }

    private void Remove(ToolRow row)
    {
        _rows.Remove(row);
        Save();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int by)
    {
        if (Selected is { } row && _rows.IndexOf(row) is var from && from + by >= 0 && from + by < _rows.Count)
        {
            _rows.Move(from, from + by);
            Save();
        }
    }

    private void OnOpenLocation(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
        {
            ClipboardText.Reveal(_host, row.Path);
        }
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
        {
            ClipboardText.Copy(_host, row.Path);
        }
    }

    private void Save()
    {
        PanelSettings.Current.Tools = _rows.Select(r => r.Path).ToList();
        PanelSettings.Current.SaveSoon();
        UpdateEmpty();
    }

    private void UpdateEmpty() => EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
}
