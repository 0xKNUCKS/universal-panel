// One process row, drawn directly instead of composed from controls. Mirrors how System Informer's
// TreeNew paints rows (phlib/treenew.c: PhTnpPaint / PhTnpDrawCell draw the visible rows of
// FlatList with GDI; there are no per-row windows):
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d
// A composed row (grids, a SymbolIcon, an Image and four TextBlocks) cost ~2 ms to re-prepare, so
// collapsing a big subtree, which brings ~35 new rows into view, took 50-150 ms. Drawing is a
// fraction of that. The ListBox around it still does selection, scrolling and virtualization.

using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace UniversalPanel.Modules.Processes;

/// <summary>Column widths shared by the header and every row.</summary>
internal sealed class ProcessColumns
{
    public double Pid { get; set; } = 72;
    public double Cpu { get; set; } = 56;
    public double Memory { get; set; } = 86;

    /// <summary>Raised after a width changes; rows redraw.</summary>
    public event EventHandler? Changed;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

internal sealed class ProcessRowView : FrameworkElement
{
    private const double ChevronWidth = 20, IconSize = 16, IconGap = 6, CellPadding = 4;
    private static readonly Typeface IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>Inherited from the list, so every row (recycled or new) reads the same widths.</summary>
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.RegisterAttached(
        "Columns", typeof(ProcessColumns), typeof(ProcessRowView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender, OnColumnsChanged));

    // Text color and font come from the list item (selection changes the foreground).
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(ProcessRowView), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(ProcessRowView), new FrameworkPropertyMetadata(13.0, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
        typeof(ProcessRowView), new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Bubbles when the expand / collapse chevron is clicked; the click doesn't select the row.</summary>
    public static readonly RoutedEvent ChevronClickEvent = EventManager.RegisterRoutedEvent(
        "ChevronClick", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ProcessRowView));

    private ProcessRow? _row;
    private ProcessColumns? _columns;
    private bool _listening;

    public ProcessRowView()
    {
        // Recycled containers get a new DataContext; a row off screen (unloaded) stops listening.
        DataContextChanged += (_, _) => Listen(IsLoaded);
        Loaded += (_, _) => Listen(true);
        Unloaded += (_, _) => Listen(false);
    }

    public static void SetColumns(DependencyObject element, ProcessColumns value) => element.SetValue(ColumnsProperty, value);

    public static ProcessColumns? GetColumns(DependencyObject element) => (ProcessColumns?)element.GetValue(ColumnsProperty);

    /// <summary>True if the point (in this element) is on the expand / collapse chevron.</summary>
    public bool IsOnChevron(Point point) =>
        _row is { ExpanderVisibility: Visibility.Visible } row
        && point.X >= row.Indent.Left && point.X < row.Indent.Left + ChevronWidth;

    private static void OnColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ProcessRowView view)
        {
            bool listening = view._listening;
            view.Listen(false);
            view._columns = e.NewValue as ProcessColumns;
            view.Listen(listening);
        }
    }

    private void OnColumnsResized(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>Follows the row's changes (CPU, memory, expand state) and column resizes while on screen.</summary>
    private void Listen(bool on)
    {
        if (_listening)
        {
            _row!.PropertyChanged -= OnRowChanged;
            if (_columns is not null)
            {
                _columns.Changed -= OnColumnsResized;
            }

            _listening = false;
        }

        _row = DataContext as ProcessRow;
        if (on && _row is not null)
        {
            _row.PropertyChanged += OnRowChanged;
            if (_columns is not null)
            {
                _columns.Changed += OnColumnsResized;
            }

            _listening = true;
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IsOnChevron(e.GetPosition(this)))
        {
            RaiseEvent(new RoutedEventArgs(ChevronClickEvent, this));
            e.Handled = true;
            return;
        }

        base.OnMouseLeftButtonDown(e);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, 28);

    protected override void OnRender(DrawingContext dc)
    {
        if (_row is not { } row || GetColumns(this) is not { } columns)
        {
            return;
        }

        // Transparent background: the whole row takes clicks, not just the drawn text.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var brush = (Brush)GetValue(ForegroundProperty);
        double size = (double)GetValue(FontSizeProperty);
        var typeface = new Typeface((FontFamily)GetValue(FontFamilyProperty), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double height = RenderSize.Height;

        double nameWidth = Math.Max(0, RenderSize.Width - columns.Pid - columns.Cpu - columns.Memory);
        double x = row.Indent.Left;

        if (row.ExpanderVisibility == Visibility.Visible)
        {
            // E76C: chevron right, E70D: chevron down (Segoe Fluent Icons / MDL2 Assets).
            var glyph = Text(row.IsExpanded ? "" : "", IconFont, 10, brush, dip);
            dc.DrawText(glyph, new Point(x + ((ChevronWidth - glyph.Width) / 2), (height - glyph.Height) / 2));
        }

        x += ChevronWidth;
        if (row.Icon is { } icon)
        {
            dc.DrawImage(icon, new Rect(x, (height - IconSize) / 2, IconSize, IconSize));
        }

        x += IconSize + IconGap;
        Cell(dc, row.Name, x, nameWidth - x, typeface, size, brush, dip, height);

        double cell = nameWidth;
        Cell(dc, row.Pid.ToString(CultureInfo.InvariantCulture), cell + CellPadding, columns.Pid - (2 * CellPadding), typeface, size, brush, dip, height);
        cell += columns.Pid;
        Cell(dc, row.CpuText, cell + CellPadding, columns.Cpu - (2 * CellPadding), typeface, size, brush, dip, height);
        cell += columns.Cpu;
        Cell(dc, row.MemoryText, cell + CellPadding, columns.Memory - (2 * CellPadding), typeface, size, brush, dip, height);
    }

    private static void Cell(DrawingContext dc, string text, double x, double width, Typeface typeface, double size, Brush brush, double dip, double height)
    {
        if (width <= 4 || text.Length == 0)
        {
            return;
        }

        var formatted = Text(text, typeface, size, brush, dip);
        formatted.MaxTextWidth = width;
        formatted.MaxLineCount = 1;
        formatted.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(formatted, new Point(x, (height - formatted.Height) / 2));
    }

    private static FormattedText Text(string text, Typeface typeface, double size, Brush brush, double dip) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, dip);
}
