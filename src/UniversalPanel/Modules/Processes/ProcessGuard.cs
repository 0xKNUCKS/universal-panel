// Dangerous-process list and checks mirror System Informer (MIT), SystemInformer/actions.c
// PhIsDangerousProcess (System PID + a fixed list of image names) and
// PhpShowContinueMessageProcesses (also treats ProcessBreakOnTermination as critical).
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d

using System.Collections.Frozen;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Processes;

internal enum ProcessRisk
{
    Normal,

    /// <summary>Ending or suspending it can crash, log off or freeze Windows.</summary>
    Critical,

    /// <summary>The panel's own process: suspending it can never be undone from the panel.</summary>
    Self,
}

internal static class ProcessGuard
{
    private static readonly FrozenSet<string> DangerousNames = new[]
    {
        "csrss.exe", "dwm.exe", "logonui.exe", "lsass.exe", "lsm.exe",
        "services.exe", "smss.exe", "wininit.exe", "winlogon.exe",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Not in System Informer's list: suspending the shell freezes the taskbar and desktop until
    // it's resumed (docs/PITFALLS.md). Only warned about for suspend; ending it is recoverable.
    private static readonly FrozenSet<string> FreezesDesktopWhenSuspended = new[]
    {
        "explorer.exe",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static ProcessRisk Assess(ProcessRow row) => Assess(row.Key, row.Name);

    public static ProcessRisk Assess(ProcessKey key, string name)
    {
        if (key.Pid == Environment.ProcessId)
        {
            return ProcessRisk.Self;
        }

        if (key.Pid is 0 or 4 || DangerousNames.Contains(name) || ProcessControl.IsCritical(key) == true)
        {
            return ProcessRisk.Critical;
        }

        return ProcessRisk.Normal;
    }

    public static bool FreezesDesktop(ProcessRow row) => FreezesDesktop(row.Name);

    public static bool FreezesDesktop(string name) => FreezesDesktopWhenSuspended.Contains(name);
}
