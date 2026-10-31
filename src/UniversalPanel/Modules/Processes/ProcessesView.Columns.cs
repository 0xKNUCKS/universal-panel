using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UniversalPanel.Modules.Processes;

// Column header for the process list: widths shared by the header and every row (the header's
// splitters change them, the rows read them), click-to-sort, and keeping the header lined up
// with the row content (item padding on the left, the scrollbar on the right).
internal sealed partial class ProcessesView
{
    private const double DefaultPidWidth = 72, DefaultCpuWidth = 56, DefaultMemoryWidth = 86;

    public static readonly DependencyProperty PidWidthProperty = RegisterColumnWidth("Pid", DefaultPidWidth);
    public static readonly DependencyProperty CpuWidthProperty = RegisterColumnWidth("Cpu", DefaultCpuWidth);
    public static readonly DependencyProperty MemoryWidthProperty = RegisterColumnWidth(nameof(ProcessRow.PrivateBytes), DefaultMemoryWidth);

    public static readonly DependencyProperty HeaderMarginProperty =
        DependencyProperty.Register(nameof(HeaderMargin), typeof(Thickness), typeof(ProcessesView), new PropertyMetadata(default(Thickness)));

    public static readonly DependencyProperty RowPaddingProperty =
        DependencyProperty.Register(nameof(RowPadding), typeof(Thickness), typeof(ProcessesView), new PropertyMetadata(new Thickness(4, 0, 0, 0)));

    public GridLength PidWidth { get => (GridLength)GetValue(PidWidthProperty); set => SetValue(PidWidthProperty, value); }

    public GridLength CpuWidth { get => (GridLength)GetValue(CpuWidthProperty); set => SetValue(CpuWidthProperty, value); }

    public GridLength MemoryWidth { get => (GridLength)GetValue(MemoryWidthProperty); set => SetValue(MemoryWidthProperty, value); }

    public Thickness HeaderMargin { get => (Thickness)GetValue(HeaderMarginProperty); set => SetValue(HeaderMarginProperty, value); }

    public Thickness RowPadding { get => (Thickness)GetValue(RowPaddingProperty); set => SetValue(RowPaddingProperty, value); }

    /// <summary>A column width, saved under its sort key (the same keys the DataGrid version used).</summary>
    private static DependencyProperty RegisterColumnWidth(string key, double defaultWidth) =>
        DependencyProperty.Register(key + "Width", typeof(GridLength), typeof(ProcessesView), new PropertyMetadata(new GridLength(defaultWidth), (d, e) =>
        {
            if (d is not ProcessesView view || e.NewValue is not GridLength { IsAbsolute: true } width)
            {
                return;
            }

            switch (key)
            {
                case "Pid": view._columns.Pid = width.Value; break;
                case "Cpu": view._columns.Cpu = width.Value; break;
                default: view._columns.Memory = width.Value; break;
            }

            view._columns.RaiseChanged(); // rows redraw at the new widths
            if (!view._restoringColumns)
            {
                Settings.ProcessColumnWidths[key] = width.Value;
                Settings.SaveSoon();
            }
        }));

    private bool _restoringColumns;
    private readonly ProcessColumns _columns = new();

    private void RestoreColumnWidths()
    {
        _restoringColumns = true;
        try
        {
            PidWidth = new GridLength(Settings.ProcessColumnWidths.GetValueOrDefault("Pid", DefaultPidWidth));
            CpuWidth = new GridLength(Settings.ProcessColumnWidths.GetValueOrDefault("Cpu", DefaultCpuWidth));
            MemoryWidth = new GridLength(Settings.ProcessColumnWidths.GetValueOrDefault(nameof(ProcessRow.PrivateBytes), DefaultMemoryWidth));
        }
        finally
        {
            _restoringColumns = false;
        }
    }

    private void OnHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
        {
            ApplySortClick(path);
            e.Handled = true;
        }
    }

    /// <summary>▲ / ▼ after the sorted column's title.</summary>
    private void UpdateSortGlyphs()
    {
        foreach (var (header, path, title) in new[]
        {
            (NameHeader, nameof(ProcessRow.Name), "Name"),
            (PidHeader, nameof(ProcessRow.Pid), "PID"),
            (CpuHeader, nameof(ProcessRow.Cpu), "CPU"),
            (MemoryHeader, nameof(ProcessRow.PrivateBytes), "Memory"),
        })
        {
            header.Text = _sortPath == path ? $"{title} {(_sortDirection == System.ComponentModel.ListSortDirection.Ascending ? "▲" : "▼")}" : title;
        }
    }

    /// <summary>Lines the header up with the row content: the item's padding and the scrollbar.</summary>
    private void AlignHeader()
    {
        if (Grid.Items.Count == 0 || Grid.ItemContainerGenerator.ContainerFromIndex(0) is not ListBoxItem item
            || FindPresenter(item) is not { ActualWidth: > 0 } content)
        {
            return;
        }

        double left = content.TransformToAncestor(Grid).Transform(default).X;
        double right = Grid.ActualWidth - left - content.ActualWidth;
        var margin = new Thickness(Math.Max(0, left - 4), 0, Math.Max(0, right), 0); // header cells pad 4 like the row cells
        if (margin != HeaderMargin)
        {
            HeaderMargin = margin;
        }
    }

    private static ContentPresenter? FindPresenter(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            if (child is ContentPresenter presenter)
            {
                return presenter;
            }

            if (FindPresenter(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
