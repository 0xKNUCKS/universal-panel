// Mirrors System Informer (MIT, Copyright (c) 2022 Winsider Seminars & Solutions, Inc.):
//   phlib/native.c      PhEnumHandlesEx2: NtQueryInformationProcess(ProcessHandleInformation),
//                       grow on STATUS_INFO_LENGTH_MISMATCH (max 8 attempts), and treat
//                       NumberOfHandles == 0 as failure (minimal processes return junk success)
//   phlib/hndlinfo.c    object type table from NtQueryObject(ObjectTypesInformation) using
//                       TypeIndex; File handle names queried on a worker thread with a 1 s timeout
//                       (PhCallNtQueryObjectWithTimeout) because they can hang on synchronous pipes
//   phlib/include/hndlinfo.h  PH_FIRST_OBJECT_TYPE / PH_NEXT_OBJECT_TYPE alignment
//   phnt/include/ntpsapi.h, ntobapi.h  struct layouts
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d
// Deviations:
//  - System Informer kills its native worker thread on timeout. .NET can't safely kill a thread, so
//    a timed-out worker is abandoned (it closes its duplicated handle whenever the call returns) and a
//    fresh one takes over, with a hard cap on abandoned threads. Hung handles are remembered per
//    process instance, so later loads skip them instantly instead of hanging again.
//  - Timeout 300 ms instead of 1 s: a name query that isn't hung returns in microseconds, so the
//    timeout only has to separate "hung" from "slow", and every hang used to cost a full second.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace UniversalPanel.Native;

internal sealed record HandleRecord(ulong Handle, string Type, string Name, uint GrantedAccess, uint Attributes);

internal static unsafe partial class HandleSnapshot
{
    private const int ProcessHandleInformation = 51;
    private const int ObjectNameInformation = 1;
    private const int ObjectTypesInformation = 3;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);
    private const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_INVALID_PARAMETER = 87;
    private static readonly TimeSpan FileNameTimeout = TimeSpan.FromMilliseconds(300);
    private const int MaxRememberedHungHandles = 1024;
    private static readonly HashSet<(ProcessKey Process, nint Handle)> HungHandles = [];

    private static readonly Lock TypesLock = new();
    // One capture at a time: the timed worker is shared and assumes a single caller.
    private static readonly Lock CaptureLock = new();
    private static Dictionary<int, string>? _typeNames;

    static HandleSnapshot()
    {
        Debug.Assert(sizeof(PROCESS_HANDLE_TABLE_ENTRY_INFO) == 40, "PROCESS_HANDLE_TABLE_ENTRY_INFO layout mismatch");
        Debug.Assert(sizeof(OBJECT_TYPE_INFORMATION) == 104, "OBJECT_TYPE_INFORMATION layout mismatch");
    }

    /// <summary>
    /// All handles of a process instance with type and (where possible) name.
    /// <paramref name="processNames"/> maps PIDs to names for Process/Thread handles.
    /// Throws <see cref="ProcessActionException"/> for expected failures.
    /// </summary>
    public static List<HandleRecord> Capture(ProcessKey key, string processName, IReadOnlyDictionary<int, string> processNames)
    {
        lock (CaptureLock)
        {
            return CaptureLocked(key, processName, processNames);
        }
    }

    private static List<HandleRecord> CaptureLocked(ProcessKey key, string processName, IReadOnlyDictionary<int, string> processNames)
    {
        // Names need PROCESS_DUP_HANDLE; listing alone needs only PROCESS_QUERY_INFORMATION.
        bool canDuplicate = true;
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_DUP_HANDLE, false, (uint)key.Pid);
        if (process.IsNull)
        {
            canDuplicate = false;
            process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION, false, (uint)key.Pid);
        }

        if (process.IsNull)
        {
            throw Marshal.GetLastPInvokeError() == ERROR_INVALID_PARAMETER
                ? new ProcessActionException(ProcessActionFailure.Exited, $"{processName} has already exited.")
                : new ProcessActionException(ProcessActionFailure.AccessDenied,
                    $"Access denied to the handles of {processName}. It may be protected, or the panel needs to run as administrator.");
        }

        try
        {
            if (!ProcessControl.IsSameInstance(key))
            {
                throw new ProcessActionException(ProcessActionFailure.Exited, $"{processName} has already exited.");
            }

            var entries = QueryHandleTable(process, processName);
            var types = GetTypeNames();
            var paths = new PathTranslator();
            var names = new NameResolver(key, process, canDuplicate, processNames, paths);

            var records = new List<HandleRecord>(entries.Count);
            foreach (var entry in entries)
            {
                var type = types.TryGetValue((int)entry.ObjectTypeIndex, out var t) ? t : $"Type {entry.ObjectTypeIndex}";
                records.Add(new HandleRecord(
                    (ulong)entry.HandleValue,
                    type,
                    names.Resolve(entry.HandleValue, type),
                    entry.GrantedAccess,
                    entry.HandleAttributes));
            }

            return records;
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    /// <summary>
    /// Closes a handle inside another process (DuplicateHandle with DUPLICATE_CLOSE_SOURCE), as
    /// System Informer's "Close" does. Can make that process misbehave; callers must confirm.
    /// </summary>
    public static void CloseRemote(ProcessKey key, string processName, ulong handleValue)
    {
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_DUP_HANDLE | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)key.Pid);
        if (process.IsNull)
        {
            throw Marshal.GetLastPInvokeError() == ERROR_INVALID_PARAMETER
                ? new ProcessActionException(ProcessActionFailure.Exited, $"{processName} has already exited.")
                : new ProcessActionException(ProcessActionFailure.AccessDenied, $"Access denied to {processName}. The panel may need to run as administrator.");
        }

        try
        {
            if (!ProcessControl.IsSameInstance(key))
            {
                throw new ProcessActionException(ProcessActionFailure.Exited, $"{processName} has already exited.");
            }

            if (!PInvoke.DuplicateHandle(process, new HANDLE((void*)(nint)handleValue), HANDLE.Null, null, 0, false, DUPLICATE_HANDLE_OPTIONS.DUPLICATE_CLOSE_SOURCE))
            {
                int error = Marshal.GetLastPInvokeError();
                throw new ProcessActionException(error == ERROR_ACCESS_DENIED ? ProcessActionFailure.AccessDenied : ProcessActionFailure.Failed,
                    $"Couldn't close handle 0x{handleValue:X}: {Marshal.GetPInvokeErrorMessage(error)}");
            }
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    private static List<PROCESS_HANDLE_TABLE_ENTRY_INFO> QueryHandleTable(HANDLE process, string processName)
    {
        uint size = 0x8000;
        for (int attempt = 0; ; attempt++)
        {
            var buffer = (byte*)NativeMemory.AllocZeroed(size);
            try
            {
                int status = NtQueryInformationProcess(Raw(process), ProcessHandleInformation, buffer, size, out uint needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH && attempt < 8)
                {
                    size = Math.Max(needed, size * 2);
                    continue;
                }

                var header = (PROCESS_HANDLE_SNAPSHOT_INFORMATION*)buffer;
                // Minimal processes can return success with an empty/invalid table (see header).
                if (status < 0 || header->NumberOfHandles == 0)
                {
                    throw new ProcessActionException(ProcessActionFailure.Failed,
                        $"{processName} didn't return handle information (NTSTATUS 0x{status:X8}).");
                }

                ulong count = header->NumberOfHandles;
                ulong fits = (size - (ulong)sizeof(PROCESS_HANDLE_SNAPSHOT_INFORMATION)) / (ulong)sizeof(PROCESS_HANDLE_TABLE_ENTRY_INFO);
                count = Math.Min(count, fits);

                var entries = new List<PROCESS_HANDLE_TABLE_ENTRY_INFO>((int)count);
                var first = (PROCESS_HANDLE_TABLE_ENTRY_INFO*)(buffer + sizeof(PROCESS_HANDLE_SNAPSHOT_INFORMATION));
                for (ulong i = 0; i < count; i++)
                {
                    entries.Add(first[i]);
                }

                return entries;
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }
    }

    /// <summary>Object type index to name. Types are fixed at boot, so this is queried once.</summary>
    private static Dictionary<int, string> GetTypeNames()
    {
        lock (TypesLock)
        {
            if (_typeNames is not null)
            {
                return _typeNames;
            }

            uint size = 0x10000;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var buffer = (byte*)NativeMemory.AllocZeroed(size);
                try
                {
                    int status = NtQueryObject(0, ObjectTypesInformation, buffer, size, out uint needed);
                    if (status is STATUS_INFO_LENGTH_MISMATCH or STATUS_BUFFER_TOO_SMALL)
                    {
                        size = Math.Max(needed, size * 2);
                        continue;
                    }

                    if (status < 0)
                    {
                        break;
                    }

                    var map = new Dictionary<int, string>();
                    uint count = *(uint*)buffer; // OBJECT_TYPES_INFORMATION.NumberOfTypes
                    byte* end = buffer + size;
                    byte* at = buffer + AlignUp((uint)sizeof(uint)); // PH_FIRST_OBJECT_TYPE
                    for (uint i = 0; i < count && at + sizeof(OBJECT_TYPE_INFORMATION) <= end; i++)
                    {
                        var info = (OBJECT_TYPE_INFORMATION*)at;
                        var nameStart = (byte*)info->TypeName.Buffer;
                        if (nameStart >= buffer && nameStart + info->TypeName.Length <= end)
                        {
                            map[info->TypeIndex] = new string((char*)nameStart, 0, info->TypeName.Length / sizeof(char));
                        }

                        at += sizeof(OBJECT_TYPE_INFORMATION) + AlignUp(info->TypeName.MaximumLength); // PH_NEXT_OBJECT_TYPE
                    }

                    return _typeNames = map;
                }
                finally
                {
                    NativeMemory.Free(buffer);
                }
            }

            return [];
        }
    }

    private static uint AlignUp(uint value) => (value + (uint)sizeof(nint) - 1) & ~((uint)sizeof(nint) - 1);

    /// <summary>NtQueryObject(ObjectNameInformation) on a handle we own; null if unnamed/failed.</summary>
    private static string? QueryName(HANDLE handle)
    {
        uint size = 0x200;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var buffer = (byte*)NativeMemory.AllocZeroed(size);
            try
            {
                int status = NtQueryObject(Raw(handle), ObjectNameInformation, buffer, size, out uint needed);
                if (status is STATUS_BUFFER_OVERFLOW or STATUS_INFO_LENGTH_MISMATCH or STATUS_BUFFER_TOO_SMALL)
                {
                    size = Math.Max(needed, size * 2);
                    continue;
                }

                if (status < 0)
                {
                    return null;
                }

                var name = (UNICODE_STRING*)buffer; // OBJECT_NAME_INFORMATION
                var start = (byte*)name->Buffer;
                if (name->Length == 0 || start < buffer || start + name->Length > buffer + size)
                {
                    return null;
                }

                return new string((char*)start, 0, name->Length / sizeof(char));
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        return null;
    }

    private static nint Raw(HANDLE handle) => (nint)handle.Value;

    /// <summary>Resolves handle names for one capture pass.</summary>
    private sealed class NameResolver(ProcessKey key, HANDLE process, bool canDuplicate, IReadOnlyDictionary<int, string> processNames, PathTranslator paths)
    {

        public string Resolve(nint handleValue, string type)
        {
            if (!canDuplicate)
            {
                return string.Empty;
            }

            return type switch
            {
                "Process" => DescribeProcess(handleValue),
                "Thread" => DescribeThread(handleValue),
                "File" => QueryFileName(handleValue),
                _ => Translate(type, QueryOther(handleValue)),
            };
        }

        // Duplicated with the least access that answers the question: 0 for names, limited
        // query for process/thread identity. Every duplicate is closed right after use.
        private HANDLE Duplicate(nint handleValue, uint access)
        {
            HANDLE dup;
            return PInvoke.DuplicateHandle(process, new HANDLE((void*)handleValue), PInvoke.GetCurrentProcess(), &dup, access, false, 0)
                ? dup
                : HANDLE.Null;
        }

        private string DescribeProcess(nint handleValue)
        {
            var dup = Duplicate(handleValue, (uint)PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION);
            if (dup.IsNull)
            {
                return string.Empty;
            }

            try
            {
                int pid = (int)PInvoke.GetProcessId(dup);
                return pid == 0 ? string.Empty : $"{NameOf(pid)} ({pid})";
            }
            finally
            {
                PInvoke.CloseHandle(dup);
            }
        }

        private string DescribeThread(nint handleValue)
        {
            var dup = Duplicate(handleValue, (uint)THREAD_ACCESS_RIGHTS.THREAD_QUERY_LIMITED_INFORMATION);
            if (dup.IsNull)
            {
                return string.Empty;
            }

            try
            {
                int tid = (int)PInvoke.GetThreadId(dup);
                int pid = (int)PInvoke.GetProcessIdOfThread(dup);
                return tid == 0 ? string.Empty : $"{NameOf(pid)} ({pid}): {tid}";
            }
            finally
            {
                PInvoke.CloseHandle(dup);
            }
        }

        private string NameOf(int pid) => processNames.TryGetValue(pid, out var name) ? name : "Non-existent process";

        private string QueryOther(nint handleValue)
        {
            var dup = Duplicate(handleValue, 0);
            if (dup.IsNull)
            {
                return string.Empty;
            }

            try
            {
                return QueryName(dup) ?? string.Empty;
            }
            finally
            {
                PInvoke.CloseHandle(dup);
            }
        }

        private string QueryFileName(nint handleValue)
        {
            // Captures are serialized (CaptureLock), so HungHandles needs no lock of its own.
            if (HungHandles.Contains((key, handleValue)))
            {
                return "(name query hung earlier)";
            }

            if (TimedWorker.IsExhausted)
            {
                return "(name skipped: too many hung queries)";
            }

            var dup = Duplicate(handleValue, 0);
            if (dup.IsNull)
            {
                return string.Empty;
            }

            // The worker owns the duplicate from here on and closes it when the query returns,
            // even if that's long after we gave up waiting.
            if (TimedWorker.TryRun(() =>
                {
                    try
                    {
                        return QueryName(dup);
                    }
                    finally
                    {
                        PInvoke.CloseHandle(dup);
                    }
                },
                FileNameTimeout,
                out var name))
            {
                return Translate("File", name ?? string.Empty);
            }

            if (HungHandles.Count >= MaxRememberedHungHandles)
            {
                HungHandles.Clear();
            }

            HungHandles.Add((key, handleValue));
            return "(name query timed out)";
        }

        private string Translate(string type, string name) => type switch
        {
            "File" => paths.ToDosPath(name),
            "Key" => paths.ToRegistryPath(name),
            _ => name,
        };
    }

    /// <summary>
    /// Runs a query on a worker thread with a timeout. A worker whose query hangs is abandoned (it
    /// stays blocked until the I/O it waits on completes, then returns to the pool) and the next
    /// query gets another worker. At most <see cref="MaxAbandoned"/> threads can be stuck at once;
    /// past that, callers skip queries until one comes back.
    /// </summary>
    private static class TimedWorker
    {
        private const int MaxAbandoned = 4;
        private static readonly Lock Gate = new();
        private static readonly Stack<Worker> Idle = new();
        private static readonly List<Worker> Abandoned = [];

        /// <summary>True when the cap of stuck threads is reached; reaps any that came back first.</summary>
        public static bool IsExhausted
        {
            get
            {
                lock (Gate)
                {
                    Reap();
                    return Abandoned.Count >= MaxAbandoned;
                }
            }
        }

        private static void Reap()
        {
            for (int i = Abandoned.Count - 1; i >= 0; i--)
            {
                if (Abandoned[i].Done.WaitOne(0))
                {
                    Idle.Push(Abandoned[i]); // Its call finally returned; reuse it.
                    Abandoned.RemoveAt(i);
                }
            }
        }

        /// <summary>Only call when <see cref="IsExhausted"/> is false (captures are serialized).</summary>
        public static bool TryRun(Func<string?> work, TimeSpan timeout, out string? result)
        {
            Worker worker;
            lock (Gate)
            {
                Debug.Assert(Abandoned.Count < MaxAbandoned, "check IsExhausted before duplicating a handle for the worker");
                worker = Idle.Count > 0 ? Idle.Pop() : new Worker();
            }

            bool finished = worker.Run(work, timeout, out result);
            lock (Gate)
            {
                if (finished)
                {
                    Idle.Push(worker);
                }
                else
                {
                    Abandoned.Add(worker);
                }
            }

            return finished;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification =
            "Workers live for the whole process (one, or one stuck plus one); there is no point at which disposing them is safe.")]
        private sealed class Worker
        {
            private readonly AutoResetEvent _start = new(false);
            private Func<string?>? _work;
            private string? _result;

            public Worker()
            {
                new Thread(Loop) { IsBackground = true, Name = "Handle name query" }.Start();
            }

            public AutoResetEvent Done { get; } = new(false);

            public bool Run(Func<string?> work, TimeSpan timeout, out string? result)
            {
                _work = work;
                _start.Set();
                if (Done.WaitOne(timeout))
                {
                    result = _result;
                    return true;
                }

                result = null;
                return false;
            }

            private void Loop()
            {
                while (true)
                {
                    _start.WaitOne();
                    try
                    {
                        _result = _work!();
                    }
                    catch (Exception)
                    {
                        _result = null;
                    }

                    Done.Set();
                }
            }
        }
    }

    /// <summary>NT object paths to the names people recognize (C:\..., HKLM\...).</summary>
    private sealed class PathTranslator
    {
        private readonly List<(string Device, string Drive)> _devices = [];
        private readonly string? _userSid = WindowsIdentity.GetCurrent().User?.Value;

        public PathTranslator()
        {
            char* target = stackalloc char[1024];
            for (char letter = 'A'; letter <= 'Z'; letter++)
            {
                string drive = $"{letter}:";
                fixed (char* name = drive)
                {
                    if (PInvoke.QueryDosDevice(name, target, 1024) > 0)
                    {
                        // First string of the multi-string is the current mapping.
                        _devices.Add((new string(target), drive));
                    }
                }
            }
        }

        public string ToDosPath(string ntPath)
        {
            foreach (var (device, drive) in _devices)
            {
                if (ntPath.StartsWith(device, StringComparison.OrdinalIgnoreCase)
                    && (ntPath.Length == device.Length || ntPath[device.Length] == '\\'))
                {
                    return drive + ntPath[device.Length..];
                }
            }

            const string mup = @"\Device\Mup\";
            return ntPath.StartsWith(mup, StringComparison.OrdinalIgnoreCase) ? @"\\" + ntPath[mup.Length..] : ntPath;
        }

        public string ToRegistryPath(string ntPath)
        {
            if (_userSid is { } sid)
            {
                if (Replace(ntPath, $@"\REGISTRY\USER\{sid}_Classes", @"HKCU\Software\Classes", out var classes))
                {
                    return classes;
                }

                if (Replace(ntPath, $@"\REGISTRY\USER\{sid}", "HKCU", out var user))
                {
                    return user;
                }
            }

            if (Replace(ntPath, @"\REGISTRY\MACHINE", "HKLM", out var machine))
            {
                return machine;
            }

            return Replace(ntPath, @"\REGISTRY\USER", "HKU", out var users) ? users : ntPath;
        }

        private static bool Replace(string path, string prefix, string replacement, out string result)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (path.Length == prefix.Length || path[prefix.Length] == '\\'))
            {
                result = replacement + path[prefix.Length..];
                return true;
            }

            result = path;
            return false;
        }
    }

    // Signatures per phnt (ntpsapi.h, ntobapi.h).
    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint processHandle, int processInformationClass, void* processInformation, uint processInformationLength, out uint returnLength);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryObject(nint handle, int objectInformationClass, void* objectInformation, uint objectInformationLength, out uint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_HANDLE_SNAPSHOT_INFORMATION
    {
        public nuint NumberOfHandles;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_HANDLE_TABLE_ENTRY_INFO
    {
        public nint HandleValue;
        public nuint HandleCount;
        public nuint PointerCount;
        public uint GrantedAccess;
        public uint ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OBJECT_TYPE_INFORMATION
    {
        public UNICODE_STRING TypeName;
        public uint TotalNumberOfObjects;
        public uint TotalNumberOfHandles;
        public uint TotalPagedPoolUsage;
        public uint TotalNonPagedPoolUsage;
        public uint TotalNamePoolUsage;
        public uint TotalHandleTableUsage;
        public uint HighWaterNumberOfObjects;
        public uint HighWaterNumberOfHandles;
        public uint HighWaterPagedPoolUsage;
        public uint HighWaterNonPagedPoolUsage;
        public uint HighWaterNamePoolUsage;
        public uint HighWaterHandleTableUsage;
        public uint InvalidAttributes;
        public uint GenericRead;    // GENERIC_MAPPING
        public uint GenericWrite;
        public uint GenericExecute;
        public uint GenericAll;
        public uint ValidAccessMask;
        public byte SecurityRequired;
        public byte MaintainHandleCount;
        public byte TypeIndex;
        public byte ReservedByte;
        public uint PoolType;
        public uint DefaultPagedPoolCharge;
        public uint DefaultNonPagedPoolCharge;
    }
}
