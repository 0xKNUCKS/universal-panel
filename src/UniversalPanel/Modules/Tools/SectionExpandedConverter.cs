using System.Globalization;
using System.Windows.Data;
using UniversalPanel.Shell;

namespace UniversalPanel.Modules.Tools;

/// <summary>Section name -> expanded, from the sections the user collapsed (saved in settings).</summary>
internal sealed class SectionExpandedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not string name || !PanelSettings.Current.ToolSectionsCollapsed.Contains(name);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
