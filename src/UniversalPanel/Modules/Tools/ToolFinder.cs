using System.IO;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Tools;

/// <summary>
/// Looks for known tools in the usual places (Program Files, per-user Programs, Documents,
/// Desktop, Downloads) and keeps only files <see cref="ToolCatalog"/> recognizes, one per tool
/// and version. Links (junctions, symlinks) aren't followed.
/// </summary>
internal static class ToolFinder
{
    // In order of preference when one install has several entry points (x96dbg picks 32/64-bit).
    private static readonly string[] Candidates =
    [
        "x96dbg.exe", "x64dbg.exe", "ida64.exe", "ida.exe", "ghidraRun.bat", "die.exe", "HxD.exe", "HxD64.exe",
        "SystemInformer.exe", "ProcessHacker.exe", "PE-bear.exe", "CFF Explorer.exe", "binaryninja.exe",
        "dnSpy.exe", "ILSpy.exe", "imhex-gui.exe", "imhex.exe", "Cutter.exe", "Procmon64.exe", "Procmon.exe",
        "procexp64.exe", "procexp.exe", "Wireshark.exe", "Postman.exe",
    ];

    public static List<string> Find(CancellationToken ct)
    {
        var priority = Candidates.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 5,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        var found = new List<(string Path, ToolIdentity Identity, int Priority)>();
        foreach (var root in Roots())
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (priority.TryGetValue(Path.GetFileName(path), out int rank))
                {
                    var identity = ToolCatalog.Identify(path);
                    if (identity.Trust != ToolTrust.Unknown)
                    {
                        found.Add((path, identity, rank));
                    }
                }
            }
        }

        // One entry per tool + version: preferred entry point, then the shortest path (x86 / x32
        // copies live in subfolders).
        return found
            .GroupBy(f => (f.Identity.Name, f.Identity.Version))
            .Select(g => g.OrderBy(f => f.Priority).ThenBy(f => f.Path.Length).First().Path)
            .ToList();
    }

    private static IEnumerable<string> Roots()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(profile, "Downloads"),
            Path.Combine(Path.GetPathRoot(profile) ?? "C:\\", "Tools"),
        ];
        return roots.Where(r => r.Length > 0 && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
