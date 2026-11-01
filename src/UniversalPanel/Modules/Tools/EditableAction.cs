using System.ComponentModel;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Tools;

/// <summary>One action on the tool's Edit page, with a live preview of the command it would run.</summary>
internal sealed class EditableAction : INotifyPropertyChanged
{
    private readonly Func<string> _toolPath;
    private string _name, _target, _run, _args;

    public EditableAction(ToolAction action, Func<string> toolPath)
    {
        Id = action.Id.Length > 0 ? action.Id : "custom-" + Guid.NewGuid().ToString("N")[..8];
        (_name, _target, _run, _args) = (action.Name, action.Target, action.Run, action.Args);
        _toolPath = toolPath;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static IReadOnlyList<Choice> Targets { get; } =
    [
        new(ToolTargets.File, "A file ({path})"),
        new(ToolTargets.Process, "A process ({pid})"),
        new(ToolTargets.None, "Nothing"),
    ];

    public static IReadOnlyList<Choice> RunModes { get; } =
    [
        new(ToolRunModes.Window, "Its own window"),
        new(ToolRunModes.Console, "Console (shows output)"),
    ];

    public string Id { get; }

    public string Name { get => _name; set => Set(ref _name, value); }

    public string Target { get => _target; set => Set(ref _target, value); }

    public string Run { get => _run; set => Set(ref _run, value); }

    public string Args { get => _args; set => Set(ref _args, value); }

    /// <summary>The command against a sample target (PID 1234, a file under Program Files).</summary>
    public string Preview
    {
        get
        {
            try
            {
                return "→ " + ToolCommand.Build(_toolPath(), ToAction(), ToolTarget.Sample, toWsl: null).Display;
            }
            catch (ToolCommandException ex)
            {
                return "⚠ " + ex.Message;
            }
        }
    }

    public ToolAction ToAction() => new() { Id = Id, Name = Name.Trim(), Target = Target, Run = Run, Args = Args.Trim() };

    /// <summary>The tool path changed (the preview depends on it).</summary>
    public void RefreshPreview() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));

    private void Set(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field != value)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
        }
    }

    public sealed record Choice(string Value, string Label);
}
