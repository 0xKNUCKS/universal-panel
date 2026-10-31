using System.Windows;
using UniversalPanel.Modules.Processes.Find;
using UniversalPanel.Native;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Processes;

// Navigation to the "Find handles or DLLs" page, which replaces the list like the details page.
internal sealed partial class ProcessesView
{
    private FindView? _find;
    private IReadOnlyList<ProcessSample> _latestProcesses = [];

    private void OnFindClick(object sender, RoutedEventArgs e)
    {
        _find ??= new FindView(Host, () => _latestProcesses, CloseFind, GoToProcess);
        FindHost.Content = _find;
        ListPage.Visibility = Visibility.Collapsed;
        FindHost.Visibility = Visibility.Visible;
        _find.FocusQuery();
    }

    private void CloseFind()
    {
        _find?.Cancel();
        FindHost.Visibility = Visibility.Collapsed;
        ListPage.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => Grid.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>From a search result to that process's details (results stay for the way back).</summary>
    private void GoToProcess(ProcessKey key)
    {
        if (!_byKey.TryGetValue(key, out var row))
        {
            Host.ShowNotice("Process has exited", "That process is no longer running.", InfoBarSeverity.Informational);
            return;
        }

        FindHost.Visibility = Visibility.Collapsed;
        OpenDetails(row);
    }
}
