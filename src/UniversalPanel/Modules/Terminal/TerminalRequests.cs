namespace UniversalPanel.Modules.Terminal;

/// <summary>
/// Other modules asking the Terminal tab for something ("open a shell here", "paste this path").
/// Brings the Terminal tab into view; requests made before it was ever opened wait for it.
/// UI thread only.
/// </summary>
internal static class TerminalRequests
{
    private static readonly Queue<Action<TerminalView>> Pending = new();
    private static TerminalView? _view;

    public static void Send(IModuleHost host, Action<TerminalView> request)
    {
        host.ShowModule(TerminalModule.ModuleId); // creates the view the first time
        if (_view is null)
        {
            Pending.Enqueue(request);
        }
        else
        {
            request(_view);
        }
    }

    public static void Attach(TerminalView view)
    {
        _view = view;
        while (Pending.TryDequeue(out var request))
        {
            request(view);
        }
    }
}
