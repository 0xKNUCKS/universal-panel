using System.Windows;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Processes;

internal sealed class ProcessesModule : IPanelModule, IDisposable
{
    private readonly ProcessSampler _sampler = new();
    private readonly ProcessHistory _history = new() { Dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher };

    public ProcessesModule()
    {
        if (Shell.PanelSettings.Current.HistoryWhileHidden)
        {
            _history.Hold(); // Background recording, released if the user turns it off.
        }
    }

    public string Id => "processes";
    public string Title => "Processes";
    public SymbolRegular Icon => SymbolRegular.Pulse24;

    public FrameworkElement CreateView(IModuleHost host) => new ProcessesView(host, _sampler, _history);

    public void OnActivated()
    {
        _sampler.Start();
        _history.Hold();
    }

    public void OnDeactivated()
    {
        _sampler.Stop();
        _history.Release();
    }

    public void Dispose()
    {
        _sampler.Dispose();
        _history.Dispose();
    }
}
