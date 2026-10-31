using System.Globalization;
using System.IO;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Processes.Details;

/// <summary>One memory region. Immutable: the list is reloaded, not updated in place.</summary>
internal sealed class MemoryRow(MemoryRegion region)
{
    public MemoryRegion Region => region;
    public ulong Base => region.Base;
    public ulong Size => region.Size;
    public string BaseText { get; } = "0x" + region.Base.ToString("X", CultureInfo.InvariantCulture);
    public string SizeText { get; } = ProcessRow.FormatBytes(region.Size);
    public string Protection => region.Protection;
    public string Type => region.Type;
    public bool Suspicious => region.Suspicious;
    public bool Committed => region.State == "Commit";

    /// <summary>File name only, for the narrow column; the full path is in the tooltip.</summary>
    public string Use { get; } = region.File is { } f ? Path.GetFileName(f) : string.Empty;

    public string Details =>
        $"{BaseText} - 0x{region.Base + region.Size:X} ({SizeText})\n" +
        $"{region.State} · {region.Type} · {region.Protection}" +
        (region.File is { } file ? $"\n{file}" : string.Empty) +
        (region.Suspicious ? "\nExecutable memory without an image behind it (or RWX): shellcode, manual mapping, unpacked code, or JIT." : string.Empty);
}
