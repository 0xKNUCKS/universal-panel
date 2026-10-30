// Follows Flow Launcher (MIT), Flow.Launcher/Helper/HotKeyMapper.cs
// https://github.com/Flow-Launcher/Flow.Launcher @ 440f38bd2addc49c1507ef32536edb9511477f82
// Registration goes through NHotkey.Wpf (RegisterHotKey on a message-only window), the same
// package Flow Launcher uses. RegisterHotKey, not a low-level keyboard hook: no keystroke
// visibility, and no AV heuristics for hooks (docs/PITFALLS.md).

using System.Windows.Input;
using NHotkey;
using NHotkey.Wpf;

namespace UniversalPanel.Shell;

internal static class HotkeyService
{
    private const string TogglePanelName = "TogglePanel";

    // Alt+` (Oem3 = backtick on US layouts). Ctrl+Alt+Space was taken on the dev machine.
    public static readonly KeyGesture DefaultToggleGesture = new(Key.Oem3, ModifierKeys.Alt, "Alt+`");

    /// <returns>Null on success, otherwise a user-facing error.</returns>
    public static string? RegisterToggle(KeyGesture gesture, Action onToggle)
    {
        try
        {
            // noRepeat: holding the combo must not flicker the panel open/closed.
            HotkeyManager.Current.AddOrReplace(
                TogglePanelName,
                gesture.Key,
                gesture.Modifiers,
                noRepeat: true,
                (_, e) =>
                {
                    onToggle();
                    e.Handled = true;
                });
            return null;
        }
        catch (HotkeyAlreadyRegisteredException)
        {
            return $"The hotkey {gesture.DisplayString} is already used by another application.";
        }
    }
}
