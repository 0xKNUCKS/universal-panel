// Like System Informer's memory string search (SystemInformer/memsrch.c, behavior): printable
// ASCII and UTF-16LE runs of a minimum length, with their addresses.
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d

using System.Text;

namespace UniversalPanel.Native;

internal sealed record MemoryString(ulong Address, bool Unicode, string Text);

internal static class MemoryStrings
{
    public const int MinLength = 5;
    private const int MaxLength = 1024; // longer runs are cut, not dropped

    /// <summary>Strings in a region, scanned chunk by chunk with state carried across chunks.</summary>
    public static List<MemoryString> Scan(ProcessKey key, string processName, ulong baseAddress, ulong size, int maxResults, CancellationToken ct)
    {
        var results = new List<MemoryString>();
        var ascii = new Run(unicode: false, results, maxResults);
        var wide = new Run(unicode: true, results, maxResults);

        try
        {
            MemoryRegions.Read(key, processName, baseAddress, size, (chunk, address) =>
            {
                ct.ThrowIfCancellationRequested();
                for (int i = 0; i < chunk.Length; i++)
                {
                    ascii.Feed(chunk[i], address + (ulong)i);
                    wide.Feed(chunk[i], address + (ulong)i);
                }

                if (results.Count >= maxResults)
                {
                    throw new StopScan();
                }
            });
        }
        catch (StopScan)
        {
        }

        ascii.Flush();
        wide.Flush();
        results.Sort((a, b) => a.Address.CompareTo(b.Address));
        return results.Count > maxResults ? results[..maxResults] : results;
    }

    private static bool Printable(byte b) => b is >= 0x20 and < 0x7F or (byte)'\t';

    private sealed class StopScan : Exception;

    /// <summary>One encoding's current run of printable characters.</summary>
    private sealed class Run(bool unicode, List<MemoryString> results, int maxResults)
    {
        private readonly StringBuilder _text = new();
        private ulong _start;
        private bool _highBytePending; // UTF-16: next byte must be 0x00
        private bool _oddPhase;        // UTF-16: which byte parity the current run uses

        public void Feed(byte b, ulong address)
        {
            if (!unicode)
            {
                if (Printable(b))
                {
                    Append((char)b, address);
                }
                else
                {
                    Flush();
                }

                return;
            }

            // UTF-16LE: printable low byte followed by a zero high byte.
            if (_highBytePending)
            {
                _highBytePending = false;
                if (b != 0)
                {
                    Flush();
                    TryStart(b, address); // this byte may begin a new run
                }

                return;
            }

            TryStart(b, address);
        }

        private void TryStart(byte b, ulong address)
        {
            bool phase = (address & 1) != 0;
            if (Printable(b) && (_text.Length == 0 || phase == _oddPhase))
            {
                if (_text.Length == 0)
                {
                    _oddPhase = phase;
                }

                Append((char)b, address);
                _highBytePending = true;
            }
            else
            {
                Flush();
            }
        }

        private void Append(char c, ulong address)
        {
            if (_text.Length == 0)
            {
                _start = address;
            }

            if (_text.Length < MaxLength)
            {
                _text.Append(c);
            }
        }

        public void Flush()
        {
            if (_text.Length >= MinLength && results.Count < maxResults)
            {
                results.Add(new MemoryString(_start, unicode, _text.ToString()));
            }

            _text.Clear();
            _highBytePending = false;
        }
    }
}
