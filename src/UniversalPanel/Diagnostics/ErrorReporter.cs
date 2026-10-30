using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace UniversalPanel.Diagnostics;

/// <summary>An error as shown to the user. Repeats of the same error bump <see cref="Count"/>.</summary>
internal sealed record ErrorNotice(string Key, string Context, string Message, Exception Exception, int Count);

/// <summary>
/// The one place every caught exception goes. Pipeline:
/// classify (fatal / known-benign / normal), log, de-duplicate, notify the UI.
/// Global handlers mirror Flow Launcher's Helper/ErrorReporting.cs (MIT); the DWM filter is the
/// PowerToys PowerLauncher/Helper/ExceptionHelper.cs check that Flow Launcher copies (MIT).
/// </summary>
internal static class ErrorReporter
{
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(10);

    // Circuit breaker: an exception thrown on every frame/layout pass would otherwise be
    // swallowed forever while the UI is unusable. Past this rate we stop swallowing and exit.
    private const int UiStormLimit = 50;
    private static readonly TimeSpan UiStormWindow = TimeSpan.FromSeconds(10);

    private static readonly Dictionary<string, (DateTime LastSeen, int Count)> Recent = [];
    private static readonly Queue<DateTime> RecentUiFailures = new();
    private static readonly Lock Gate = new();

    /// <summary>Raised on the UI thread.</summary>
    public static event Action<ErrorNotice>? Notified;

    public static void InstallGlobalHandlers(Application app)
    {
        app.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>Logs and (optionally) surfaces a non-fatal error.</summary>
    /// <param name="context">Where it happened, e.g. "Processes.Refresh". Used for grouping.</param>
    /// <param name="notify">False for background noise that only belongs in the log.</param>
    public static void Report(Exception ex, string context, bool notify = true)
    {
        ex = Unwrap(ex);

        if (IsRecoverableDwmCompositionException(ex))
        {
            Log.Warn(context, "Recoverable DWM composition exception, ignored.", ex);
            return;
        }

        var key = $"{context}|{ex.GetType().FullName}|{ex.Message}";
        int count;
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            count = Recent.TryGetValue(key, out var prev) && now - prev.LastSeen < DedupeWindow ? prev.Count + 1 : 1;
            Recent[key] = (now, count);
            if (Recent.Count > 256)
            {
                foreach (var stale in Recent.Where(kv => now - kv.Value.LastSeen >= DedupeWindow).Select(kv => kv.Key).ToList())
                {
                    Recent.Remove(stale);
                }
            }
        }

        // Full details once per burst; repeats get a one-liner so a hot loop can't flood the log.
        if (count == 1)
        {
            Log.Error(context, ex.Message, ex);
        }
        else if (IsPowerOfTwo(count))
        {
            Log.Error(context, $"Repeated x{count}: {ex.GetType().Name}: {ex.Message}");
        }

        if (notify)
        {
            var notice = new ErrorNotice(key, context, ex.Message, ex, count);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted)
            {
                return;
            }

            dispatcher.BeginInvoke(DispatcherPriority.Background, () => Notified?.Invoke(notice));
        }
    }

    /// <summary>
    /// Exceptions that mean process state can't be trusted. Never swallow these.
    /// (StackOverflow and most access violations can't be caught in .NET at all.)
    /// </summary>
    public static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException and not InsufficientMemoryException
            or AccessViolationException
            or SEHException
            or InvalidProgramException
            or StackOverflowException;

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (Debugger.IsAttached)
        {
            Debugger.Break();
        }

        if (IsFatal(e.Exception) || IsUiStorm())
        {
            Log.Fatal("UI", "Unrecoverable UI exception, shutting down.", e.Exception);
            Log.Flush();
            return; // Not handled: the process terminates.
        }

        Report(e.Exception, "UI");
        e.Handled = true;
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Non-UI-thread exception. The runtime terminates the process after this; just persist it.
        Log.Fatal("AppDomain", "Unhandled exception, process terminating.", e.ExceptionObject as Exception);
        Log.Flush();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Report(e.Exception, "Task", notify: false);
        e.SetObserved();
    }

    private static bool IsUiStorm()
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            RecentUiFailures.Enqueue(now);
            while (RecentUiFailures.Count > 0 && now - RecentUiFailures.Peek() > UiStormWindow)
            {
                RecentUiFailures.Dequeue();
            }

            return RecentUiFailures.Count > UiStormLimit;
        }
    }

    private static Exception Unwrap(Exception ex) =>
        ex is AggregateException agg && agg.Flatten().InnerExceptions is [var single] ? single : ex;

    private static bool IsPowerOfTwo(int n) => (n & (n - 1)) == 0;

    // --- Ported from PowerToys (MIT) via Flow Launcher, Helper/ExceptionHelper.cs ---
    // WPF's WindowChrome (used by WPF-UI FluentWindow) can throw these while DWM composition is
    // changing (sleep/resume, RDP, display changes). They are harmless and must not be surfaced.
    private const int DWM_E_COMPOSITIONDISABLED = unchecked((int)0x80263001);
    private const int STATUS_MESSAGE_LOST_HR = unchecked((int)0xD0000701);

    internal static bool IsRecoverableDwmCompositionException(Exception exception)
    {
        if (exception is not COMException comException)
        {
            return false;
        }

        if (comException.HResult is DWM_E_COMPOSITIONDISABLED)
        {
            return true;
        }

        if (comException.HResult is STATUS_MESSAGE_LOST_HR && comException.Source == "PresentationFramework")
        {
            return true;
        }

        var stackTrace = comException.StackTrace;
        return !string.IsNullOrEmpty(stackTrace)
            && stackTrace.Contains("DwmCompositionChanged", StringComparison.OrdinalIgnoreCase);
    }
}
