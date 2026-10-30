namespace UniversalPanel.Diagnostics;

/// <summary>
/// Error boundaries. Use at the edges where one feature must not take down the app: module
/// lifecycle calls, timer ticks, button handlers, fire-and-forget tasks. Inside a feature, use
/// normal try/catch for errors you expect and can act on (e.g. access denied on a process).
/// Fatal exceptions are never caught here.
/// </summary>
internal static class Guard
{
    public static bool Run(string context, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (!ErrorReporter.IsFatal(ex))
        {
            ErrorReporter.Report(ex, context);
            return false;
        }
    }

    public static T Run<T>(string context, Func<T> func, Func<Exception, T> fallback)
    {
        try
        {
            return func();
        }
        catch (Exception ex) when (!ErrorReporter.IsFatal(ex))
        {
            ErrorReporter.Report(ex, context);
            return fallback(ex);
        }
    }

    public static async Task<bool> RunAsync(string context, Func<Task> func)
    {
        try
        {
            await func();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (!ErrorReporter.IsFatal(ex))
        {
            ErrorReporter.Report(ex, context);
            return false;
        }
    }

    /// <summary>Fire-and-forget without losing the exception to UnobservedTaskException.</summary>
    public static void Forget(this Task task, string context)
    {
        task.ContinueWith(
            t => ErrorReporter.Report(t.Exception!, context),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
