using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using UniversalPanel.Diagnostics;

namespace UniversalPanel.Shell;

/// <summary>
/// Persisted preferences in %LOCALAPPDATA%\UniversalPanel\settings.json, shared by the shell and
/// modules through <see cref="Current"/>. Treated as untrusted input (docs/SECURITY.md):
/// unreadable or out-of-range values fall back to defaults.
/// </summary>
internal sealed class PanelSettings
{
    public const double DefaultWidth = 460;
    public const double MinWidth = 380;
    public const double MaxWidth = 1400;
    private const double MinColumnWidth = 30;
    private const double MaxColumnWidth = 1000;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalPanel", "settings.json");

    private static readonly Lazy<PanelSettings> Instance = new(Load);
    private DispatcherTimer? _saveDebounce;

    public static PanelSettings Current => Instance.Value;

    public double PanelWidth { get; set; } = DefaultWidth;

    // --- General (Settings page) ------------------------------------------------------------

    /// <summary>Toggle hotkey as WPF Key / ModifierKeys names; null means the default (Alt+`).</summary>
    public string? HotkeyKey { get; set; }

    public string? HotkeyModifiers { get; set; }

    /// <summary>"Dark", "Light" or "System".</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>"Off", "Normal" (Run key) or "Elevated" (logon scheduled task).</summary>
    public string StartWithWindows { get; set; } = "Off";

    public double ProcessRefreshSeconds { get; set; } = 1;

    /// <summary>Process history keeps sampling while the panel is hidden (costs ~1-2 ms CPU/s).</summary>
    public bool HistoryWhileHidden { get; set; }

    // --- Processes module -----------------------------------------------------------------

    public bool ProcessTree { get; set; } = true;

    /// <summary>Sort column (a SortMemberPath), or null for natural order.</summary>
    public string? ProcessSort { get; set; }

    public bool ProcessSortDescending { get; set; }

    /// <summary>Process list column widths by SortMemberPath (fixed-width columns only).</summary>
    public Dictionary<string, double> ProcessColumnWidths { get; set; } = [];

    // --- Tools module ---------------------------------------------------------------------

    /// <summary>Launcher entries, in display order: full paths to executables or scripts.
    /// Deliberately no "always run as administrator" flag: this file is user-writable, so it must
    /// never be able to make an elevated panel start something elevated without a click.</summary>
    public List<string> Tools { get; set; } = [];

    private static PanelSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(FilePath)) ?? new();
                settings.Sanitize();
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("Settings", "Couldn't read settings; using defaults.", ex);
        }

        return new PanelSettings();
    }

    private void Sanitize()
    {
        PanelWidth = double.IsFinite(PanelWidth) ? Math.Clamp(PanelWidth, MinWidth, MaxWidth) : DefaultWidth;
        ProcessColumnWidths = (ProcessColumnWidths ?? [])
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && double.IsFinite(kv.Value))
            .Take(32)
            .ToDictionary(kv => kv.Key, kv => Math.Clamp(kv.Value, MinColumnWidth, MaxColumnWidth));
        Theme = Theme is "Dark" or "Light" or "System" ? Theme : "Dark";
        StartWithWindows = StartWithWindows is "Off" or "Normal" or "Elevated" ? StartWithWindows : "Off";
        ProcessRefreshSeconds = double.IsFinite(ProcessRefreshSeconds) ? Math.Clamp(ProcessRefreshSeconds, 0.25, 10) : 1;
        if (HotkeyKey is { Length: > 32 } || HotkeyModifiers is { Length: > 64 })
        {
            HotkeyKey = HotkeyModifiers = null;
        }

        Tools = (Tools ?? [])
            .Where(t => t is { Length: > 3 and < 1024 } && Path.IsPathFullyQualified(t) && !t.Contains('"'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();

        if (ProcessSort is { Length: > 64 })
        {
            ProcessSort = null;
        }
    }

    /// <summary>Saves after a short pause, so bursts (column drags) write once. UI thread only.</summary>
    public void SaveSoon()
    {
        _saveDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            _saveDebounce!.Stop();
            Save();
        }, Dispatcher.CurrentDispatcher);
        _saveDebounce.Stop();
        _saveDebounce.Start();
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
