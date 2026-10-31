// Region walk after System Informer (SystemInformer/memsrch.c, PhpMemoryStringSearchNextBuffer):
// committed regions, skipping NOACCESS / GUARD, filtered by type, read in bounded chunks.
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d
// String matching ported from Detect It Easy's XBinary (xbinary.cpp: multiSearch_allStrings,
// isAnsiSymbol, isUnicodeSymbol): ANSI runs of 0x20-0x7F bytes, and UTF-16LE as two parity
// lanes of 16-bit code units (0x20-0xFF and Cyrillic), state carried across chunks.
// https://github.com/horsicq/Formats @ dd3704da34e065b3754135bfce8cb3612804c227

using System.Text;

namespace UniversalPanel.Native;

internal sealed record MemoryString(ulong Address, bool Unicode, string Text);

internal static class MemoryStrings
{
    public const int MinLength = 5;
    private const int MaxLength = 1024;

    /// <summary>Strings in a region, scanned chunk by chunk with state carried across chunks.</summary>
    /// <param name="contains">Optional: only strings containing this (case-insensitive) are kept,
    /// so the result cap counts matches, not every string.</param>
    public static List<MemoryString> Scan(ProcessKey key, string processName, ulong baseAddress, ulong size, int maxResults, CancellationToken ct, string? contains = null)
    {
        var results = new List<MemoryString>();
        var matcher = new Matcher(results, maxResults, contains);

        try
        {
            MemoryRegions.Read(key, processName, baseAddress, size, (chunk, address) =>
            {
                ct.ThrowIfCancellationRequested();
                for (int i = 0; i < chunk.Length; i++)
                {
                    matcher.Feed(chunk[i], address + (ulong)i);
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

        matcher.Flush();
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

    // XBinary::isAnsiSymbol(bExtra = false)
    private static bool IsAnsiSymbol(byte b) => b is >= 0x20 and < 0x80;

    // XBinary::isUnicodeSymbol(bExtra = true)
    private static bool IsUnicodeSymbol(ushort c) => c is >= 0x20 and <= 0xFF or >= 0x0400 and <= 0x04FF;

    private sealed class StopScan : Exception;

    /// <summary>
    /// The scan state: one ANSI run and two UTF-16 lanes (even / odd start address), fed byte by
    /// byte; each code unit is the previous byte plus this one, so its lane is this byte's parity.
    /// </summary>
    private sealed class Matcher(List<MemoryString> results, int maxResults, string? contains)
    {
        private readonly Lane _ansi = new(unicode: false);
        private readonly Lane[] _wide = [new(unicode: true), new(unicode: true)];
        private byte _previous;
        private bool _started;

        public void Feed(byte b, ulong address)
        {
            if (IsAnsiSymbol(b))
            {
                _ansi.Append((char)b, address);
            }
            else
            {
                End(_ansi);
            }

            if (_started)
            {
                var code = (ushort)(_previous | (b << 8));
                var lane = _wide[(int)(address & 1)];
                if (IsUnicodeSymbol(code))
                {
                    lane.Append((char)code, address - 1);
                }
                else
                {
                    End(lane);
                }
            }

            _previous = b;
            _started = true;
        }

        public void Flush()
        {
            End(_ansi);
            End(_wide[0]);
            End(_wide[1]);
        }

        private void End(Lane lane)
        {
            if (lane.Text.Length >= MinLength && results.Count < maxResults)
            {
                var text = lane.Text.ToString();
                if (contains is null || text.Contains(contains, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new MemoryString(lane.Start, lane.Unicode, text));
                }
            }

            lane.Text.Clear();
        }
    }

    private sealed class Lane(bool unicode)
    {
        public readonly StringBuilder Text = new();
        public ulong Start;
        public bool Unicode => unicode;

        public void Append(char c, ulong address)
        {
            if (Text.Length == 0)
            {
                Start = address;
            }

            if (Text.Length < MaxLength)
            {
                Text.Append(c); // longer runs are cut, not split (XBinary nMaxLenght)
            }
        }
    }
}
