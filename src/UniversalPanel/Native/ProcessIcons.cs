// Mirrors System Informer (MIT), SystemInformer/procprv.c PhImageListExtractIcon: icon index 0 of
// the image file at the system small-icon size for the current DPI, cached per file name, with the
// stock application icon for processes that have no icon (or no readable path).
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d
// Extraction uses the documented SHDefExtractIcon / SHGetStockIconInfo instead of System Informer's
// own PE resource reader.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Win32;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace UniversalPanel.Native;

internal static class ProcessIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();
    private static ImageSource? _fallback;

    /// <summary>
    /// Frozen (thread-safe) small icon for an executable, or the generic application icon.
    /// Safe to call from any thread; each path is extracted once.
    /// </summary>
    public static ImageSource? Get(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Fallback();
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var cached))
            {
                return cached ?? Fallback();
            }
        }

        var icon = Extract(path);
        lock (Gate)
        {
            Cache[path] = icon;
        }

        return icon ?? Fallback();
    }

    private static int SmallIconSize() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393)
            ? PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, PInvoke.GetDpiForSystem())
            : PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);

    private static unsafe BitmapSource? Extract(string path)
    {
        int size = SmallIconSize();
        // nIconSize: LOWORD = large size, HIWORD = small size. Both handles destroy their icon on dispose.
        var hr = PInvoke.SHDefExtractIcon(path, 0, 0, out var large, out var small, (uint)((size << 16) | size));
        using (large)
        using (small)
        {
            if (hr.Value != 0 || small.IsInvalid) // S_FALSE: the file has no icon.
            {
                return null;
            }

            return ToImage(new HICON((void*)small.DangerousGetHandle()));
        }
    }

    private static ImageSource? Fallback()
    {
        lock (Gate)
        {
            if (_fallback is not null)
            {
                return _fallback;
            }
        }

        var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
        if (PInvoke.SHGetStockIconInfo(SHSTOCKICONID.SIID_APPLICATION, SHGSI_FLAGS.SHGSI_ICON | SHGSI_FLAGS.SHGSI_SMALLICON, ref info).Failed
            || info.hIcon.IsNull)
        {
            return null;
        }

        try
        {
            var image = ToImage(info.hIcon);
            lock (Gate)
            {
                return _fallback ??= image;
            }
        }
        finally
        {
            PInvoke.DestroyIcon(info.hIcon);
        }
    }

    // The bitmap copies the icon's pixels, so the HICON can be destroyed right after. Frozen so
    // the sampler thread can create it and the UI thread can draw it.
    private static unsafe BitmapSource ToImage(HICON icon)
    {
        var image = Imaging.CreateBitmapSourceFromHIcon((nint)icon.Value, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        image.Freeze();
        return image;
    }
}
