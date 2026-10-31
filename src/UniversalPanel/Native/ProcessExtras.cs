// Mirrors System Informer (MIT):
//   phlib/native.c            PhGetProcessEnvironment: ProcessBasicInformation -> PEB ->
//                             ProcessParameters -> Environment / EnvironmentSize, read remotely
//   phnt/include/ntpebteb.h   PEB.ProcessParameters at 0x20 (x64)
//   phnt/include/ntrtl.h      RTL_USER_PROCESS_PARAMETERS.Environment 0x80, EnvironmentSize 0x3F0
//   SystemInformer/procprp.c  protection (PS_PROTECTION) and mitigation policy display
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d

using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace UniversalPanel.Native;

internal sealed record ProcessExtrasInfo(
    string? Protection,
    IReadOnlyList<string> Mitigations,
    IReadOnlyList<string> Privileges,
    IReadOnlyList<KeyValuePair<string, string>>? Environment);

internal static unsafe partial class ProcessExtras
{
    private const int ProcessBasicInformation = 0;
    private const int ProcessProtectionInformation = 61;
    private const int PebProcessParametersOffset = 0x20;
    private const int ParamsEnvironmentOffset = 0x80;
    private const int ParamsEnvironmentSizeOffset = 0x3F0;
    private const int MaxEnvironmentBytes = 1 << 20;

    private static readonly string[] Signers =
        ["", "Authenticode", "CodeGen", "Antimalware", "Lsa", "Windows", "WinTcb", "WinSystem", "App"];

    public static ProcessExtrasInfo Query(ProcessKey key)
    {
        string? protection = null;
        var limited = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)key.Pid);
        if (!limited.IsNull)
        {
            try
            {
                if (ProcessControl.IsSameInstance(key))
                {
                    protection = QueryProtection(limited);
                }
            }
            finally
            {
                PInvoke.CloseHandle(limited);
            }
        }

        // Mitigations and the environment need full query (+ VM read); protected processes refuse.
        var full = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ, false, (uint)key.Pid);
        if (full.IsNull || !ProcessControl.IsSameInstance(key))
        {
            if (!full.IsNull) PInvoke.CloseHandle(full);
            return new(protection, [], QueryPrivileges(key), null);
        }

        try
        {
            return new(protection, QueryMitigations(full), QueryPrivileges(key), QueryEnvironment(full));
        }
        finally
        {
            PInvoke.CloseHandle(full);
        }
    }

    private static string QueryProtection(HANDLE process)
    {
        byte level = 0;
        if (NtQueryInformationProcess(Raw(process), ProcessProtectionInformation, &level, 1, null) < 0 || level == 0)
        {
            return "None";
        }

        int type = level & 0x7, signer = level >> 4;
        string kind = type switch { 1 => "Light (PPL)", 2 => "Full (PP)", _ => $"Type {type}" };
        return $"{kind}, {(signer < Signers.Length ? Signers[signer] : $"signer {signer}")}";
    }

    private static List<string> QueryMitigations(HANDLE process)
    {
        var list = new List<string>();
        uint flags = 0;
        bool Policy(int kind)
        {
            uint value = 0;
            bool ok = GetProcessMitigationPolicy(Raw(process), kind, &value, sizeof(uint));
            flags = value;
            return ok;
        }

        // ProcessDEPPolicy only answers for 32-bit processes; 64-bit ones always run with DEP.
        int wow64 = 0;
        if (IsWow64Process(Raw(process), &wow64) && wow64 == 0)
        {
            list.Add("DEP (always on)");
        }
        else if (Policy(0))
        {
            list.Add((flags & 1) != 0 ? "DEP" : "DEP off");
        }

        if (Policy(1) && (flags & 0b101) != 0)                                                  // ProcessASLRPolicy
        {
            list.Add((flags & 0b100) != 0 ? "ASLR (high entropy)" : "ASLR");
        }

        if (Policy(7) && (flags & 1) != 0) list.Add("CFG");                                     // ProcessControlFlowGuardPolicy
        if (Policy(15) && (flags & 1) != 0) list.Add("CET shadow stack");                       // ProcessUserShadowStackPolicy
        if (Policy(8) && (flags & 1) != 0) list.Add("Microsoft-signed DLLs only");             // ProcessSignaturePolicy
        if (Policy(2) && (flags & 1) != 0) list.Add("No dynamic code");                         // ProcessDynamicCodePolicy
        return list;
    }

    private static List<string> QueryPrivileges(ProcessKey key)
    {
        var list = new List<string>();
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)key.Pid);
        if (process.IsNull)
        {
            return list;
        }

        nint token = 0;
        try
        {
            if (!OpenProcessToken(Raw(process), 0x0008 /* TOKEN_QUERY */, &token))
            {
                return list;
            }

            uint needed = 0;
            GetTokenInformation(token, 3 /* TokenPrivileges */, null, 0, &needed);
            if (needed == 0 || needed > 64 * 1024)
            {
                return list;
            }

            byte* buffer = stackalloc byte[(int)needed];
            if (!GetTokenInformation(token, 3, buffer, needed, &needed))
            {
                return list;
            }

            uint count = *(uint*)buffer;
            var entries = (LUID_AND_ATTRIBUTES*)(buffer + sizeof(uint));
            char* name = stackalloc char[128];
            for (uint i = 0; i < count; i++)
            {
                if ((entries[i].Attributes & 0x2) == 0) // SE_PRIVILEGE_ENABLED
                {
                    continue;
                }

                uint length = 128;
                var luid = entries[i].Luid;
                if (LookupPrivilegeNameW(null, &luid, name, &length))
                {
                    list.Add(new string(name, 0, (int)length));
                }
            }

            return list;
        }
        finally
        {
            if (token != 0) CloseHandle(token);
            PInvoke.CloseHandle(process);
        }
    }

    private static List<KeyValuePair<string, string>>? QueryEnvironment(HANDLE process)
    {
        PROCESS_BASIC_INFORMATION basic;
        if (NtQueryInformationProcess(Raw(process), ProcessBasicInformation, &basic, (uint)sizeof(PROCESS_BASIC_INFORMATION), null) < 0
            || basic.PebBaseAddress == 0)
        {
            return null;
        }

        nint parameters = 0, environment = 0;
        nuint size = 0;
        if (!Read(process, basic.PebBaseAddress + PebProcessParametersOffset, &parameters, sizeof(nint)) || parameters == 0
            || !Read(process, parameters + ParamsEnvironmentOffset, &environment, sizeof(nint)) || environment == 0
            || !Read(process, parameters + ParamsEnvironmentSizeOffset, &size, sizeof(nuint)) || size == 0)
        {
            return null;
        }

        int length = (int)Math.Min((ulong)size, MaxEnvironmentBytes) & ~1;
        var bytes = new byte[length];
        fixed (byte* p = bytes)
        {
            if (!Read(process, environment, p, length))
            {
                return null;
            }
        }

        // "NAME=value\0NAME=value\0\0", UTF-16. Entries starting with '=' are per-drive cwd markers.
        var list = new List<KeyValuePair<string, string>>();
        foreach (var entry in Encoding.Unicode.GetString(bytes).Split('\0'))
        {
            if (entry.Length < 2 || entry[0] == '=')
            {
                continue;
            }

            int eq = entry.IndexOf('=', 1);
            if (eq <= 0)
            {
                continue;
            }

            list.Add(new(entry[..eq], entry[(eq + 1)..]));
        }

        list.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static bool Read(HANDLE process, nint address, void* buffer, int length)
    {
        nuint read = 0;
        return PInvoke.ReadProcessMemory(process, (void*)address, buffer, (nuint)length, &read) && (int)read == length;
    }

    private static nint Raw(HANDLE handle) => (nint)handle.Value;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public int ExitStatus;
        public nint PebBaseAddress;
        public nuint AffinityMask;
        public int BasePriority;
        public nuint UniqueProcessId;
        public nuint InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    // Documented signatures (ntdll per phnt; kernel32 / advapi32 per the SDK), declared by hand.
    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int infoClass, void* info, uint length, uint* returnLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWow64Process(nint process, int* wow64);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMitigationPolicy(nint process, int policy, void* buffer, nuint length);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, nint* token);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int infoClass, void* info, uint length, uint* returnLength);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeNameW(string? systemName, LUID* luid, char* name, uint* length);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
