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

// Launcher: click opens a tool with the user's normal rights (even when the panel is elevated);
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

    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: ToolRow row })
        {
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

    private async void OnFindClick(object sender, RoutedEventArgs e)
    {
        FindButton.IsEnabled = false;
        await LoadingRing.ShowAsync(Spinner);
        try
        {
            await Guard.RunAsync("Tools.Find", async () =>
            {
                var found = await Task.Run(() => ToolFinder.Find(CancellationToken.None));
                int added = Add(found);
                _host.ShowNotice(
                    added > 0 ? $"Added {added} tool{(added == 1 ? string.Empty : "s")}" : "No new tools found",
                    added > 0 ? string.Empty : "Looked in Program Files, Documents, Desktop and Downloads. Use Add for anything else.",
                    InfoBarSeverity.Informational);
            });
        }
        finally
        {
            LoadingRing.Set(Spinner, false);
            FindButton.IsEnabled = true;
        }
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
