using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Processes;

// Find window (drag the crosshair onto a window) and Start suspended.
internal sealed partial class ProcessesView
{
    private IDisposable? _crosshairKeepOpen;
    private int? _selectWhenListed;

    private void OnCrosshairDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement button)
        {
            return;
        }

        // Capture keeps mouse messages coming to us while the cursor is over other windows.
        _crosshairKeepOpen = Host.SuppressAutoHide();
        button.CaptureMouse();
        Mouse.OverrideCursor = Cursors.Cross;
        e.Handled = true;
    }

    private void OnCrosshairUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement button || !button.IsMouseCaptured)
        {
            return;
        }

        button.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        _crosshairKeepOpen?.Dispose();
        _crosshairKeepOpen = null;
        e.Handled = true;

        if (WindowOwner.UnderCursor() is not { } found)
        {
            return;
        }

        if (found.Pid == Environment.ProcessId)
        {
            return; // Released over the panel itself: treat as cancelled.
        }

        if (!SelectProcess(found.Pid))
        {
            Host.ShowNotice("Window's process not in the list", $"PID {found.Pid} ({found.Title}).", InfoBarSeverity.Informational);
        }
    }

    /// <summary>Selects the newest process with this PID, clearing the filter and expanding its tree path.</summary>
    private bool SelectProcess(int pid)
    {
        var row = _rows.Where(r => r.Pid == pid).OrderByDescending(r => r.Key.CreateTime).FirstOrDefault();
        if (row is null)
        {
            return false;
        }

        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Text = string.Empty; // Back to the full list (and the tree, if enabled).
            _filter = string.Empty;
            ApplyViewMode();
        }

        for (var parent = ParentOf(row); parent is not null; parent = ParentOf(parent))
        {
            SetExpanded(parent, true);
        }

        Grid.SelectedItem = row;
        Grid.ScrollIntoView(row);
        Grid.Focus();
        return true;
    }

    private ProcessRow? ParentOf(ProcessRow row) =>
        _rows.FirstOrDefault(r => r.Pid == row.ParentPid && r.Key != row.Key
            && (r.Pid is 0 or 4 || r.Key.CreateTime <= row.Key.CreateTime));

    private void OnStartSuspended(object sender, RoutedEventArgs e) =>
        _ = Guard.RunAsync("Processes.StartSuspended", StartSuspendedAsync);

    private Task StartSuspendedAsync()
    {
        string? path;
        using (Host.SuppressAutoHide())
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Start a program suspended",
                Filter = "Programs (*.exe)|*.exe",
            };
            path = dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
        }

        if (path is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            int pid = WindowOwner.StartSuspended(path);
            _selectWhenListed = pid; // Selected as soon as the next sample lists it.
            Host.ShowNotice($"Started {System.IO.Path.GetFileName(path)} suspended (PID {pid})",
                "Attach your debugger, then right-click it > Resume.", InfoBarSeverity.Success);
        }
        catch (Win32Exception ex)
        {
            Host.ShowNotice("Couldn't start the program", ex.Message, InfoBarSeverity.Warning);
        }

        return Task.CompletedTask;
    }

    /// <summary>Called after each sample: selects a just-started process once it appears.</summary>
    private void SelectPendingProcess()
    {
        if (_selectWhenListed is { } pid && SelectProcess(pid))
        {
            _selectWhenListed = null;
        }
    }
}
