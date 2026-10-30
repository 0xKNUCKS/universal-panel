using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Processes.Details;

/// <summary>
/// Per-process page: threads (live, from the shared sampler's snapshot), modules and handles.
/// One instance is reused; <see cref="Show"/> retargets it.
/// </summary>
internal sealed partial class ProcessDetailsView : UserControl
{
    private readonly ObservableCollection<ThreadRow> _threads = [];
    private readonly Dictionary<ThreadKey, ThreadRow> _threadsByKey = [];
    private readonly ListCollectionView _threadsView;
    private readonly Action _back;
    private bool _sortedOnce;

    public ProcessDetailsView(IModuleHost host, Action back)
    {
        Host = host;
        _back = back;
        InitializeComponent();

        _threadsView = (ListCollectionView)CollectionViewSource.GetDefaultView(_threads);
        _threadsView.SortDescriptions.Add(new SortDescription(nameof(ThreadRow.Cpu), ListSortDirection.Descending));
        _threadsView.SortDescriptions.Add(new SortDescription(nameof(ThreadRow.Tid), ListSortDirection.Ascending));
        ThreadsGrid.ItemsSource = _threadsView;
        ModulesGrid.ItemsSource = _modules;
    }

    internal IModuleHost Host { get; }

    public ProcessKey? Target { get; private set; }

    public string TargetName { get; private set; } = string.Empty;

    public void Show(ProcessRow process)
    {
        Target = process.Key;
        TargetName = process.Name;
        ProcessName.Text = process.Name;
        ProcessInfo.Text = $"PID {process.Pid}" + (process.ImagePath is { } path ? $" · {path}" : string.Empty);
        ProcessInfo.ToolTip = process.ImagePath;

        _threads.Clear();
        _threadsByKey.Clear();
        _sortedOnce = false;
        ThreadsSummary.Text = "Loading…";
        Tabs.SelectedIndex = 0;

        _modules.Clear();
        _moduleRecords = [];
        _ = Guard.RunAsync("Details.Modules", LoadModulesAsync);
    }

    /// <summary>Moves keyboard focus into the page so Backspace and the grid keys reach it.</summary>
    public void FocusContent() =>
        Dispatcher.BeginInvoke(() => ThreadsGrid.Focus(), System.Windows.Threading.DispatcherPriority.Input);

    /// <summary>Applies a sampler frame; ignored unless it's for the process being shown.</summary>
    public void Apply(ProcessFrame frame)
    {
        if (Target is not { } target || frame.Focus != target)
        {
            return;
        }

        if (frame.FocusedThreads is not { } threads)
        {
            ThreadsSummary.Text = $"{TargetName} has exited.";
            _threads.Clear();
            _threadsByKey.Clear();
            return;
        }

        var alive = new HashSet<ThreadKey>(threads.Count);
        foreach (var sample in threads)
        {
            var key = new ThreadKey(sample.Record.Tid, sample.Record.CreateTime);
            alive.Add(key);
            if (_threadsByKey.TryGetValue(key, out var row))
            {
                row.Update(sample);
            }
            else
            {
                row = new ThreadRow(sample, DescribeAddress);
                _threadsByKey.Add(key, row);
                _threads.Add(row);
            }
        }

        for (int i = _threads.Count - 1; i >= 0; i--)
        {
            if (!alive.Contains(_threads[i].Key))
            {
                _threadsByKey.Remove(_threads[i].Key);
                _threads.RemoveAt(i);
            }
        }

        // Re-sorting by CPU every tick would make rows jump under the cursor; sort once per load
        // and when the user clicks a header (DataGrid handles that).
        if (!_sortedOnce && _threads.Count > 0)
        {
            _sortedOnce = true;
            _threadsView.Refresh();
        }

        ThreadsSummary.Text = $"{threads.Count} threads";
    }

    private void OnBackClick(object sender, System.Windows.RoutedEventArgs e) => _back();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back && e.OriginalSource is not TextBox)
        {
            e.Handled = true;
            _back();
        }
    }

    private void OnRowToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is DataGridRow container)
        {
            container.ToolTip = container.Item switch
            {
                ThreadRow thread => thread.Details,
                ModuleRow module => $"{module.Path}\nBase {module.BaseText} · size {module.SizeText}",
                _ => null,
            };
        }
    }
}
