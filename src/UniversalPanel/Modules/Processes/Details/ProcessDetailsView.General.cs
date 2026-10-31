using System.Globalization;
using System.IO;
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

        var (info, extras, platform) = await Task.Run(() => (ProcessGeneral.Query(key), ProcessExtras.Query(key), DetectPlatform(key, process.Name, process.ImagePath)));
        if (version == _generalVersion && Target == key)
        {
            var rows = DescribeGeneral(process, info);
            rows.Add(new("Protection", extras.Protection ?? Unavailable));
            rows.Add(new("Mitigations", extras.Mitigations.Count > 0 ? string.Join(", ", extras.Mitigations) : Unavailable));
            rows.Add(new("Platform", platform ?? Unavailable));
            int dieRow = rows.Count;
            rows.Add(new(DieLabel, DieScanner.IsInstalled ? "Scanning…" : "Not installed (downloaded when the panel is built)"));
            rows.Add(new("Privileges", extras.Privileges.Count > 0 ? string.Join("\n", extras.Privileges) : Unavailable));
            rows.Add(new($"Environment{(extras.Environment is { } env ? $" ({env.Count})" : string.Empty)}",
                extras.Environment is { } vars ? string.Join("\n", vars.Select(kv => $"{kv.Key}={kv.Value}")) : Unavailable));
            GeneralList.ItemsSource = rows;

            if (DieScanner.IsInstalled)
            {
                string die = process.ImagePath is { } image ? await DescribeDieAsync(image) : Unavailable;
                if (version == _generalVersion && Target == key)
                {
                    rows[dieRow] = new(DieLabel, die);
                    GeneralList.ItemsSource = rows.ToList();
                }
            }
        }
    }

    private const string DieLabel = "Detect It Easy";
    private readonly Dictionary<(string Path, DateTime Written), string> _dieCache = [];
    private CancellationTokenSource? _dieCts;

    /// <summary>diec's findings for the image, one per line; cached per file version.</summary>
    private async Task<string> DescribeDieAsync(string image)
    {
        (string, DateTime) id;
        try
        {
            id = (image, File.GetLastWriteTimeUtc(image));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unavailable;
        }

        if (_dieCache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        // Switching processes abandons the previous scan instead of queueing behind it.
        _dieCts?.Cancel();
        var cts = _dieCts = new CancellationTokenSource();
        try
        {
            var findings = await DieScanner.ScanAsync(image, cts.Token);
            string text = findings.Count > 0 ? string.Join("\n", findings.Select(f => f.Text)) : "Nothing detected";
            _dieCache[id] = text;
            return text;
        }
        catch (DieUnavailableException ex)
        {
            return ex.Message;
        }
        catch (OperationCanceledException)
        {
            return Unavailable;
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

    /// <summary>Language / runtime from the image and loaded modules (null if neither can be read).</summary>
    private static string? DetectPlatform(ProcessKey key, string name, string? imagePath)
    {
        List<string> modules;
        try
        {
            modules = ModuleSnapshot.Capture(key, name).Select(m => m.Name).ToList();
        }
        catch (ProcessActionException)
        {
            modules = [];
        }

        return PlatformDetect.Describe(imagePath, modules);
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
