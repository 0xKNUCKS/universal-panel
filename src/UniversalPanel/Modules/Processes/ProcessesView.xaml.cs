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
    private string _filter = string.Empty;

    public ProcessesView(IModuleHost host, ProcessSampler sampler)
    {
        Host = host;
        InitializeComponent();

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = MatchesFilter;
        _view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Name), ListSortDirection.Ascending));
        // Live sorting only for values that change on their own; Name and PID never do.
        _view.IsLiveSorting = true;
        foreach (var property in LiveSortProperties)
        {
            _view.LiveSortingProperties.Add(property);
        }

        Grid.ItemsSource = _view;

        _filterDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Input, (_, _) =>
        {
            _filterDebounce!.Stop();
            _view.Refresh();
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

        Summary.Text = $"{frame.Processes.Count} processes · CPU {frame.SystemCpuPercent:0}%";
    }

    private bool MatchesFilter(object item)
    {
        if (_filter.Length == 0)
        {
            return true;
        }

        var row = (ProcessRow)item;
        return row.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || row.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture).StartsWith(_filter, StringComparison.Ordinal);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text.Trim();
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        // DataGrid replaces SortDescriptions itself; keep live sorting aware of the new key.
        var path = e.Column.SortMemberPath;
        if (!_view.LiveSortingProperties.Contains(path) && LiveSortProperties.Contains(path))
        {
            _view.LiveSortingProperties.Add(path);
        }
    }
}
