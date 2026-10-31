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

        var (info, extras, dotnet) = await Task.Run(() => (ProcessGeneral.Query(key), ProcessExtras.Query(key), DetectDotNet(key, process.Name)));
        if (version == _generalVersion && Target == key)
        {
            var rows = DescribeGeneral(process, info);
            rows.Add(new("Protection", extras.Protection ?? Unavailable));
            rows.Add(new("Mitigations", extras.Mitigations.Count > 0 ? string.Join(", ", extras.Mitigations) : Unavailable));
            rows.Add(new(".NET", dotnet ?? Unavailable));
            rows.Add(new("Privileges", extras.Privileges.Count > 0 ? string.Join("\n", extras.Privileges) : Unavailable));
            rows.Add(new($"Environment{(extras.Environment is { } env ? $" ({env.Count})" : string.Empty)}",
                extras.Environment is { } vars ? string.Join("\n", vars.Select(kv => $"{kv.Key}={kv.Value}")) : Unavailable));
            GeneralList.ItemsSource = rows;
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

    /// <summary>Which .NET runtime is loaded, from the module list (null if it can't be read).</summary>
    private static string? DetectDotNet(ProcessKey key, string name)
    {
        try
        {
            var modules = ModuleSnapshot.Capture(key, name);
            bool core = modules.Any(m => m.Name.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase));
            bool framework = modules.Any(m => m.Name.Equals("clr.dll", StringComparison.OrdinalIgnoreCase));
            return core ? ".NET (CoreCLR)" : framework ? ".NET Framework (CLR)" : "Not loaded";
        }
        catch (ProcessActionException)
        {
            return null;
        }
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
