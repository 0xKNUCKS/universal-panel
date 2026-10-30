// Mirrors System Informer (MIT, Copyright (c) 2022 Winsider Seminars & Solutions, Inc.):
//   phnt/include/ntexapi.h  SYSTEM_PROCESS_INFORMATION (field layout)
//   phlib/native.c          PhEnumProcessesEx (grow on STATUS_INFO_LENGTH_MISMATCH /
//                           STATUS_BUFFER_TOO_SMALL, keep the size for the next call)
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d
// Changes: the buffer is kept between calls instead of reallocated each time, entries are parsed
// into managed records with bounds checks, and the idle process gets its conventional name.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UniversalPanel.Native;

/// <summary>Identity of a process instance. PIDs are reused; PID + creation time is not.</summary>
internal readonly record struct ProcessKey(int Pid, long CreateTime);

internal sealed record ProcessRecord(
    ProcessKey Key,
    int ParentPid,
    string Name,
    long CpuTime100ns,
    ulong WorkingSetBytes,
    ulong PrivateBytes,
    int Threads,
    int Handles,
    int SessionId,
    int BasePriority);

/// <summary>
/// Reads every process in one <c>NtQuerySystemInformation(SystemProcessInformation)</c> call, no
/// per-process handles needed. Not thread-safe: own one instance per sampling thread.
/// </summary>
internal sealed unsafe partial class ProcessSnapshotReader : IDisposable
{
    private const int SystemProcessInformation = 5;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);
    private const uint InitialBufferSize = 0x4000;
    // The list can grow between the size query and the retry; a little slack avoids a third call.
    private const uint GrowthSlack = 0x2000;

    private void* _buffer;
    private uint _bufferSize = InitialBufferSize;

    static ProcessSnapshotReader()
    {
        // Layout check against phnt: 0x100 bytes on x64 before the trailing thread array.
        Debug.Assert(sizeof(SYSTEM_PROCESS_INFORMATION) == 0x100, "SYSTEM_PROCESS_INFORMATION layout mismatch");
    }

    public List<ProcessRecord> Capture()
    {
        ObjectDisposedException.ThrowIf(_bufferSize == 0, this);

        uint returned;
        while (true)
        {
            _buffer = _buffer is null ? NativeMemory.Alloc(_bufferSize) : _buffer;
            int status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _bufferSize, out returned);

            if (status is STATUS_INFO_LENGTH_MISMATCH or STATUS_BUFFER_TOO_SMALL)
            {
                NativeMemory.Free(_buffer);
                _buffer = null;
                _bufferSize = Math.Max(returned, _bufferSize) + GrowthSlack;
                continue;
            }

            if (status < 0)
            {
                throw new InvalidOperationException($"NtQuerySystemInformation(SystemProcessInformation) failed: NTSTATUS 0x{status:X8}")
                {
                    HResult = status,
                };
            }

            break;
        }

        return Parse((byte*)_buffer, Math.Min(returned, _bufferSize));
    }

    private static List<ProcessRecord> Parse(byte* buffer, uint length)
    {
        var records = new List<ProcessRecord>(512);
        uint offset = 0;

        while (offset + (uint)sizeof(SYSTEM_PROCESS_INFORMATION) <= length)
        {
            var spi = (SYSTEM_PROCESS_INFORMATION*)(buffer + offset);
            int pid = (int)spi->UniqueProcessId;

            records.Add(new ProcessRecord(
                new ProcessKey(pid, spi->CreateTime),
                (int)spi->InheritedFromUniqueProcessId,
                ReadName(spi, buffer, length, pid),
                spi->KernelTime + spi->UserTime,
                spi->WorkingSetSize,
                spi->PagefileUsage, // "Private bytes" in System Informer / Process Explorer.
                (int)spi->NumberOfThreads,
                (int)spi->HandleCount,
                (int)spi->SessionId,
                spi->BasePriority));

            if (spi->NextEntryOffset == 0)
            {
                break;
            }

            offset += spi->NextEntryOffset;
        }

        return records;
    }

    private static string ReadName(SYSTEM_PROCESS_INFORMATION* spi, byte* buffer, uint length, int pid)
    {
        var name = spi->ImageName;
        if (name.Buffer == 0 || name.Length == 0)
        {
            return pid == 0 ? "System Idle Process" : string.Empty;
        }

        // The name string lives inside the same buffer; refuse anything pointing outside it.
        var start = (byte*)name.Buffer;
        if (start < buffer || start + name.Length > buffer + length)
        {
            return string.Empty;
        }

        return new string((char*)start, 0, name.Length / sizeof(char));
    }

    public void Dispose()
    {
        if (_buffer is not null)
        {
            NativeMemory.Free(_buffer);
            _buffer = null;
        }

        _bufferSize = 0;
    }

    // Signature per phnt ntexapi.h. NtQuerySystemInformation isn't in the Win32 metadata with the
    // full struct we need, so this one is declared by hand.
    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(
        int systemInformationClass,
        void* systemInformation,
        uint systemInformationLength,
        out uint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_INFORMATION
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public ulong WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UNICODE_STRING ImageName;
        public int BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public nuint UniqueProcessKey;
        public nuint PeakVirtualSize;
        public nuint VirtualSize;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivatePageCount;
        public long ReadOperationCount;
        public long WriteOperationCount;
        public long OtherOperationCount;
        public long ReadTransferCount;
        public long WriteTransferCount;
        public long OtherTransferCount;
    }
}
