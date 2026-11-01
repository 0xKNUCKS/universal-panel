using System.Windows;
using System.Windows.Media;

namespace UniversalPanel.Shell;

/// <summary>
/// Marks a part of a module whose keys aren't the panel's: Esc there goes to the content (a
/// terminal running vim, say) instead of hiding the panel.
/// </summary>
internal static class PanelKeys
{
    public static readonly DependencyProperty KeepsEscapeProperty = DependencyProperty.RegisterAttached(
        "KeepsEscape", typeof(bool), typeof(PanelKeys), new FrameworkPropertyMetadata(false));

    public static bool GetKeepsEscape(DependencyObject element) => (bool)element.GetValue(KeepsEscapeProperty);

    public static void SetKeepsEscape(DependencyObject element, bool value) => element.SetValue(KeepsEscapeProperty, value);

    /// <summary>True if the element or one of its ancestors keeps Esc.</summary>
    public static bool KeepsEscape(DependencyObject? element)
    {
        for (; element is not null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (GetKeepsEscape(element))
            {
                return true;
            }
        }

        return false;
    }
}
