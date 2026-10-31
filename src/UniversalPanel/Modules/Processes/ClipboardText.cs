using System.Windows;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Processes;

internal static class ClipboardText
{
    /// <summary>Shows a file in Explorer; a location Windows won't open becomes a notice, not an error.</summary>
    public static void Reveal(IModuleHost host, string path)
    {
        try
        {
            Native.ShellReveal.SelectInExplorer(path);
        }
        catch (Native.ProcessActionException ex)
        {
            host.ShowNotice("Can't open that location", ex.Message, InfoBarSeverity.Warning);
        }
    }

    /// <summary>Copies text; a clipboard held open by another app becomes a notice, not an error.</summary>
    public static void Copy(IModuleHost host, string text)
    {
        try
        {
            Clipboard.SetDataObject(text, copy: true);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // CLIPBRD_E_CANT_OPEN: another app holds the clipboard open. Expected, not a bug.
            host.ShowNotice("Clipboard busy", "Another app is using the clipboard. Try again.", InfoBarSeverity.Warning);
        }
    }
}
