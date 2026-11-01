using System.ComponentModel;
using System.Runtime.CompilerServices;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Tools;

/// <summary>One action on the tool's Edit page, with a live preview of the command it would run.</summary>
internal sealed class EditableAction : INotifyPropertyChanged
{
    private readonly Func<string> _toolPath;
    private string _name, _target, _run, _args;
    private bool _quote, _isExpanded;

    public EditableAction(ToolAction action, Func<string> toolPath, bool expanded)
    {
        Id = action.Id.Length > 0 ? action.Id : "custom-" + Guid.NewGuid().ToString("N")[..8];
        (_name, _target, _run, _args, _quote) = (action.Name, action.Target, action.Run, action.Args, action.Quote);
        _toolPath = toolPath;
        _isExpanded = expanded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>What the action is given when it runs (the "Runs on" box).</summary>
    public static IReadOnlyList<Choice> Targets { get; } =
    [
        new(ToolTargets.Process, "A process"),
        new(ToolTargets.File, "A file"),
        new(ToolTargets.None, "Nothing"),
    ];

    public static IReadOnlyList<Choice> RunModes { get; } =
    [
        new(ToolRunModes.Window, "The tool's own window"),
        new(ToolRunModes.Console, "A console window that shows the output"),
    ];

    public string Id { get; }

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>The card header: the name, or a hint while it's empty.</summary>
    public string Title => string.IsNullOrWhiteSpace(_name) ? "(unnamed action)" : _name.Trim();

    public string Target { get => _target; set => Set(ref _target, value); }

    public string Run { get => _run; set => Set(ref _run, value); }

    public string Args { get => _args; set => Set(ref _args, value); }

    /// <summary>Bound to "Pass arguments exactly as typed" (the inverse of Quote).</summary>
    public bool AsTyped { get => !_quote; set => Set(ref _quote, !value); }

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    /// <summary>The command against a sample target (PID 1234, a file under Program Files).</summary>
    public string Preview
    {
        get
        {
            try
            {
                return ToolCommand.Build(_toolPath(), ToAction(), ToolTarget.SampleFor(Target), toWsl: null).Display;
            }
            catch (ToolCommandException ex)
            {
                return "⚠ " + ex.Message;
            }
        }
    }

    public ToolAction ToAction() => new() { Id = Id, Name = Name.Trim(), Target = Target, Run = Run, Args = Args.Trim(), Quote = _quote };

    /// <summary>The tool path changed (the preview depends on it).</summary>
    public void RefreshPreview() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
        }
    }

    /// <summary>The placeholders the Insert buttons offer, with what each becomes.</summary>
    public static IReadOnlyList<Choice> Placeholders { get; } =
    [
        new("{pid}", "Process ID"),
        new("{path}", "Full path"),
        new("{dir}", "Folder"),
        new("{name}", "File name"),
    ];

    public sealed record Choice(string Value, string Label);
}
