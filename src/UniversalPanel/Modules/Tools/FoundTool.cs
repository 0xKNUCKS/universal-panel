using System.ComponentModel;

namespace UniversalPanel.Modules.Tools;

/// <summary>A "Find installed" result: the tool, and whether the user wants it added.</summary>
internal sealed class FoundTool(ToolRow tool, bool alreadyAdded) : INotifyPropertyChanged
{
    private bool _isChecked = !alreadyAdded;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ToolRow Tool { get; } = tool;

    public bool CanAdd { get; } = !alreadyAdded;

    public string? Note => CanAdd ? null : "Already in your list";

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
