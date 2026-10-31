using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Processes.Details;

// Strings of one memory region, shown in place of the region list inside the Memory tab.
internal sealed partial class ProcessDetailsView
{
    private const int MaxStrings = 50_000;
    private CancellationTokenSource? _stringsScan;
    private ListCollectionView? _stringsView;
    private string _stringFilter = string.Empty;

    /// <summary>A string row; addresses as hex text for display and copy.</summary>
    internal sealed record StringRow(MemoryString Source)
    {
        public string AddressText { get; } = "0x" + Source.Address.ToString("X", CultureInfo.InvariantCulture);
        public string Encoding => Source.Unicode ? "UTF-16" : "ASCII";
        public string Text => Source.Text;
    }

    /// <summary>What the strings view scans: one region, or the whole process (Region null).</summary>
    private (ProcessKey Target, MemoryRow? Region)? _stringsScope;

    private void OnFindStrings(object sender, RoutedEventArgs e)
    {
        if (SelectedRegion is { } region && Target is { } target)
        {
            StartStrings(target, region);
        }
    }

    private void OnFindStringsInProcess(object sender, RoutedEventArgs e)
    {
        if (Target is { } target)
        {
            StartStrings(target, region: null);
        }
    }

    private void StartStrings(ProcessKey target, MemoryRow? region)
    {
        _stringsScope = (target, region);
        StringSearch.Text = string.Empty;
        _ = Guard.RunAsync("Details.Memory.Strings", () => ScanStringsAsync(term: null));
    }

    /// <summary>
    /// Scans the current scope. With a term, only matching strings are collected, so the cap
    /// counts matches and the whole process is searched; without one, the first strings are shown.
    /// </summary>
    private async Task ScanStringsAsync(string? term)
    {
        if (_stringsScope is not { } scope)
        {
            return;
        }

        _stringsScan?.Cancel();
        var cts = new CancellationTokenSource();
        _stringsScan = cts;
        _scanTerm = term;

        var (target, region) = scope;
        StringsTitle.Text = region is null ? $"Strings in {TargetName}" : $"Strings in {region.BaseText} ({region.SizeText})";
        StringsSummary.Text = "Scanning…";
        StringsGrid.ItemsSource = null;
        MemoryListPanel.Visibility = Visibility.Collapsed;
        StringsPanel.Visibility = Visibility.Visible;
        await LoadingRing.ShowAsync(StringsSpinner);

        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (_stringsScan == cts)
            {
                StringsSummary.Text = $"Scanning region {p.Done:N0} of {p.Total:N0}…";
            }
        });

        try
        {
            string name = TargetName;
            bool mapped = IncludeMapped.IsChecked == true;
            var found = await Task.Run(() => region is null
                ? MemoryStrings.ScanProcess(target, name, mapped, MaxStrings, progress, cts.Token, term)
                : MemoryStrings.Scan(target, name, region.Base, region.Size, MaxStrings, cts.Token, term), cts.Token);
            if (_stringsScan != cts)
            {
                return;
            }

            _stringsView = (ListCollectionView)CollectionViewSource.GetDefaultView(found.Select(s => new StringRow(s)).ToList());
            _stringsView.Filter = item => _stringFilter.Length == 0 || ((StringRow)item).Text.Contains(_stringFilter, StringComparison.OrdinalIgnoreCase);
            StringsGrid.ItemsSource = _stringsView;
            UpdateStringsSummary(found.Count);
        }
        catch (OperationCanceledException)
        {
            // Closed or replaced.
        }
        catch (ProcessActionException ex)
        {
            StringsSummary.Text = ex.Message;
        }
        finally
        {
            if (_stringsScan == cts)
            {
                LoadingRing.Set(StringsSpinner, false);
            }
        }
    }

    private string? _scanTerm;

    private void UpdateStringsSummary(int total)
    {
        int shown = _stringsView?.Count ?? 0;
        string count = shown == total ? $"{total:N0}" : $"{shown:N0} of {total:N0}";
        string what = _scanTerm is null ? "strings" : $"strings containing \"{_scanTerm}\"";
        string tail = total >= MaxStrings
            ? (_scanTerm is null ? $" · first {MaxStrings:N0}: type a term and press Enter to search everything" : $" · first {MaxStrings:N0} matches")
            : _scanTerm is null ? " · Enter rescans with the filter as a search term" : string.Empty;
        StringsSummary.Text = string.Create(CultureInfo.CurrentCulture, $"{count} {what}{tail}");
    }

    private void OnStringSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var term = StringSearch.Text.Trim();
            _ = Guard.RunAsync("Details.Memory.Strings", () => ScanStringsAsync(term.Length == 0 ? null : term));
        }
    }

    private void CloseStrings()
    {
        _stringsScan?.Cancel();
        _stringsScan = null;
        LoadingRing.Set(StringsSpinner, false);
        StringsPanel.Visibility = Visibility.Collapsed;
        MemoryListPanel.Visibility = Visibility.Visible;
    }

    private void OnStringsBack(object sender, RoutedEventArgs e) => CloseStrings();

    private void OnStringFilterChanged(object sender, TextChangedEventArgs e)
    {
        _stringFilter = StringSearch.Text.Trim();
        if (_stringsView is { } view)
        {
            view.Refresh();
            UpdateStringsSummary(view.SourceCollection.Cast<object>().Count());
        }
    }

    private StringRow? SelectedString => StringsGrid.SelectedItem as StringRow;

    private void OnStringsRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(StringsGrid, (DependencyObject)e.OriginalSource) is DataGridRow row)
        {
            StringsGrid.SelectedItem = row.Item;
        }
    }

    private void OnStringsContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (SelectedString is null)
        {
            e.Handled = true;
        }
    }

    private void OnCopyString(object sender, RoutedEventArgs e)
    {
        if (SelectedString is { } s)
        {
            ClipboardText.Copy(Host, s.Text);
        }
    }

    private void OnCopyStringAddress(object sender, RoutedEventArgs e)
    {
        if (SelectedString is { } s)
        {
            ClipboardText.Copy(Host, s.AddressText);
        }
    }
}
