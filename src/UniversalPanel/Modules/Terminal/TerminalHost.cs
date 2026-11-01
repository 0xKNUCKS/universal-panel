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
/// The panel's link to its terminal host (TerminalHostProcess). The panel creates a pipe with a
/// random name that only this user can open, starts the host with the user's normal rights
/// (through Explorer when the panel is elevated) and accepts the connection only if it comes from
/// this same program. The host sends output and exit codes, which the panel only displays.
/// </summary>
internal sealed partial class TerminalHost : IDisposable
{
    private static TerminalHost? _current;
    private static Task<TerminalHost>? _starting;

    private readonly NamedPipeServerStream _pipe;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<int, TerminalSession> _sessions = new();
    private int _nextId;
    private bool _closed;

    private TerminalHost(NamedPipeServerStream pipe) => _pipe = pipe;

    /// <summary>The running host, started (and connected) on first use.</summary>
    public static async Task<TerminalHost> GetAsync()
    {
        if (_current is { _closed: false } host)
        {
            return host;
        }

        // Tabs opened together share one start (otherwise each would start its own host).
        _starting ??= StartAsync();
        try
        {
            _current = await _starting.ConfigureAwait(true);
            return _current;
        }
        finally
        {
            _starting = null;
        }
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
            string self = Path.Combine(AppContext.BaseDirectory, "UniversalPanel.exe");
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

        var host = new TerminalHost(pipe);
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
            await TerminalProtocol.WriteAsync(_pipe, _gate, type, id, payload).ConfigureAwait(false);
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
            while (await TerminalProtocol.ReadAsync(_pipe).ConfigureAwait(false) is { } message)
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
        _pipe.Dispose();
    }

    public void Dispose() => Stopped(null);

    /// <summary>Ends the host (and every shell in its job) when the panel exits.</summary>
    public static void Shutdown() => _current?.Dispose();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
}
