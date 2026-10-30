using System.Windows;
using System.Windows.Controls;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;
using Windows.Win32.System.Threading;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace UniversalPanel.Modules.Processes;

internal sealed partial class ProcessesView
{
    // Base priority reported by the kernel for each class (what SYSTEM_PROCESS_INFORMATION returns).
    private static readonly (string Label, PROCESS_CREATION_FLAGS Class, int BasePriority)[] PriorityClasses =
    [
        ("Realtime", PROCESS_CREATION_FLAGS.REALTIME_PRIORITY_CLASS, 24),
        ("High", PROCESS_CREATION_FLAGS.HIGH_PRIORITY_CLASS, 13),
        ("Above normal", PROCESS_CREATION_FLAGS.ABOVE_NORMAL_PRIORITY_CLASS, 10),
        ("Normal", PROCESS_CREATION_FLAGS.NORMAL_PRIORITY_CLASS, 8),
        ("Below normal", PROCESS_CREATION_FLAGS.BELOW_NORMAL_PRIORITY_CLASS, 6),
        ("Idle", PROCESS_CREATION_FLAGS.IDLE_PRIORITY_CLASS, 4),
    ];

    private void BuildPriorityMenu()
    {
        foreach (var (label, priorityClass, basePriority) in PriorityClasses)
        {
            // IsCheckable so the template draws the check; the state is reset from the row on every
            // open (UpdatePriorityMenu), so the click's own toggle never sticks.
            var item = new MenuItem { Header = label, Tag = basePriority, IsCheckable = true };
            item.Click += (_, _) =>
            {
                if (Selected is { } row)
                {
                    _ = Guard.RunAsync("Processes.Priority", () => SetPriorityAsync(row, label, priorityClass));
                }
            };
            PriorityMenu.Items.Add(item);
        }
    }

    /// <summary>Checks the row's current class; System can't be changed, so the submenu is disabled.</summary>
    private void UpdatePriorityMenu(ProcessRow row)
    {
        PriorityMenu.IsEnabled = row.Pid is not (0 or 4);
        foreach (MenuItem item in PriorityMenu.Items)
        {
            item.IsChecked = (int)item.Tag == row.BasePriority;
        }
    }

    private async Task SetPriorityAsync(ProcessRow row, string label, PROCESS_CREATION_FLAGS priorityClass)
    {
        if (priorityClass == PROCESS_CREATION_FLAGS.REALTIME_PRIORITY_CLASS)
        {
            var message = $"Realtime priority lets {row.Name} starve input, audio and the system itself if it spins. Windows gives High instead unless the panel runs as administrator.";
            if (!await Host.ConfirmAsync("Set realtime priority?", message, "Set realtime", destructive: true))
            {
                return;
            }
        }

        Execute($"{row.Name}: priority set to {label}", () => ProcessControl.SetPriority(row.Key, row.Name, priorityClass));
    }
}
