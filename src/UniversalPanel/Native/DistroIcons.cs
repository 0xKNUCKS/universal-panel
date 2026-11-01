// A WSL distro's own icon. Distros from the Microsoft Store: the package's Square44x44Logo asset,
// picked the way PowerToys Run does (Microsoft.Plugin.Program/Programs/UWPApplication.cs
// SetTargetSizeIcon / LogoPathFromUri @ 669a19e). Distros installed from a .wsl file: the icon WSL
// registered for them (Lxss "ShortcutPath"). Anything else: wsl.exe's icon.

using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using Microsoft.Win32;
using UniversalPanel.Shell;

namespace UniversalPanel.Native;

internal static partial class DistroIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    /// <summary>Frozen icon for a distro, drawn at <paramref name="sizeDip"/>; any thread.</summary>
    public static ImageSource? Get(string distro, int sizeDip)
    {
        string key = $"{distro}|{sizeDip}";
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var icon = Diagnostics.Guard.Run("DistroIcons", () => Load(distro, sizeDip), _ => null)
            ?? ProcessIcons.Get(Path.Combine(Environment.SystemDirectory, "wsl.exe"), sizeDip);
        lock (Gate)
        {
            Cache[key] = icon;
        }

        return icon;
    }

    private static ImageSource? Load(string distro, int sizeDip)
    {
        using var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
        if (lxss is null)
        {
            return null;
        }

        foreach (var id in lxss.GetSubKeyNames())
        {
            using var key = lxss.OpenSubKey(id);
            if (key?.GetValue("DistributionName") is not string name || !name.Equals(distro, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // HKCU is the user's to write: only a local .ico is read.
            if (key.GetValue("ShortcutPath") is string ico && ToolEntry.IsSafePath(ico)
                && ico.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && File.Exists(ico))
            {
                return ProcessIcons.Get(ico, sizeDip);
            }

            if (key.GetValue("PackageFamilyName") is string family && PackageRoot(family) is { } root
                && PackageLogo(root, sizeDip * 2) is { } png)
            {
                return LoadPng(png);
            }

            return null;
        }

        return null;
    }

    /// <summary>The install folder of the package family's first installed package.</summary>
    private static unsafe string? PackageRoot(string family)
    {
        uint count = 0, bufferLength = 0;
        if (GetPackagesByPackageFamily(family, ref count, null, ref bufferLength, null) != ErrorInsufficientBuffer || count == 0)
        {
            return null;
        }

        var names = new nint[count];
        var buffer = new char[bufferLength];
        fixed (nint* pNames = names)
        fixed (char* pBuffer = buffer)
        {
            if (GetPackagesByPackageFamily(family, ref count, pNames, ref bufferLength, pBuffer) != 0)
            {
                return null;
            }

            uint pathLength = 0;
            string fullName = new((char*)names[0]);
            if (GetPackagePathByFullName(fullName, ref pathLength, null) != ErrorInsufficientBuffer)
            {
                return null;
            }

            var path = new char[pathLength];
            fixed (char* pPath = path)
            {
                return GetPackagePathByFullName(fullName, ref pathLength, pPath) == 0 ? new string(pPath) : null;
            }
        }
    }

    /// <summary>The Square44x44Logo variant closest to <paramref name="pixels"/>, unplated first (no tile background).</summary>
    private static string? PackageLogo(string root, int pixels)
    {
        string manifest = Path.Combine(root, "AppxManifest.xml");
        if (!File.Exists(manifest) || Square44Logo(manifest) is not { } logo
            || Path.IsPathRooted(logo) || logo.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        string path = Path.GetFullPath(Path.Combine(root, logo));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string prefix = path[..^Path.GetExtension(path).Length];
        string extension = Path.GetExtension(path);
        int[] sizes = [16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 128, 256];
        foreach (int size in sizes.OrderBy(s => s < pixels ? 1000 + pixels - s : s - pixels))
        {
            string[] candidates =
            [
                $"{prefix}.targetsize-{size}_altform-unplated{extension}",
                $"{prefix}.altform-unplated_targetsize-{size}{extension}",
                $"{prefix}.targetsize-{size}{extension}",
            ];
            if (candidates.FirstOrDefault(File.Exists) is { } found)
            {
                return found;
            }
        }

        return new[] { 200, 100, 150, 400 }.Select(s => $"{prefix}.scale-{s}{extension}").FirstOrDefault(File.Exists);
    }

    private static string? Square44Logo(string manifest)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1 << 20 };
        using var reader = XmlReader.Create(manifest, settings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "VisualElements"
                && reader.GetAttribute("Square44x44Logo") is { Length: > 0 } logo)
            {
                return logo;
            }
        }

        return null;
    }

    private static BitmapSource LoadPng(string path)
    {
        using var stream = File.OpenRead(path);
        var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        image.Freeze();
        return image;
    }

    private const int ErrorInsufficientBuffer = 122;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, nint* packageFullNames, ref uint bufferLength, char* buffer);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetPackagePathByFullName(string packageFullName, ref uint pathLength, char* path);
}
