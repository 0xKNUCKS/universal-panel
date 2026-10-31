using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UniversalPanel.Native;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Processes;

// Parent/child tree. The list stays flat (like System Informer's FlatList): in tree mode rows are sorted by their
// position in the flattened tree (TreeOrder) and rows under a collapsed parent are filtered out.
// Header clicks sort siblings within each parent, as in System Informer; a third click returns to
// creation order. Searching shows a flat list of matches.
internal sealed partial class ProcessesView
{
    private readonly HashSet<ProcessKey> _collapsed = [];
    private bool _treeEnabled = true;
    private bool _treeApplied;
    private string? _sortPath;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    private bool TreeActive => _treeEnabled && _filter.Length == 0;

    /// <summary>Re-points sorting/filtering at tree or flat mode. Called on mode changes only.</summary>
    private void ApplyViewMode()
    {
        _treeApplied = TreeActive;

        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            _view.LiveSortingProperties.Clear();
            _view.LiveFilteringProperties.Clear();

            if (_treeApplied)
            {
                _view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.TreeOrder), ListSortDirection.Ascending));
                _view.LiveSortingProperties.Add(nameof(ProcessRow.TreeOrder));
                _view.IsLiveFiltering = true;
                _view.LiveFilteringProperties.Add(nameof(ProcessRow.IsHiddenByCollapse));
            }
            else
            {
                var path = _sortPath ?? nameof(ProcessRow.Name);
                _view.SortDescriptions.Add(new SortDescription(path, _sortPath is null ? ListSortDirection.Ascending : _sortDirection));
                // Live sorting watches only the active key, and only keys that change on their own.
                if (LiveSortProperties.Contains(path))
                {
                    _view.LiveSortingProperties.Add(path);
                }

                _view.IsLiveFiltering = false;
            }

            foreach (var row in _rows)
            {
                row.SetTreeVisible(_treeApplied);
            }

            Relayout();
        }
    }

    private void Relayout()
    {
        if (_treeApplied)
        {
            ProcessTree.Layout(_rows, _collapsed, CompareSiblings);
        }
    }

    /// <summary>After each sample: forget collapsed state of exited processes, then re-layout.</summary>
    private void UpdateTree(HashSet<ProcessKey> alive)
    {
        _collapsed.RemoveWhere(key => !alive.Contains(key));
        Relayout();
    }

    // Live filtering hides / shows rows one by one, so it's cheapest for a small subtree. Above
    // this, one deferred refresh is cheaper: it rebuilds just the ~30 visible rows, and with the
    // light ListBox rows that's ~30 ms (it was ~250 ms with DataGrid rows).
    private const int RefreshAboveRows = 60;

    /// <summary>Expand / collapse re-layout: live filtering for small subtrees, one refresh for big ones.</summary>
    private void RelayoutForToggle(ProcessRow row)
    {
        if (SubtreeSize(row) <= RefreshAboveRows)
        {
            Relayout();
            return;
        }

        using (_view.DeferRefresh())
        {
            Relayout();
        }
    }

    /// <summary>Rows under <paramref name="row"/>: everything after it in tree order until depth returns to its level.</summary>
    private int SubtreeSize(ProcessRow row)
    {
        int end = _rows.Where(r => r.TreeOrder > row.TreeOrder && r.Depth <= row.Depth).Select(r => r.TreeOrder).DefaultIfEmpty(int.MaxValue).Min();
        return _rows.Count(r => r.TreeOrder > row.TreeOrder && r.TreeOrder < end);
    }

    private int CompareSiblings(ProcessRow a, ProcessRow b)
    {
        int result = _sortPath switch
        {
            nameof(ProcessRow.Name) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            nameof(ProcessRow.Pid) => a.Pid.CompareTo(b.Pid),
            nameof(ProcessRow.Cpu) => a.Cpu.CompareTo(b.Cpu),
            nameof(ProcessRow.PrivateBytes) => a.PrivateBytes.CompareTo(b.PrivateBytes),
            _ => a.Key.CreateTime.CompareTo(b.Key.CreateTime), // natural order: creation
        };

        if (_sortPath is not null && _sortDirection == ListSortDirection.Descending)
        {
            result = -result;
        }

        // Stable tie-break so equal rows never swap places between ticks.
        return result != 0 ? result : a.Pid.CompareTo(b.Pid);
    }

    /// <summary>Header click: ascending -> descending -> back to natural order, in both modes.</summary>
    private void ApplySortClick(string path)
    {
        if (path != _sortPath)
        {
            _sortPath = path;
            _sortDirection = ListSortDirection.Ascending;
        }
        else if (_sortDirection == ListSortDirection.Ascending)
        {
            _sortDirection = ListSortDirection.Descending;
        }
        else
        {
            _sortPath = null;
        }

        UpdateSortGlyphs();
        ApplyViewMode();

        Settings.ProcessSort = _sortPath;
        Settings.ProcessSortDescending = _sortDirection == ListSortDirection.Descending;
        Settings.SaveSoon();
    }

    private void OnChevronClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: ProcessRow row })
        {
            SetExpanded(row, !row.IsExpanded);
            e.Handled = true;
        }
    }

    private void SetExpanded(ProcessRow row, bool expanded)
    {
        if (!row.HasChildren || row.IsExpanded == expanded)
        {
            return;
        }

        if (expanded)
        {
            _collapsed.Remove(row.Key);
        }
        else
        {
            _collapsed.Add(row.Key);
        }

        row.SetExpanded(expanded);
        RelayoutForToggle(row);
    }

    /// <summary>Right expands; Left collapses, or moves to the parent when already collapsed.</summary>
    private bool HandleTreeKey(Key key, ProcessRow row)
    {
        if (!_treeApplied)
        {
            return false;
        }

        if (key == Key.Right && row.HasChildren && !row.IsExpanded)
        {
            SetExpanded(row, true);
            return true;
        }

        if (key == Key.Left)
        {
            if (row.HasChildren && row.IsExpanded)
            {
                SetExpanded(row, false);
                return true;
            }

            var parent = _rows.FirstOrDefault(r => r.Pid == row.ParentPid && r.Key != row.Key && r.Key.CreateTime <= row.Key.CreateTime);
            if (parent is not null)
            {
                Grid.SelectedItem = parent;
                Grid.ScrollIntoView(parent);
                return true;
            }
        }

        return false;
    }

    private void OnTreeToggleClick(object sender, RoutedEventArgs e)
    {
        _treeEnabled = !_treeEnabled;
        UpdateTreeToggle();
        ApplyViewMode();
        Settings.ProcessTree = _treeEnabled;
        Settings.SaveSoon();
    }

    private static Shell.PanelSettings Settings => Shell.PanelSettings.Current;

    private void UpdateTreeToggle()
    {
        TreeToggle.Appearance = _treeEnabled ? ControlAppearance.Primary : ControlAppearance.Secondary;
        TreeToggle.ToolTip = _treeEnabled ? "Showing the process tree (click for a flat list)" : "Showing a flat list (click for the process tree)";
    }

    /// <summary>Restores tree mode, sort and column widths, and starts tracking width changes.</summary>
    private void RestoreViewSettings()
    {
        _treeEnabled = Settings.ProcessTree;
        UpdateTreeToggle();

        if (Settings.ProcessSort is nameof(ProcessRow.Name) or nameof(ProcessRow.Pid) or nameof(ProcessRow.Cpu) or nameof(ProcessRow.PrivateBytes))
        {
            _sortPath = Settings.ProcessSort;
            _sortDirection = Settings.ProcessSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        }

        UpdateSortGlyphs();
        RestoreColumnWidths();
    }
}
