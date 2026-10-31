// Runs the bundled Detect It Easy CLI (diec -j) on a file, sandboxed, because the panel may be
// elevated and diec parses untrusted files:
// - every file in die\bin is checked against the SHA-256 manifest embedded in this assembly and
//   held open (no write / delete sharing) while diec runs; unknown files (a planted DLL, qt.conf)
//   and reparse points are refused;
// - diec runs on a restricted copy of our token: low integrity, Administrators deny-only, all
//   privileges removed (the low-integrity launch from Microsoft's "Designing Applications to Run at
//   a Low Integrity Level": DuplicateTokenEx/CreateRestrictedToken + SetTokenInformation
//   (TokenIntegrityLevel) + CreateProcessAsUser), so even a swapped binary gets less than the user;
// - in a job: one process, no children, 1 GB, no clipboard/desktop/global atoms, killed with us;
// - only its stdout pipe is inherited (PROC_THREAD_ATTRIBUTE_HANDLE_LIST), with a minimal
//   environment (no QT_* variables), an explicit -D database path, and "--" before the target.

using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniversalPanel.Diagnostics;

namespace UniversalPanel.Native;

/// <summary>One DIE finding, e.g. Type "Compiler", Text "Compiler: Microsoft Visual C/C++(19.38)[LTCG/C]".</summary>
internal sealed record DieFinding(string Type, string Name, string Version, string Text);

internal sealed class DieUnavailableException(string message) : Exception(message);

internal static partial class DieScanner
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "die", "bin");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const int MaxOutput = 8 * 1024 * 1024;
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private static readonly Lazy<Dictionary<string, string>> Manifest = new(LoadManifest);

    public static bool IsInstalled => File.Exists(Path.Combine(Root, "diec.exe"));

    /// <summary>Scans a file. Throws <see cref="DieUnavailableException"/> with a user-facing reason.</summary>
    public static async Task<List<DieFinding>> ScanAsync(string target, CancellationToken ct)
    {
        if (!IsInstalled)
        {
            throw new DieUnavailableException("Not installed (it is downloaded when the panel is built)");
        }

        if (!Path.IsPathFullyQualified(target) || target.Contains('"') || !File.Exists(target))
        {
            throw new DieUnavailableException("Not a readable file");
        }

        await OneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string json = await Task.Run(() => Run(target, ct), ct).ConfigureAwait(false);
            return Parse(json);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static string Run(string target, CancellationToken ct)
    {
        var locks = VerifyAndLock();
        try
        {
            string diec = Path.Combine(Root, "diec.exe");
            string database = Path.Combine(Root, "db");
            return Launch(diec, $"\"{diec}\" -j -D \"{database}\" -- \"{target}\"", ct);
        }
        finally
        {
            foreach (var stream in locks)
            {
                stream.Dispose();
            }
        }
    }

    // ---- integrity ---------------------------------------------------------------------------

    private static Dictionary<string, string> LoadManifest()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("die.manifest.sha256")
            ?? throw new DieUnavailableException("Manifest missing from the build");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 65)
            {
                map[line[65..].Replace('/', '\\')] = line[..64];
            }
        }

        return map;
    }

    /// <summary>Checks die\bin against the manifest; returns the open, write-locked files.</summary>
    private static List<FileStream> VerifyAndLock()
    {
        var manifest = Manifest.Value;
        var root = new DirectoryInfo(Root);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0
            || root.EnumerateDirectories("*", options).Any(d => (d.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            throw Refuse("a folder is a link");
        }

        int found = 0;
        foreach (var file in root.EnumerateFiles("*", options))
        {
            string relative = Path.GetRelativePath(Root, file.FullName);
            if (!manifest.ContainsKey(relative) || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Refuse($"unexpected file {relative}");
            }

            found++;
        }

        if (found != manifest.Count)
        {
            throw Refuse("files are missing");
        }

        var locks = new List<FileStream>(manifest.Count);
        try
        {
            foreach (var (relative, expected) in manifest)
            {
                // FileShare.Read: nobody can modify, rename or delete it until diec is done.
                var stream = new FileStream(Path.Combine(Root, relative), FileMode.Open, FileAccess.Read, FileShare.Read);
                locks.Add(stream);
                if (!Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(expected, StringComparison.Ordinal))
                {
                    throw Refuse($"{relative} was modified");
                }
            }

            return locks;
        }
        catch
        {
            foreach (var stream in locks)
            {
                stream.Dispose();
            }

            throw;
        }
    }

    private static DieUnavailableException Refuse(string why)
    {
        Log.Warn("Die", $"Refusing to run diec: {why}");
        return new DieUnavailableException($"Refused: {why} (files don't match the pinned build)");
    }

    // ---- sandboxed launch --------------------------------------------------------------------

    private static unsafe string Launch(string application, string commandLine, CancellationToken ct)
    {
        nint token = 0, job = 0, attributes = 0;
        PROCESS_INFORMATION info = default;
        using var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        try
        {
            token = CreateSandboxToken();
            job = CreateSandboxJob();

            // Inherit exactly one handle: the write end of the output pipe.
            nint pipe = output.ClientSafePipeHandle.DangerousGetHandle();
            nuint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, &size);
            attributes = Marshal.AllocHGlobal((nint)size);
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, &size));
            Check(UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, &pipe, (nuint)sizeof(nint), null, null));

            var startup = new STARTUPINFOEXW { lpAttributeList = attributes };
            startup.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
            startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            startup.StartupInfo.hStdOutput = pipe;
            startup.StartupInfo.hStdError = pipe;

            // Only what Windows itself needs; nothing from our environment (QT_PLUGIN_PATH etc.).
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string environment = $"SystemDrive={Path.GetPathRoot(windows)?.TrimEnd('\\')}\0SystemRoot={windows}\0windir={windows}\0\0";
            var command = (commandLine + "\0").ToCharArray();
            fixed (char* app = application)
            fixed (char* cmd = command)
            fixed (char* env = environment)
            fixed (char* dir = Root)
            {
                Check(CreateProcessAsUserW(token, app, cmd, null, null, true,
                    CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW,
                    env, dir, &startup, &info));
            }

            output.DisposeLocalCopyOfClientHandle();
            if (!AssignProcessToJobObject(job, info.hProcess))
            {
                int error = Marshal.GetLastPInvokeError();
                TerminateProcess(info.hProcess, 1);
                throw new Win32Exception(error);
            }

            ResumeThread(info.hThread);
            var read = ReadAllAsync(output);
            using (ct.Register(() => TerminateJobObject(job, 1)))
            {
                if (!read.Wait(Timeout))
                {
                    TerminateJobObject(job, 1);
                    throw new DieUnavailableException("Timed out");
                }
            }

            ct.ThrowIfCancellationRequested();
            return read.Result;
        }
        catch (Win32Exception ex)
        {
            Log.Warn("Die", "Couldn't start diec in its sandbox", ex);
            throw new DieUnavailableException($"Couldn't start: {ex.Message}");
        }
        finally
        {
            if (attributes != 0)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            if (info.hThread != 0)
            {
                CloseHandle(info.hThread);
            }

            if (info.hProcess != 0)
            {
                CloseHandle(info.hProcess);
            }

            if (job != 0)
            {
                CloseHandle(job); // KILL_ON_JOB_CLOSE: nothing outlives the scan
            }

            if (token != 0)
            {
                CloseHandle(token);
            }
        }
    }

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int n;
        while ((n = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + n > MaxOutput)
            {
                throw new DieUnavailableException("Output too large");
            }

            buffer.Write(chunk, 0, n);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Our token minus every privilege, Administrators deny-only, at low integrity.</summary>
    private static unsafe nint CreateSandboxToken()
    {
        nint own = 0, restricted = 0, admins = 0, low = 0;
        try
        {
            Check(OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT, out own));
            Check(ConvertStringSidToSidW("S-1-5-32-544", out admins));
            var disable = new SID_AND_ATTRIBUTES { Sid = admins };
            Check(CreateRestrictedToken(own, DISABLE_MAX_PRIVILEGE, 1, &disable, 0, null, 0, null, out restricted));

            Check(ConvertStringSidToSidW("S-1-16-4096", out low)); // Low mandatory level
            var label = new SID_AND_ATTRIBUTES { Sid = low, Attributes = SE_GROUP_INTEGRITY };
            Check(SetTokenInformation(restricted, TokenIntegrityLevel, &label, (uint)(sizeof(SID_AND_ATTRIBUTES) + GetLengthSid(low))));

            nint result = restricted;
            restricted = 0;
            return result;
        }
        finally
        {
            if (own != 0)
            {
                CloseHandle(own);
            }

            if (restricted != 0)
            {
                CloseHandle(restricted);
            }

            LocalFree(admins);
            LocalFree(low);
        }
    }

    private static unsafe nint CreateSandboxJob()
    {
        nint job = CreateJobObjectW(null, null);
        Check(job != 0);
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_ACTIVE_PROCESS
                    | JOB_OBJECT_LIMIT_PROCESS_MEMORY | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION,
                ActiveProcessLimit = 1,
            },
            ProcessMemoryLimit = 1024 * 1024 * 1024,
        };
        var ui = new JOBOBJECT_BASIC_UI_RESTRICTIONS { UIRestrictionsClass = JOB_OBJECT_UILIMIT_ALL };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))
            || !SetInformationJobObject(job, JobObjectBasicUIRestrictions, &ui, (uint)sizeof(JOBOBJECT_BASIC_UI_RESTRICTIONS)))
        {
            int error = Marshal.GetLastPInvokeError();
            CloseHandle(job);
            throw new Win32Exception(error);
        }

        return job;
    }

    private static void Check(bool ok)
    {
        if (!ok)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    // ---- output ------------------------------------------------------------------------------

    private static List<DieFinding> Parse(string json)
    {
        var findings = new List<DieFinding>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (doc.RootElement.TryGetProperty("detects", out var detects) && detects.ValueKind == JsonValueKind.Array)
            {
                foreach (var detect in detects.EnumerateArray())
                {
                    if (detect.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var value in values.EnumerateArray())
                        {
                            findings.Add(new DieFinding(Text(value, "type"), Text(value, "name"), Text(value, "version"), Text(value, "string")));
                        }
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            Log.Warn("Die", "diec returned output that isn't JSON", ex);
            throw new DieUnavailableException("Unreadable result");
        }

        return findings;

        static string Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
    }

    // ---- interop (documented signatures, declared by hand) -----------------------------------

    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001, TOKEN_DUPLICATE = 0x0002, TOKEN_QUERY = 0x0008, TOKEN_ADJUST_DEFAULT = 0x0080;
    private const uint DISABLE_MAX_PRIVILEGE = 0x1;
    private const uint SE_GROUP_INTEGRITY = 0x20;
    private const int TokenIntegrityLevel = 25;
    private const uint CREATE_SUSPENDED = 0x4, CREATE_UNICODE_ENVIRONMENT = 0x400, EXTENDED_STARTUPINFO_PRESENT = 0x80000, CREATE_NO_WINDOW = 0x08000000;
    private const uint STARTF_USESTDHANDLES = 0x100;
    private const nuint PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x20002;
    private const int JobObjectBasicUIRestrictions = 4, JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x8, JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x100,
        JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x400, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const uint JOB_OBJECT_UILIMIT_ALL = 0xFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct STARTUPINFOW
    {
        public uint cb;
        public char* lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public byte* lpReserved2;
        public nint hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_UI_RESTRICTIONS
    {
        public uint UIRestrictionsClass;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSidToSidW(string sid, out nint result);

    [LibraryImport("advapi32.dll")]
    private static partial uint GetLengthSid(nint sid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateRestrictedToken(nint existing, uint flags, uint disableSidCount, SID_AND_ATTRIBUTES* sidsToDisable,
        uint deletePrivilegeCount, void* privilegesToDelete, uint restrictedSidCount, SID_AND_ATTRIBUTES* sidsToRestrict, out nint newToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetTokenInformation(nint token, int infoClass, void* info, uint length);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessAsUserW(nint token, char* application, char* commandLine, void* processAttributes, void* threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, void* environment, char* directory, STARTUPINFOEXW* startup, PROCESS_INFORMATION* info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool InitializeProcThreadAttributeList(nint list, uint count, uint flags, nuint* size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, void* value, nuint size, void* previous, nuint* returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial nint CreateJobObjectW(void* attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(nint job, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
