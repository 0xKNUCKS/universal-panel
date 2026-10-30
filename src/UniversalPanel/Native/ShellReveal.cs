using Windows.Win32;
using Windows.Win32.UI.Shell.Common;

namespace UniversalPanel.Native;

internal static class ShellReveal
{
    /// <summary>
    /// Opens an Explorer window on the file's folder with the file selected, through the shell API
    /// (SHOpenFolderAndSelectItems, as System Informer's PhShellExploreFile does) rather than
    /// building an explorer.exe command line. Must run on an STA thread (the WPF UI thread).
    /// </summary>
    public static unsafe void SelectInExplorer(string path)
    {
        PInvoke.SHParseDisplayName(path, null, out ITEMIDLIST* pidl, 0, out _).ThrowOnFailure();
        try
        {
            PInvoke.SHOpenFolderAndSelectItems(pidl, 0, null, 0).ThrowOnFailure();
        }
        finally
        {
            PInvoke.ILFree(pidl);
        }
    }
}
