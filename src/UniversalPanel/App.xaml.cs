using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using UniversalPanel.Diagnostics;
using UniversalPanel.Modules;
using UniversalPanel.Native;
using UniversalPanel.Modules.Processes;
using UniversalPanel.Modules.Settings;
using UniversalPanel.Shell;
using Windows.Win32;
using Windows.Win32.System.LibraryLoader;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;

namespace UniversalPanel;

public partial class App : Application, ISingleInstanceApp
{
    private PanelWindow? _panel;
    private TaskbarIcon? _tray;
    private KeyGesture _hotkey = HotkeyService.DefaultToggleGesture;

    // Startup flow mirrors Flow Launcher's App.Main (MIT): gate on single instance, then Run().
    [STAThread]
    public static void Main()
    {
        HardenDllSearchPath();
        Log.Start();
        Log.Info("App", $"Starting {typeof(App).Assembly.GetName().Version} on {Environment.OSVersion}, x64={Environment.Is64BitProcess}");

        bool firstInstance = false;
        try
        {
            // A "Restart as administrator" copy waits for the old instance to release the lock.
            Elevation.WaitForReplacedInstance(Environment.GetCommandLineArgs());
            if (Elevation.IsElevated)
            {
                Log.Info("App", Elevation.TryEnableDebugPrivilege() ? "Elevated; SeDebugPrivilege enabled." : "Elevated; SeDebugPrivilege not available.");
            }

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

        var settings = PanelSettings.Current;
        // Dark only: the acrylic panel and its secondary text are tuned for it (light had poor contrast).
        ThemeSettings.Apply("Dark");
        ProcessSampler.Interval = TimeSpan.FromSeconds(settings.ProcessRefreshSeconds);

        var settingsModule = new SettingsModule(new SettingsActions(
            CurrentHotkey: () => _hotkey,
            ApplyHotkey: ChangeHotkey,
            ResetPanelWidth: () => _panel?.ResetWidth(),
            OpenLogFolder: OpenLogFolder));

        _panel = new PanelWindow([new ProcessesModule(), new Modules.Tools.ToolsModule(), .. PlaceholderModule.Defaults, settingsModule]);
        _panel.Initialize();

        _hotkey = HotkeyFormat.FromSettings(settings);
        _tray = CreateTrayIcon();

        var gesture = _hotkey;
        var hotkeyError = RegisterHotkey(gesture);
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
        if (!Elevation.IsElevated)
        {
            menu.Items.Add(CreateMenuItem("Restart as administrator", RestartElevated));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Exit", Shutdown));

        var tray = new TaskbarIcon
        {
            ToolTipText = TrayToolTip(),
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

    private string? RegisterHotkey(KeyGesture gesture) =>
        HotkeyService.RegisterToggle(gesture, () => Guard.Run("Hotkey.Toggle", () => _panel?.Toggle()));

    /// <summary>
    /// Settings page: switch to a new hotkey, or keep the old one (re-registered) if the new one is
    /// taken. AddOrReplace removes the old registration first, so a failure would otherwise leave none.
    /// </summary>
    private string? ChangeHotkey(KeyGesture gesture)
    {
        if (RegisterHotkey(gesture) is { } error)
        {
            RegisterHotkey(_hotkey);
            return error;
        }

        _hotkey = gesture;
        HotkeyFormat.Save(PanelSettings.Current, gesture);
        if (_tray is not null)
        {
            _tray.ToolTipText = TrayToolTip();
        }

        return null;
    }

    private string TrayToolTip() =>
        $"Universal Panel ({_hotkey.DisplayString})" + (Elevation.IsElevated ? " (Administrator)" : string.Empty);

    /// <summary>UAC prompt; on success this instance exits and the elevated copy takes over.</summary>
    private void RestartElevated()
    {
        if (Elevation.TryRestartElevated())
        {
            Log.Info("App", "Restarting elevated.");
            Shutdown();
        }
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

    /// <summary>
    /// Removes the current directory and PATH from the native DLL search order for this process,
    /// so a planted DLL next to a file we open (or in a writable PATH entry) can't be loaded.
    /// The app directory, System32 and explicitly added directories are still searched.
    /// </summary>
    private static void HardenDllSearchPath()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10)
            && !PInvoke.SetDefaultDllDirectories(LOAD_LIBRARY_FLAGS.LOAD_LIBRARY_SEARCH_DEFAULT_DIRS))
        {
            Debug.WriteLine("SetDefaultDllDirectories failed.");
        }
    }
}
