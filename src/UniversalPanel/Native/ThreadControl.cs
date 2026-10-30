// Thread access mirrors System Informer (MIT):
//   SystemInformer/thrdprv.c  open with THREAD_QUERY_INFORMATION, fall back to
//                             THREAD_QUERY_LIMITED_INFORMATION
//   phlib/nativethread.c      PhGetThreadStartAddress = NtQueryInformationThread(
//                             ThreadQuerySetWin32StartAddress)
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace UniversalPanel.Native;

/// <summary>Identity of a thread instance; TIDs are reused just like PIDs.</summary>
internal readonly record struct ThreadKey(int Tid, long CreateTime);

internal static partial class ThreadControl
{
    private const int ThreadQuerySetWin32StartAddress = 9;

    /// <summary>
    /// The Win32 start address (the function passed to CreateThread), or null when the thread
    /// can't be opened. The address in the snapshot is the kernel-side RtlUserThreadStart for every
    /// user thread, so it's useless for telling threads apart.
    /// </summary>
    public static unsafe ulong? TryGetStartAddress(ThreadKey key)
    {
        var handle = PInvoke.OpenThread(THREAD_ACCESS_RIGHTS.THREAD_QUERY_INFORMATION, false, (uint)key.Tid);
        if (handle.IsNull)
        {
            handle = PInvoke.OpenThread(THREAD_ACCESS_RIGHTS.THREAD_QUERY_LIMITED_INFORMATION, false, (uint)key.Tid);
        }

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

            nuint address = 0;
            return NtQueryInformationThread(Raw(handle), ThreadQuerySetWin32StartAddress, &address, (uint)sizeof(nuint), null) >= 0
                ? address
                : null;
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }

    // Actions mirror System Informer's PhUiSuspendThreads / PhUiResumeThreads (THREAD_SUSPEND_RESUME)
    // and PhUiTerminateThreads (THREAD_TERMINATE, exit status 0), via the documented Win32 calls.

    public static void Suspend(ThreadKey key, int pid) =>
        WithThread(key, pid, THREAD_ACCESS_RIGHTS.THREAD_SUSPEND_RESUME, "suspend", h => PInvoke.SuspendThread(h) != uint.MaxValue);

    public static void Resume(ThreadKey key, int pid) =>
        WithThread(key, pid, THREAD_ACCESS_RIGHTS.THREAD_SUSPEND_RESUME, "resume", h => PInvoke.ResumeThread(h) != uint.MaxValue);

    public static void Terminate(ThreadKey key, int pid) =>
        WithThread(key, pid, THREAD_ACCESS_RIGHTS.THREAD_TERMINATE, "end", h => PInvoke.TerminateThread(h, 0));

    private static void WithThread(ThreadKey key, int pid, THREAD_ACCESS_RIGHTS access, string verb, Func<HANDLE, bool> action)
    {
        var name = $"thread {key.Tid}";
        var handle = PInvoke.OpenThread(access | THREAD_ACCESS_RIGHTS.THREAD_QUERY_LIMITED_INFORMATION, false, (uint)key.Tid);
        if (handle.IsNull)
        {
            throw Failure(Marshal.GetLastPInvokeError(), name, verb);
        }

        try
        {
            // Same identity check as processes: the TID must still be this thread instance, and
            // still belong to the process the user is looking at.
            if (!HasCreateTime(handle, key) || PInvoke.GetProcessIdOfThread(handle) != (uint)pid)
            {
                throw new ProcessActionException(ProcessActionFailure.Exited, $"{name} has already exited.");
            }

            if (!action(handle))
            {
                throw Failure(Marshal.GetLastPInvokeError(), name, verb);
            }
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }

    private static ProcessActionException Failure(int error, string name, string verb) => error switch
    {
        5 => new(ProcessActionFailure.AccessDenied, $"Access denied to {name}. The process may be protected, or the panel needs to run as administrator."),
        87 => new(ProcessActionFailure.Exited, $"{name} has already exited."),
        _ => new(ProcessActionFailure.Failed, $"Couldn't {verb} {name}: {Marshal.GetPInvokeErrorMessage(error)}"),
    };

    private static unsafe bool HasCreateTime(HANDLE handle, ThreadKey key)
    {
        System.Runtime.InteropServices.ComTypes.FILETIME created, exited, kernel, user;
        if (!PInvoke.GetThreadTimes(handle, &created, &exited, &kernel, &user))
        {
            return false;
        }

        long createTime = ((long)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
        return createTime == key.CreateTime;
    }

    private static unsafe nint Raw(HANDLE handle) => (nint)handle.Value;

    // Signature per phnt ntpsapi.h.
    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationThread(
        nint threadHandle,
        int threadInformationClass,
        void* threadInformation,
        uint threadInformationLength,
        uint* returnLength);
}
