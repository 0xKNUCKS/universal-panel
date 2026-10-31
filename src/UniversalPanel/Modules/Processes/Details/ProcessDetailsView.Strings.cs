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

    private void OnFindStrings(object sender, RoutedEventArgs e)
    {
        if (SelectedRegion is { } region && Target is { } target)
        {
            _ = Guard.RunAsync("Details.Memory.Strings", () => ShowStringsAsync(target, region));
        }
    }

    private async Task ShowStringsAsync(ProcessKey target, MemoryRow region)
    {
        CloseStrings();
        var cts = new CancellationTokenSource();
        _stringsScan = cts;

        StringsTitle.Text = $"Strings in {region.BaseText} ({region.SizeText})";
        StringsSummary.Text = "Scanning…";
        StringsGrid.ItemsSource = null;
        MemoryListPanel.Visibility = Visibility.Collapsed;
        StringsPanel.Visibility = Visibility.Visible;
        await LoadingRing.ShowAsync(StringsSpinner);

        try
        {
            string name = TargetName;
            var found = await Task.Run(() => MemoryStrings.Scan(target, name, region.Base, region.Size, MaxStrings, cts.Token), cts.Token);
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

    private void UpdateStringsSummary(int total)
    {
        int shown = _stringsView?.Count ?? 0;
        var capped = total >= MaxStrings ? $" (first {MaxStrings:N0})" : string.Empty;
        StringsSummary.Text = string.Create(CultureInfo.CurrentCulture,
            $"{(shown == total ? $"{total:N0}" : $"{shown:N0} of {total:N0}")} strings{capped} · min. {MemoryStrings.MinLength} characters");
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
