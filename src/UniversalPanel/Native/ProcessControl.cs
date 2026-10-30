// Actions mirror System Informer (MIT), SystemInformer/actions.c:
//   PhUiTerminateProcesses  PROCESS_TERMINATE, exit status 1 (Task Manager / Process Explorer use 1,
//                           and winlogon only restarts explorer.exe for status 1)
//   PhUiSuspendProcesses /  PROCESS_SUSPEND_RESUME + NtSuspendProcess / NtResumeProcess
//   PhUiResumeProcesses
// Native signatures per phnt/include/ntpsapi.h.
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d
// Addition: every action re-checks the process creation time after opening it, so a PID that was
// reused since the list was sampled can't redirect the action to a different process.

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace UniversalPanel.Native;

internal enum ProcessActionFailure { AccessDenied, Exited, Failed }

/// <summary>An expected failure of a process action, with a message fit for the user.</summary>
internal sealed class ProcessActionException(ProcessActionFailure failure, string message) : Exception(message)
{
    public ProcessActionFailure Failure { get; } = failure;
}

internal static partial class ProcessControl
{
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_INVALID_PARAMETER = 87;
    private const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);
    private const int STATUS_PROCESS_IS_TERMINATING = unchecked((int)0xC000010A);
    private const int ProcessBreakOnTermination = 29;

    public static void Terminate(ProcessKey key, string name) =>
        WithProcess(key, name, PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE, h =>
        {
            if (!PInvoke.TerminateProcess(h, 1))
            {
                ThrowLastError(name, "end");
            }
        });

    public static void Suspend(ProcessKey key, string name) =>
        WithProcess(key, name, PROCESS_ACCESS_RIGHTS.PROCESS_SUSPEND_RESUME, h => CheckStatus(NtSuspendProcess(Raw(h)), name, "suspend"));

    public static void Resume(ProcessKey key, string name) =>
        WithProcess(key, name, PROCESS_ACCESS_RIGHTS.PROCESS_SUSPEND_RESUME, h => CheckStatus(NtResumeProcess(Raw(h)), name, "resume"));

    /// <summary>
    /// Mirrors System Informer's PhUiSetPriorityClassProcesses: PROCESS_SET_INFORMATION, and never
    /// for System, since changing its priority can bugcheck some Windows versions.
    /// Realtime silently becomes High unless the caller holds SeIncreaseBasePriorityPrivilege.
    /// </summary>
    public static void SetPriority(ProcessKey key, string name, PROCESS_CREATION_FLAGS priorityClass)
    {
        if (key.Pid is 0 or 4)
        {
            throw new ProcessActionException(ProcessActionFailure.Failed, $"Changing the priority of {name} isn't allowed; it can crash Windows.");
        }

        WithProcess(key, name, PROCESS_ACCESS_RIGHTS.PROCESS_SET_INFORMATION, h =>
        {
            if (!PInvoke.SetPriorityClass(h, priorityClass))
            {
                ThrowLastError(name, "change the priority of");
            }
        });
    }

    /// <summary>
    /// True if the kernel will bugcheck when this process exits (ProcessBreakOnTermination), false
    /// if not, null if it can't be queried (e.g. access denied when not elevated).
    /// </summary>
    public static unsafe bool? IsCritical(ProcessKey key)
    {
        var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)key.Pid);
        if (handle.IsNull)
        {
            return null;
        }

        try
        {
            if (!HasCreateTime(handle, key))
            {
                return null;
            }

            uint value = 0;
            return NtQueryInformationProcess(Raw(handle), ProcessBreakOnTermination, &value, sizeof(uint), null) >= 0
                ? value != 0
                : null;
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }

    private static void WithProcess(ProcessKey key, string name, PROCESS_ACCESS_RIGHTS access, Action<HANDLE> action)
    {
        // Least access needed for the action, plus limited query for the identity check.
        var handle = PInvoke.OpenProcess(access | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)key.Pid);
        if (handle.IsNull)
        {
            int error = Marshal.GetLastPInvokeError();
            throw error switch
            {
                ERROR_ACCESS_DENIED => Denied(name),
                ERROR_INVALID_PARAMETER => Exited(name),
                _ => new ProcessActionException(ProcessActionFailure.Failed, $"Couldn't open {name}: {Marshal.GetPInvokeErrorMessage(error)}"),
            };
        }

        try
        {
            if (!HasCreateTime(handle, key))
            {
                throw Exited(name);
            }

            action(handle);
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }

    private static unsafe bool HasCreateTime(HANDLE handle, ProcessKey key)
    {
        System.Runtime.InteropServices.ComTypes.FILETIME created, exited, kernel, user;
        if (!PInvoke.GetProcessTimes(handle, &created, &exited, &kernel, &user))
        {
            return false;
        }

        long createTime = ((long)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
        return createTime == key.CreateTime;
    }

    private static void CheckStatus(int status, string name, string verb)
    {
        if (status >= 0)
        {
            return;
        }

        throw status switch
        {
            STATUS_ACCESS_DENIED => Denied(name),
            STATUS_PROCESS_IS_TERMINATING => Exited(name),
            _ => new ProcessActionException(ProcessActionFailure.Failed, $"Couldn't {verb} {name} (NTSTATUS 0x{status:X8})."),
        };
    }

    private static void ThrowLastError(string name, string verb)
    {
        int error = Marshal.GetLastPInvokeError();
        throw error == ERROR_ACCESS_DENIED
            ? Denied(name)
            : new ProcessActionException(ProcessActionFailure.Failed, $"Couldn't {verb} {name}: {Marshal.GetPInvokeErrorMessage(error)}");
    }

    private static ProcessActionException Denied(string name) =>
        new(ProcessActionFailure.AccessDenied, $"Access denied to {name}. It may be protected, or the panel needs to run as administrator.");

    private static ProcessActionException Exited(string name) =>
        new(ProcessActionFailure.Exited, $"{name} has already exited.");

    private static unsafe nint Raw(HANDLE handle) => (nint)handle.Value;

    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(nint processHandle);

    [LibraryImport("ntdll.dll")]
    private static partial int NtResumeProcess(nint processHandle);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationProcess(
        nint processHandle,
        int processInformationClass,
        void* processInformation,
        uint processInformationLength,
        uint* returnLength);
}
