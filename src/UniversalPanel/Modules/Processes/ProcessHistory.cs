using System.Collections.ObjectModel;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Processes;

internal sealed record HistoryEvent(
    DateTime Time,
    bool Started,
    ProcessKey Key,
    string Name,
    int ParentPid,
    string ParentName,
    string? Path,
    string? CommandLine,
    TimeSpan? Lifetime)
{
    public string TimeText => Time.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
    public string Kind => Started ? "Started" : "Exited";
    public string Process => $"{Name} ({Key.Pid})";
    public string Parent => $"{ParentName} ({ParentPid})";

    public string Details
    {
        get
        {
            var lines = new List<string> { $"{Kind} {Time:G}: {Process}", $"Parent: {Parent}" };
            if (Path is not null) lines.Add(Path);
            if (CommandLine is not null) lines.Add(CommandLine);
            if (Lifetime is { } life) lines.Add($"Ran for {(life.TotalSeconds < 60 ? $"{life.TotalSeconds:0.0} s" : life.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture))}");
            return string.Join("\n", lines);
        }
    }
}

/// <summary>
/// Process start/exit log. Its own watcher (one SystemProcessInformation snapshot per second, no
/// handles beyond a command-line query for new processes) runs while anyone holds it on: the
/// Processes list while visible, and the "keep recording while hidden" setting.
/// Processes that live less than one sampling period can be missed; ETW would be needed for those.
/// </summary>
internal sealed class ProcessHistory : IDisposable
{
    public const int MaxEvents = 2000;
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(1);

    private readonly ProcessSnapshotReader _reader = new();
    private readonly Lock _gate = new();
    private Dictionary<ProcessKey, ProcessRecord>? _previous;
    private CancellationTokenSource? _cts;
    private int _holders;

    /// <summary>Newest first. UI thread only (events are marshalled to it).</summary>
    public ObservableCollection<HistoryEvent> Events { get; } = [];

    public System.Windows.Threading.Dispatcher? Dispatcher { get; set; }

    public void Hold()
    {
        lock (_gate)
        {
            if (_holders++ == 0)
            {
                var cts = new CancellationTokenSource();
                _cts = cts;
                Task.Run(() => RunAsync(cts.Token)).Forget("Processes.History");
            }
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_holders > 0 && --_holders == 0)
            {
                _cts?.Cancel();
                _cts = null;
                // Next start re-baselines: what changed while nobody watched isn't reported.
                _previous = null;
            }
        }
    }

    public void Clear() => Events.Clear();

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Period);
        try
        {
            do
            {
                Guard.Run("Processes.History.Sample", Sample);
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Sample()
    {
        List<ProcessRecord> records;
        lock (_gate)
        {
            records = _reader.Capture();
        }

        var current = new Dictionary<ProcessKey, ProcessRecord>(records.Count);
        var names = new Dictionary<int, string>(records.Count);
        foreach (var record in records)
        {
            current[record.Key] = record;
            names.TryAdd(record.Key.Pid, record.Name);
        }

        var previous = _previous;
        _previous = current;
        if (previous is null)
        {
            return; // First snapshot is the baseline.
        }

        var now = DateTime.Now;
        var events = new List<HistoryEvent>();
        foreach (var (key, record) in current)
        {
            if (!previous.ContainsKey(key))
            {
                string parent = names.TryGetValue(record.ParentPid, out var n) ? n : previous.Values.FirstOrDefault(p => p.Key.Pid == record.ParentPid)?.Name ?? "exited";
                events.Add(new HistoryEvent(
                    DateTime.FromFileTime(key.CreateTime), true, key, record.Name, record.ParentPid, parent,
                    ProcessImage.TryGetPath(key.Pid), ProcessGeneral.TryGetCommandLine(key.Pid), null));
            }
        }

        foreach (var (key, record) in previous)
        {
            if (!current.ContainsKey(key))
            {
                string parent = names.TryGetValue(record.ParentPid, out var n) ? n : previous.Values.FirstOrDefault(p => p.Key.Pid == record.ParentPid)?.Name ?? "exited";
                events.Add(new HistoryEvent(now, false, key, record.Name, record.ParentPid, parent, null, null, now - DateTime.FromFileTime(key.CreateTime)));
            }
        }

        if (events.Count == 0)
        {
            return;
        }

        events.Sort((a, b) => a.Time.CompareTo(b.Time));
        Dispatcher?.InvokeAsync(() =>
        {
            foreach (var e in events)
            {
                Events.Insert(0, e);
            }

            while (Events.Count > MaxEvents)
            {
                Events.RemoveAt(Events.Count - 1);
            }
        });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        lock (_gate)
        {
            _reader.Dispose();
        }
    }
}
