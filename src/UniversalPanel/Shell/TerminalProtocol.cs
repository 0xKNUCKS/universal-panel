using System.Buffers.Binary;
using System.IO;

namespace UniversalPanel.Shell;

/// <summary>Messages between the panel and its terminal host (see TerminalHostProcess).</summary>
internal enum TerminalMessage : byte
{
    // Panel -> host.
    Create = 1,  // payload: JSON TerminalStart
    Input = 2,   // payload: UTF-8 keystrokes
    Resize = 3,  // payload: columns (u16), rows (u16)
    Close = 4,   // no payload

    // Host -> panel: data only. The panel never acts on what the host sends, it displays it.
    Output = 10, // payload: UTF-8 terminal output
    Exited = 11, // payload: exit code (i32)
    Failed = 12, // payload: UTF-8 reason
    Started = 13, // payload: the shell's PID (i32); only used to select it in Processes when asked
}

/// <summary>What the panel asks the host to start: an absolute program path and its arguments.</summary>
internal sealed record TerminalStart(string Program, List<string> Arguments, string Directory, int Columns, int Rows);

/// <summary>
/// Framing: type (1 byte), session id (i32), payload length (i32), payload. Payloads are capped so
/// neither side can be made to allocate without limit by a broken or hostile peer.
/// </summary>
internal static class TerminalProtocol
{
    public const int MaxPayload = 1 << 20;
    private const int HeaderSize = 9;

    public static async Task WriteAsync(Stream stream, SemaphoreSlim gate, TerminalMessage type, int session, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length > MaxPayload)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        var header = new byte[HeaderSize];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), session);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5), payload.Length);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            if (payload.Length > 0)
            {
                await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            }

            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The next message, or null when the other side closed the pipe.</summary>
    /// <exception cref="InvalidDataException">A malformed frame (the connection should be dropped).</exception>
    public static async Task<(TerminalMessage Type, int Session, byte[] Payload)?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[HeaderSize];
        if (!await ReadExactlyOrEndAsync(stream, header, ct).ConfigureAwait(false))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(5));
        if (length is < 0 or > MaxPayload || !Enum.IsDefined((TerminalMessage)header[0]))
        {
            throw new InvalidDataException("Malformed terminal message.");
        }

        var payload = new byte[length];
        if (length > 0 && !await ReadExactlyOrEndAsync(stream, payload, ct).ConfigureAwait(false))
        {
            return null;
        }

        return ((TerminalMessage)header[0], BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1)), payload);
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }
}
