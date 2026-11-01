// Launching user-picked tools. When the panel is elevated, a normal launch must not inherit that:
// it goes through Explorer's own ShellExecute (Raymond Chen, The Old New Thing, "How can I launch
// an unelevated process from my elevated process and vice versa?", 2013-11-18: IShellWindows::
// FindWindowSW(desktop) -> SID_STopLevelBrowser -> IShellBrowser::QueryActiveShellView ->
// IShellView::GetItemObject(SVGIO_BACKGROUND) -> IShellFolderViewDual.Application.ShellExecute),
// so the tool runs as Explorer does. If that isn't possible the launch fails instead of falling
// back to an elevated start. "Run as administrator" is always an explicit, per-click choice.

using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace UniversalPanel.Native;

internal static class ToolLauncher
{
    private const int ErrorCancelled = 1223; // the user said no to UAC

    /// <summary>Starts the file with the user's normal (unelevated) rights.</summary>
    /// <param name="arguments">An already-quoted command line (see <see cref="ToolCommand"/>).</param>
    public static void Open(string path, string arguments = "", string? directory = null)
    {
        directory ??= Path.GetDirectoryName(path) ?? string.Empty;
        try
        {
            if (Elevation.IsElevated)
            {
                ShellExecuteAsExplorer(path, arguments, directory);
            }
            else
            {
                using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, arguments)
                {
                    UseShellExecute = true,
                    WorkingDirectory = directory,
                });
            }
        }
        catch (Exception ex) when (ex is Win32Exception or COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            throw new ProcessActionException(ProcessActionFailure.Failed, $"Couldn't start {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>Starts the file elevated (UAC prompt unless the panel is elevated). False if the user cancelled.</summary>
    public static bool RunAsAdministrator(string path, string arguments = "", string? directory = null)
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = directory ?? Path.GetDirectoryName(path) ?? string.Empty,
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
        catch (Win32Exception ex)
        {
            throw new ProcessActionException(ProcessActionFailure.Failed, $"Couldn't start {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static void ShellExecuteAsExplorer(string path, string arguments, string directory)
    {
        var shellWindows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows, throwOnError: true)!)!;
        try
        {
            object location = CSIDL_DESKTOP;
            object root = null!;
            var desktop = shellWindows.FindWindowSW(ref location, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH)
                ?? throw new COMException("Explorer's desktop isn't running");
            var browser = (IShellBrowser)((IServiceProvider)desktop).QueryService(SID_STopLevelBrowser, typeof(IShellBrowser).GUID);
            var view = browser.QueryActiveShellView();
            var dispatch = view.GetItemObject(SVGIO_BACKGROUND, IID_IDispatch);
            dynamic folderView = dispatch;
            dynamic shell = folderView.Application;
            shell.ShellExecute(path, arguments, directory, "open", SW_SHOWNORMAL);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellWindows);
        }
    }

    private static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");
    private const int CSIDL_DESKTOP = 0, SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1, SW_SHOWNORMAL = 1;
    private const uint SVGIO_BACKGROUND = 0;

    // Vtable order from exdisp.h / shobjidl_core.h; only the methods called have real signatures.
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        void _Count();
        void _Item();
        void _NewEnum();
        void _Register();
        void _RegisterPending();
        void _Revoke();
        void _OnNavigate();
        void _OnActivated();

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW(ref object pvarLoc, ref object pvarLocRoot, int swClass, out int phwnd, int swfwOptions);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(in Guid guidService, in Guid riid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void _GetWindow();
        void _ContextSensitiveHelp();
        void _InsertMenusSB();
        void _SetMenuSB();
        void _RemoveMenusSB();
        void _SetStatusTextSB();
        void _EnableModelessSB();
        void _TranslateAcceleratorSB();
        void _BrowseObject();
        void _GetViewStateStream();
        void _GetControlWindow();
        void _SendControlMsg();
        IShellView QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void _GetWindow();
        void _ContextSensitiveHelp();
        void _TranslateAccelerator();
        void _EnableModeless();
        void _UIActivate();
        void _Refresh();
        void _CreateViewWindow();
        void _DestroyViewWindow();
        void _GetCurrentInfo();
        void _AddPropertySheetPages();
        void _SaveViewState();
        void _SelectItem();

        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetItemObject(uint uItem, in Guid riid);
    }
}
