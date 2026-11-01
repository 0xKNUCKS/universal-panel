using System.IO;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Terminal;

/// <summary>
/// Checks the terminal control before anything from it is loaded. It comes from a community
/// repackage of Windows Terminal's WPF control (Microsoft doesn't publish one on NuGet), so the
/// two binaries must be validly Authenticode-signed by Microsoft; packages.lock.json additionally
/// pins the package's content hash. Kept free of Microsoft.Terminal.Wpf types on purpose: calling
/// this must not load that assembly.
/// </summary>
internal static class TerminalRenderer
{
    private static readonly string[] Files = ["Microsoft.Terminal.Wpf.dll", "Microsoft.Terminal.Control.dll"];
    private static string? _problem;
    private static bool _checked;

    /// <summary>Null if the control can be used; otherwise what's wrong.</summary>
    public static string? Problem()
    {
        if (_checked)
        {
            return _problem;
        }

        _checked = true;
        foreach (var file in Files)
        {
            string path = Path.Combine(AppContext.BaseDirectory, file);
            var trust = File.Exists(path) ? FileTrust.Verify(path) : null;
            if (trust is not { IsTrusted: true } || trust.Signer?.Contains("Microsoft Corporation", StringComparison.Ordinal) != true)
            {
                _problem = $"{file} is missing or not signed by Microsoft.";
                Log.Warn("Terminal", _problem);
                break;
            }
        }

        return _problem;
    }
}
