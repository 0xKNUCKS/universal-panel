using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Controls;
using UniversalPanel.Modules.Processes;
using UniversalPanel.Native;
using UniversalPanel.Shell;

namespace UniversalPanel.Modules.Terminal;

/// <summary>
/// The "Selection" menu: what to do with a PID or a path selected in a terminal's output. Output is
/// untrusted, so the text is only matched against a running process or an existing local file;
/// nothing in it is run.
/// </summary>
internal static class TerminalSelection
{
    public static void Fill(ContextMenu menu, IModuleHost host, string selected, TerminalProfile profile)
    {
        menu.Items.Clear();
        string text = selected.Trim().Trim('"', '\'', '`').Trim();

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0 && IsRunning(pid))
        {
            var show = new MenuItem { Header = $"Show process {pid}" };
            show.Click += (_, _) => ProcessRequests.Show(host, pid);
            menu.Items.Add(show);

            var openWith = new MenuItem { Header = "Open with" };
            Tools.OpenWithMenu.Fill(openWith, host, ToolTarget.ForProcess(pid, ProcessImage.TryGetPath(pid)));
            menu.Items.Add(openWith);
            return;
        }

        if (ToWindowsPath(text, profile) is { } path && ToolEntry.IsSafePath(path) && (File.Exists(path) || Directory.Exists(path)))
        {
            var location = new MenuItem { Header = "Open file location", ToolTip = path };
            location.Click += (_, _) => Diagnostics.Guard.Run("Terminal.OpenLocation", () => ClipboardText.Reveal(host, path));
            menu.Items.Add(location);

            if (File.Exists(path))
            {
                var openWith = new MenuItem { Header = "Open with" };
                Tools.OpenWithMenu.Fill(openWith, host, ToolTarget.ForFile(path));
                menu.Items.Add(openWith);
            }

            return;
        }

        menu.Items.Add(new MenuItem { Header = "Select a PID or a path first", IsEnabled = false });
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A Windows path as is; in a WSL tab, /mnt/c/... becomes C:\... (other Linux paths have no local Windows path).</summary>
    private static string? ToWindowsPath(string text, TerminalProfile profile)
    {
        if (text.Length is 0 or > 1024 || text.Any(char.IsControl))
        {
            return null;
        }

        if (profile.Distro is not null && text.StartsWith("/mnt/", StringComparison.Ordinal)
            && text.Length >= 6 && char.IsAsciiLetter(text[5]) && (text.Length == 6 || text[6] == '/'))
        {
            return $"{char.ToUpperInvariant(text[5])}:\\{text[Math.Min(7, text.Length)..].Replace('/', '\\')}";
        }

        return text;
    }
}
