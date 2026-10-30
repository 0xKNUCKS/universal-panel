using System.Windows;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Processes;

internal sealed class ProcessesModule : IPanelModule, IDisposable
{
    private readonly ProcessSampler _sampler = new();

    public string Id => "processes";
    public string Title => "Processes";
    public SymbolRegular Icon => SymbolRegular.Pulse24;

    public FrameworkElement CreateView(IModuleHost host) => new ProcessesView(host, _sampler);

    public void OnActivated() => _sampler.Start();

    public void OnDeactivated() => _sampler.Stop();

    public void Dispose() => _sampler.Dispose();
}
