using System.IO;
using System.Windows;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Processes;

// Create dump (full / minidump) from the row menu.
internal sealed partial class ProcessesView
{
    private void OnFullDump(object sender, RoutedEventArgs e) => StartDump(DumpKind.Full);

    private void OnMiniDump(object sender, RoutedEventArgs e) => StartDump(DumpKind.Mini);

    private void StartDump(DumpKind kind)
    {
        if (Selected is { } row)
        {
            _ = Guard.RunAsync("Processes.Dump", () => DumpAsync(row, kind));
        }
    }

    private async Task DumpAsync(ProcessRow row, DumpKind kind)
    {
        string? path;
        // The save dialog takes focus; keep the panel open behind it.
        using (Host.SuppressAutoHide())
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = $"Save {(kind == DumpKind.Full ? "full dump" : "minidump")} of {row.Name}",
                FileName = $"{Path.GetFileNameWithoutExtension(row.Name)}_{row.Pid}_{DateTime.Now:yyyyMMdd_HHmmss}.dmp",
                DefaultExt = ".dmp",
                Filter = "Dump files (*.dmp)|*.dmp",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                OverwritePrompt = true,
            };
            path = dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
        }

        if (path is null)
        {
            return;
        }

        Host.ShowNotice($"Dumping {row.Name}…", kind == DumpKind.Full ? "A full dump can take a while and be several GB." : string.Empty, InfoBarSeverity.Informational);
        try
        {
            await Task.Run(() => ProcessDump.Write(row.Key, row.Name, path, kind));
            var size = ProcessRow.FormatBytes((ulong)new FileInfo(path).Length);
            Host.ShowNotice($"Dump saved ({size})", path, InfoBarSeverity.Success);
        }
        catch (ProcessActionException ex)
        {
            Host.ShowNotice("Couldn't create the dump", ex.Message, ex.Failure == ProcessActionFailure.Exited ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
        }
        catch (IOException ex)
        {
            Host.ShowNotice("Couldn't write the dump file", ex.Message, InfoBarSeverity.Warning);
        }
    }
}
