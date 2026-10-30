using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Processes.Details;

// Handles tab. Loaded lazily the first time the tab is opened for a process (and on Refresh), off
// the UI thread. "Hide unnamed" is on by default, as in System Informer.
internal sealed partial class ProcessDetailsView
{
    private readonly ObservableCollection<HandleRow> _handles = [];
    private ListCollectionView? _handlesView;
    private IReadOnlyList<ProcessSample> _lastProcesses = [];
    private ProcessKey? _handlesLoadedFor;
    private int _handlesLoadVersion;
    private string _handleFilter = string.Empty;

    private HandleRow? SelectedHandle => HandlesGrid.SelectedItem as HandleRow;

    private void InitializeHandles()
    {
        _handlesView = (ListCollectionView)CollectionViewSource.GetDefaultView(_handles);
        _handlesView.Filter = MatchesHandleFilter;
        _handlesView.SortDescriptions.Add(new SortDescription(nameof(HandleRow.Type), ListSortDirection.Ascending));
        _handlesView.SortDescriptions.Add(new SortDescription(nameof(HandleRow.Name), ListSortDirection.Ascending));
        HandlesGrid.ItemsSource = _handlesView;
    }

    private void ResetHandles()
    {
        _handlesLoadedFor = null;
        _handlesLoadVersion++; // Orphans any in-flight load for the previous process.
        LoadingRing.Set(HandlesSpinner, false);
        _handles.Clear();
        HandlesSummary.Text = string.Empty;
    }

    private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == Tabs && Tabs.SelectedItem == HandlesTab && Target is { } target && _handlesLoadedFor != target)
        {
            _ = Guard.RunAsync("Details.Handles", LoadHandlesAsync);
        }
    }

    private void OnRefreshHandles(object sender, RoutedEventArgs e) =>
        _ = Guard.RunAsync("Details.Handles", LoadHandlesAsync);

    private async Task LoadHandlesAsync()
    {
        if (Target is not { } target)
        {
            return;
        }

        int version = ++_handlesLoadVersion;
        string name = TargetName;
        _handlesLoadedFor = target;
        HandlesSummary.Text = "Loading…";
        _handles.Clear(); // Stale rows under the spinner read as "done"; show a clean loading state.
        await LoadingRing.ShowAsync(HandlesSpinner);

        // PID -> name for "Process" and "Thread" handles, from the latest list sample.
        var names = new Dictionary<int, string>(_lastProcesses.Count);
        foreach (var process in _lastProcesses)
        {
            names.TryAdd(process.Record.Key.Pid, process.Record.Name);
        }

        List<HandleRecord> records;
        try
        {
            records = await Task.Run(() => HandleSnapshot.Capture(target, name, names));
        }
        catch (ProcessActionException ex)
        {
            if (version == _handlesLoadVersion)
            {
                _handles.Clear();
                HandlesSummary.Text = ex.Message;
                LoadingRing.Set(HandlesSpinner, false);
            }

            return;
        }

        // A newer load (which owns the spinner now) or another process took over meanwhile.
        if (version != _handlesLoadVersion || Target != target)
        {
            return;
        }

        // Measured: ~125 ms for 8,400 rows on the UI thread; the wait is the capture, not this.
        _handles.Clear();
        foreach (var record in records)
        {
            _handles.Add(new HandleRow(record));
        }

        LoadingRing.Set(HandlesSpinner, false);
        UpdateHandlesSummary();
    }

    private void UpdateHandlesSummary()
    {
        int shown = _handlesView?.Count ?? 0;
        HandlesSummary.Text = shown == _handles.Count ? $"{_handles.Count} handles" : $"{shown} of {_handles.Count} handles";
    }

    private bool MatchesHandleFilter(object item)
    {
        var handle = (HandleRow)item;
        if (HideUnnamed.IsChecked == true && !handle.IsNamed)
        {
            return false;
        }

        return _handleFilter.Length == 0
            || handle.Name.Contains(_handleFilter, StringComparison.OrdinalIgnoreCase)
            || handle.Type.Contains(_handleFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void OnHandleFilterChanged(object sender, RoutedEventArgs e)
    {
        // HideUnnamed's IsChecked="True" raises Checked during InitializeComponent, before the
        // rest of the tab exists; there's nothing to filter yet.
        if (_handlesView is null)
        {
            return;
        }

        _handleFilter = HandleSearch.Text.Trim();
        _handlesView?.Refresh();
        UpdateHandlesSummary();
    }

    private void OnHandlesRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(HandlesGrid, (DependencyObject)e.OriginalSource) is DataGridRow row)
        {
            HandlesGrid.SelectedItem = row.Item;
        }
    }

    private void OnHandlesContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (SelectedHandle is null)
        {
            e.Handled = true;
        }
    }

    private void OnHandlesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && SelectedHandle is { } handle)
        {
            e.Handled = true;
            _ = Guard.RunAsync("Details.Handles.Close", () => CloseHandleAsync(handle));
        }
    }

    private void OnCopyHandleName(object sender, RoutedEventArgs e)
    {
        if (SelectedHandle is { IsNamed: true } handle)
        {
            ClipboardText.Copy(Host, handle.Name);
        }
    }

    private void OnCloseHandle(object sender, RoutedEventArgs e)
    {
        if (SelectedHandle is { } handle)
        {
            _ = Guard.RunAsync("Details.Handles.Close", () => CloseHandleAsync(handle));
        }
    }

    private async Task CloseHandleAsync(HandleRow handle)
    {
        if (Target is not { } process)
        {
            return;
        }

        var risk = ProcessGuard.Assess(process, TargetName);
        if (risk == ProcessRisk.Self)
        {
            Host.ShowNotice("That's the panel itself", "Closing one of its own handles out from under it can crash the panel.", InfoBarSeverity.Informational);
            return;
        }

        var what = handle.IsNamed ? $"{handle.Type} handle {handle.ValueText} ({handle.Name})" : $"{handle.Type} handle {handle.ValueText}";
        var (title, message) = risk == ProcessRisk.Critical
            ? ("Close handle in critical process?", $"Closing {what} in {TargetName}, a critical system process, can crash Windows.")
            : ("Close handle?", $"Close {what} in {TargetName}? If the process is still using it, it can crash or lose data.");

        if (!await Host.ConfirmAsync(title, message, "Close handle", destructive: true))
        {
            return;
        }

        try
        {
            HandleSnapshot.CloseRemote(process, TargetName, handle.Value);
            Host.ShowNotice($"Closed handle {handle.ValueText}", string.Empty, InfoBarSeverity.Success);
        }
        catch (ProcessActionException ex)
        {
            Host.ShowNotice("Couldn't complete that", ex.Message, ex.Failure == ProcessActionFailure.Exited ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
            return;
        }

        await LoadHandlesAsync();
    }
}
