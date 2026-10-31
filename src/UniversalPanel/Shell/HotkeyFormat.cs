using System.Windows.Input;

namespace UniversalPanel.Shell;

/// <summary>Builds, displays and persists the toggle hotkey.</summary>
internal static class HotkeyFormat
{
    /// <summary>Human-readable form, e.g. "Ctrl+Alt+K" or "Alt+`" (not WPF's "Alt+Oem3").</summary>
    public static string Display(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key switch
        {
            Key.Oem3 => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.Oem2 => "/",
            Key.Oem5 => "\\",
            Key.Oem1 => ";",
            Key.Oem7 => "'",
            Key.OemOpenBrackets => "[",
            Key.Oem6 => "]",
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            _ => key.ToString(),
        });
        return string.Join("+", parts);
    }

    /// <summary>
    /// A usable global hotkey: needs a modifier unless it's a function key (a bare letter would
    /// swallow that key system-wide), and must not be a modifier key by itself.
    /// </summary>
    public static bool TryCreate(Key key, ModifierKeys modifiers, out KeyGesture gesture)
    {
        gesture = HotkeyService.DefaultToggleGesture;
        bool isModifier = key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;
        bool isFunctionKey = key is >= Key.F1 and <= Key.F24;
        if (isModifier || (modifiers == ModifierKeys.None && !isFunctionKey))
        {
            return false;
        }

        try
        {
            gesture = new KeyGesture(key, modifiers, Display(key, modifiers));
            return true;
        }
        catch (NotSupportedException)
        {
            return false; // KeyGesture rejects some combinations; treat as unusable.
        }
    }

    public static KeyGesture FromSettings(PanelSettings settings) =>
        Enum.TryParse<Key>(settings.HotkeyKey, out var key)
        && Enum.TryParse<ModifierKeys>(settings.HotkeyModifiers, out var modifiers)
        && TryCreate(key, modifiers, out var gesture)
            ? gesture
            : HotkeyService.DefaultToggleGesture;

    public static void Save(PanelSettings settings, KeyGesture gesture)
    {
        settings.HotkeyKey = gesture.Key.ToString();
        settings.HotkeyModifiers = gesture.Modifiers.ToString();
        settings.SaveSoon();
    }
}
