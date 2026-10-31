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
    private readonly PanelSettings _settings = PanelSettings.Current;
    private PixelRect _rect;

    private readonly Dictionary<IPanelModule, FrameworkElement> _views = [];
    private IPanelModule? _active;
    private bool _isOpen;
    private int _autoHideSuppressions;
    private CancellationTokenSource? _dialogCts;

    internal PanelWindow(IReadOnlyList<IPanelModule> modules)
    {
        InitializeComponent();
        ModuleList.ItemsSource = modules;

        SourceInitialized += (_, _) =>
        {
            this.Cloak();
            this.EnableDwmTransitions(); // DWM's hide fade carries the backdrop out (PanelAnimator).
        };
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
        // Cloak before showing: the window was hidden normally (for DWM's hide fade), and must not
        // flash, or get DWM's show animation on top of ours, before it's positioned.
        this.Cloak();
        Show();

        var rect = MonitorPlacement.RightEdgeOfMonitorUnderCursor(_settings.PanelWidth);
        _rect = rect;
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

    // Left-edge resize: the right edge stays put. HorizontalChange is incremental here because the
    // thumb moves with the window's left edge on every step.
    private void OnResizeDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        double scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        double width = Math.Clamp(_settings.PanelWidth - e.HorizontalChange, PanelSettings.MinWidth, PanelSettings.MaxWidth);
        int right = _rect.X + _rect.Width;
        int px = Math.Min((int)Math.Round(width * scale), right - _rect.Left);
        _settings.PanelWidth = px / scale;
        _rect = new PixelRect(right - px, _rect.Y, px, _rect.Height) { Left = _rect.Left };
        this.SetWindowPos(_rect.X, _rect.Y, _rect.Width, _rect.Height);
    }

    /// <summary>Back to the default width (Settings page); re-docks right away if open.</summary>
    public void ResetWidth()
    {
        _settings.PanelWidth = PanelSettings.DefaultWidth;
        _settings.Save();
        if (_isOpen)
        {
            _rect = MonitorPlacement.RightEdgeOfMonitorUnderCursor(_settings.PanelWidth);
            this.SetWindowPos(_rect.X, _rect.Y, _rect.Width, _rect.Height);
        }
    }

    private void OnResizeCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _settings.Save();

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
        ShowView(GetOrCreateView(next));

        if (_isOpen)
        {
            ActivateModule(next);
        }
    }

    private FrameworkElement? _shownView;

    private void ShowView(FrameworkElement view)
    {
        if (_shownView is not null && _shownView != view)
        {
            _shownView.Visibility = Visibility.Collapsed;
            // Error views aren't cached (re-selecting retries), so don't keep them around.
            if (!_views.ContainsValue(_shownView))
            {
                ModuleHost.Children.Remove(_shownView);
            }
        }

        if (!ModuleHost.Children.Contains(view))
        {
            ModuleHost.Children.Add(view);
        }

        view.Visibility = Visibility.Visible;
        _shownView = view;
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
