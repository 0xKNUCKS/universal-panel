// Debug privilege mirrors System Informer (MIT), which enables SeDebugPrivilege when elevated so
// protected-but-not-PPL processes (services, other users) can be opened for querying.
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d
// The P/Invokes below are the documented advapi32 signatures, declared by hand.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace UniversalPanel.Native;

internal static partial class Elevation
{
    private const int ERROR_CANCELLED = 1223;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const int ERROR_NOT_ALL_ASSIGNED = 1300;

    /// <summary>True when running with an elevated (full administrator) token.</summary>
    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>
    /// Enables SeDebugPrivilege on the panel's own token. Only meaningful when elevated; returns
    /// false (without throwing) if the privilege isn't held.
    /// </summary>
    public static unsafe bool TryEnableDebugPrivilege()
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out var luid))
            {
                return false;
            }

            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            // AdjustTokenPrivileges succeeds even when the privilege isn't held; check the error.
            return AdjustTokenPrivileges(token, false, &privileges, 0, null, null)
                && Marshal.GetLastPInvokeError() != ERROR_NOT_ALL_ASSIGNED;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// Starts an elevated copy of the panel (UAC prompt) that replaces this one. Returns false if
    /// the user cancelled the prompt. The new copy waits for this process to exit before it claims
    /// the single-instance lock.
    /// </summary>
    public static bool TryRestartElevated()
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unknown.");
        var startInfo = new ProcessStartInfo(self)
        {
            UseShellExecute = true, // required for the "runas" verb (UAC)
            Verb = "runas",
        };
        startInfo.ArgumentList.Add(ReplaceArgument);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            Process.Start(startInfo)?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return false;
        }
    }

    public const string ReplaceArgument = "--replace";

    /// <summary>If started as a replacement, waits (up to 10 s) for the previous instance to exit.</summary>
    public static void WaitForReplacedInstance(string[] args)
    {
        int i = Array.IndexOf(args, ReplaceArgument);
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid))
        {
            return;
        }

        try
        {
            using var previous = Process.GetProcessById(pid);
            // Only wait on our own executable: never let an argument make us wait on a stranger.
            if (string.Equals(previous.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            {
                previous.WaitForExit(10_000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Already gone (or not ours to inspect): nothing to wait for.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool AdjustTokenPrivileges(nint tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAll, TOKEN_PRIVILEGES* newState, uint bufferLength, TOKEN_PRIVILEGES* previousState, uint* returnLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
