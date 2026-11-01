namespace UniversalPanel.Modules.Processes;

/// <summary>
/// Other modules asking Processes to show a process (e.g. a terminal tab's shell). Brings
/// Processes into view; a request made before it was ever opened waits for it. UI thread only.
/// </summary>
internal static class ProcessRequests
{
    private static ProcessesView? _view;
    private static int? _pending;

    public static void Show(IModuleHost host, int pid)
    {
        host.ShowModule(ProcessesModule.ModuleId); // creates the view the first time
        if (_view is null)
        {
            _pending = pid;
        }
        else
        {
            _view.Reveal(pid);
        }
    }

    public static void Attach(ProcessesView view)
    {
        _view = view;
        if (_pending is { } pid)
        {
            _pending = null;
            view.Reveal(pid);
        }
    }
}
