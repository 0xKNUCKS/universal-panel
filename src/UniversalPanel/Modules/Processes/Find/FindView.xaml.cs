using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Processes.Find;

/// <summary>One match: a handle or a loaded module of some process.</summary>
internal sealed record FindResult(ProcessKey Key, string ProcessName, string Kind, string Name, string Detail)
{
    public string Process => $"{ProcessName} ({Key.Pid})";
    public string Details => $"{Name}\n{Kind} · {Detail} · {Process}";
}

/// <summary>
/// System Informer's "Find handles or DLLs": substring search over the handle names and module
/// paths of every process the panel can open. Built on the same, already-verified captures as the
/// details tabs (HandleSnapshot, ModuleSnapshot), one process at a time, off the UI thread.
/// </summary>
internal sealed partial class FindView : UserControl
{
    private const int MaxResults = 5000;

    private readonly IModuleHost _host;
    private readonly Func<IReadOnlyList<ProcessSample>> _processes;
    private readonly Action _back;
    private readonly Action<ProcessKey> _goTo;
    private CancellationTokenSource? _search;

    public FindView(IModuleHost host, Func<IReadOnlyList<ProcessSample>> processes, Action back, Action<ProcessKey> goTo)
    {
        _host = host;
        _processes = processes;
        _back = back;
        _goTo = goTo;
        InitializeComponent();
    }

    public void FocusQuery() =>
        Dispatcher.BeginInvoke(() => { Query.Focus(); Query.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);

    /// <summary>Stops a running search (leaving the page, or starting a new one).</summary>
    public void Cancel()
    {
        _search?.Cancel();
        _search = null;
    }

    private async Task SearchAsync(string query)
    {
        Cancel();
        if (query.Length == 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _search = cts;
        var processes = _processes();
        Results.ItemsSource = null;
        Summary.Text = $"Searching 0 of {processes.Count} processes…";
        await LoadingRing.ShowAsync(Spinner);

        var progress = new Progress<int>(done =>
        {
            if (_search == cts)
            {
                Summary.Text = $"Searching {done} of {processes.Count} processes…";
            }
        });

        try
        {
            var (results, skipped) = await Task.Run(() => Search(query, processes, progress, cts.Token), cts.Token);
            if (_search != cts)
            {
                return;
            }

            Results.ItemsSource = results;
            var capped = results.Count >= MaxResults ? $" (first {MaxResults:N0})" : string.Empty;
            Summary.Text = string.Create(CultureInfo.CurrentCulture,
                $"{results.Count:N0} matches{capped} · {skipped:N0} processes couldn't be opened");
        }
        catch (OperationCanceledException)
        {
            // Superseded or left the page.
        }
        finally
        {
            // A newer search owns the spinner (and _search); otherwise this one is finished.
            if (_search == cts || _search is null)
            {
                LoadingRing.Set(Spinner, false);
            }

            if (_search == cts)
            {
                _search = null; // So a later Cancel() never touches a disposed source.
            }

            cts.Dispose();
        }
    }

    private static (List<FindResult> Results, int Skipped) Search(
        string query, IReadOnlyList<ProcessSample> processes, IProgress<int> progress, CancellationToken ct)
    {
        var names = new Dictionary<int, string>(processes.Count);
        foreach (var p in processes)
        {
            names.TryAdd(p.Record.Key.Pid, p.Record.Name);
        }

        var results = new List<FindResult>();
        int done = 0, skipped = 0;
        foreach (var process in processes)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(++done);

            var key = process.Record.Key;
            var name = process.Record.Name;
            if (key.Pid is 0 or 4)
            {
                continue; // Idle/System: no user-mode handles or modules to read.
            }

            bool opened = false;
            try
            {
                foreach (var handle in HandleSnapshot.Capture(key, name, names))
                {
                    if (handle.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new FindResult(key, name, handle.Type, handle.Name, $"handle 0x{handle.Handle:X}"));
                    }
                }

                opened = true;
            }
            catch (ProcessActionException)
            {
                // Access denied or exited: counted below, not an error.
            }

            try
            {
                foreach (var module in ModuleSnapshot.Capture(key, name))
                {
                    if (module.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new FindResult(key, name, "DLL", module.Path, $"base 0x{module.BaseAddress:X}"));
                    }
                }

                opened = true;
            }
            catch (ProcessActionException)
            {
            }

            if (!opened)
            {
                skipped++;
            }

            if (results.Count >= MaxResults)
            {
                results.RemoveRange(MaxResults, results.Count - MaxResults);
                break;
            }
        }

        return (results, skipped);
    }

    private FindResult? Selected => Results.SelectedItem as FindResult;

    private void OnFindClick(object sender, RoutedEventArgs e) =>
        _ = Guard.RunAsync("Find.Search", () => SearchAsync(Query.Text.Trim()));

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = Guard.RunAsync("Find.Search", () => SearchAsync(Query.Text.Trim()));
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back && e.OriginalSource is not TextBox)
        {
            e.Handled = true;
            _back();
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => _back();

    private void OnResultsRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Results, (DependencyObject)e.OriginalSource) is DataGridRow row)
        {
            Results.SelectedItem = row.Item;
        }
    }

    private void OnResultsContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Selected is null)
        {
            e.Handled = true;
        }
    }

    private void OnResultsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Results, (DependencyObject)e.OriginalSource) is DataGridRow { Item: FindResult result })
        {
            _goTo(result.Key);
        }
    }

    private void OnGoToProcess(object sender, RoutedEventArgs e)
    {
        if (Selected is { } result)
        {
            _goTo(result.Key);
        }
    }

    private void OnCopyResultName(object sender, RoutedEventArgs e)
    {
        if (Selected is { } result)
        {
            ClipboardText.Copy(_host, result.Name);
        }
    }
}
