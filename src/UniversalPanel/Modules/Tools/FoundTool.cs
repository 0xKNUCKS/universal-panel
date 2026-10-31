using System.ComponentModel;

namespace UniversalPanel.Modules.Tools;

/// <summary>A "Find installed" result: the tool, and whether the user wants it added.</summary>
internal sealed class FoundTool(ToolRow tool, bool alreadyAdded) : INotifyPropertyChanged
{
    private bool _isChecked = !alreadyAdded;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ToolRow Tool { get; } = tool;

    public bool CanAdd { get; } = !alreadyAdded;

    public string? Note => CanAdd ? null : "Already added";

    /// <summary>Tools already in the list are greyed out (and can't be checked).</summary>
    public double Opacity => CanAdd ? Tool.Opacity : 0.45;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (CanAdd && _isChecked != value)
            {
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }
    }
}
