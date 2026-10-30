using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using UniversalPanel.Diagnostics;
using UniversalPanel.Modules;
using UniversalPanel.Shell;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;

namespace UniversalPanel;

public partial class App : Application, ISingleInstanceApp
{
    private PanelWindow? _panel;
    private TaskbarIcon? _tray;

    // Startup flow mirrors Flow Launcher's App.Main (MIT): gate on single instance, then Run().
    [STAThread]
    public static void Main()
    {
        Log.Start();
        Log.Info("App", $"Starting {typeof(App).Assembly.GetName().Version} on {Environment.OSVersion}, x64={Environment.Is64BitProcess}");

        bool firstInstance = false;
        try
        {
            firstInstance = SingleInstance.InitializeAsFirstInstance();
            if (!firstInstance)
            {
                Log.Info("App", "Another instance is running; asked it to show.");
                return;
            }

            var app = new App();
            ErrorReporter.InstallGlobalHandlers(app);
            app.InitializeComponent();
            app.Run();
        }
        catch (Exception ex)
        {
            // Same intent as Flow Launcher's ShowErrorMsgBoxAndFailFast: never die silently.
            Log.Fatal("App", "Startup failed.", ex);
            MessageBox.Show(ex.ToString(), "Universal Panel failed to start", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (firstInstance)
            {
                SingleInstance.Cleanup();
            }

            Log.Info("App", "Exited.");
            Log.Flush();
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _panel = new PanelWindow(PlaceholderModule.Defaults);
        _panel.Initialize();
        _tray = CreateTrayIcon();

        var gesture = HotkeyService.DefaultToggleGesture;
        var hotkeyError = HotkeyService.RegisterToggle(gesture, () => Guard.Run("Hotkey.Toggle", _panel.Toggle));
        if (hotkeyError is not null)
        {
            Log.Warn("Hotkey", hotkeyError);
            _panel.ShowPanel();
            _panel.ShowNotice("Hotkey unavailable", hotkeyError + " Use the tray icon to open the panel.", InfoBarSeverity.Warning);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }

    public void OnSecondAppStarted() => Guard.Run("SingleInstance.Show", () => _panel?.ShowPanel());

    private TaskbarIcon CreateTrayIcon()
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("Show panel", () => _panel?.ShowPanel()));
        menu.Items.Add(CreateMenuItem("Open log folder", OpenLogFolder));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Exit", Shutdown));

        var tray = new TaskbarIcon
        {
            ToolTipText = $"Universal Panel ({HotkeyService.DefaultToggleGesture.DisplayString})",
            IconSource = new GeneratedIconSource
            {
                Text = "U",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A)),
                FontWeight = FontWeights.Bold,
            },
            ContextMenu = menu,
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
        };
        tray.TrayLeftMouseUp += (_, _) => Guard.Run("Tray.Toggle", () => _panel?.Toggle());

        // false: ForceCreate() otherwise turns on Windows 11 Efficiency Mode for the whole
        // process, which would throttle the monitors (docs/PITFALLS.md).
        tray.ForceCreate(false);
        return tray;
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => Guard.Run($"Tray.{header}", action);
        return item;
    }

    private static void OpenLogFolder()
    {
        Directory.CreateDirectory(Log.Directory);
        // Absolute path to explorer.exe: no PATH / current-directory lookup.
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var startInfo = new ProcessStartInfo(explorer) { UseShellExecute = false };
        startInfo.ArgumentList.Add(Log.Directory);
        Process.Start(startInfo)?.Dispose();
    }
}
