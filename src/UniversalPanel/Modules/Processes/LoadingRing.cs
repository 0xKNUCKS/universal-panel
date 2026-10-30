using System.Windows.Controls;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Processes;

/// <summary>
/// A small indeterminate ring placed in a host ContentControl while something loads.
/// The ring exists only while loading: its animation runs forever once loaded, so a hidden ring
/// left in the tree would keep the render loop busy.
/// </summary>
internal static class LoadingRing
{
    public static void Set(ContentControl host, bool loading)
    {
        if (loading == host.Content is not null)
        {
            return;
        }

        host.Content = loading ? Create() : null;
    }

    // WPF-UI draws the ring in the system accent color, which can all but vanish against the
    // acrylic panel. Use the primary text color over a faint track so it reads on any accent.
    // Resource references keep it right if the theme changes.
    private static ProgressRing Create()
    {
        var ring = new ProgressRing { IsIndeterminate = true, Width = 40, Height = 40 };
        ring.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        ring.SetResourceReference(ProgressRing.CoverRingStrokeProperty, "ControlStrokeColorDefaultBrush");
        return ring;
    }

    /// <summary>
    /// Shows the ring and lets one frame render before the caller continues. Without the yield, a
    /// fast background load's continuation (Normal priority) runs before rendering (lower
    /// priority), so the ring would never reach the screen.
    /// </summary>
    public static async Task ShowAsync(ContentControl host)
    {
        Set(host, true);
        await Dispatcher.Yield(DispatcherPriority.Background); // the host's (UI) dispatcher: callers run on it
    }
}
