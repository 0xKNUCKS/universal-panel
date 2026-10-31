using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using UniversalPanel.Native;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Tools;

/// <summary>One launcher entry. Starts with just the path; identity and icon arrive later.</summary>
internal sealed class ToolRow(string path) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; } = path;

    public ToolIdentity Identity { get; private set; } =
        new(System.IO.Path.GetFileNameWithoutExtension(path), null, ToolTrust.Unknown, string.Empty, null);

    public ImageSource? Icon { get; private set; }

    public bool Exists { get; private set; } = true;

    public string Name => Identity.Name;

    public string Version => Exists ? Identity.Version ?? string.Empty : "Missing";

    public string Subtitle => !Exists
        ? "Missing: " + Path
        : string.Join(" · ", new[] { Identity.Version, Identity.Publisher }.Where(s => !string.IsNullOrEmpty(s)).DefaultIfEmpty(System.IO.Path.GetFileName(Path)));

    public string ToolTipText => string.IsNullOrEmpty(Identity.Detail) ? Path : $"{Identity.Detail}\n{Path}";

    public string BadgeText => Identity.Trust switch
    {
        ToolTrust.Verified => "Verified",
        ToolTrust.Identified => "Identified",
        ToolTrust.Warning => "Check",
        _ => string.Empty,
    };

    public SymbolRegular BadgeSymbol => Identity.Trust switch
    {
        ToolTrust.Verified => SymbolRegular.ShieldCheckmark24,
        ToolTrust.Identified => SymbolRegular.Checkmark24,
        ToolTrust.Warning => SymbolRegular.Warning24,
        _ => SymbolRegular.Empty,
    };

    public Brush? BadgeBrush => Application.Current.TryFindResource(Identity.Trust switch
    {
        ToolTrust.Verified => "SystemFillColorSuccessBrush",
        ToolTrust.Warning => "SystemFillColorCautionBrush",
        _ => "TextFillColorSecondaryBrush",
    }) as Brush;

    public Visibility BadgeVisibility => Identity.Trust == ToolTrust.Unknown ? Visibility.Collapsed : Visibility.Visible;

    public double Opacity => Exists ? 1 : 0.5;

    /// <summary>Reads identity and icon; runs off the UI thread (signature checks take a while).</summary>
    public (ToolIdentity Identity, ImageSource? Icon, bool Exists) Inspect()
    {
        if (!File.Exists(Path))
        {
            return (Identity, null, false);
        }

        var identity = ToolCatalog.Identify(Path);
        // Ghidra starts from a .bat; its own icon ships in support\.
        string iconSource = identity.Name == "Ghidra" && File.Exists(GhidraIcon) ? GhidraIcon : Path;
        return (identity, ProcessIcons.Get(iconSource), true);
    }

    private string GhidraIcon => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? string.Empty, "support", "ghidra.ico");

    public void Apply((ToolIdentity Identity, ImageSource? Icon, bool Exists) result)
    {
        (Identity, Icon, Exists) = result;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); // everything changed
    }
}
