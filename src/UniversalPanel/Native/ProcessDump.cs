// Mirrors System Informer (MIT):
//   SystemInformer/mainwnd.c  dump presets: "normal" (Task Manager's flags) and "minimal"
//   SystemInformer/mdump.c    PhUiCreateDumpFileProcess: open with QUERY_INFORMATION | VM_READ
//                             (+ DUP_HANDLE for handle data), then MiniDumpWriteDump
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d
// Deviations: MiniDumpWithIptTrace is left out (older dbghelp versions reject unknown flags), and
// 32-bit processes are dumped from this 64-bit process (System Informer re-launches its 32-bit
// build for those), so their dumps show the WOW64 layer; WinDbg's !wow64exts handles that.

using System.IO;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Diagnostics.Debug; // MINIDUMP_TYPE
using Windows.Win32.System.Threading;

namespace UniversalPanel.Native;

internal enum DumpKind { Full, Mini }

internal static partial class ProcessDump
{
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_INVALID_PARAMETER = 87;

    private static MINIDUMP_TYPE Flags(DumpKind kind) => kind switch
    {
        DumpKind.Full =>
            MINIDUMP_TYPE.MiniDumpWithFullMemory |
            MINIDUMP_TYPE.MiniDumpWithHandleData |
            MINIDUMP_TYPE.MiniDumpWithUnloadedModules |
            MINIDUMP_TYPE.MiniDumpWithFullMemoryInfo |
            MINIDUMP_TYPE.MiniDumpWithThreadInfo |
            MINIDUMP_TYPE.MiniDumpIgnoreInaccessibleMemory,
        _ =>
            MINIDUMP_TYPE.MiniDumpWithDataSegs |
            MINIDUMP_TYPE.MiniDumpWithUnloadedModules |
            MINIDUMP_TYPE.MiniDumpWithThreadInfo |
            MINIDUMP_TYPE.MiniDumpIgnoreInaccessibleMemory,
    };

    // Declared by hand (documented dbghelp signature): CsWin32 only generates a SafeHandle
    // overload that dereferences its null optional parameters and throws.
    [LibraryImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MiniDumpWriteDump(nint process, uint processId, nint file, uint dumpType, nint exceptionParam, nint userStreamParam, nint callbackParam);

    /// <summary>Writes a dump to <paramref name="path"/>. A partial file is deleted on failure.</summary>
    public static unsafe void Write(ProcessKey key, string processName, string path, DumpKind kind)
    {
        var access = PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ;
        var process = PInvoke.OpenProcess(access | PROCESS_ACCESS_RIGHTS.PROCESS_DUP_HANDLE, false, (uint)key.Pid);
        if (process.IsNull)
        {
            process = PInvoke.OpenProcess(access, false, (uint)key.Pid); // without handle data
        }

        if (process.IsNull)
        {
            throw Marshal.GetLastPInvokeError() == ERROR_INVALID_PARAMETER
                ? new ProcessActionException(ProcessActionFailure.Exited, $"{processName} has already exited.")
                : new ProcessActionException(ProcessActionFailure.AccessDenied,
                    $"Access denied to {processName}. It may be protected, or the panel needs to run as administrator.");
        }

        try
        {
            if (!ProcessControl.IsSameInstance(key))
            {
                throw new ProcessActionException(ProcessActionFailure.Exited, $"{processName} has already exited.");
            }

            bool written = false;
            try
            {
                using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    if (!MiniDumpWriteDump((nint)process.Value, (uint)key.Pid, file.SafeFileHandle.DangerousGetHandle(), (uint)Flags(kind), 0, 0, 0))
                    {
                        int error = Marshal.GetLastPInvokeError();
                        throw new ProcessActionException(
                            error == ERROR_ACCESS_DENIED ? ProcessActionFailure.AccessDenied : ProcessActionFailure.Failed,
                            $"Couldn't dump {processName}: {Marshal.GetPInvokeErrorMessage(error)}");
                    }
                }

                written = true;
            }
            finally
            {
                if (!written && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }
}
