using System.IO;
using System.Windows.Controls;
using UniversalPanel.Shell;

namespace UniversalPanel.Modules.Terminal;

/// <summary>
/// "Terminal ›" on a process or a module: a shell opened in its folder, or its path / PID pasted
/// into the open terminal (never followed by Enter, so nothing runs until you press it).
/// </summary>
internal static class TerminalMenu
{
    public static void Fill(MenuItem menu, IModuleHost host, int? pid, string? path)
    {
        menu.Items.Clear();
        string? folder = path is not null && ToolEntry.IsSafePath(path) ? Path.GetDirectoryName(path) : null;
        if (folder is not null)
        {
            if (TerminalProfiles.Cached() is { } profiles)
            {
                foreach (var profile in profiles)
                {
                    var item = new MenuItem
                    {
                        Header = profile.Name,
                        Icon = new Image { Source = profile.Icon, Width = 16, Height = 16 },
                        ToolTip = folder,
                    };
                    item.Click += (_, _) => TerminalRequests.Send(host, view => view.OpenIn(profile, folder));
                    menu.Items.Add(item);
                }
            }
            else
            {
                menu.Items.Add(new MenuItem { Header = "Looking for shells…", IsEnabled = false });
            }

            menu.Items.Add(new Separator());
        }

        if (path is not null)
        {
            var paste = new MenuItem { Header = "Paste path" };
            paste.Click += (_, _) => TerminalRequests.Send(host, view => view.Paste(path, isPath: true));
            menu.Items.Add(paste);
        }

        if (pid is { } id)
        {
            var paste = new MenuItem { Header = "Paste PID" };
            paste.Click += (_, _) => TerminalRequests.Send(host, view => view.Paste(id.ToString(System.Globalization.CultureInfo.InvariantCulture), isPath: false));
            menu.Items.Add(paste);
        }
    }
}
