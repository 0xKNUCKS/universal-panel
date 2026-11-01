// One shell in a Windows pseudo console (ConPTY). Modeled on Microsoft's GUIConsole sample (MIT):
// microsoft/terminal samples/ConPTY/GUIConsole/GUIConsole.ConPTY (PseudoConsole.cs,
// PseudoConsolePipe.cs, Processes/ProcessFactory.cs) @ 703ef7f969d90d3f7e132b9a5f51a8815e77d586:
// two anonymous pipes, CreatePseudoConsole, then CreateProcess with
// PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE. Differences: the process starts suspended and joins the
// caller's job (kill-on-close) before it runs; the command line is built with ToolCommand.Quote;
// our ends of the pipes ConPTY duplicated are closed right away.

using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UniversalPanel.Native;

internal sealed unsafe partial class PseudoConsole : IDisposable
{
    private readonly nint _console;
    private readonly nint _process;
    private readonly FileStream _input;
    private bool _disposed;
    private int _consoleClosed;

    private PseudoConsole(nint console, nint process, int processId, FileStream input, FileStream output)
    {
        _console = console;
        _process = process;
        ProcessId = processId;
        _input = input;
        Output = output;
        Exited = Task.Run(WaitForExit);
        // The output pipe stays open after the shell exits until the pseudo console is closed;
        // closing it flushes what's left and ends Output.
        Exited.ContinueWith(_ => CloseConsole(), TaskScheduler.Default);
    }

    /// <summary>What the shell writes (VT text, UTF-8). Ends when the pseudo console closes.</summary>
    /// <summary>The shell's PID.</summary>
    public int ProcessId { get; }

    public FileStream Output { get; }

    /// <summary>Completes with the exit code when the shell exits.</summary>
    public Task<int> Exited { get; }

    /// <param name="job">Job the shell is put in before it runs (kill-on-close in the host).</param>
    public static PseudoConsole Start(string program, IReadOnlyList<string> arguments, string directory, int columns, int rows, nint job)
    {
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        nint console = 0, attributes = 0;
        PROCESS_INFORMATION info = default;
        try
        {
            Check(CreatePipe(out inputRead, out inputWrite, 0, 0));
            Check(CreatePipe(out outputRead, out outputWrite, 0, 0));
            int hr = CreatePseudoConsole(new COORD(columns, rows), inputRead, outputWrite, 0, out console);
            if (hr != 0)
            {
                throw new Win32Exception(hr, "Couldn't create a pseudo console.");
            }

            // ConPTY holds its own copies now.
            inputRead.Dispose();
            outputWrite.Dispose();

            nuint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, &size);
            attributes = Marshal.AllocHGlobal((nint)size);
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, &size));
            Check(UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, (void*)console, (nuint)sizeof(nint), null, null));

            var startup = new STARTUPINFOEXW { lpAttributeList = attributes };
            startup.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
            // Explicitly no standard handles: otherwise a host that has some (e.g. started from a
            // console) passes them on, and the shell talks to those instead of the pseudo console.
            startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            var commandLine = (string.Join(' ', new[] { program }.Concat(arguments).Select(ToolCommand.Quote)) + "\0").ToCharArray();
            fixed (char* app = program)
            fixed (char* cmd = commandLine)
            fixed (char* dir = directory)
            {
                Check(CreateProcessW(app, cmd, null, null, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT, null, dir, &startup, &info));
            }

            if (job != 0 && !AssignProcessToJobObject(job, info.hProcess))
            {
                int error = Marshal.GetLastPInvokeError();
                TerminateProcess(info.hProcess, 1);
                throw new Win32Exception(error, "Couldn't put the shell in the host's job.");
            }

            if (ResumeThread(info.hThread) == uint.MaxValue)
            {
                int error = Marshal.GetLastPInvokeError();
                TerminateProcess(info.hProcess, 1);
                throw new Win32Exception(error, "Couldn't start the shell.");
            }

            CloseHandle(info.hThread);
            var session = new PseudoConsole(console, info.hProcess, (int)info.dwProcessId, new FileStream(inputWrite, FileAccess.Write, 1), new FileStream(outputRead, FileAccess.Read, 1));
            console = 0;
            info.hProcess = 0;
            return session;
        }
        finally
        {
            if (attributes != 0)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            if (info.hProcess != 0)
            {
                CloseHandle(info.hProcess);
            }

            if (console != 0)
            {
                ClosePseudoConsole(console);
                inputWrite?.Dispose();
                outputRead?.Dispose();
            }

            inputRead?.Dispose();
            outputWrite?.Dispose();
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (!_disposed)
        {
            _input.Write(data);
            _input.Flush();
        }
    }

    public void Resize(int columns, int rows)
    {
        if (!_disposed)
        {
            _ = ResizePseudoConsole(_console, new COORD(columns, rows)); // a failed resize just keeps the old size
        }
    }

    /// <summary>Ends the session: the shell is terminated if it's still running.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TerminateProcess(_process, 1);
        _input.Dispose();
        CloseConsole(); // ends Output
        Output.Dispose();
    }

    private void CloseConsole()
    {
        if (Interlocked.Exchange(ref _consoleClosed, 1) == 0)
        {
            ClosePseudoConsole(_console);
        }
    }

    private int WaitForExit()
    {
        _ = WaitForSingleObject(_process, uint.MaxValue); // INFINITE: returns when the process ends
        GetExitCodeProcess(_process, out uint code);
        CloseHandle(_process);
        return unchecked((int)code);
    }

    private static void Check(bool ok)
    {
        if (!ok)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x80000, CREATE_SUSPENDED = 0x4, CREATE_UNICODE_ENVIRONMENT = 0x400;
    private const uint STARTF_USESTDHANDLES = 0x100;
    private const nuint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x20016;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD(int x, int y)
    {
        public short X = (short)Math.Clamp(x, 1, short.MaxValue);
        public short Y = (short)Math.Clamp(y, 1, short.MaxValue);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, nint attributes, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);

    [LibraryImport("kernel32.dll")]
    private static partial int ResizePseudoConsole(nint console, COORD size);

    [LibraryImport("kernel32.dll")]
    private static partial void ClosePseudoConsole(nint console);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, uint count, uint flags, nuint* size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, void* value, nuint size, void* previous, nuint* returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(char* application, char* commandLine, void* processAttributes, void* threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, void* environment, char* directory, STARTUPINFOEXW* startup, PROCESS_INFORMATION* info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
