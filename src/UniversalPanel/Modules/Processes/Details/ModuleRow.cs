using System.Globalization;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Processes.Details;

/// <summary>One loaded module. Immutable: the list is reloaded, not updated in place.</summary>
internal sealed class ModuleRow(ModuleRecord module)
{
    public string Name => module.Name;
    public string Path => module.Path;
    public ulong BaseAddress => module.BaseAddress;
    public uint Size => module.Size;
    public string BaseText { get; } = "0x" + module.BaseAddress.ToString("X", CultureInfo.InvariantCulture);
    public string SizeText { get; } = ProcessRow.FormatBytes(module.Size);
}
