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

    /// <summary>A click on empty space (no row, not the scrollbar) clears the selection.</summary>
    private void OnGridLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (ItemsControl.ContainerFromElement(Grid, source) is null
            && !IsInScrollBar(source))
        {
            Grid.UnselectAll();
        }
    }

    private static bool IsInScrollBar(DependencyObject? element)
    {
        for (; element is not null; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is System.Windows.Controls.Primitives.ScrollBar)
            {
                return true;
            }
        }

        return false;
    }

    private void OnGridRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Select the row under the cursor so the menu acts on what was right-clicked.
        if (ItemsControl.ContainerFromElement(Grid, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: ProcessRow row })
        {
            Grid.SelectedItem = row;
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
        Tools.OpenWithMenu.Fill(OpenWithMenu, Host, ToolTarget.ForProcess(row.Pid, row.ImagePath));
        Terminal.TerminalMenu.Fill(TerminalMenu, Host, row.Pid, row.ImagePath);
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (Selected is { ImagePath: { } path })
        {
            ClipboardText.Copy(Host, path);
        }
    }

    private void OnOpenLocation(object sender, RoutedEventArgs e)
    {
        if (Selected is { ImagePath: { } path })
        {
            // Explorer takes focus, so the panel hides itself as usual; that's the point of the action.
            Guard.Run("Processes.OpenLocation", () => ClipboardText.Reveal(Host, path));
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
        else if (e.Key is Key.Left or Key.Right && Selected is { } current && HandleTreeKey(e.Key, current))
        {
            e.Handled = true;
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
