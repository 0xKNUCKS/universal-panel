using System.Diagnostics;
using System.IO;
using System.Text;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Tools;

/// <summary>
/// Looks for the tools in Catalog/known-tools.json: their "files" in the catalog's "search" folders
/// and in the folders on PATH, and
/// their "wsl" commands in each installed WSL distro. Keeps only what <see cref="ToolCatalog"/>
/// recognizes, one per tool and version. Links (junctions, symlinks) aren't followed.
/// </summary>
internal static class ToolFinder
{
    private static readonly string Wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");

    /// <returns>File paths, and "wsl:distro:command" for tools inside WSL.</returns>
    public static List<string> Find(CancellationToken ct)
    {
        // Preference when one install has several entry points: catalog order, then file order
        // (x96dbg before x64dbg, ida64 before ida).
        var priority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ToolCatalog.Tools.SelectMany(t => t.Files))
        {
            priority.TryAdd(file, priority.Count);
        }

        var found = new List<(string Path, ToolIdentity Identity, int Priority)>();
        void Consider(string path)
        {
            if (priority.TryGetValue(Path.GetFileName(path), out int rank)
                && ToolCatalog.Identify(path) is { Trust: not ToolTrust.Unknown } identity)
            {
                found.Add((path, identity, rank));
            }
        }

        var deep = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = Math.Clamp(ToolCatalog.Search.Depth, 1, 8),
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };
        foreach (var root in Roots())
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.*", deep))
            {
                ct.ThrowIfCancellationRequested();
                Consider(path);
            }
        }

        // Command-line tools usually live on PATH (only the folders themselves, not below them).
        var shallow = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var folder in ToolCatalog.Search.PathFolders ? PathFolders() : [])
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.exe", shallow))
            {
                ct.ThrowIfCancellationRequested();
                Consider(path);
            }
        }

        // One entry per tool + version: preferred entry point, then the shortest path (x86 / x32
        // copies live in subfolders).
        var result = found
            .GroupBy(f => (f.Identity.Name, f.Identity.Version))
            .Select(g => g.OrderBy(f => f.Priority).ThenBy(f => f.Path.Length).First().Path)
            .ToList();
        result.AddRange(FindInWsl(ct));
        return result;
    }

    /// <summary>The catalog's search folders, expanded; only local folders that exist.</summary>
    private static IEnumerable<string> Roots() =>
        ToolCatalog.Search.Folders
            .Select(Expand)
            .Where(f => Path.IsPathFullyQualified(f) && !Shell.ToolEntry.IsNetworkPath(f) && Directory.Exists(f))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>{Documents} {Desktop} {Downloads} {Home} (where Windows really keeps them, OneDrive or not), then %VARIABLES%.</summary>
    private static string Expand(string folder)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Environment.ExpandEnvironmentVariables(folder
            .Replace("{Documents}", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), StringComparison.OrdinalIgnoreCase)
            .Replace("{Desktop}", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), StringComparison.OrdinalIgnoreCase)
            .Replace("{Downloads}", Path.Combine(home, "Downloads"), StringComparison.OrdinalIgnoreCase)
            .Replace("{Home}", home, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> PathFolders() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Environment.ExpandEnvironmentVariables)
            .Where(f => Path.IsPathFullyQualified(f) && !Shell.ToolEntry.IsNetworkPath(f) && Directory.Exists(f)) // never touch a share
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The catalog's WSL commands present in each distro, as "wsl:distro:command".</summary>
    private static List<string> FindInWsl(CancellationToken ct)
    {
        var result = new List<string>();
        if (!File.Exists(Wsl))
        {
            return result;
        }

        var commands = ToolCatalog.Tools.Select(t => t.Wsl).OfType<string>().Where(c => WslTool.TryParse(WslTool.Make("d", c), out _, out _)).Distinct().ToList();
        if (commands.Count == 0)
        {
            return result;
        }

        foreach (var distro in Distros())
        {
            ct.ThrowIfCancellationRequested();
            // The names are passed as arguments ("$@"), not pasted into the script.
            var present = Run(["-d", distro, "--exec", "sh", "-c", "for c in \"$@\"; do command -v \"$c\" >/dev/null 2>&1 && echo \"$c\"; done", "sh", .. commands], Encoding.UTF8);
            result.AddRange(present.Where(commands.Contains).Select(c => WslTool.Make(distro, c)));
        }

        return result;
    }

    /// <summary>Installed distros ("wsl -l -q" prints UTF-16).</summary>
    public static List<string> Distros() =>
        Run(["-l", "-q"], Encoding.Unicode)
            .Where(d => WslTool.TryParse(WslTool.Make(d, "x"), out _, out _))
            .ToList();

    private static List<string> Run(IEnumerable<string> arguments, Encoding encoding)
    {
        try
        {
            var start = new ProcessStartInfo(Wsl)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = encoding,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15_000);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Trim('\0', '\uFEFF'))
                .Where(l => l.Length > 0)
                .ToList();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("Tools", "Couldn't ask WSL for its distros or tools.", ex);
            return [];
        }
    }
}
