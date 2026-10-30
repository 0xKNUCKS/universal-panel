using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Processes.Details;

/// <summary>One thread in the details grid; updated in place like <see cref="ProcessRow"/>.</summary>
internal sealed class ThreadRow : INotifyPropertyChanged
{
    private ThreadSample _sample;
    private double _cpu;
    private string _state;
    private string _start;

    public ThreadRow(ThreadSample sample, Func<ulong?, string> describeAddress)
    {
        _sample = sample;
        _cpu = Math.Round(sample.CpuPercent, 1);
        _state = ThreadStates.Short(sample.Record.State, sample.Record.WaitReason);
        _start = describeAddress(sample.StartAddress);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ThreadKey Key => new(_sample.Record.Tid, _sample.Record.CreateTime);
    public int Tid => _sample.Record.Tid;
    public ulong? StartAddress => _sample.StartAddress;
    public bool IsSuspended => ThreadStates.IsSuspended(_sample.Record.State, _sample.Record.WaitReason);

    public double Cpu => _cpu;
    public string CpuText => _cpu >= 0.1 ? _cpu.ToString("0.0", CultureInfo.CurrentCulture) : string.Empty;
    public string State => _state;
    public string Start => _start;

    /// <summary>Built when the tooltip opens.</summary>
    public string Details =>
        $"TID {Tid} · {ThreadStates.Describe(_sample.Record.State, _sample.Record.WaitReason)}\n" +
        $"Priority {_sample.Record.Priority} (base {_sample.Record.BasePriority})\n" +
        $"Context switches {_sample.Record.ContextSwitches:N0} · created {DateTime.FromFileTime(_sample.Record.CreateTime):G}\n" +
        $"Start {_start}";

    public void Update(ThreadSample next)
    {
        _sample = next;

        var cpu = Math.Round(next.CpuPercent, 1);
        if (cpu != _cpu)
        {
            _cpu = cpu;
            Raise(nameof(Cpu));
            Raise(nameof(CpuText));
        }

        var state = ThreadStates.Short(next.Record.State, next.Record.WaitReason);
        if (state != _state)
        {
            _state = state;
            Raise(nameof(State));
        }
    }

    /// <summary>Re-resolves the start address text, e.g. once the module list arrives.</summary>
    public void RefreshStart(Func<ulong?, string> describeAddress)
    {
        var start = describeAddress(_sample.StartAddress);
        if (start != _start)
        {
            _start = start;
            Raise(nameof(Start));
        }
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
