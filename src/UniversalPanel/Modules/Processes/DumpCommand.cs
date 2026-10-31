using System.IO;
using System.Windows;
using UniversalPanel.Native;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Processes;

/// <summary>Create dump (save dialog, background write, notices); shared by the list and details.</summary>
internal static class DumpCommand
{
    public static async Task RunAsync(IModuleHost host, DependencyObject owner, ProcessKey key, string processName, DumpKind kind)
    {
        string? path;
        // The save dialog takes focus; keep the panel open behind it.
        using (host.SuppressAutoHide())
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = $"Save {(kind == DumpKind.Full ? "full dump" : "minidump")} of {processName}",
                FileName = $"{Path.GetFileNameWithoutExtension(processName)}_{key.Pid}_{DateTime.Now:yyyyMMdd_HHmmss}.dmp",
                DefaultExt = ".dmp",
                Filter = "Dump files (*.dmp)|*.dmp",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                OverwritePrompt = true,
            };
            path = dialog.ShowDialog(Window.GetWindow(owner)) == true ? dialog.FileName : null;
        }

        if (path is null)
        {
            return;
        }

        host.ShowNotice($"Dumping {processName}…", kind == DumpKind.Full ? "A full dump can take a while and be several GB." : string.Empty, InfoBarSeverity.Informational);
        try
        {
            await Task.Run(() => ProcessDump.Write(key, processName, path, kind));
            var size = ProcessRow.FormatBytes((ulong)new FileInfo(path).Length);
            host.ShowNotice($"Dump saved ({size})", path, InfoBarSeverity.Success);
        }
        catch (ProcessActionException ex)
        {
            host.ShowNotice("Couldn't create the dump", ex.Message, ex.Failure == ProcessActionFailure.Exited ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
        }
        catch (IOException ex)
        {
            host.ShowNotice("Couldn't write the dump file", ex.Message, InfoBarSeverity.Warning);
        }
    }
}
