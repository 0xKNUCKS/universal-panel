// Placement logic follows EarTrumpet's FlyoutWindow.PositionWindowRelativeToTaskbar (MIT):
// use the monitor's *working area* (excludes the taskbar and docked app bars), in physical pixels.
// https://github.com/File-New-Project/EarTrumpet @ f492b8fa6459d9c5b442ad07ea28d70e9db7f493
// EarTrumpet anchors to the taskbar's monitor; we anchor to the monitor the user picked
// (Settings > Show on): the one under the cursor (default), the primary, or a specific display.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace UniversalPanel.Native;

/// <param name="Left">Left edge of the monitor's work area, the furthest the panel may extend.</param>
internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Left { get; init; }
}

/// <summary>A connected display. <see cref="Device"/> ("\\.\DISPLAY2") is what settings store.</summary>
internal sealed record MonitorChoice(string Device, int Width, int Height, bool IsPrimary)
{
    public string Label
    {
        get
        {
            string number = Device.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase) ? Device[11..] : Device;
            return $"Display {number} · {Width} × {Height}{(IsPrimary ? " · primary" : string.Empty)}";
        }
    }
}

internal static unsafe partial class MonitorPlacement
{
    public const string UnderCursor = "Cursor";
    public const string Primary = "Primary";

    /// <summary>
    /// Full-height strip on the right edge of the working area of the chosen monitor.
    /// </summary>
    /// <param name="widthDip">Panel width in device-independent pixels (1/96 inch).</param>
    /// <param name="choice"><see cref="UnderCursor"/>, <see cref="Primary"/> or a device name;
    /// a display that is no longer connected falls back to the primary one.</param>
    public static PixelRect RightEdge(double widthDip, string choice)
    {
        HMONITOR monitor = choice == UnderCursor ? MonitorUnderCursor() : PrimaryMonitor();
        if (choice is not (UnderCursor or Primary)
            && Enumerate().FirstOrDefault(m => m.Device.Equals(choice, StringComparison.OrdinalIgnoreCase)) is { Handle: not 0 } chosen)
        {
            monitor = (HMONITOR)chosen.Handle;
        }

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        PInvoke.GetMonitorInfo(monitor, ref info);

        uint dpiX = 96;
        if (OperatingSystem.IsWindowsVersionAtLeast(10)
            && PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var x, out _).Succeeded)
        {
            dpiX = x;
        }

        var work = info.rcWork;
        int width = Math.Min((int)Math.Round(widthDip * dpiX / 96.0), work.right - work.left);
        return new PixelRect(work.right - width, work.top, width, work.bottom - work.top) { Left = work.left };
    }

    /// <summary>Connected displays, primary first, then by device name.</summary>
    public static List<MonitorChoice> List() =>
        Enumerate()
            .Select(m => new MonitorChoice(m.Device, m.Width, m.Height, m.IsPrimary))
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Device, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // (0, 0) is always on the primary monitor.
    private static HMONITOR PrimaryMonitor() => PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);

    private static HMONITOR MonitorUnderCursor()
    {
        PInvoke.GetCursorPos(out var cursor);
        return PInvoke.MonitorFromPoint(cursor, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
    }

    private readonly record struct Found(nint Handle, string Device, int Width, int Height, bool IsPrimary);

    private static List<Found> Enumerate()
    {
        var found = new List<Found>();
        var handle = GCHandle.Alloc(found);
        try
        {
            EnumDisplayMonitors(0, null, &Collect, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return found;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Collect(nint monitor, nint hdc, Bounds* clip, nint state)
    {
        var info = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
        if (GetMonitorInfoW(monitor, &info))
        {
            var list = (List<Found>)GCHandle.FromIntPtr(state).Target!;
            var r = info.rcMonitor;
            list.Add(new Found(monitor, new string(info.szDevice), r.right - r.left, r.bottom - r.top, (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
        }

        return 1; // continue
    }

    private const uint MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Bounds
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFOEXW
    {
        public uint cbSize;
        public Bounds rcMonitor;
        public Bounds rcWork;
        public uint dwFlags;
        public fixed char szDevice[32];
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(nint hdc, Bounds* clip, delegate* unmanaged[Stdcall]<nint, nint, Bounds*, nint, int> callback, nint state);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, MONITORINFOEXW* info);
}
