using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UniversalPanel.Native;
using UniversalPanel.Shell;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Tools;

/// <summary>
/// One launcher entry: the saved <see cref="ToolEntry"/> plus what the panel reads from the file
/// (identity, icon). Starts with just the entry; identity and icon arrive from <see cref="Inspect"/>.
/// </summary>
internal sealed class ToolRow(ToolEntry entry) : INotifyPropertyChanged
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif"];

    /// <summary>Icons are extracted at the tile size (48 DIP, in device pixels), not the 16 px small icon.</summary>
    public const int IconSize = 48;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ToolEntry Entry { get; private set; } = entry;

    public string Path => Entry.Path;

    public ToolIdentity Identity { get; private set; } =
        new(WslTool.TryParse(entry.Path, out _, out var command) ? command : System.IO.Path.GetFileNameWithoutExtension(entry.Path), null, ToolTrust.Unknown, string.Empty, null);

    /// <summary>A tool inside a WSL distro ("wsl:distro:command"), not a file.</summary>
    public bool IsWsl => WslTool.TryParse(Path, out _, out _);

    /// <summary>Set once <see cref="Apply"/> has run (identity and icon are known).</summary>
    public bool Inspected { get; private set; }

    /// <summary>The user's actions if they changed them, else the catalog's defaults for this tool.</summary>
    public IReadOnlyList<ToolAction> Actions => Entry.Actions ?? ToolCatalog.DefaultActions(Identity.Known);

    /// <summary>Command-line tools open in a console (double-click: usage, or a shell for WSL tools).</summary>
    public bool IsConsoleTool => IsWsl || Categories.Contains(ToolCategories.CommandLine);

    public ImageSource? Icon { get; private set; }

    public bool Exists { get; private set; } = true;

    /// <summary>The user's name for it, else what the file says it is.</summary>
    public string Name => Entry.Name ?? Identity.Name;

    public string Version => Exists ? Identity.Version ?? string.Empty : "Missing";

    /// <summary>The user's sections, else the catalog's defaults for this tool.</summary>
    public IReadOnlyList<string> Categories => Entry.Categories is { Count: > 0 } chosen ? chosen : ToolCatalog.DefaultCategories(Identity.Known);

    public string Subtitle => !Exists
        ? "Missing: " + Path
        : string.Join(" · ", new[] { Identity.Version, Identity.Publisher }.Where(s => !string.IsNullOrEmpty(s)).DefaultIfEmpty(System.IO.Path.GetFileName(Path)));

    public string ToolTipText => string.Join("\n", new[]
    {
        Name != Identity.Name ? $"{Name} ({Identity.Name})" : Name,
        Identity.Detail,
        string.Join(", ", Categories),
        Path,
    }.Where(s => !string.IsNullOrEmpty(s)));

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

    /// <summary>Case-insensitive match on name, what the file says, publisher, sections and path.</summary>
    public bool Matches(string query) =>
        query.Length == 0
        || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Identity.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Identity.Publisher?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
        || Categories.Any(c => c.Contains(query, StringComparison.OrdinalIgnoreCase))
        || Path.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads identity and icon; runs off the UI thread (signature checks take a while).</summary>
    public (ToolIdentity Identity, ImageSource? Icon, bool Exists) Inspect()
    {
        var entry = Entry;
        if (IsWsl)
        {
            var wsl = System.IO.Path.Combine(Environment.SystemDirectory, "wsl.exe");
            return (ToolCatalog.Identify(entry.Path), LoadIcon(ToolIcons.PathOf(entry.Icon)) ?? ProcessIcons.Get(wsl, IconSize), true);
        }

        if (!File.Exists(entry.Path))
        {
            return (Identity, LoadIcon(ToolIcons.PathOf(entry.Icon)), false);
        }

        var identity = ToolCatalog.Identify(entry.Path);
        // Ghidra starts from a .bat; its own icon ships in support\.
        string ghidraIcon = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(entry.Path) ?? string.Empty, "support", "ghidra.ico");
        string defaultIcon = identity.Known == "ghidra" && File.Exists(ghidraIcon) ? ghidraIcon : entry.Path;
        return (identity, LoadIcon(ToolIcons.PathOf(entry.Icon)) ?? ProcessIcons.Get(defaultIcon, IconSize), true);
    }

    /// <summary>A custom icon: images are decoded directly, anything else (.ico, .exe, .dll) through the shell.</summary>
    public static ImageSource? LoadIcon(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        if (!ImageExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            return ProcessIcons.Get(path, IconSize);
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file open
            image.DecodePixelWidth = 256; // plenty for a 48 DIP tile at 400% DPI
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public void Apply((ToolIdentity Identity, ImageSource? Icon, bool Exists) result)
    {
        (Identity, Icon, Exists) = result;
        Inspected = true;
        Changed();
    }

    /// <summary>Takes the edited entry; call <see cref="Inspect"/> again for a new path or icon.</summary>
    public void Update(ToolEntry entry)
    {
        Entry = entry;
        Changed();
    }

    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
