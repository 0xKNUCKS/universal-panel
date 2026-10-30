using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace UniversalPanel.Native;

internal static class ProcessImage
{
    /// <summary>
    /// Full Win32 path of a process image, or null when it can't be read (System, Idle, minimal
    /// processes, or access denied). Uses PROCESS_QUERY_LIMITED_INFORMATION, the least access that
    /// works, which protected processes still grant to non-elevated callers.
    /// </summary>
    public static unsafe string? TryGetPath(int pid)
    {
        if (pid is 0 or 4)
        {
            return null;
        }

        var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle.IsNull)
        {
            return null;
        }

        try
        {
            char* small = stackalloc char[MAX_PATH_CHARS];
            if (TryQuery(handle, small, MAX_PATH_CHARS, out var path))
            {
                return path;
            }

            // Long-path aware (manifest): retry once at the NT limit, from the pool.
            var large = System.Buffers.ArrayPool<char>.Shared.Rent(LONG_PATH_CHARS);
            try
            {
                fixed (char* p = large)
                {
                    return TryQuery(handle, p, LONG_PATH_CHARS, out path) ? path : null;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<char>.Shared.Return(large);
            }
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }

    private const int MAX_PATH_CHARS = 520;
    private const int LONG_PATH_CHARS = 32 * 1024;

    private static unsafe bool TryQuery(HANDLE handle, char* buffer, int capacity, out string? path)
    {
        uint size = (uint)capacity;
        path = PInvoke.QueryFullProcessImageName(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, &size)
            ? new string(buffer, 0, (int)size)
            : null;
        return path is not null;
    }

    /// <summary>System-wide kernel + user time (kernel includes idle) and idle time, in 100 ns.</summary>
    public static (ulong Total, ulong Idle) GetSystemTimes()
    {
        if (!PInvoke.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return (0, 0);
        }

        return (ToUInt64(kernel) + ToUInt64(user), ToUInt64(idle));
    }

    /// <summary>Physical memory (total, available) in bytes, or (0, 0) if the call fails.</summary>
    public static (ulong Total, ulong Available) GetMemoryStatus()
    {
        var status = new Windows.Win32.System.SystemInformation.MEMORYSTATUSEX
        {
            dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Windows.Win32.System.SystemInformation.MEMORYSTATUSEX>(),
        };
        return PInvoke.GlobalMemoryStatusEx(ref status) ? (status.ullTotalPhys, status.ullAvailPhys) : (0, 0);
    }

    private static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME ft) =>
        ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
}
