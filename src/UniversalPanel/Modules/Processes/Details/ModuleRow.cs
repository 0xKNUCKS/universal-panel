using System.ComponentModel;
using System.Globalization;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Processes.Details;

/// <summary>
/// One loaded module. The list is reloaded, not updated in place; only the signature and PE
/// fields arrive later (verified in the background) and notify.
/// </summary>
internal sealed class ModuleRow(ModuleRecord module) : INotifyPropertyChanged
{
    // Folders any user process can write to: a DLL loaded from here is worth a second look.
    private static readonly string[] UserWritable =
    [
        System.IO.Path.GetTempPath(),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Temp",
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads",
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Desktop",
        @"C:\Users\Public",
    ];

    private FileTrustInfo? _trust;
    private PeHeaders? _pe;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name => module.Name;
    public string Path => module.Path;
    public ulong BaseAddress => module.BaseAddress;
    public uint Size => module.Size;
    public string BaseText { get; } = "0x" + module.BaseAddress.ToString("X", CultureInfo.InvariantCulture);
    public string SizeText { get; } = ProcessRow.FormatBytes(module.Size);

    public bool FromUserWritableFolder { get; } =
        UserWritable.Any(folder => module.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase));

    public string SignerText => _trust?.Text ?? "…";

    /// <summary>Unsigned / invalid, or loaded from a user-writable folder.</summary>
    public bool Flagged => (_trust is { IsTrusted: false }) || FromUserWritableFolder;

    public string Details
    {
        get
        {
            var lines = new List<string> { Path, $"Base {BaseText} · size {SizeText}" };
            lines.Add(_trust is null ? "Signature: checking…" : $"Signature: {_trust.Text}{(_trust.State == TrustState.SignedCatalog ? " (catalog)" : string.Empty)}");
            if (_pe is { } pe)
            {
                var relocated = pe.PreferredBase == BaseAddress ? "loaded at preferred base" : $"relocated from 0x{pe.PreferredBase:X}";
                lines.Add($"{pe.Machine}{(pe.IsDotNet ? " · .NET" : string.Empty)} · entry 0x{BaseAddress + pe.EntryPointRva:X} · {relocated}");
                if (pe.LinkTime is { } linked)
                {
                    lines.Add($"Linked {linked.ToLocalTime():G}");
                }
            }

            if (FromUserWritableFolder)
            {
                lines.Add("Loaded from a user-writable folder.");
            }

            return string.Join("\n", lines);
        }
    }

    public void SetVerification(FileTrustInfo trust, PeHeaders? pe)
    {
        _trust = trust;
        _pe = pe;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SignerText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Flagged)));
    }
}
