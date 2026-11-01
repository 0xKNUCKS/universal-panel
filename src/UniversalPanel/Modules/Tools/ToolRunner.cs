using UniversalPanel.Native;
using UniversalPanel.Shell;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Tools;

/// <summary>
/// Runs a tool's action (from the Tools tab or "Open with" in Processes) with the same checks
/// everywhere: the tool must still be there, a broken or foreign signature asks first, an elevated
/// panel confirms "Run as administrator" (Windows won't), and launches are unelevated otherwise.
/// </summary>
internal static class ToolRunner
{
    /// <summary>Opens the tool itself: GUI tools start, command-line tools show their usage in a
    /// console, WSL tools open a shell in their distro.</summary>
    public static Task OpenAsync(IModuleHost host, ToolRow tool, bool asAdministrator = false)
    {
        var action = new ToolAction { Name = tool.Name, Target = ToolTargets.None, Run = tool.IsConsoleTool ? ToolRunModes.Console : ToolRunModes.Window };
        if (WslTool.TryParse(tool.Path, out var distro, out _))
        {
            // A shell in the distro is more useful than one tool's usage text.
            var shell = new ToolCommand(System.IO.Path.Combine(Environment.SystemDirectory, "wsl.exe"), ["-d", distro],
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Console: true);
            return StartCheckedAsync(host, tool, shell, asAdministrator);
        }

        return RunAsync(host, tool, action, new ToolTarget(null, null), asAdministrator);
    }

    public static async Task RunAsync(IModuleHost host, ToolRow tool, ToolAction action, ToolTarget target, bool asAdministrator = false)
    {
        ToolCommand command;
        try
        {
            // WSL tools convert paths with the distro's wslpath, which starts WSL: off the UI thread.
            command = await Task.Run(() => ToolCommand.Build(tool.Path, action, target, WslConverter(tool.Path)));
        }
        catch (ToolCommandException ex)
        {
            host.ShowNotice($"Couldn't run {action.Name}", ex.Message, InfoBarSeverity.Warning);
            return;
        }

        await StartCheckedAsync(host, tool, command, asAdministrator);
    }

    private static Func<string, string>? WslConverter(string toolPath) =>
        WslTool.TryParse(toolPath, out var distro, out _) ? p => ToolCommand.WslPath(distro, p) : null;

    private static async Task StartCheckedAsync(IModuleHost host, ToolRow tool, ToolCommand command, bool asAdministrator)
    {
        if (!await CanStartAsync(host, tool))
        {
            return;
        }

        // An elevated panel gets no UAC prompt, so this dialog is the only check left.
        if (asAdministrator && Elevation.IsElevated && !await host.ConfirmAsync(
                $"Run {tool.Name} as administrator?",
                $"{command.Display}\n\n{(string.IsNullOrEmpty(tool.Identity.Detail) ? "Not a recognized tool." : tool.Identity.Detail)}\n\nThe panel is elevated, so Windows won't ask again.",
                "Run as administrator", destructive: false))
        {
            return;
        }

        // Command-line tools run inside the panel's console runner, which keeps the window open.
        string program = command.Program, arguments = command.ArgumentLine;
        if (command.Console)
        {
            program = Environment.ProcessPath!;
            arguments = ConsoleRunner.ArgumentsFor(command);
        }

        try
        {
            if (asAdministrator)
            {
                using (host.SuppressAutoHide())
                {
                    ToolLauncher.RunAsAdministrator(program, arguments, command.WorkingDirectory);
                }
            }
            else
            {
                ToolLauncher.Open(program, arguments, command.WorkingDirectory);
            }
        }
        catch (ProcessActionException ex)
        {
            host.ShowNotice($"Couldn't start {tool.Name}", ex.Message, InfoBarSeverity.Warning);
        }
    }

    /// <summary>The file must still be there; a broken or foreign signature needs a yes first.</summary>
    public static async Task<bool> CanStartAsync(IModuleHost host, ToolRow tool)
    {
        tool.Apply(await Task.Run(tool.Inspect)); // the file may have changed since it was listed
        if (!tool.Exists)
        {
            host.ShowNotice($"{tool.Name} isn't there anymore", tool.Path, InfoBarSeverity.Warning);
            return false;
        }

        return tool.Identity.Trust != ToolTrust.Warning || await host.ConfirmAsync(
            $"{tool.Name} doesn't look official",
            $"{tool.Identity.Detail}\n\n{tool.Path}",
            "Open anyway", destructive: true);
    }
}
