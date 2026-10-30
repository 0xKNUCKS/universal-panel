using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Processes.Details;

// Modules tab. Loaded once per Show (and on Refresh), off the UI thread; also feeds start-address
// resolution for the Threads tab.
internal sealed partial class ProcessDetailsView
{
    private readonly ObservableCollection<ModuleRow> _modules = [];
    private IReadOnlyList<ModuleRecord> _moduleRecords = [];
    private int _modulesLoadVersion;

    private ModuleRow? SelectedModule => ModulesGrid.SelectedItem as ModuleRow;

    private async Task LoadModulesAsync()
    {
        if (Target is not { } target)
        {
            return;
        }

        int version = ++_modulesLoadVersion;
        string name = TargetName;
        ModulesSummary.Text = "Loading…";
        // Rows only: the records keep resolving thread start addresses until the new list lands.
        _modules.Clear(); // Stale rows under the spinner read as "done"; show a clean loading state.
        await LoadingRing.ShowAsync(ModulesSpinner);

        List<ModuleRecord> records;
        try
        {
            records = await Task.Run(() => ModuleSnapshot.Capture(target, name));
        }
        catch (ProcessActionException ex)
        {
            if (version == _modulesLoadVersion)
            {
                _modules.Clear();
                _moduleRecords = [];
                ModulesSummary.Text = ex.Message;
                LoadingRing.Set(ModulesSpinner, false);
            }

            return;
        }

        // A newer load (which owns the spinner now) or another process took over meanwhile.
        if (version != _modulesLoadVersion || Target != target)
        {
            return;
        }

        _moduleRecords = records;
        _modules.Clear();
        foreach (var record in records)
        {
            _modules.Add(new ModuleRow(record));
        }

        LoadingRing.Set(ModulesSpinner, false);
        ModulesSummary.Text = $"{records.Count} modules";

        // Start addresses can now be shown as module+offset.
        foreach (var thread in _threads)
        {
            thread.RefreshStart(DescribeAddress);
        }
    }

    private string DescribeAddress(ulong? address) => address switch
    {
        null => "(unavailable)",
        { } a => ModuleSnapshot.Describe(_moduleRecords, a) ?? "0x" + a.ToString("X", System.Globalization.CultureInfo.InvariantCulture),
    };

    private void OnRefreshModules(object sender, RoutedEventArgs e) =>
        _ = Guard.RunAsync("Details.Modules", LoadModulesAsync);

    private void OnModulesRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ModulesGrid, (DependencyObject)e.OriginalSource) is DataGridRow row)
        {
            ModulesGrid.SelectedItem = row.Item;
        }
    }

    private void OnModulesContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (SelectedModule is null)
        {
            e.Handled = true;
        }
    }

    private void OnModuleOpenLocation(object sender, RoutedEventArgs e)
    {
        if (SelectedModule is { } module)
        {
            Guard.Run("Details.Modules.OpenLocation", () => ShellReveal.SelectInExplorer(module.Path));
        }
    }

    private void OnModuleCopyPath(object sender, RoutedEventArgs e)
    {
        if (SelectedModule is { } module)
        {
            ClipboardText.Copy(Host, module.Path);
        }
    }

    private void OnModuleCopyBase(object sender, RoutedEventArgs e)
    {
        if (SelectedModule is { } module)
        {
            ClipboardText.Copy(Host, module.BaseText);
        }
    }
}
