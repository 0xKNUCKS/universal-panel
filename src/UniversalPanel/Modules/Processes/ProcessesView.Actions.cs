using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Processes;

// Confirmation policy follows System Informer's PhpShowContinueMessageProcesses: terminate always
// asks; suspend asks only when the target is dangerous; resume never asks.
internal sealed partial class ProcessesView
{
    private ProcessRow? Selected => Grid.SelectedItem as ProcessRow;

    private void OnGridRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Select the row under the cursor so the menu acts on what was right-clicked.
        if (ItemsControl.ContainerFromElement(Grid, (DependencyObject)e.OriginalSource) is DataGridRow row)
        {
            Grid.SelectedItem = row.Item;
        }
    }

    private void OnGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Selected is not { } row)
        {
            e.Handled = true;
            return;
        }

        UpdatePriorityMenu(row);
        OpenLocationItem.IsEnabled = row.ImagePath is not null;
        CopyPathItem.IsEnabled = row.ImagePath is not null;
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (Selected is not { ImagePath: { } path })
        {
            return;
        }

        try
        {
            Clipboard.SetDataObject(path, copy: true);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // CLIPBRD_E_CANT_OPEN: another app holds the clipboard open. Expected, not a bug.
            Host.ShowNotice("Clipboard busy", "Another app is using the clipboard. Try again.", InfoBarSeverity.Warning);
        }
    }

    private void OnOpenLocation(object sender, RoutedEventArgs e)
    {
        if (Selected is { ImagePath: { } path })
        {
            // Explorer takes focus, so the panel hides itself as usual; that's the point of the action.
            Guard.Run("Processes.OpenLocation", () => ShellReveal.SelectInExplorer(path));
        }
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Selected is { } row)
        {
            e.Handled = true;
            _ = Guard.RunAsync("Processes.End", () => EndAsync(row));
        }
        else if (e.Key == Key.Enter && Selected is { } selected)
        {
            e.Handled = true;
            OpenDetails(selected);
        }
    }

    private void OnEndProcess(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
        {
            _ = Guard.RunAsync("Processes.End", () => EndAsync(row));
        }
    }

    private void OnSuspend(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
        {
            _ = Guard.RunAsync("Processes.Suspend", () => SuspendAsync(row));
        }
    }

    private void OnResume(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
        {
            Guard.Run("Processes.Resume", () => Execute($"Resumed {row.Name}", () => ProcessControl.Resume(row.Key, row.Name)));
        }
    }

    private async Task EndAsync(ProcessRow row)
    {
        var risk = ProcessGuard.Assess(row);
        if (risk == ProcessRisk.Self)
        {
            Host.ShowNotice("That's the panel itself", "Use the tray icon > Exit to close it.", InfoBarSeverity.Informational);
            return;
        }

        var (title, message) = risk == ProcessRisk.Critical
            ? ("End critical process?", $"{row.Name} (PID {row.Pid}) is a critical system process. Ending it will crash Windows or log you off immediately.")
            : ("End process?", $"End {row.Name} (PID {row.Pid})? Unsaved data in it will be lost.");

        if (await Host.ConfirmAsync(title, message, "End process", destructive: true))
        {
            Execute(successTitle: null, () => ProcessControl.Terminate(row.Key, row.Name));
        }
    }

    private async Task SuspendAsync(ProcessRow row)
    {
        var risk = ProcessGuard.Assess(row);
        if (risk == ProcessRisk.Self)
        {
            Host.ShowNotice("That's the panel itself", "Suspending it would freeze the panel with no way to resume it.", InfoBarSeverity.Warning);
            return;
        }

        if (risk == ProcessRisk.Critical || ProcessGuard.FreezesDesktop(row))
        {
            var message = $"Suspending {row.Name} (PID {row.Pid}) can freeze Windows or the desktop until it's resumed.";
            if (!await Host.ConfirmAsync("Suspend system process?", message, "Suspend", destructive: true))
            {
                return;
            }
        }

        Execute($"Suspended {row.Name}", () => ProcessControl.Suspend(row.Key, row.Name));
    }

    /// <summary>Expected failures (access denied, already exited) become notices, not errors.</summary>
    private void Execute(string? successTitle, Action action)
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
