using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;

namespace UniversalPanel.Shell;

/// <summary>
/// Start with Windows, the two ways docs/PITFALLS.md describes:
///  - Normal: a per-user Run key value (no elevation; the Run key can't start anything elevated).
///  - Elevated: a logon scheduled task with "run with highest privileges", so the panel starts
///    elevated without a UAC prompt at every logon. Creating or deleting it needs one UAC prompt.
/// Only one of the two exists at a time. All commands use absolute paths and argument lists.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "UniversalPanel";
    private const string TaskName = "UniversalPanel (elevated at logon)";
    private const int ERROR_CANCELLED = 1223;

    private static string Executable => Environment.ProcessPath ?? throw new InvalidOperationException("Process path unknown.");

    private static string SchTasks => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    /// <summary>Applies the mode; returns null on success or a user-facing error.</summary>
    public static string? Apply(string mode)
    {
        try
        {
            switch (mode)
            {
                case "Normal":
                    if (!DeleteTask(out var deleteError))
                    {
                        return deleteError;
                    }

                    SetRunKey(true);
                    return null;

                case "Elevated":
                    if (!CreateTask(out var createError))
                    {
                        return createError;
                    }

                    SetRunKey(false);
                    return null;

                default:
                    SetRunKey(false);
                    return DeleteTask(out var error) ? null : error;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or Win32Exception)
        {
            Log.Warn("Startup", $"Couldn't apply start-with-Windows mode {mode}.", ex);
            return $"Couldn't change the startup setting: {ex.Message}";
        }
    }

    private static void SetRunKey(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(RunValueName, $"\"{Executable}\"", RegistryValueKind.String);
        }
        else if (key.GetValue(RunValueName) is not null)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    private static bool CreateTask(out string? error)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Universal Panel elevated when you sign in (created from its Settings page).</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(Executable)}</Command>
                </Exec>
              </Actions>
            </Task>
            """;

        // In the user's own app-data folder (user-only ACL), deleted right after import.
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalPanel", "startup-task.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        try
        {
            return RunSchTasks(["/Create", "/TN", TaskName, "/XML", file, "/F"], out error);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static bool DeleteTask(out string? error)
    {
        error = null;
        // Querying needs no elevation; only prompt for UAC when there's actually a task to delete.
        if (!TaskExists())
        {
            return true;
        }

        return RunSchTasks(["/Delete", "/TN", TaskName, "/F"], out error);
    }

    private static bool TaskExists()
    {
        var info = new ProcessStartInfo(SchTasks) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("/Query");
        info.ArgumentList.Add("/TN");
        info.ArgumentList.Add(TaskName);
        using var process = Process.Start(info)!;
        process.WaitForExit(10_000);
        return process.HasExited && process.ExitCode == 0;
    }

    /// <summary>Runs schtasks, elevated (one UAC prompt) unless the panel already is.</summary>
    private static bool RunSchTasks(string[] arguments, out string? error)
    {
        var info = new ProcessStartInfo(SchTasks)
        {
            UseShellExecute = !Elevation.IsElevated, // "runas" needs ShellExecute
            Verb = Elevation.IsElevated ? string.Empty : "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info)!;
            process.WaitForExit(30_000);
            if (process.HasExited && process.ExitCode == 0)
            {
                error = null;
                return true;
            }

            error = $"Task Scheduler refused the change (exit code {(process.HasExited ? process.ExitCode : -1)}).";
            return false;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            error = "Cancelled at the administrator prompt; nothing was changed.";
            return false;
        }
    }
}
