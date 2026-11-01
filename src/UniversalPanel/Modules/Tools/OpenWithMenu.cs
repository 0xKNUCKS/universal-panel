using System.Windows.Controls;
using System.Windows.Input;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Tools;

/// <summary>
/// "Open with ›" for a process or a file: every action of the user's tools that fits the target
/// (process actions need a PID, file actions a path). Shift+click runs it as administrator.
/// </summary>
internal static class OpenWithMenu
{
    public static void Fill(MenuItem menu, IModuleHost host, ToolTarget target)
    {
        menu.Items.Clear();
        _ = Guard.RunAsync("Tools.Inspect", ToolLibrary.EnsureInspectedAsync); // identities for next time

        foreach (var tool in ToolLibrary.Rows.Where(t => t.Exists).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            foreach (var action in tool.Actions.Where(a => Fits(a, target)))
            {
                var item = new MenuItem
                {
                    Header = action.Name,
                    Icon = new Image { Source = tool.Icon, Width = 16, Height = 16 },
                    ToolTip = $"{tool.Name} {action.Args}\nShift+click: run as administrator",
                };
                item.Click += (_, _) =>
                {
                    bool admin = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                    _ = Guard.RunAsync("Tools.OpenWith", () => ToolRunner.RunAsync(host, tool, action, target, admin));
                };
                menu.Items.Add(item);
            }
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = ToolLibrary.Rows.Count == 0 ? "No tools yet: add some in Tools" : "None of your tools has an action for this",
                IsEnabled = false,
            });
        }
    }

    private static bool Fits(ToolAction action, ToolTarget target) => action.Target switch
    {
        ToolTargets.Process => target.Pid is not null,
        ToolTargets.File => target.Path is not null,
        _ => false,
    };
}
