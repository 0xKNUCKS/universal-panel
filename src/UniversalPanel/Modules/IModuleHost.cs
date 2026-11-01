using Wpf.Ui.Controls;

namespace UniversalPanel.Modules;

/// <summary>What the shell offers to a module. Implemented by the panel window.</summary>
internal interface IModuleHost
{
    /// <summary>Shows a message in the panel's notice bar (expected failures, warnings, info).</summary>
    void ShowNotice(string title, string message, InfoBarSeverity severity);

    /// <summary>
    /// Keeps the panel open while focus legitimately leaves it (launching a tool, a UAC prompt,
    /// a file dialog). Dispose the returned scope when done.
    /// </summary>
    IDisposable SuppressAutoHide();

    /// <summary>
    /// In-panel confirmation dialog. Cancel is the default button, so Enter never confirms a
    /// destructive action by accident. Returns false if the panel is hidden while it's open.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive);

    /// <summary>Switches the panel to another module (e.g. Processes asking the Terminal for a shell).</summary>
    void ShowModule(string id);
}
