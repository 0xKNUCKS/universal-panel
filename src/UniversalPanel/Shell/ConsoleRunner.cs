using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using UniversalPanel.Native;

namespace UniversalPanel.Shell;

/// <summary>
/// "UniversalPanel.exe --run-console &lt;folder&gt; &lt;program&gt; [args...]": opens a console, runs the
/// program in it with exactly those arguments, then keeps the window open until a key is pressed.
/// This is how command-line tool actions show their output: unlike "cmd /k", nothing re-parses the
/// command line. The panel starts it with the user's normal rights (see ToolLauncher), so the tool
/// runs with them too; it never sees the panel's settings, pipes or handles.
/// </summary>
internal static partial class ConsoleRunner
{
    public const string Switch = "--run-console";

    /// <summary>The panel's arguments for running <paramref name="command"/> through this runner.</summary>
    public static string ArgumentsFor(ToolCommand command) =>
        string.Join(' ', new[] { Switch, command.WorkingDirectory, command.Program }.Concat(command.Arguments).Select(ToolCommand.Quote));

    /// <param name="args">Folder, program, then the program's arguments.</param>
    public static int Run(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !AllocConsole())
        {
            return 2;
        }

        string folder = Directory.Exists(args[0]) ? args[0] : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var command = new ToolCommand(args[1], args.Skip(2).ToList(), folder, true);
        Console.Title = $"{Path.GetFileNameWithoutExtension(command.Program)} — Universal Panel";
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Write(ConsoleColor.DarkGray, $"> {command.Display}\n\n");

        int exitCode;
        try
        {
            var start = new ProcessStartInfo(command.Program) { UseShellExecute = false, WorkingDirectory = folder };
            foreach (var argument in command.Arguments)
            {
                start.ArgumentList.Add(argument); // shares this console; no shell in between
            }

            using var process = Process.Start(start)!;
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch (Win32Exception ex)
        {
            Write(ConsoleColor.Red, $"Couldn't start it: {ex.Message}\n");
            exitCode = -1;
        }

        Write(ConsoleColor.DarkGray, $"\n[exited with code {exitCode}]  Press any key to close.");
        Console.ReadKey(intercept: true);
        return exitCode;
    }

    private static void Write(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ResetColor();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();
}
