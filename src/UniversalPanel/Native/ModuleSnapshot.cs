// System Informer enumerates modules by walking the target's PEB loader list itself
// (phlib/nativemodule.c, PhEnumProcessModules). We use the documented Toolhelp32 API, which
// reads the same loader list through the OS, including both lists of a WOW64 process.
// https://learn.microsoft.com/windows/win32/api/tlhelp32/nf-tlhelp32-createtoolhelp32snapshot

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.ToolHelp;

namespace UniversalPanel.Native;

internal sealed record ModuleRecord(string Name, string Path, ulong BaseAddress, uint Size);

internal static class ModuleSnapshot
{
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_BAD_LENGTH = 24;
    private const int ERROR_PARTIAL_COPY = 299;
    private const int MaxBadLengthRetries = 5;

    /// <summary>
    /// Loaded modules of a process instance, sorted by base address.
    /// Throws <see cref="ProcessActionException"/> for expected failures (access denied, exited).
    /// </summary>
    public static unsafe List<ModuleRecord> Capture(ProcessKey key, string processName)
    {
        // Toolhelp takes a bare PID; check the instance before and after so a PID reused in
        // between can't hand us someone else's module list.
        EnsureSameInstance(key, processName);

        HANDLE snapshot = default;
        for (int attempt = 0; ; attempt++)
        {
            snapshot = PInvoke.CreateToolhelp32Snapshot(
                CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPMODULE | CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPMODULE32,
                (uint)key.Pid);
            if ((nint)snapshot.Value != -1) // INVALID_HANDLE_VALUE
            {
                break;
            }

            int error = Marshal.GetLastPInvokeError();
            // Documented: ERROR_BAD_LENGTH means the loader list changed mid-read; retry.
            if (error == ERROR_BAD_LENGTH && attempt < MaxBadLengthRetries)
            {
                continue;
            }

            throw error switch
            {
                ERROR_ACCESS_DENIED or ERROR_PARTIAL_COPY => new ProcessActionException(ProcessActionFailure.AccessDenied,
                    $"Access denied to the modules of {processName}. It may be protected, or the panel needs to run as administrator."),
                _ => new ProcessActionException(ProcessActionFailure.Failed,
                    $"Couldn't list the modules of {processName}: {Marshal.GetPInvokeErrorMessage(error)}"),
            };
        }

        var modules = new List<ModuleRecord>(128);
        // For a WOW64 process the two lists (TH32CS_SNAPMODULE + TH32CS_SNAPMODULE32) both contain
        // the main executable; keep one entry per base address + path.
        var seen = new HashSet<(ulong, string)>();
        try
        {
            var entry = new MODULEENTRY32W { dwSize = (uint)sizeof(MODULEENTRY32W) };
            for (bool ok = PInvoke.Module32FirstW(snapshot, &entry); ok; ok = PInvoke.Module32NextW(snapshot, &entry))
            {
                if (!seen.Add(((ulong)entry.modBaseAddr, entry.szExePath.ToString().ToUpperInvariant())))
                {
                    continue;
                }

                modules.Add(new ModuleRecord(
                    entry.szModule.ToString(),
                    entry.szExePath.ToString(),
                    (ulong)entry.modBaseAddr,
                    entry.modBaseSize));
            }
        }
        finally
        {
            PInvoke.CloseHandle(snapshot);
        }

        EnsureSameInstance(key, processName);
        modules.Sort((a, b) => a.BaseAddress.CompareTo(b.BaseAddress));
        return modules;
    }

    private static void EnsureSameInstance(ProcessKey key, string processName)
    {
        if (!ProcessControl.IsSameInstance(key))
        {
            throw new ProcessActionException(ProcessActionFailure.Exited, $"{processName} has already exited.");
        }
    }

    /// <summary>"ntdll.dll+0x1A2B0" for an address inside a module, else null.</summary>
    public static string? Describe(IReadOnlyList<ModuleRecord> sortedModules, ulong address)
    {
        int lo = 0, hi = sortedModules.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            var module = sortedModules[mid];
            if (address < module.BaseAddress)
            {
                hi = mid - 1;
            }
            else if (address >= module.BaseAddress + module.Size)
            {
                lo = mid + 1;
            }
            else
            {
                return $"{module.Name}+0x{address - module.BaseAddress:X}";
            }
        }

        return null;
    }
}
