using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using UniversalPanel.Modules.Tools;
using UniversalPanel.Native;
using UniversalPanel.Shell;

namespace UniversalPanel.Modules.Terminal;

/// <summary>A kind of shell the "+" menu offers: an absolute program path and its arguments.</summary>
internal sealed record TerminalProfile(string Name, string Program, IReadOnlyList<string> Arguments, string Kind)
{
    /// <summary>The program's own icon; a distro's from its package. Frozen.</summary>
    public ImageSource? Icon { get; init; }

    /// <summary>Folder to start in; null = the user's profile folder.</summary>
    public string? Directory { get; init; }

    /// <summary>The WSL distro, for WSL profiles.</summary>
    public string? Distro { get; init; }
}

/// <summary>The shells installed here: PowerShell 7, Windows PowerShell, cmd, each WSL distro, Python.</summary>
internal static class TerminalProfiles
{
    private const int IconSize = 16;
    private static Task<List<TerminalProfile>>? _detecting;

    /// <summary>Detected once per run (it asks WSL for its distros, which takes a moment).</summary>
    public static Task<List<TerminalProfile>> GetAsync() => _detecting ??= Task.Run(Detect);

    /// <summary>The shells, if detection has finished; otherwise starts it and returns null.</summary>
    public static List<TerminalProfile>? Cached() => GetAsync() is { IsCompletedSuccessfully: true } done ? done.Result : null;

    private static List<TerminalProfile> Detect()
    {
        string system = Environment.SystemDirectory;
        var profiles = new List<TerminalProfile>();

        string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (File.Exists(pwsh))
        {
            profiles.Add(new("PowerShell", pwsh, ["-NoLogo"], "powershell") { Icon = ProcessIcons.Get(pwsh, IconSize) });
        }

        string powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(powershell))
        {
            profiles.Add(new("Windows PowerShell", powershell, ["-NoLogo"], "powershell") { Icon = ProcessIcons.Get(powershell, IconSize) });
        }

        string cmd = Path.Combine(system, "cmd.exe");
        profiles.Add(new("Command Prompt", cmd, [], "cmd") { Icon = ProcessIcons.Get(cmd, IconSize) });

        string wsl = Path.Combine(system, "wsl.exe");
        if (File.Exists(wsl))
        {
            foreach (var distro in ToolFinder.Distros())
            {
                profiles.Add(new(distro, wsl, ["-d", distro, "--cd", "~"], "wsl") { Icon = DistroIcons.Get(distro, IconSize), Distro = distro });
            }
        }

        if (FindPython() is { } python)
        {
            string version = FileVersionInfo.GetVersionInfo(python).ProductVersion is { Length: > 0 } v ? $"Python {v.Split(' ')[0]}" : "Python";
            profiles.Add(new(version, python, [], "python") { Icon = ProcessIcons.Get(python, IconSize) });
        }

        return profiles;
    }

    /// <summary>python.exe on PATH, skipping the Microsoft Store stub (WindowsApps), which only opens the Store.</summary>
    private static string? FindPython() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Environment.ExpandEnvironmentVariables)
            .Where(f => ToolEntry.IsSafePath(f) && !f.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.Combine(f, "python.exe"))
            .FirstOrDefault(File.Exists);
}
