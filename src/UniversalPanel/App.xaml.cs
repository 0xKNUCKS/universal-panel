using System.Windows;
using UniversalPanel.Diagnostics;
using UniversalPanel.Modules;
using UniversalPanel.Shell;

namespace UniversalPanel;

public partial class App : Application
{
    private PanelWindow? _panel;

    [STAThread]
    public static void Main()
    {
        Log.Start();
        Log.Info("App", $"Starting {typeof(App).Assembly.GetName().Version} on {Environment.OSVersion}, x64={Environment.Is64BitProcess}");

        try
        {
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
            Log.Info("App", "Exited.");
            Log.Flush();
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _panel = new PanelWindow(PlaceholderModule.Defaults);
        _panel.Initialize();
        _panel.ShowPanel();
    }
}
