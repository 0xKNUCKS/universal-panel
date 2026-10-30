using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UniversalPanel.Modules.Processes.Details;

namespace UniversalPanel.Modules.Processes;

// Navigation between the process list and the per-process details page.
internal sealed partial class ProcessesView
{
    private ProcessDetailsView? _details;

    private bool DetailsOpen => DetailsHost.Visibility == Visibility.Visible;

    private void OpenDetails(ProcessRow row)
    {
        _details ??= new ProcessDetailsView(Host, CloseDetails);
        _details.Show(row);
        DetailsHost.Content = _details;

        // Threads come from the same snapshot the list uses; the next tick includes them.
        _sampler.Focus = row.Key;

        ListPage.Visibility = Visibility.Collapsed;
        DetailsHost.Visibility = Visibility.Visible;
        _details.FocusContent();
    }

    private void CloseDetails()
    {
        _sampler.Focus = null;
        DetailsHost.Visibility = Visibility.Collapsed;
        ListPage.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => Grid.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnDetails(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
        {
            OpenDetails(row);
        }
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click on a row, not on a header or the scrollbar.
        if (ItemsControl.ContainerFromElement(Grid, (DependencyObject)e.OriginalSource) is DataGridRow { Item: ProcessRow row })
        {
            OpenDetails(row);
        }
    }
}
