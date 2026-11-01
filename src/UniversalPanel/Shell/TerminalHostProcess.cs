using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;

namespace UniversalPanel.Shell;

/// <summary>
/// "UniversalPanel.exe --terminal-host &lt;pipe&gt;": owns the Terminal tab's shells. The panel starts it
/// with the user's normal rights (through Explorer when the panel is elevated), so shells never
/// inherit the panel's elevation. It connects back over the panel's private pipe, starts shells in
/// pseudo consoles when asked, and sends their output; all shells are in one kill-on-close job,
/// so they end with the host, and the host ends when the panel closes the pipe.
/// "--terminal-host --stdio" is the admin host: an elevated panel starts it as its own child and
/// talks to it over the inherited stdin/stdout pipes, which no other process can open.
/// </summary>
internal static partial class TerminalHostProcess
{
    public const string Switch = "--terminal-host";
    public const string Stdio = "--stdio";

    [GeneratedRegex(@"^UniversalPanel\.Terminal\.[0-9a-f]{32}$")]
    private static partial Regex PipeName();

    public static int Run(IReadOnlyList<string> args)
    {
        if (args.Count != 1 || (args[0] != Stdio && !PipeName().IsMatch(args[0])))
        {
            return 2;
        }

        nint job = CreateKillOnCloseJob();
        var sessions = new ConcurrentDictionary<int, PseudoConsole>();
        try
        {
            var gate = new SemaphoreSlim(1, 1);
            if (args[0] == Stdio)
            {
                using var input = Console.OpenStandardInput();
                using var output = Console.OpenStandardOutput();
                ServeAsync(input, output, gate, sessions, job).GetAwaiter().GetResult();
                return 0;
            }

            using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(15_000);
            ServeAsync(pipe, pipe, gate, sessions, job).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Warn("Terminal", "Terminal host stopped.", ex);
            return 1;
        }
        finally
        {
            foreach (var session in sessions.Values)
            {
                session.Dispose();
            }

            if (job != 0)
            {
                CloseHandle(job); // kill-on-close: anything left in the job ends here
            }
        }
    }

    private static async Task ServeAsync(Stream input, Stream pipe, SemaphoreSlim gate, ConcurrentDictionary<int, PseudoConsole> sessions, nint job)
    {
        while (await TerminalProtocol.ReadAsync(input) is { } message)
        {
            var (type, id, payload) = message;
            switch (type)
            {
                case TerminalMessage.Create when !sessions.ContainsKey(id):
                    Create(pipe, gate, sessions, job, id, payload);
                    break;
                case TerminalMessage.Input when sessions.TryGetValue(id, out var target):
                    target.Write(payload);
                    break;
                case TerminalMessage.Resize when payload.Length == 4 && sessions.TryGetValue(id, out var target):
                    target.Resize(BinaryPrimitives.ReadUInt16LittleEndian(payload), BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2)));
                    break;
                case TerminalMessage.Close when sessions.TryRemove(id, out var target):
                    target.Dispose();
                    break;
            }
        }
    }

    private static void Create(Stream pipe, SemaphoreSlim gate, ConcurrentDictionary<int, PseudoConsole> sessions, nint job, int id, byte[] payload)
    {
        PseudoConsole console;
        try
        {
            var start = JsonSerializer.Deserialize<TerminalStart>(payload) ?? throw new InvalidDataException("Empty start request.");
            // Only a local program that exists; arguments stay separate (quoted per argument).
            if (!ToolEntry.IsSafePath(start.Program) || !File.Exists(start.Program) || start.Arguments.Count > 64)
            {
                throw new InvalidDataException("Not a program this host will start.");
            }

            string directory = Directory.Exists(start.Directory) && ToolEntry.IsSafePath(start.Directory)
                ? start.Directory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            console = PseudoConsole.Start(start.Program, start.Arguments, directory, start.Columns, start.Rows, job);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or System.ComponentModel.Win32Exception or IOException)
        {
            _ = TerminalProtocol.WriteAsync(pipe, gate, TerminalMessage.Failed, id, Encoding.UTF8.GetBytes(ex.Message));
            return;
        }

        sessions[id] = console;
        var pid = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(pid, console.ProcessId);
        _ = TerminalProtocol.WriteAsync(pipe, gate, TerminalMessage.Started, id, pid);
        _ = PumpAsync(pipe, gate, sessions, id, console);
    }

    /// <summary>Forwards a shell's output to the panel, then its exit code.</summary>
    private static async Task PumpAsync(Stream pipe, SemaphoreSlim gate, ConcurrentDictionary<int, PseudoConsole> sessions, int id, PseudoConsole console)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int n;
            while ((n = await console.Output.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await TerminalProtocol.WriteAsync(pipe, gate, TerminalMessage.Output, id, buffer.AsMemory(0, n)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pseudo console closed (shell exited or the panel closed the session).
        }

        try
        {
            int code = await console.Exited.ConfigureAwait(false);
            var exit = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(exit, code);
            await TerminalProtocol.WriteAsync(pipe, gate, TerminalMessage.Exited, id, exit).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The panel went away.
        }

        if (sessions.TryRemove(id, out var ended))
        {
            ended.Dispose();
        }
    }

    // ---- job ---------------------------------------------------------------------------------

    private static unsafe nint CreateKillOnCloseJob()
    {
        nint job = CreateJobObjectW(null, null);
        if (job == 0)
        {
            return 0;
        }

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        return job;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

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

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial nint CreateJobObjectW(void* attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(nint job, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
