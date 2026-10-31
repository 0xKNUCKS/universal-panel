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
    /// <param name="contains">Optional: only strings containing this (case-insensitive) are kept,
    /// so the result cap counts matches, not every string.</param>
    public static List<MemoryString> Scan(ProcessKey key, string processName, ulong baseAddress, ulong size, int maxResults, CancellationToken ct, string? contains = null)
    {
        var results = new List<MemoryString>();
        var ascii = new Run(unicode: false, results, maxResults, contains);
        var wide = new Run(unicode: true, results, maxResults, contains);

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

    /// <summary>
    /// Strings across the whole process: every committed, readable region (no guard / no-access /
    /// execute-only pages), private and image memory; mapped files only when asked, since they're
    /// mostly fonts and locale data. Regions that can't be read are skipped.
    /// </summary>
    public static List<MemoryString> ScanProcess(
        ProcessKey key, string processName, bool includeMapped, int maxResults, IProgress<(int Done, int Total)>? progress, CancellationToken ct, string? contains = null)
    {
        var regions = MemoryRegions.Capture(key, processName)
            .Where(r => r.State == "Commit" && Readable(r.Protection)
                && (r.Type is "Private" or "Image" || (includeMapped && r.Type == "Mapped")))
            .ToList();

        var results = new List<MemoryString>();
        for (int i = 0; i < regions.Count && results.Count < maxResults; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((i, regions.Count));
            try
            {
                results.AddRange(Scan(key, processName, regions[i].Base, regions[i].Size, maxResults - results.Count, ct, contains));
            }
            catch (ProcessActionException ex) when (ex.Failure != ProcessActionFailure.Exited)
            {
                // One unreadable region doesn't stop the scan.
            }
        }

        progress?.Report((regions.Count, regions.Count));
        return results;
    }

    private static bool Readable(string protection) =>
        protection.Length > 0 && protection != "NA" && protection != "X" && !protection.Contains("+G", StringComparison.Ordinal);

    private static bool Printable(byte b) => b is >= 0x20 and < 0x7F or (byte)'\t';

    private sealed class StopScan : Exception;

    /// <summary>One encoding's current run of printable characters.</summary>
    private sealed class Run(bool unicode, List<MemoryString> results, int maxResults, string? contains)
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
                var text = _text.ToString();
                if (contains is null || text.Contains(contains, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new MemoryString(_start, unicode, text));
                }
            }

            _text.Clear();
            _highBytePending = false;
        }
    }
}
