using System.IO;
using Windows.Win32;
using Windows.Win32.UI.Shell.Common;

namespace UniversalPanel.Native;

internal static class ShellReveal
{
    /// <summary>
    /// Opens an Explorer window on the file's folder with the file selected, through the shell API
    /// (SHOpenFolderAndSelectItems, as System Informer's PhShellExploreFile does) rather than
    /// building an explorer.exe command line. Must run on an STA thread (the WPF UI thread).
    /// Files the shell can't resolve (for example inside protected app packages under WindowsApps,
    /// where SHParseDisplayName fails with E_INVALIDARG) fall back to opening the parent folder;
    /// if that's refused too, throws <see cref="ProcessActionException"/> with a readable message.
    /// </summary>
    public static void SelectInExplorer(string path)
    {
        // With no item list, SHOpenFolderAndSelectItems opens the item's parent with the item
        // selected; so the fallback shows the containing folder, selected in its own parent.
        if (TryReveal(path) || (Path.GetDirectoryName(path) is { } folder && TryReveal(folder)))
        {
            return;
        }

        throw new ProcessActionException(ProcessActionFailure.AccessDenied,
            $"Windows won't let Explorer open {path}. Files in protected app packages (WindowsApps) can't be browsed; use Copy path instead.");
    }

    private static unsafe bool TryReveal(string path)
    {
        if (PInvoke.SHParseDisplayName(path, null, out ITEMIDLIST* pidl, 0, out _).Failed)
        {
            return false;
        }

        try
        {
            return PInvoke.SHOpenFolderAndSelectItems(pidl, 0, null, 0).Succeeded;
        }
        finally
        {
            PInvoke.ILFree(pidl);
        }
    }
}
