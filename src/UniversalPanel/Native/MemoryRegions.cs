// Region walk and labels follow System Informer (MIT), SystemInformer/memprv.c / memlist.c:
// walk the address space region by region, show committed and reserved regions, label type
// (Image / Mapped / Private) and protection, and the backing file for Image and Mapped regions.
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d
// Uses the documented VirtualQueryEx + GetMappedFileNameW instead of NtQueryVirtualMemory.
// "Suspicious" = executable memory with no image file behind it (private or mapped RX/RWX), the
// usual sign of shellcode, manual mapping or an unpacked stub; RWX image pages are flagged too.

using System.IO;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Threading;

namespace UniversalPanel.Native;

internal sealed record MemoryRegion(
    ulong Base,
    ulong Size,
    string State,
    string Type,
    string Protection,
    string? File,
    bool Executable,
    bool Suspicious);

internal static unsafe class MemoryRegions
{
    private const int ERROR_INVALID_PARAMETER = 87;
    private const int MaxRegions = 200_000; // safety stop for pathological address spaces

    public static List<MemoryRegion> Capture(ProcessKey key, string processName)
    {
        var process = Open(key, processName, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION);
        try
        {
            var regions = new List<MemoryRegion>(1024);
            var paths = new PathCache();
            ulong address = 0;
            MEMORY_BASIC_INFORMATION info;
            while (regions.Count < MaxRegions
                && PInvoke.VirtualQueryEx(process, (void*)address, &info, (nuint)sizeof(MEMORY_BASIC_INFORMATION)) != 0)
            {
                ulong size = info.RegionSize;
                if (info.State != VIRTUAL_ALLOCATION_TYPE.MEM_FREE)
                {
                    regions.Add(Describe(process, info, paths));
                }

                ulong next = (ulong)info.BaseAddress + size;
                if (size == 0 || next <= address)
                {
                    break;
                }

                address = next;
            }

            return regions;
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    /// <summary>Copies a region's readable bytes to a file; unreadable pages are written as zeros.</summary>
    public static void DumpRegion(ProcessKey key, string processName, ulong baseAddress, ulong size, string path)
    {
        var process = Open(key, processName, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ);
        bool written = false;
        try
        {
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                ReadRegion(process, baseAddress, size, chunk => file.Write(chunk));
            }

            written = true;
        }
        finally
        {
            PInvoke.CloseHandle(process);
            if (!written && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Reads a region in page-aligned chunks, handing each to <paramref name="sink"/>.</summary>
    public static void Read(ProcessKey key, string processName, ulong baseAddress, ulong size, Action<ReadOnlySpan<byte>, ulong> sink)
    {
        var process = Open(key, processName, PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ);
        try
        {
            ulong offset = 0;
            ReadRegion(process, baseAddress, size, chunk =>
            {
                sink(chunk, baseAddress + offset);
                offset += (ulong)chunk.Length;
            });
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    private static void ReadRegion(HANDLE process, ulong baseAddress, ulong size, Action<ReadOnlySpan<byte>> sink)
    {
        const int ChunkSize = 1 << 20;
        var buffer = new byte[ChunkSize];
        for (ulong offset = 0; offset < size; offset += ChunkSize)
        {
            int length = (int)Math.Min(ChunkSize, size - offset);
            fixed (byte* p = buffer)
            {
                nuint read = 0;
                if (!PInvoke.ReadProcessMemory(process, (void*)(baseAddress + offset), p, (nuint)length, &read) || (int)read < length)
                {
                    // Partially readable (guard / no-access pages): fill the rest page by page.
                    Array.Clear(buffer, (int)read, length - (int)read);
                    for (int page = (int)read & ~0xFFF; page < length; page += 0x1000)
                    {
                        int pageLength = Math.Min(0x1000, length - page);
                        nuint pageRead = 0;
                        PInvoke.ReadProcessMemory(process, (void*)(baseAddress + offset + (ulong)page), p + page, (nuint)pageLength, &pageRead);
                    }
                }
            }

            sink(buffer.AsSpan(0, length));
        }
    }

    private static HANDLE Open(ProcessKey key, string processName, PROCESS_ACCESS_RIGHTS access)
    {
        var process = PInvoke.OpenProcess(access, false, (uint)key.Pid);
        if (process.IsNull)
        {
            throw Marshal.GetLastPInvokeError() == ERROR_INVALID_PARAMETER
                ? new ProcessActionException(ProcessActionFailure.Exited, $"{processName} has already exited.")
                : new ProcessActionException(ProcessActionFailure.AccessDenied,
                    $"Access denied to the memory of {processName}. It may be protected, or the panel needs to run as administrator.");
        }

        if (!ProcessControl.IsSameInstance(key))
        {
            PInvoke.CloseHandle(process);
            throw new ProcessActionException(ProcessActionFailure.Exited, $"{processName} has already exited.");
        }

        return process;
    }

    private static MemoryRegion Describe(HANDLE process, MEMORY_BASIC_INFORMATION info, PathCache paths)
    {
        bool committed = info.State == VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT;
        var protect = committed ? info.Protect : info.AllocationProtect;
        string type = info.Type switch
        {
            PAGE_TYPE.MEM_IMAGE => "Image",
            PAGE_TYPE.MEM_MAPPED => "Mapped",
            PAGE_TYPE.MEM_PRIVATE => "Private",
            _ => "",
        };

        string? file = info.Type is PAGE_TYPE.MEM_IMAGE or PAGE_TYPE.MEM_MAPPED ? paths.Get(process, (ulong)info.AllocationBase) : null;
        bool executable = committed && IsExecutable(protect);
        // Executable without an image behind it, or writable-and-executable anywhere.
        bool rwx = (protect & (PAGE_PROTECTION_FLAGS)0xFF) == PAGE_PROTECTION_FLAGS.PAGE_EXECUTE_READWRITE;
        bool suspicious = executable && (info.Type != PAGE_TYPE.MEM_IMAGE || rwx);

        return new MemoryRegion(
            (ulong)info.BaseAddress,
            info.RegionSize,
            committed ? "Commit" : "Reserve",
            type,
            committed ? ProtectionText(protect) : $"Reserved ({ProtectionText(protect)})",
            file,
            executable,
            suspicious);
    }

    private static bool IsExecutable(PAGE_PROTECTION_FLAGS protect) =>
        (protect & (PAGE_PROTECTION_FLAGS.PAGE_EXECUTE | PAGE_PROTECTION_FLAGS.PAGE_EXECUTE_READ
            | PAGE_PROTECTION_FLAGS.PAGE_EXECUTE_READWRITE | PAGE_PROTECTION_FLAGS.PAGE_EXECUTE_WRITECOPY)) != 0;

    /// <summary>System Informer style: "RX", "RW", "RWX", "WCX", plus +G (guard) / +NC (no cache).</summary>
    private static string ProtectionText(PAGE_PROTECTION_FLAGS protect)
    {
        string text = (protect & (PAGE_PROTECTION_FLAGS)0xFF) switch
        {
            PAGE_PROTECTION_FLAGS.PAGE_NOACCESS => "NA",
            PAGE_PROTECTION_FLAGS.PAGE_READONLY => "R",
            PAGE_PROTECTION_FLAGS.PAGE_READWRITE => "RW",
            PAGE_PROTECTION_FLAGS.PAGE_WRITECOPY => "WC",
            PAGE_PROTECTION_FLAGS.PAGE_EXECUTE => "X",
            PAGE_PROTECTION_FLAGS.PAGE_EXECUTE_READ => "RX",
            PAGE_PROTECTION_FLAGS.PAGE_EXECUTE_READWRITE => "RWX",
            PAGE_PROTECTION_FLAGS.PAGE_EXECUTE_WRITECOPY => "WCX",
            0 => "",
            var other => $"0x{(uint)other:X}",
        };

        if (protect.HasFlag(PAGE_PROTECTION_FLAGS.PAGE_GUARD)) text += "+G";
        if (protect.HasFlag(PAGE_PROTECTION_FLAGS.PAGE_NOCACHE)) text += "+NC";
        return text;
    }

    /// <summary>One GetMappedFileName per allocation, translated to a C:\ path.</summary>
    private sealed class PathCache
    {
        private readonly Dictionary<ulong, string?> _byAllocation = [];
        private List<(string Device, string Drive)>? _devices;

        public string? Get(HANDLE process, ulong allocationBase)
        {
            if (_byAllocation.TryGetValue(allocationBase, out var cached))
            {
                return cached;
            }

            char* buffer = stackalloc char[1024];
            uint length = PInvoke.GetMappedFileName(process, (void*)allocationBase, buffer, 1024);
            string? path = length == 0 ? null : ToDosPath(new string(buffer, 0, (int)length));
            _byAllocation[allocationBase] = path;
            return path;
        }

        private string ToDosPath(string ntPath)
        {
            if (_devices is null)
            {
                _devices = [];
                char* target = stackalloc char[1024];
                for (char letter = 'A'; letter <= 'Z'; letter++)
                {
                    string drive = $"{letter}:";
                    fixed (char* name = drive)
                    {
                        if (PInvoke.QueryDosDevice(name, target, 1024) > 0)
                        {
                            _devices.Add((new string(target), drive));
                        }
                    }
                }
            }

            foreach (var (device, drive) in _devices)
            {
                if (ntPath.StartsWith(device, StringComparison.OrdinalIgnoreCase)
                    && (ntPath.Length == device.Length || ntPath[device.Length] == '\\'))
                {
                    return drive + ntPath[device.Length..];
                }
            }

            return ntPath;
        }
    }
}
