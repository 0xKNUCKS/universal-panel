// Placement logic follows EarTrumpet's FlyoutWindow.PositionWindowRelativeToTaskbar (MIT):
// use the monitor's *working area* (excludes the taskbar and docked app bars), in physical pixels.
// https://github.com/File-New-Project/EarTrumpet @ f492b8fa6459d9c5b442ad07ea28d70e9db7f493
// EarTrumpet anchors to the taskbar's monitor; we anchor to the monitor under the cursor.

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace UniversalPanel.Native;

internal readonly record struct PixelRect(int X, int Y, int Width, int Height);

internal static class MonitorPlacement
{
    /// <summary>
    /// Full-height strip on the right edge of the working area of the monitor under the cursor.
    /// </summary>
    /// <param name="widthDip">Panel width in device-independent pixels (1/96 inch).</param>
    public static PixelRect RightEdgeOfMonitorUnderCursor(double widthDip)
    {
        PInvoke.GetCursorPos(out var cursor);
        var monitor = PInvoke.MonitorFromPoint(cursor, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        PInvoke.GetMonitorInfo(monitor, ref info);

        uint dpiX = 96;
        if (OperatingSystem.IsWindowsVersionAtLeast(10)
            && PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var x, out _).Succeeded)
        {
            dpiX = x;
        }

        var work = info.rcWork;
        int width = (int)Math.Round(widthDip * dpiX / 96.0);
        return new PixelRect(work.right - width, work.top, width, work.bottom - work.top);
    }
}
