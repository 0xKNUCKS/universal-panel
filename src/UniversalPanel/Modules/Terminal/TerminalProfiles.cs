using System.Diagnostics;
using System.IO;
using UniversalPanel.Modules.Tools;
using UniversalPanel.Shell;

namespace UniversalPanel.Modules.Terminal;

/// <summary>A kind of shell the "+" menu offers: an absolute program path and its arguments.</summary>
internal sealed record TerminalProfile(string Name, string Program, IReadOnlyList<string> Arguments, string Kind);

/// <summary>The shells installed here: PowerShell 7, Windows PowerShell, cmd, each WSL distro, Python.</summary>
internal static class TerminalProfiles
{
    public static List<TerminalProfile> Detect()
    {
        string system = Environment.SystemDirectory;
        var profiles = new List<TerminalProfile>();

        string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (File.Exists(pwsh))
        {
            profiles.Add(new("PowerShell", pwsh, ["-NoLogo"], "powershell"));
        }

        string powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(powershell))
        {
            profiles.Add(new("Windows PowerShell", powershell, ["-NoLogo"], "powershell"));
        }

        profiles.Add(new("Command Prompt", Path.Combine(system, "cmd.exe"), [], "cmd"));

        string wsl = Path.Combine(system, "wsl.exe");
        if (File.Exists(wsl))
        {
            foreach (var distro in ToolFinder.Distros())
            {
                profiles.Add(new(distro, wsl, ["-d", distro, "--cd", "~"], "wsl"));
            }
        }

        if (FindPython() is { } python)
        {
            string version = FileVersionInfo.GetVersionInfo(python).ProductVersion is { Length: > 0 } v ? $"Python {v.Split(' ')[0]}" : "Python";
            profiles.Add(new(version, python, [], "python"));
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
