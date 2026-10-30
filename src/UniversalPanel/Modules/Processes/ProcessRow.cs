using System.ComponentModel;
using System.Runtime.CompilerServices;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Processes;

/// <summary>
/// One process in the grid. Updated in place every sample so selection and scroll position
/// survive refreshes; change notifications fire only for values that actually changed.
/// </summary>
internal sealed class ProcessRow(ProcessSample sample) : INotifyPropertyChanged
{
    private ProcessSample _sample = sample;
    private double _cpu = Math.Round(sample.CpuPercent, 1);

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProcessKey Key => _sample.Record.Key;
    public int Pid => Key.Pid;
    public string Name => _sample.Record.Name;
    public string? ImagePath => _sample.ImagePath;
    public int BasePriority => _sample.Record.BasePriority;

    public double Cpu => _cpu;

    /// <summary>Blank below 0.1 %, like System Informer, so idle processes don't add noise.</summary>
    public string CpuText => _cpu >= 0.1 ? _cpu.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) : string.Empty;

    public ulong PrivateBytes => _sample.Record.PrivateBytes;
    public string MemoryText => FormatBytes(PrivateBytes);

    public string Details =>
        $"{ImagePath ?? Name}\n" +
        $"PID {Pid} · parent {_sample.Record.ParentPid} · session {_sample.Record.SessionId}\n" +
        $"Threads {_sample.Record.Threads} · handles {_sample.Record.Handles} · working set {FormatBytes(_sample.Record.WorkingSetBytes)}";

    public void Update(ProcessSample next)
    {
        var previous = _sample;
        _sample = next;

        var cpu = Math.Round(next.CpuPercent, 1);
        if (cpu != _cpu)
        {
            _cpu = cpu;
            Raise(nameof(Cpu));
            Raise(nameof(CpuText));
        }

        if (next.Record.PrivateBytes != previous.Record.PrivateBytes)
        {
            Raise(nameof(PrivateBytes));
            Raise(nameof(MemoryText));
        }

        if (next.Record.BasePriority != previous.Record.BasePriority)
        {
            Raise(nameof(BasePriority));
        }

        // Details is only read when a tooltip opens; refresh it without per-tick churn.
        if (next.Record.Threads != previous.Record.Threads || next.Record.Handles != previous.Record.Handles)
        {
            Raise(nameof(Details));
        }
    }

    internal static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1UL << 30 => $"{bytes / (double)(1UL << 30):0.00} GB",
        >= 1UL << 20 => $"{bytes / (double)(1UL << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
