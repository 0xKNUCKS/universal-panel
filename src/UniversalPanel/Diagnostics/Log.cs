using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace UniversalPanel.Diagnostics;

internal enum LogLevel { Debug, Info, Warning, Error, Fatal }

/// <summary>
/// Minimal file logger. Callers only format a line and enqueue it; a background task batches
/// lines to disk. The queue is bounded (drop-oldest), files rotate daily, each day is size-capped
/// and old days are pruned, so a runaway error loop can't fill the disk or stall the UI thread.
/// The logger never throws.
/// </summary>
internal static class Log
{
    private const int MaxQueuedLines = 4096;
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int RetentionDays = 7;

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalPanel", "logs");

    private static readonly Channel<string> Queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(MaxQueuedLines) { FullMode = BoundedChannelFullMode.DropOldest });

    private static readonly Lock FileLock = new();
    private static string? _capReachedForFile;

    public static void Start()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            PruneOldFiles();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Log.Start failed: {ex}");
        }

        _ = Task.Run(WriteLoopAsync);
    }

    public static void Info(string source, string message) => Write(LogLevel.Info, source, message);
    public static void Warn(string source, string message, Exception? ex = null) => Write(LogLevel.Warning, source, message, ex);
    public static void Error(string source, string message, Exception? ex = null) => Write(LogLevel.Error, source, message, ex);
    public static void Fatal(string source, string message, Exception? ex = null) => Write(LogLevel.Fatal, source, message, ex);

    public static void Write(LogLevel level, string source, string message, Exception? ex = null)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-7} [{source}] (T{Environment.CurrentManagedThreadId}) {message}";
        if (ex is not null)
        {
            line += Environment.NewLine + ex;
        }

        Debug.WriteLine(line);
        Queue.Writer.TryWrite(line);
    }

    /// <summary>Synchronously writes everything still queued. Used on the crash path.</summary>
    public static void Flush() => DrainToDisk();

    private static async Task WriteLoopAsync()
    {
        try
        {
            while (await Queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                DrainToDisk();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Log writer stopped: {ex}");
        }
    }

    private static void DrainToDisk()
    {
        lock (FileLock)
        {
            if (!Queue.Reader.TryRead(out var first))
            {
                return;
            }

            var batch = new StringBuilder(first).AppendLine();
            while (batch.Length < 64 * 1024 && Queue.Reader.TryRead(out var line))
            {
                batch.AppendLine(line);
            }

            try
            {
                var path = Path.Combine(Directory, $"{DateTime.Now:yyyy-MM-dd}.log");
                if (_capReachedForFile == path)
                {
                    return;
                }

                var info = new FileInfo(path);
                if (info.Exists && info.Length + batch.Length > MaxFileBytes)
                {
                    _capReachedForFile = path;
                    File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} Log size cap reached; dropping further lines today.{Environment.NewLine}");
                    return;
                }

                File.AppendAllText(path, batch.ToString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Log write failed: {ex.Message}");
            }
        }
    }

    private static void PruneOldFiles()
    {
        var cutoff = DateTime.Now.Date.AddDays(-RetentionDays);
        foreach (var file in new DirectoryInfo(Directory).EnumerateFiles("*.log"))
        {
            if (file.LastWriteTime < cutoff)
            {
                file.Delete();
            }
        }
    }
}
