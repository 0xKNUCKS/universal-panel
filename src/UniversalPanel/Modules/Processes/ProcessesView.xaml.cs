using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Processes;

internal sealed partial class ProcessesView : UserControl
{
    private static readonly string[] LiveSortProperties = [nameof(ProcessRow.Cpu), nameof(ProcessRow.PrivateBytes)];

    private readonly ObservableCollection<ProcessRow> _rows = [];
    private readonly Dictionary<ProcessKey, ProcessRow> _byKey = [];
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _filterDebounce;
    private readonly ProcessSampler _sampler;
    private string _filter = string.Empty;

    public ProcessesView(IModuleHost host, ProcessSampler sampler)
    {
        Host = host;
        _sampler = sampler;
        InitializeComponent();

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = MatchesFilter;
        _view.IsLiveSorting = true;
        ApplyViewMode(); // Tree mode by default (ProcessesView.Tree.cs).

        Grid.ItemsSource = _view;
        BuildPriorityMenu();
        LoadingRing.Set(ListSpinner, true); // Until the first sample arrives.

        _filterDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Input, (_, _) =>
        {
            _filterDebounce!.Stop();
            // Starting or clearing a search switches between flat and tree mode.
            if (TreeActive != _treeApplied)
            {
                ApplyViewMode();
            }
            else
            {
                _view.Refresh();
            }
        }, Dispatcher);
        _filterDebounce.Stop();

        sampler.Sampled += frame => Dispatcher.InvokeAsync(() => Apply(frame), DispatcherPriority.Background);
    }

    internal IModuleHost Host { get; }

    private void Apply(ProcessFrame frame)
    {
        var alive = new HashSet<ProcessKey>(frame.Processes.Count);
        foreach (var sample in frame.Processes)
        {
            alive.Add(sample.Record.Key);
            if (_byKey.TryGetValue(sample.Record.Key, out var row))
            {
                row.Update(sample);
            }
            else
            {
                row = new ProcessRow(sample);
                row.SetTreeVisible(_treeApplied);
                _byKey.Add(row.Key, row);
                _rows.Add(row);
            }
        }

        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            if (!alive.Contains(_rows[i].Key))
            {
                _byKey.Remove(_rows[i].Key);
                _rows.RemoveAt(i);
            }
        }

        UpdateTree(alive);
        LoadingRing.Set(ListSpinner, false);
        Summary.Text = DescribeSystem(frame);

        if (DetailsOpen)
        {
            _details?.Apply(frame);
        }
    }

    private static string DescribeSystem(ProcessFrame frame)
    {
        long threads = 0, handles = 0;
        foreach (var process in frame.Processes)
        {
            threads += process.Record.Threads;
            handles += process.Record.Handles;
        }

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var text = string.Create(culture,
            $"CPU {frame.SystemCpuPercent:0.00}% | {frame.Processes.Count:N0} processes | {threads:N0} threads | {handles:N0} handles");

        if (frame.MemoryTotal > 0)
        {
            ulong used = frame.MemoryTotal - frame.MemoryAvailable;
            text += string.Create(culture,
                $" | Memory {100.0 * used / frame.MemoryTotal:0.00}% | {ProcessRow.FormatBytes(used)} of {ProcessRow.FormatBytes(frame.MemoryTotal)}");
        }

        return text;
    }

    private bool MatchesFilter(object item)
    {
        var row = (ProcessRow)item;
        if (_treeApplied)
        {
            return !row.IsHiddenByCollapse;
        }

        if (_filter.Length == 0)
        {
            return true;
        }

        return row.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || row.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture).StartsWith(_filter, StringComparison.Ordinal);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text.Trim();
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private void OnRowToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is DataGridRow { Item: ProcessRow row } container)
        {
            container.ToolTip = row.Details;
        }
    }
}
