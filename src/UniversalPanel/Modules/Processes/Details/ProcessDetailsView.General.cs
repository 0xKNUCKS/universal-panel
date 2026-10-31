using System.Globalization;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Processes.Details;

// General tab: identity, parent, start time, user, command line. Values that need the process or
// its token are read off the UI thread; anything the panel isn't allowed to read shows as "—".
internal sealed partial class ProcessDetailsView
{
    private const string Unavailable = "—";
    private int _generalVersion;

    private async Task LoadGeneralAsync(ProcessRow process)
    {
        int version = ++_generalVersion;
        var key = process.Key;
        GeneralList.ItemsSource = DescribeGeneral(process, info: null);

        var info = await Task.Run(() => ProcessGeneral.Query(key));
        if (version == _generalVersion && Target == key)
        {
            GeneralList.ItemsSource = DescribeGeneral(process, info);
        }
    }

    private List<KeyValuePair<string, string>> DescribeGeneral(ProcessRow process, ProcessGeneralInfo? info)
    {
        string Pending(string? value) => info is null ? "Loading…" : value ?? Unavailable;

        string started = Unavailable;
        if (process.Key.CreateTime > 0)
        {
            var at = DateTime.FromFileTime(process.Key.CreateTime);
            started = $"{at.ToString("G", CultureInfo.CurrentCulture)} ({Ago(DateTime.Now - at)})";
        }

        string integrity = Pending(info?.Integrity);
        if (info?.Elevated == true)
        {
            integrity += ", elevated";
        }

        return
        [
            new("Name", process.Name),
            new("PID", process.Pid.ToString(CultureInfo.InvariantCulture)),
            new("Parent", DescribeParent(process)),
            new("Started", started),
            new("User", Pending(info?.User)),
            new("Integrity", integrity),
            new("Architecture", Pending(info?.Architecture)),
            new("Priority", PriorityName(process.BasePriority)),
            new("Path", process.ImagePath ?? Unavailable),
            new("Command line", Pending(info?.CommandLine)),
        ];
    }

    /// <summary>Same parent rule as the tree: the parent must not be newer than the child.</summary>
    private string DescribeParent(ProcessRow process)
    {
        var parent = _lastProcesses.FirstOrDefault(p =>
            p.Record.Key.Pid == process.ParentPid
            && p.Record.Key != process.Key
            && (p.Record.Key.Pid is 0 or 4 || p.Record.Key.CreateTime <= process.Key.CreateTime));
        return parent is null
            ? $"Non-existent process ({process.ParentPid})"
            : $"{parent.Record.Name} ({process.ParentPid})";
    }

    private static string PriorityName(int basePriority) => basePriority switch
    {
        >= 24 => "Realtime",
        >= 13 => "High",
        >= 10 => "Above normal",
        >= 8 => "Normal",
        >= 6 => "Below normal",
        _ => "Idle",
    };

    private static string Ago(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes} min ago",
        { TotalDays: < 1 } => $"{(int)span.TotalHours} h {span.Minutes} min ago",
        _ => $"{(int)span.TotalDays} d {span.Hours} h ago",
    };
}
