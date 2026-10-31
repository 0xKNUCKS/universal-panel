using System.Windows;
using UniversalPanel.Modules.Processes.History;

namespace UniversalPanel.Modules.Processes;

// Navigation to the process history page, which replaces the list like the Find page.
internal sealed partial class ProcessesView
{
    private HistoryView? _historyView;

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        _historyView ??= new HistoryView(_history, CloseHistory);
        HistoryHost.Content = _historyView;
        ListPage.Visibility = Visibility.Collapsed;
        HistoryHost.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => _historyView.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CloseHistory()
    {
        HistoryHost.Visibility = Visibility.Collapsed;
        ListPage.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => Grid.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }
}
