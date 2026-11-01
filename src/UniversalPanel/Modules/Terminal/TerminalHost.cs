using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;
using UniversalPanel.Shell;

namespace UniversalPanel.Modules.Terminal;

/// <summary>
/// The panel's link to a terminal host (TerminalHostProcess). Normal shells: the panel creates a
/// pipe with a random name that only this user can open, starts the host with the user's normal
/// rights (through Explorer when the panel is elevated) and accepts the connection only if it
/// comes from this same program. Admin shells (elevated panel only): a second host, the panel's
/// own elevated child, over its stdin/stdout. Either host sends output and exit codes, which the
/// panel only displays.
/// </summary>
internal sealed partial class TerminalHost : IDisposable
{
    // [0] normal, [1] admin.
    private static readonly TerminalHost?[] Current = new TerminalHost?[2];
    private static readonly Task<TerminalHost>?[] Starting = new Task<TerminalHost>?[2];

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly IDisposable _owner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<int, TerminalSession> _sessions = new();
    private int _nextId;
    private bool _closed;

    private TerminalHost(Stream input, Stream output, IDisposable owner)
    {
        _input = input;
        _output = output;
        _owner = owner;
    }

    /// <summary>The running host, started (and connected) on first use.</summary>
    /// <param name="admin">The elevated host; only when the panel itself is elevated.</param>
    public static async Task<TerminalHost> GetAsync(bool admin = false)
    {
        if (admin && !Elevation.IsElevated)
        {
            // An unelevated panel driving an elevated shell would let any program running as the
            // user type into it through the panel (Windows Terminal refuses mixed elevation too).
            throw new UnauthorizedAccessException("Admin terminals need the panel to run as administrator.");
        }

        int slot = admin ? 1 : 0;
        if (Current[slot] is { _closed: false } host)
        {
            return host;
        }

        // Tabs opened together share one start (otherwise each would start its own host).
        Starting[slot] ??= admin ? Task.FromResult(StartAdmin()) : StartAsync();
        try
        {
            Current[slot] = await Starting[slot]!.ConfigureAwait(true);
            return Current[slot]!;
        }
        finally
        {
            Starting[slot] = null;
        }
    }

    private static string Self => Path.Combine(AppContext.BaseDirectory, "UniversalPanel.exe");

    /// <summary>The elevated panel's own child, so it's elevated; the pipes are inherited, not named.</summary>
    private static TerminalHost StartAdmin()
    {
        var start = new ProcessStartInfo(Self)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(TerminalHostProcess.Switch);
        start.ArgumentList.Add(TerminalHostProcess.Stdio);
        var process = Process.Start(start) ?? throw new IOException("The admin terminal host didn't start.");
        var host = new TerminalHost(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, process);
        _ = host.ReadAsync();
        return host;
    }

    private static async Task<TerminalHost> StartAsync()
    {
        string name = "UniversalPanel.Terminal." + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        try
        {
            // The panel's own exe, next to its files (the host is this same program in another mode).
            string self = Self;
            string arguments = $"{TerminalHostProcess.Switch} {name}";
            if (Elevation.IsElevated)
            {
                ToolLauncher.Open(self, arguments, Path.GetDirectoryName(self)); // Explorer starts it unelevated
            }
            else
            {
                var start = new ProcessStartInfo(self) { UseShellExecute = false };
                start.ArgumentList.Add(TerminalHostProcess.Switch);
                start.ArgumentList.Add(name);
                Process.Start(start)?.Dispose();
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(true);

            // Only this program may be the host (the name is random, but pipe names can be listed).
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)
                || !string.Equals(ProcessImage.TryGetPath((int)pid), self, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Something other than the terminal host connected.");
            }
        }
        catch
        {
            pipe.Dispose();
            throw;
        }

        var host = new TerminalHost(pipe, pipe, pipe);
        _ = host.ReadAsync();
        return host;
    }

    /// <summary>Starts a shell for <paramref name="session"/> with the given terminal size.</summary>
    public async Task StartAsync(TerminalSession session, TerminalStart start)
    {
        int id = Interlocked.Increment(ref _nextId);
        session.Attach(this, id);
        _sessions[id] = session;
        await SendAsync(TerminalMessage.Create, id, JsonSerializer.SerializeToUtf8Bytes(start)).ConfigureAwait(false);
    }

    public Task InputAsync(int id, string text) => SendAsync(TerminalMessage.Input, id, Encoding.UTF8.GetBytes(text));

    public Task ResizeAsync(int id, int columns, int rows)
    {
        var size = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(size, (ushort)Math.Clamp(columns, 1, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(size.AsSpan(2), (ushort)Math.Clamp(rows, 1, ushort.MaxValue));
        return SendAsync(TerminalMessage.Resize, id, size);
    }

    public Task CloseAsync(int id)
    {
        _sessions.TryRemove(id, out _);
        return SendAsync(TerminalMessage.Close, id, ReadOnlyMemory<byte>.Empty);
    }

    private async Task SendAsync(TerminalMessage type, int id, ReadOnlyMemory<byte> payload)
    {
        try
        {
            await TerminalProtocol.WriteAsync(_output, _gate, type, id, payload).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Stopped(ex);
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await TerminalProtocol.ReadAsync(_input).ConfigureAwait(false) is { } message)
            {
                if (!_sessions.TryGetValue(message.Session, out var session))
                {
                    continue; // closed, or an id the panel never gave out
                }

                switch (message.Type)
                {
                    case TerminalMessage.Output:
                        session.OnOutput(message.Payload);
                        break;
                    case TerminalMessage.Started when message.Payload.Length == 4:
                        session.ProcessId = BinaryPrimitives.ReadInt32LittleEndian(message.Payload);
                        break;
                    case TerminalMessage.Exited when message.Payload.Length == 4:
                        _sessions.TryRemove(message.Session, out _);
                        session.OnEnded($"exited with code {BinaryPrimitives.ReadInt32LittleEndian(message.Payload)}");
                        break;
                    case TerminalMessage.Failed:
                        _sessions.TryRemove(message.Session, out _);
                        session.OnEnded("couldn't start: " + Encoding.UTF8.GetString(message.Payload, 0, Math.Min(message.Payload.Length, 500)));
                        break;
                }
            }

            Stopped(null);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
            Stopped(ex);
        }
    }

    /// <summary>The host went away: every open session ends; the next one starts a new host.</summary>
    private void Stopped(Exception? ex)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        if (ex is not null)
        {
            Log.Warn("Terminal", "Lost the terminal host.", ex);
        }

        foreach (var session in _sessions.Values)
        {
            session.OnEnded("the terminal host stopped");
        }

        _sessions.Clear();
        // Closing our ends gives the host end-of-input; it exits, and its job ends the shells.
        _output.Dispose();
        _input.Dispose();
        _owner.Dispose();
    }

    public void Dispose() => Stopped(null);

    /// <summary>Ends the hosts (and every shell in their jobs) when the panel exits.</summary>
    public static void Shutdown()
    {
        foreach (var host in Current)
        {
            host?.Dispose();
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
}
