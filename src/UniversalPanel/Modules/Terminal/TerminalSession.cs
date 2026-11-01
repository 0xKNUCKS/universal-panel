using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Microsoft.Terminal.Wpf;
using UniversalPanel.Diagnostics;
using UniversalPanel.Native;
using UniversalPanel.Shell;

namespace UniversalPanel.Modules.Terminal;

/// <summary>
/// One tab: a shell in the terminal host, shown by Microsoft's terminal control. Keystrokes go to
/// the host as UTF-8; output comes back as UTF-8 and is decoded with a stateful decoder, so a
/// character split across two reads stays intact.
/// </summary>
internal sealed class TerminalSession(TerminalProfile profile, Dispatcher dispatcher, bool admin) : ITerminalConnection, INotifyPropertyChanged
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private TerminalHost? _host;
    private int _id;
    private int _columns = 120, _rows = 30;
    private bool _started;
    private string _name = profile.Name;
    private bool _ended;

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

    public event PropertyChangedEventHandler? PropertyChanged;

    public TerminalProfile Profile { get; } = profile;

    /// <summary>Runs in the elevated host. Never saved: a restored tab is always a normal one.</summary>
    public bool Admin { get; } = admin;

    public string Name
    {
        get => _name;
        set
        {
            _name = string.IsNullOrWhiteSpace(value) ? Profile.Name : value.Trim()[..Math.Min(value.Trim().Length, 40)];
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    /// <summary>The shell has exited (the tab stays until closed, showing why).</summary>
    public bool Ended
    {
        get => _ended;
        private set
        {
            _ended = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Ended)));
        }
    }

    // ---- ITerminalConnection (called by the terminal control) --------------------------------

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _ = Guard.RunAsync("Terminal.Start", async () =>
        {
            try
            {
                var host = await TerminalHost.GetAsync(Admin);
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                await host.StartAsync(this, new TerminalStart(Profile.Program, Profile.Arguments.ToList(), home, _columns, _rows));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ProcessActionException)
            {
                Log.Warn("Terminal", "Couldn't start the terminal host.", ex);
                OnEnded("couldn't start the terminal host: " + ex.Message);
            }
        });
    }

    public void WriteInput(string data)
    {
        if (_host is { } host && !Ended)
        {
            _ = host.InputAsync(_id, data);
        }
    }

    public void Resize(uint rows, uint columns)
    {
        (_rows, _columns) = ((int)rows, (int)columns);
        if (_host is { } host && !Ended)
        {
            _ = host.ResizeAsync(_id, _columns, _rows);
        }
    }

    public void Close()
    {
        if (_host is { } host && !Ended)
        {
            _ = host.CloseAsync(_id);
        }

        _host = null;
    }

    // ---- from the host -----------------------------------------------------------------------

    public void Attach(TerminalHost host, int id) => (_host, _id) = (host, id);

    /// <summary>Output bytes (pipe thread): decoded here, shown on the UI thread.</summary>
    public void OnOutput(byte[] bytes)
    {
        string text;
        lock (_decoder)
        {
            var chars = new char[_decoder.GetCharCount(bytes, flush: false)];
            _decoder.GetChars(bytes, chars, flush: false);
            text = new string(chars);
        }

        Show(text);
    }

    public void OnEnded(string reason)
    {
        Show($"\r\n\u001b[90m[{reason}]\u001b[0m\r\n");
        dispatcher.BeginInvoke(() => Ended = true);
    }

    private void Show(string text)
    {
        if (text.Length > 0)
        {
            dispatcher.BeginInvoke(() => TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(text)));
        }
    }
}
