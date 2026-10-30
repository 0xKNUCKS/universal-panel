// CPU accounting follows System Informer's time-based mode (MIT), SystemInformer/procprv.c:
// per-process usage = (kernel + user delta) / system total time delta, where the system total is
// the sum over all processors of kernel (which includes idle) + user time. GetSystemTimes returns
// exactly those sums across every processor group.
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d

using UniversalPanel.Diagnostics;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Processes;

internal sealed record ProcessSample(ProcessRecord Record, double CpuPercent, string? ImagePath);

internal sealed record ProcessFrame(IReadOnlyList<ProcessSample> Processes, double SystemCpuPercent);

/// <summary>
/// Samples all processes on a background thread while started. Stopped = no timers, no work.
/// </summary>
internal sealed class ProcessSampler : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WarmUp = TimeSpan.FromMilliseconds(400);

    private readonly ProcessSnapshotReader _reader = new();
    private readonly Dictionary<ProcessKey, long> _lastCpuTime = [];
    private readonly Dictionary<ProcessKey, string?> _paths = [];
    private readonly Lock _sampleLock = new();
    private ulong _lastSystemTotal;
    private ulong _lastSystemIdle;
    private CancellationTokenSource? _cts;

    /// <summary>Raised on a background thread after every sample.</summary>
    public event Action<ProcessFrame>? Sampled;

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
        _lastSystemTotal = 0;
        _lastSystemIdle = 0;
    }

    private void Sample(bool publish)
    {
        var records = _reader.Capture();
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
            if (!_paths.TryGetValue(record.Key, out var path))
            {
                _paths[record.Key] = path = ProcessImage.TryGetPath(record.Key.Pid);
            }

            samples.Add(new ProcessSample(record, cpu, path));
        }

        Prune(_lastCpuTime, seen);
        Prune(_paths, seen);

        if (publish)
        {
            double systemCpu = totalDelta > 0 ? 100.0 * (1 - (double)idleDelta / totalDelta) : 0;
            Sampled?.Invoke(new ProcessFrame(samples, Math.Clamp(systemCpu, 0, 100)));
        }
    }

    private static void Prune<T>(Dictionary<ProcessKey, T> map, HashSet<ProcessKey> alive)
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
