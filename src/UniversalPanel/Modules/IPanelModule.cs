using System.Windows;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules;

/// <summary>
/// One section of the panel. The shell owns lifecycle and error isolation: every call below runs
/// inside an error boundary, so a throwing module shows an error view instead of crashing the app.
/// </summary>
internal interface IPanelModule
{
    /// <summary>Stable id, used for logging context and (later) settings.</summary>
    string Id { get; }

    string Title { get; }

    SymbolRegular Icon { get; }

    /// <summary>Called once, lazily, the first time the module is selected.</summary>
    FrameworkElement CreateView();

    /// <summary>Module became visible (panel shown and module selected). Start timers/sampling here.</summary>
    void OnActivated();

    /// <summary>Module hidden (panel dismissed or another module selected). Stop all background work here.</summary>
    void OnDeactivated();
}
