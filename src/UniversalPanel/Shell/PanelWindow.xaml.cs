// Show/hide lifecycle mirrors EarTrumpet (MIT), EarTrumpet/UI/Views/FlyoutWindow.xaml.cs:
// cloak on SourceInitialized, pre-warm with Show();Hide(), WS_EX_TOOLWINDOW, position in physical
// pixels from the monitor working area, animate in, cloak + Hide on exit.
// https://github.com/File-New-Project/EarTrumpet @ f492b8fa6459d9c5b442ad07ea28d70e9db7f493

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UniversalPanel.Diagnostics;
using UniversalPanel.Modules;
using UniversalPanel.Native;
using Windows.Win32.UI.WindowsAndMessaging;
using Wpf.Ui.Controls;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace UniversalPanel.Shell;

public partial class PanelWindow : FluentWindow, IModuleHost
{
    private const double PanelWidthDip = 460;

    private readonly Dictionary<IPanelModule, FrameworkElement> _views = [];
    private IPanelModule? _active;
    private bool _isOpen;
    private int _autoHideSuppressions;
    private CancellationTokenSource? _dialogCts;

    internal PanelWindow(IReadOnlyList<IPanelModule> modules)
    {
        InitializeComponent();
        ModuleList.ItemsSource = modules;

        SourceInitialized += (_, _) => this.Cloak();
        ErrorReporter.Notified += OnErrorNotified;
        Closed += (_, _) => ErrorReporter.Notified -= OnErrorNotified;
    }

    public bool IsOpen => _isOpen;

    /// <summary>Create the HWND up front so the first show is instant.</summary>
    public void Initialize()
    {
        Show();
        Hide();
        this.ApplyExtendedWindowStyle(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);
        ModuleList.SelectedIndex = 0;
    }

    public void Toggle()
    {
        if (_isOpen)
        {
            HidePanel();
        }
        else
        {
            ShowPanel();
        }
    }

    public void ShowPanel()
    {
        if (_isOpen)
        {
            Activate();
            this.SetForegroundWindow();
            return;
        }

        _isOpen = true;
        Show();

        var rect = MonitorPlacement.RightEdgeOfMonitorUnderCursor(PanelWidthDip);
        // Twice on purpose: the first move can land on a monitor with a different DPI, and WPF
        // then rescales the window (WM_DPICHANGED) during that call. The second call sets the
        // exact rect at the new DPI.
        this.SetWindowPos(rect.X, rect.Y, rect.Width, rect.Height);
        this.SetWindowPos(rect.X, rect.Y, rect.Width, rect.Height);

        ActivateModule(_active);
        PanelAnimator.BeginEntrance(this, Root, () => { });
    }

    public void HidePanel()
    {
        if (!_isOpen)
        {
            return;
        }

        _isOpen = false;
        _dialogCts?.Cancel(); // An open confirmation must not survive the panel being dismissed.
        DeactivateModule(_active);
        PanelAnimator.BeginExit(this, Root, () =>
        {
            if (_isOpen)
            {
                // Re-opened while the exit animation was running.
                this.Cloak(false);
                return;
            }

            Hide();
        });
    }

    /// <summary>
    /// Keeps the panel open while focus legitimately leaves it (launching a tool, a UAC prompt,
    /// a file dialog). Dispose the returned scope when done.
    /// </summary>
    public IDisposable SuppressAutoHide()
    {
        _autoHideSuppressions++;
        return new Scope(() => _autoHideSuppressions--);
    }

    public void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        NoticeBar.Title = title;
        NoticeBar.Message = message;
        NoticeBar.Severity = severity;
        NoticeBar.IsOpen = true;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive)
    {
        _dialogCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _dialogCts = cts;

        var dialog = new ContentDialog(DialogHost)
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
            PrimaryButtonAppearance = destructive ? ControlAppearance.Danger : ControlAppearance.Primary,
            DefaultButton = ContentDialogButton.Close,
        };

        try
        {
            return await dialog.ShowAsync(cts.Token) == ContentDialogResult.Primary;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (_dialogCts == cts)
            {
                _dialogCts = null;
            }
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_autoHideSuppressions == 0)
        {
            HidePanel();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HidePanel();
            e.Handled = true;
        }
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => HidePanel();

    private void OnModuleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModuleList.SelectedItem is not IPanelModule next || next == _active)
        {
            return;
        }

        if (_isOpen)
        {
            DeactivateModule(_active);
        }

        _active = next;
        ModuleTitle.Text = next.Title;
        ModuleHost.Content = GetOrCreateView(next);

        if (_isOpen)
        {
            ActivateModule(next);
        }
    }

    private FrameworkElement GetOrCreateView(IPanelModule module)
    {
        if (_views.TryGetValue(module, out var view))
        {
            return view;
        }

        // Failed views aren't cached, so re-selecting the module retries.
        return Guard.Run(
            $"{module.Id}.CreateView",
            () => _views[module] = module.CreateView(this),
            ex => CreateErrorView(module, ex));
    }

    private static void ActivateModule(IPanelModule? module)
    {
        if (module is not null)
        {
            Guard.Run($"{module.Id}.OnActivated", module.OnActivated);
        }
    }

    private static void DeactivateModule(IPanelModule? module)
    {
        if (module is not null)
        {
            Guard.Run($"{module.Id}.OnDeactivated", module.OnDeactivated);
        }
    }

    private static TextBlock CreateErrorView(IPanelModule module, Exception ex) => new TextBlock
    {
        Text = $"{module.Title} failed to load: {ex.Message}\n\nFull details are in the log (tray icon > Open log folder). Select the module again to retry.",
        TextWrapping = TextWrapping.Wrap,
        Appearance = TextColor.Secondary,
    };

    private void OnErrorNotified(ErrorNotice notice)
    {
        var title = notice.Count > 1 ? $"{notice.Context} (x{notice.Count})" : notice.Context;
        ShowNotice(title, notice.Message, InfoBarSeverity.Error);
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
