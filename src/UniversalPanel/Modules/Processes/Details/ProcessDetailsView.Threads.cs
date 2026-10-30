using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Processes.Details;

// Thread actions. Confirmation policy follows System Informer: ending a thread always asks
// ("may cause the process to stop working"); suspend/resume don't, except for the panel's own
// threads (refused) and critical processes (asked), the same guard as process actions.
internal sealed partial class ProcessDetailsView
{
    private ThreadRow? SelectedThread => ThreadsGrid.SelectedItem as ThreadRow;

    private void OnThreadsRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ThreadsGrid, (DependencyObject)e.OriginalSource) is DataGridRow row)
        {
            ThreadsGrid.SelectedItem = row.Item;
        }
    }

    private void OnThreadsContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (SelectedThread is null)
        {
            e.Handled = true;
        }
    }

    private void OnThreadsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && SelectedThread is { } thread)
        {
            e.Handled = true;
            _ = Guard.RunAsync("Threads.End", () => EndThreadAsync(thread));
        }
    }

    private void OnSuspendThread(object sender, RoutedEventArgs e)
    {
        if (SelectedThread is { } thread)
        {
            _ = Guard.RunAsync("Threads.Suspend", () => SuspendThreadAsync(thread));
        }
    }

    private void OnResumeThread(object sender, RoutedEventArgs e)
    {
        if (SelectedThread is { } thread && Target is { } process)
        {
            Guard.Run("Threads.Resume", () => ExecuteThread($"Resumed thread {thread.Tid}", () => ThreadControl.Resume(thread.Key, process.Pid)));
        }
    }

    private void OnEndThread(object sender, RoutedEventArgs e)
    {
        if (SelectedThread is { } thread)
        {
            _ = Guard.RunAsync("Threads.End", () => EndThreadAsync(thread));
        }
    }

    private async Task SuspendThreadAsync(ThreadRow thread)
    {
        if (Target is not { } process)
        {
            return;
        }

        var risk = ProcessGuard.Assess(process, TargetName);
        if (risk == ProcessRisk.Self)
        {
            Host.ShowNotice("That's the panel itself", "Suspending one of its threads can freeze the panel with no way to resume it.", InfoBarSeverity.Warning);
            return;
        }

        if (risk == ProcessRisk.Critical || ProcessGuard.FreezesDesktop(TargetName))
        {
            var message = $"Suspending a thread of {TargetName} can freeze Windows or the desktop until it's resumed.";
            if (!await Host.ConfirmAsync("Suspend system thread?", message, "Suspend", destructive: true))
            {
                return;
            }
        }

        ExecuteThread($"Suspended thread {thread.Tid}", () => ThreadControl.Suspend(thread.Key, process.Pid));
    }

    private async Task EndThreadAsync(ThreadRow thread)
    {
        if (Target is not { } process)
        {
            return;
        }

        var risk = ProcessGuard.Assess(process, TargetName);
        if (risk == ProcessRisk.Self)
        {
            Host.ShowNotice("That's the panel itself", "Ending one of its threads would crash the panel. Use the tray icon > Exit instead.", InfoBarSeverity.Informational);
            return;
        }

        var (title, message) = risk == ProcessRisk.Critical
            ? ("End critical thread?", $"Thread {thread.Tid} belongs to {TargetName}, a critical system process. Ending it can crash Windows.")
            : ("End thread?", $"End thread {thread.Tid} of {TargetName}? Terminating a thread may cause the process to stop working.");

        if (await Host.ConfirmAsync(title, message, "End thread", destructive: true))
        {
            ExecuteThread(successTitle: null, () => ThreadControl.Terminate(thread.Key, process.Pid));
        }
    }

    private void ExecuteThread(string? successTitle, Action action)
    {
        try
        {
            action();
            if (successTitle is not null)
            {
                Host.ShowNotice(successTitle, string.Empty, InfoBarSeverity.Success);
            }
        }
        catch (ProcessActionException ex)
        {
            var severity = ex.Failure == ProcessActionFailure.Exited ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
            Host.ShowNotice("Couldn't complete that", ex.Message, severity);
        }
    }
}
