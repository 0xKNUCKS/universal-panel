using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace UniversalPanel.Shell;

internal static class ThemeSettings
{
    /// <summary>Applies "Dark", "Light" or "System" through WPF-UI, keeping the acrylic backdrop.</summary>
    public static void Apply(string theme)
    {
        if (theme == "System")
        {
            ApplicationThemeManager.ApplySystemTheme();
            return;
        }

        ApplicationThemeManager.Apply(theme == "Light" ? ApplicationTheme.Light : ApplicationTheme.Dark, WindowBackdropType.Acrylic);
    }
}
