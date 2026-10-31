// Mirrors System Informer (MIT), phlib/nativeprocess.c / native.c:
//   PhGetProcessCommandLine   NtQueryInformationProcess(ProcessCommandLineInformation) on Win8.1+,
//                             which needs only PROCESS_QUERY_LIMITED_INFORMATION
//   PhGetTokenUser / PhGetTokenIntegrityLevel / PhGetTokenElevation  via the process token
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d
// Each field is best effort: anything the caller isn't allowed to read stays null.

using System.Runtime.InteropServices;
using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace UniversalPanel.Native;

internal sealed record ProcessGeneralInfo(
    string? CommandLine,
    string? User,
    string? Integrity,
    bool? Elevated,
    string? Architecture);

internal static unsafe partial class ProcessGeneral
{
    private const int ProcessCommandLineInformation = 60;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const ushort IMAGE_FILE_MACHINE_UNKNOWN = 0;
    private const ushort IMAGE_FILE_MACHINE_I386 = 0x014C;
    private const ushort IMAGE_FILE_MACHINE_ARMNT = 0x01C4;

    public static ProcessGeneralInfo Query(ProcessKey key)
    {
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)key.Pid);
        if (process.IsNull)
        {
            return new(null, null, null, null, null);
        }

        try
        {
            if (!ProcessControl.IsSameInstance(key))
            {
                return new(null, null, null, null, null);
            }

            var (user, integrity, elevated) = QueryToken(process);
            return new(QueryCommandLine(process), user, integrity, elevated, QueryArchitecture(process));
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    /// <summary>Just the command line (cheap; for process history). Null if not readable.</summary>
    public static string? TryGetCommandLine(int pid)
    {
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process.IsNull)
        {
            return null;
        }

        try
        {
            return QueryCommandLine(process);
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    private static string? QueryCommandLine(HANDLE process)
    {
        uint size = 0x1000;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var buffer = (byte*)NativeMemory.AllocZeroed(size);
            try
            {
                int status = NtQueryInformationProcess((nint)process.Value, ProcessCommandLineInformation, buffer, size, out uint needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    size = Math.Max(needed, size * 2);
                    continue;
                }

                if (status < 0)
                {
                    return null;
                }

                // Result is a UNICODE_STRING whose buffer follows it in the same allocation.
                ushort length = *(ushort*)buffer;
                var text = *(char**)(buffer + sizeof(nint));
                return text >= buffer && (byte*)text + length <= buffer + size
                    ? new string(text, 0, length / sizeof(char))
                    : null;
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        return null;
    }

    private static (string? User, string? Integrity, bool? Elevated) QueryToken(HANDLE process)
    {
        using var owner = Borrow(process);
        if (!PInvoke.OpenProcessToken(owner, TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token))
        {
            return (null, null, null);
        }

        try
        {
            var raw = new HANDLE((void*)token.DangerousGetHandle());
            uint returned;
            string? user = null;
            byte* userBuffer = stackalloc byte[256];
            if (PInvoke.GetTokenInformation(raw, TOKEN_INFORMATION_CLASS.TokenUser, userBuffer, 256, &returned))
            {
                var sid = new SecurityIdentifier((nint)((TOKEN_USER*)userBuffer)->User.Sid.Value);
                try
                {
                    user = sid.Translate(typeof(NTAccount)).Value;
                }
                catch (IdentityNotMappedException)
                {
                    user = sid.Value;
                }
            }

            string? integrity = null;
            byte* labelBuffer = stackalloc byte[128];
            if (PInvoke.GetTokenInformation(raw, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, labelBuffer, 128, &returned))
            {
                var label = new SecurityIdentifier((nint)((TOKEN_MANDATORY_LABEL*)labelBuffer)->Label.Sid.Value);
                // The last sub-authority is the integrity RID (S-1-16-<rid>).
                var rid = uint.Parse(label.Value[(label.Value.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                integrity = rid switch
                {
                    >= 0x4000 => "System",
                    >= 0x3000 => "High",
                    >= 0x2100 => "Medium Plus",
                    >= 0x2000 => "Medium",
                    >= 0x1000 => "Low",
                    _ => "Untrusted",
                };
            }

            bool? elevated = null;
            TOKEN_ELEVATION elevation;
            if (PInvoke.GetTokenInformation(raw, TOKEN_INFORMATION_CLASS.TokenElevation, &elevation, (uint)sizeof(TOKEN_ELEVATION), &returned))
            {
                elevated = elevation.TokenIsElevated != 0;
            }

            return (user, integrity, elevated);
        }
        finally
        {
            token.Dispose();
        }
    }

    private static string? QueryArchitecture(HANDLE process)
    {
        using var owner = Borrow(process);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10586)
            || !PInvoke.IsWow64Process2(owner, out var processMachineEnum, out _))
        {
            return null;
        }

        ushort processMachine = (ushort)processMachineEnum;
        return processMachine switch
        {
            IMAGE_FILE_MACHINE_UNKNOWN => "64-bit",
            IMAGE_FILE_MACHINE_I386 => "32-bit (WOW64)",
            IMAGE_FILE_MACHINE_ARMNT => "32-bit ARM (WOW64)",
            _ => $"WOW64 (machine 0x{processMachine:X4})",
        };
    }

    // Non-owning wrapper for CsWin32's SafeHandle overloads; the caller still closes the handle.
    private static Microsoft.Win32.SafeHandles.SafeProcessHandle Borrow(HANDLE process) =>
        new((nint)process.Value, ownsHandle: false);

    // Signature per phnt ntpsapi.h.
    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint processHandle, int processInformationClass, void* processInformation, uint processInformationLength, out uint returnLength);
}
