using System.IO;
using System.Text.Json;
using UniversalPanel.Diagnostics;

namespace UniversalPanel.Shell;

/// <summary>
/// Persisted panel preferences in %LOCALAPPDATA%\UniversalPanel\settings.json. Treated as
/// untrusted input (docs/SECURITY.md): unreadable or out-of-range values fall back to defaults.
/// </summary>
internal sealed class PanelSettings
{
    public const double DefaultWidth = 460;
    public const double MinWidth = 380;
    public const double MaxWidth = 1400;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalPanel", "settings.json");

    public double PanelWidth { get; set; } = DefaultWidth;

    public static PanelSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(FilePath)) ?? new();
                settings.PanelWidth = double.IsFinite(settings.PanelWidth)
                    ? Math.Clamp(settings.PanelWidth, MinWidth, MaxWidth)
                    : DefaultWidth;
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("Settings", "Couldn't read settings; using defaults.", ex);
        }

        return new PanelSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Write-then-rename so a crash mid-write can't leave a truncated file behind.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Settings", "Couldn't save settings.", ex);
        }
    }
}
