using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Terminal.Wpf;

namespace UniversalPanel.Modules.Terminal;

/// <summary>A tab in the strip: its session and the terminal control showing it.</summary>
internal sealed class TerminalTab : INotifyPropertyChanged
{
    // Campbell, Windows Terminal's default scheme (COLORREF: 0x00BBGGRR).
    private static readonly uint[] Campbell =
    [
        0x0C0C0C, 0x1F0FC5, 0x0EA113, 0x009CC1, 0xDA3700, 0x981788, 0xDD963A, 0xCCCCCC,
        0x767676, 0x5648E7, 0x0CC616, 0xA5F1F9, 0xFF783B, 0x9E00B4, 0xD6D661, 0xF2F2F2,
    ];

    private static readonly Color Background = Color.FromRgb(0x1C, 0x1C, 0x1C);
    private bool _isRenaming;

    private TerminalTab(TerminalSession session, TerminalControl surface)
    {
        Session = session;
        Surface = surface;
        session.PropertyChanged += (_, e) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(e.PropertyName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TerminalSession Session { get; }

    public FrameworkElement Surface { get; }

    public string Name { get => Session.Name; set => Session.Name = value; }

    public bool Ended => Session.Ended;

    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            _isRenaming = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRenaming)));
        }
    }

    /// <summary>Only after <see cref="TerminalRenderer.Problem"/> said the control is genuine.</summary>
    public static TerminalTab Create(TerminalProfile profile, Dispatcher dispatcher)
    {
        var session = new TerminalSession(profile, dispatcher);
        var control = new TerminalControl { AutoResize = true };
        control.Loaded += (_, _) => control.SetTheme(
            new TerminalTheme
            {
                DefaultBackground = ToColorRef(Background),
                DefaultForeground = 0xCCCCCC,
                DefaultSelectionBackground = 0x5A5A5A,
                CursorStyle = CursorStyle.BlinkingBar,
                ColorTable = Campbell,
            },
            "Cascadia Mono",
            12,
            Background);
        control.Connection = session;
        return new TerminalTab(session, control);
    }

    /// <summary>Keyboard to the shell: the control's inner HwndHost forwards focus to the native window.</summary>
    public void Focus() => (FindHost(Surface) ?? Surface).Focus();

    private static System.Windows.Interop.HwndHost? FindHost(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if ((child as System.Windows.Interop.HwndHost ?? FindHost(child)) is { } host)
            {
                return host;
            }
        }

        return null;
    }

    private static uint ToColorRef(Color c) => (uint)(c.R | (c.G << 8) | (c.B << 16));
}
