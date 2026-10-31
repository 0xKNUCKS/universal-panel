using System.Windows;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;

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
            _ = Guard.RunAsync("Processes.Dump", () => DumpCommand.RunAsync(Host, this, row.Key, row.Name, kind));
        }
    }
}
