// Mirrors EarTrumpet (MIT), EarTrumpet/Extensions/WindowExtensions.cs
// https://github.com/File-New-Project/EarTrumpet @ f492b8fa6459d9c5b442ad07ea28d70e9db7f493
// Changes: P/Invoke via CsWin32 (generated from Win32 metadata) instead of hand-written signatures.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace UniversalPanel.Native;

internal static class WindowExtensions
{
    public static HWND GetHwnd(this Window window) => new(new WindowInteropHelper(window).Handle);

    /// <summary>Physical-pixel placement; WPF's Left/Top are DIPs and don't map across monitors.</summary>
    public static void SetWindowPos(this Window window, int x, int y, int width, int height)
    {
        PInvoke.SetWindowPos(
            window.GetHwnd(),
            HWND.Null,
            x,
            y,
            width,
            height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    /// <summary>DWM-cloaks the window: it keeps its HWND and layout but isn't drawn.</summary>
    public static unsafe void Cloak(this Window window, bool hide = true)
    {
        BOOL value = hide;
        PInvoke.DwmSetWindowAttribute(
            window.GetHwnd(),
            DWMWINDOWATTRIBUTE.DWMWA_CLOAK,
            &value,
            (uint)sizeof(BOOL));
    }

    /// <summary>Clears DWMWA_TRANSITIONS_FORCEDISABLED so DWM animates show/hide normally.</summary>
    public static unsafe void EnableDwmTransitions(this Window window)
    {
        BOOL disabled = false;
        PInvoke.DwmSetWindowAttribute(
            window.GetHwnd(),
            DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED,
            &disabled,
            (uint)sizeof(BOOL));
    }

    public static void ApplyExtendedWindowStyle(this Window window, WINDOW_EX_STYLE newExStyle)
    {
        var hwnd = window.GetHwnd();
        var currentExStyle = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if (currentExStyle == 0)
        {
            Trace.WriteLine($"ApplyExtendedWindowStyle failed: ({Marshal.GetLastWin32Error()})");
            return;
        }

        if (PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, currentExStyle | (int)newExStyle) != currentExStyle)
        {
            Trace.WriteLine($"ApplyExtendedWindowStyle unexpected previous style ({Marshal.GetLastWin32Error()})");
        }
    }

    public static void SetForegroundWindow(this Window window) => PInvoke.SetForegroundWindow(window.GetHwnd());
}
