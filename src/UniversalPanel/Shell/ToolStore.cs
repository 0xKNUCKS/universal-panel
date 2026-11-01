using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;

namespace UniversalPanel.Shell;

/// <summary>
/// The user's tool list in %LOCALAPPDATA%\UniversalPanel\tools.json, kept apart from settings.json
/// (UI state that's rewritten on every resize) so resetting the UI can't cost the user their
/// tools. Like settings.json it's user-writable, so it's untrusted on load: entries are sanitized,
/// de-duplicated and capped, and an unreadable file means an empty list plus a log entry.
/// </summary>
internal static class ToolStore
{
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalPanel");

    public static string FilePath { get; } = Path.Combine(Folder, "tools.json");

    private const int MaxTools = 200;

    public static List<ToolEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var file = JsonSerializer.Deserialize<ToolFile>(File.ReadAllText(FilePath)) ?? new ToolFile();
                return (file.Tools ?? [])
                    .Where(t => t is not null && t.Sanitize())
                    .DistinctBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxTools)
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("Tools", "Couldn't read tools.json; starting with no tools.", ex);
        }

        return [];
    }

    /// <summary>Saves (write-then-rename) and removes copied icons no tool uses anymore.</summary>
    public static void Save(IReadOnlyCollection<ToolEntry> tools)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new ToolFile { Tools = tools.ToList() }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
            ToolIcons.RemoveUnused(tools.Select(t => t.Icon));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Tools", "Couldn't save tools.json.", ex);
        }
    }

    /// <summary>An object, not a bare array, so fields can be added later.</summary>
    private sealed class ToolFile
    {
        public List<ToolEntry>? Tools { get; set; } = [];
    }
}

/// <summary>
/// Custom tool icons, copied into %LOCALAPPDATA%\UniversalPanel\icons as "&lt;sha256&gt;.png" when
/// picked, so moving or deleting the original doesn't lose them. tools.json stores only that
/// name, and only names matching the pattern are used, so the file can't point the panel at
/// anything outside the folder; cleanup likewise only ever deletes pattern-named files there.
/// </summary>
internal static partial class ToolIcons
{
    private const int MaxSize = 256;

    public static string Folder { get; } = Path.Combine(ToolStore.Folder, "icons");

    [GeneratedRegex("^[0-9a-f]{64}\\.png$")]
    private static partial Regex Name();

    public static bool IsValidName(string? name) => name is not null && Name().IsMatch(name);

    /// <summary>The icon's file, if the name is valid and the file is there; otherwise null.</summary>
    public static string? PathOf(string? name) =>
        IsValidName(name) && File.Exists(Path.Combine(Folder, name!)) ? Path.Combine(Folder, name!) : null;

    /// <summary>Copies an image, .ico, .exe or .dll's icon in as PNG; returns its name, or null if it has no usable icon.</summary>
    public static string? Import(string source)
    {
        if (!ToolEntry.IsSafePath(source) || !File.Exists(source))
        {
            return null;
        }

        try
        {
            var bitmap = Decode(source);
            if (bitmap is null)
            {
                return null;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            byte[] png = buffer.ToArray();

            string name = Convert.ToHexStringLower(SHA256.HashData(png)) + ".png";
            string target = Path.Combine(Folder, name);
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(Folder);
                string temp = target + ".tmp";
                File.WriteAllBytes(temp, png);
                File.Move(temp, target, overwrite: true);
            }

            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FileFormatException or InvalidOperationException)
        {
            Log.Warn("Tools", $"Couldn't copy the icon from {source}.", ex);
            return null;
        }
    }

    /// <summary>Deletes copied icons that no tool references (pattern-named files only).</summary>
    public static void RemoveUnused(IEnumerable<string?> used)
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        var keep = used.Where(IsValidName).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Folder, "*.png"))
        {
            string name = Path.GetFileName(file);
            if (IsValidName(name) && !keep.Contains(name))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn("Tools", $"Couldn't remove unused icon {name}.", ex);
                }
            }
        }
    }

    private static BitmapSource? Decode(string source)
    {
        string extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is ".exe" or ".dll")
        {
            return ProcessIcons.ExtractAt(source, MaxSize);
        }

        BitmapDecoder decoder;
        using (var stream = File.OpenRead(source))
        {
            decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        }

        // .ico files hold several sizes: take the largest (scaled down below if over 256).
        var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
        if (frame is null)
        {
            return null;
        }

        int longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (longest <= MaxSize)
        {
            return frame;
        }

        double scale = (double)MaxSize / longest;
        var scaled = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }
}
