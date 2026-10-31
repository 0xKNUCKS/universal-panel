using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Processes.Details;

// Memory tab: loaded when the tab opens (and on Refresh), off the UI thread. Reserved regions are
// hidden by default; "Only suspicious" narrows to executable memory without an image.
internal sealed partial class ProcessDetailsView
{
    private readonly ObservableCollection<MemoryRow> _memory = [];
    private ListCollectionView? _memoryView;
    private ProcessKey? _memoryLoadedFor;
    private int _memoryLoadVersion;

    private MemoryRow? SelectedRegion => MemoryGrid.SelectedItem as MemoryRow;

    private void InitializeMemory()
    {
        _memoryView = (ListCollectionView)CollectionViewSource.GetDefaultView(_memory);
        _memoryView.Filter = item =>
        {
            var row = (MemoryRow)item;
            return (ShowReserved.IsChecked == true || row.Committed)
                && (OnlySuspicious.IsChecked != true || row.Suspicious);
        };
        MemoryGrid.ItemsSource = _memoryView;
    }

    private void ResetMemory()
    {
        CloseStrings();
        _memoryLoadedFor = null;
        _memoryLoadVersion++;
        _memory.Clear();
        MemorySummary.Text = string.Empty;
        LoadingRing.Set(MemorySpinner, false);
    }

    private void LoadMemoryIfNeeded()
    {
        if (Tabs.SelectedItem == MemoryTab && Target is { } target && _memoryLoadedFor != target)
        {
            _ = Guard.RunAsync("Details.Memory", LoadMemoryAsync);
        }
    }

    private void OnRefreshMemory(object sender, RoutedEventArgs e) =>
        _ = Guard.RunAsync("Details.Memory", LoadMemoryAsync);

    private async Task LoadMemoryAsync()
    {
        if (Target is not { } target)
        {
            return;
        }

        int version = ++_memoryLoadVersion;
        string name = TargetName;
        _memoryLoadedFor = target;
        _memory.Clear();
        MemorySummary.Text = "Loading…";
        await LoadingRing.ShowAsync(MemorySpinner);

        List<MemoryRegion> regions;
        try
        {
            regions = await Task.Run(() => MemoryRegions.Capture(target, name));
        }
        catch (ProcessActionException ex)
        {
            if (version == _memoryLoadVersion)
            {
                MemorySummary.Text = ex.Message;
                LoadingRing.Set(MemorySpinner, false);
            }

            return;
        }

        if (version != _memoryLoadVersion || Target != target)
        {
            return;
        }

        foreach (var region in regions)
        {
            _memory.Add(new MemoryRow(region));
        }

        LoadingRing.Set(MemorySpinner, false);
        UpdateMemorySummary();
    }

    private void UpdateMemorySummary()
    {
        int suspicious = _memory.Count(r => r.Suspicious);
        int shown = _memoryView?.Count ?? 0;
        MemorySummary.Text = $"{shown} of {_memory.Count} regions" + (suspicious > 0 ? $" · {suspicious} suspicious" : string.Empty);
    }

    private void OnMemoryFilterChanged(object sender, RoutedEventArgs e)
    {
        // Checked fires during InitializeComponent for IsChecked="True"; nothing to filter yet.
        if (_memoryView is null)
        {
            return;
        }

        _memoryView.Refresh();
        UpdateMemorySummary();
    }

    private void OnMemoryRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(MemoryGrid, (DependencyObject)e.OriginalSource) is DataGridRow row)
        {
            MemoryGrid.SelectedItem = row.Item;
        }
    }

    private void OnMemoryContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (SelectedRegion is not { } region)
        {
            e.Handled = true;
            return;
        }

        DumpRegionItem.IsEnabled = region.Committed;
        StringsItem.IsEnabled = region.Committed;
    }

    private void OnMemoryDumpClick(object sender, RoutedEventArgs e)
    {
        if (MemoryDumpButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = MemoryDumpButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnMemoryFullDump(object sender, RoutedEventArgs e) => StartDump(DumpKind.Full);

    private void OnMemoryMiniDump(object sender, RoutedEventArgs e) => StartDump(DumpKind.Mini);

    private void StartDump(DumpKind kind)
    {
        if (Target is { } target)
        {
            _ = Guard.RunAsync("Details.Dump", () => DumpCommand.RunAsync(Host, this, target, TargetName, kind));
        }
    }

    private void OnCopyRegionAddress(object sender, RoutedEventArgs e)
    {
        if (SelectedRegion is { } region)
        {
            ClipboardText.Copy(Host, region.BaseText);
        }
    }

    private void OnDumpRegion(object sender, RoutedEventArgs e)
    {
        if (SelectedRegion is { } region && Target is { } target)
        {
            _ = Guard.RunAsync("Details.Memory.Dump", () => DumpRegionAsync(target, region));
        }
    }

    private async Task DumpRegionAsync(ProcessKey target, MemoryRow region)
    {
        string? path;
        using (Host.SuppressAutoHide())
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = $"Save region {region.BaseText} of {TargetName}",
                FileName = $"{Path.GetFileNameWithoutExtension(TargetName)}_{target.Pid}_{region.BaseText}.bin",
                DefaultExt = ".bin",
                Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            };
            path = dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
        }

        if (path is null)
        {
            return;
        }

        try
        {
            string name = TargetName;
            await Task.Run(() => MemoryRegions.DumpRegion(target, name, region.Base, region.Size, path));
            Host.ShowNotice($"Region saved ({region.SizeText})", path, InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is ProcessActionException or IOException)
        {
            Host.ShowNotice("Couldn't save the region", ex.Message, InfoBarSeverity.Warning);
        }
    }
}
