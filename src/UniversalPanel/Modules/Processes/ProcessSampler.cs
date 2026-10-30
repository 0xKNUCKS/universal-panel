// CPU accounting follows System Informer's time-based mode (MIT), SystemInformer/procprv.c:
// per-process usage = (kernel + user delta) / system total time delta, where the system total is
// the sum over all processors of kernel (which includes idle) + user time. GetSystemTimes returns
// exactly those sums across every processor group.
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d

using UniversalPanel.Diagnostics;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Processes;

internal sealed record ProcessSample(ProcessRecord Record, double CpuPercent, string? ImagePath, System.Windows.Media.ImageSource? Icon);

internal sealed record ThreadSample(ThreadRecord Record, double CpuPercent, ulong? StartAddress);

/// <param name="Focus">The process whose threads are in <paramref name="FocusedThreads"/>.</param>
/// <param name="FocusedThreads">Null when nothing is focused or the focused process has exited.</param>
internal sealed record ProcessFrame(
    IReadOnlyList<ProcessSample> Processes,
    double SystemCpuPercent,
    ProcessKey? Focus,
    IReadOnlyList<ThreadSample>? FocusedThreads);

/// <summary>
/// Samples all processes on a background thread while started. Stopped = no timers, no work.
/// </summary>
internal sealed class ProcessSampler : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WarmUp = TimeSpan.FromMilliseconds(400);

    private readonly ProcessSnapshotReader _reader = new();
    private readonly Dictionary<ProcessKey, long> _lastCpuTime = [];
    private readonly Dictionary<ProcessKey, (string? Path, System.Windows.Media.ImageSource? Icon)> _images = [];
    private readonly Dictionary<ThreadKey, long> _lastThreadCpu = [];
    private readonly Dictionary<ThreadKey, ulong?> _startAddresses = [];
    private ProcessKey? _threadsOf;
    private readonly Lock _sampleLock = new();
    private ulong _lastSystemTotal;
    private ulong _lastSystemIdle;
    private CancellationTokenSource? _cts;

    /// <summary>Raised on a background thread after every sample.</summary>
    public event Action<ProcessFrame>? Sampled;

    /// <summary>
    /// Process whose threads are sampled too (from the same snapshot), or null. Settable from any
    /// thread; takes effect on the next tick.
    /// </summary>
    public ProcessKey? Focus { get; set; }

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        Task.Run(() => RunAsync(cts.Token)).Forget("Processes.Sampler");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Baseline first so the first published frame already has real CPU deltas instead of
            // an average over however long the panel was hidden.
            lock (_sampleLock)
            {
                ResetDeltas();
                Sample(publish: false);
            }

            await Task.Delay(WarmUp, ct);

            using var timer = new PeriodicTimer(Interval);
            do
            {
                lock (_sampleLock)
                {
                    Guard.Run("Processes.Sample", () => Sample(publish: !ct.IsCancellationRequested));
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    private void ResetDeltas()
    {
        _lastCpuTime.Clear();
        _lastThreadCpu.Clear();
        _lastSystemTotal = 0;
        _lastSystemIdle = 0;
    }

    private void Sample(bool publish)
    {
        var focus = Focus;
        var records = _reader.Capture(focus, out var threadRecords);
        var (systemTotal, systemIdle) = ProcessImage.GetSystemTimes();

        ulong totalDelta = _lastSystemTotal == 0 ? 0 : systemTotal - _lastSystemTotal;
        ulong idleDelta = _lastSystemIdle == 0 ? 0 : systemIdle - _lastSystemIdle;
        _lastSystemTotal = systemTotal;
        _lastSystemIdle = systemIdle;

        var samples = new List<ProcessSample>(records.Count);
        var seen = new HashSet<ProcessKey>(records.Count);

        foreach (var record in records)
        {
            seen.Add(record.Key);

            double cpu = 0;
            if (totalDelta > 0 && _lastCpuTime.TryGetValue(record.Key, out var previous))
            {
                cpu = Math.Max(0, record.CpuTime100ns - previous) * 100.0 / totalDelta;
            }

            _lastCpuTime[record.Key] = record.CpuTime100ns;

            // Path never changes for a process instance: query once, cache by PID + create time.
            // Path and icon never change for a process instance: resolve once, cache by PID +
            // create time. Icons are also cached per path, so 13 claude.exe extract one icon.
            if (!_images.TryGetValue(record.Key, out var image))
            {
                var path = ProcessImage.TryGetPath(record.Key.Pid);
                _images[record.Key] = image = (path, ProcessIcons.Get(path));
            }

            samples.Add(new ProcessSample(record, cpu, image.Path, image.Icon));
        }

        Prune(_lastCpuTime, seen);
        Prune(_images, seen);

        var threads = SampleThreads(focus, threadRecords, totalDelta);

        if (publish)
        {
            double systemCpu = totalDelta > 0 ? 100.0 * (1 - (double)idleDelta / totalDelta) : 0;
            Sampled?.Invoke(new ProcessFrame(samples, Math.Clamp(systemCpu, 0, 100), focus, threads));
        }
    }

    // Same CPU formula as processes (System Informer computes thread usage the same way).
    private List<ThreadSample>? SampleThreads(ProcessKey? focus, List<ThreadRecord>? records, ulong totalDelta)
    {
        if (focus != _threadsOf)
        {
            _threadsOf = focus;
            _lastThreadCpu.Clear();
            _startAddresses.Clear();
        }

        if (records is null)
        {
            return null;
        }

        var samples = new List<ThreadSample>(records.Count);
        var seen = new HashSet<ThreadKey>(records.Count);
        foreach (var record in records)
        {
            var key = new ThreadKey(record.Tid, record.CreateTime);
            seen.Add(key);

            double cpu = 0;
            if (totalDelta > 0 && _lastThreadCpu.TryGetValue(key, out var previous))
            {
                cpu = Math.Max(0, record.CpuTime100ns - previous) * 100.0 / totalDelta;
            }

            _lastThreadCpu[key] = record.CpuTime100ns;

            // One query per thread instance, then cached (the start address never changes).
            if (!_startAddresses.TryGetValue(key, out var start))
            {
                _startAddresses[key] = start = ThreadControl.TryGetStartAddress(key);
            }

            samples.Add(new ThreadSample(record, cpu, start));
        }

        Prune(_lastThreadCpu, seen);
        Prune(_startAddresses, seen);
        return samples;
    }

    private static void Prune<TKey, T>(Dictionary<TKey, T> map, HashSet<TKey> alive)
        where TKey : notnull
    {
        if (map.Count == alive.Count)
        {
            return;
        }

        foreach (var key in map.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            map.Remove(key);
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_sampleLock)
        {
            _reader.Dispose();
        }
    }
}
