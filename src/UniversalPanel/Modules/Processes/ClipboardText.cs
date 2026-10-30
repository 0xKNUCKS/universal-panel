using System.Windows;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Processes;

internal static class ClipboardText
{
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
