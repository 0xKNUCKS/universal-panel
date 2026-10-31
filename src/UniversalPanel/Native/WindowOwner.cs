using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace UniversalPanel.Native;

internal static unsafe partial class WindowOwner
{
    /// <summary>PID and title of the top-level window under the cursor (System Informer's "find window").</summary>
    public static unsafe (int Pid, string Title)? UnderCursor()
    {
        if (!PInvoke.GetCursorPos(out var point))
        {
            return null;
        }

        var window = PInvoke.WindowFromPoint(point);
        if (window.IsNull)
        {
            return null;
        }

        var root = PInvoke.GetAncestor(window, GET_ANCESTOR_FLAGS.GA_ROOT);
        if (!root.IsNull)
        {
            window = root;
        }

        uint pid;
        if (PInvoke.GetWindowThreadProcessId(window, &pid) == 0)
        {
            return null;
        }

        char* title = stackalloc char[256];
        int length = PInvoke.GetWindowText(window, title, 256);
        return pid == 0 ? null : ((int)pid, new string(title, 0, Math.Max(0, length)));
    }

    /// <summary>
    /// Starts an executable with CREATE_SUSPENDED (its main thread waits before the first
    /// instruction, so a debugger can attach). Returns the PID; resume it from the process menu.
    /// The working directory is the executable's folder, as Explorer would use.
    /// </summary>
    public static unsafe int StartSuspended(string executable)
    {
        var startup = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW) };
        PROCESS_INFORMATION info;
        // CreateProcess may modify the command line buffer, so it must be writable.
        var commandLine = ("\"" + executable + "\"\0").ToCharArray();
        string? directory = System.IO.Path.GetDirectoryName(executable);
        fixed (char* app = executable)
        fixed (char* cmd = commandLine)
        fixed (char* dir = directory)
        {
            if (!CreateProcessW(app, cmd, null, null, false, CREATE_SUSPENDED, null, dir, &startup, &info))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        PInvoke.CloseHandle(info.hThread);
        PInvoke.CloseHandle(info.hProcess);
        return (int)info.dwProcessId;
    }

    private const uint CREATE_SUSPENDED = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public char* lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public byte* lpReserved2;
        public nint hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public HANDLE hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    // Documented kernel32 signature, declared by hand for exact control of every argument.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(char* applicationName, char* commandLine, void* processAttributes, void* threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, void* environment, char* currentDirectory,
        STARTUPINFOW* startupInfo, PROCESS_INFORMATION* processInformation);
}
