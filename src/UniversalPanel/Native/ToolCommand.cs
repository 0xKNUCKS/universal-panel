// Turns a tool action ("-n 8 {path}") plus a target (a process, a file) into the exact program and
// argument list to start. The rules, so it's safe with any path:
// - the template is split into arguments FIRST (spaces separate, double quotes group), and only
//   then are {pid} {path} {dir} {name} filled in, inside each argument: a path with spaces, "&",
//   "%" or "$(...)" stays exactly one argument and can't become a second command;
// - nothing goes through a shell. Windows tools get a command line quoted per the MSVC rules
//   (what CommandLineToArgvW reads back); WSL tools run as "wsl.exe -d <distro> --exec <command>"
//   (--exec skips the Linux shell; without it bash would expand "$(...)" in a path), and their
//   {path} / {dir} go through the distro's own wslpath, which knows where Windows drives are mounted;
// - .bat / .cmd tools are run by cmd.exe, which re-parses its command line, so an argument with
//   one of cmd's special characters is refused instead of escaped.

using System.Diagnostics;
using System.IO;
using System.Text;

namespace UniversalPanel.Native;

/// <summary>What an action runs against: a process (its PID and image) or a file.</summary>
internal sealed record ToolTarget(int? Pid, string? Path)
{
    public static ToolTarget ForProcess(int pid, string? imagePath) => new(pid, imagePath);

    public static ToolTarget ForFile(string path) => new(null, path);

    /// <summary>
    /// Stand-in values for the Edit page's preview, matching what the action really gets: a process
    /// action gets a PID and its exe; a file action (a process's exe or a module) only a path.
    /// </summary>
    public static ToolTarget SampleFor(string actionTarget) => actionTarget switch
    {
        ToolTargets.Process => new(1234, @"C:\Program Files\Example App\example.exe"),
        ToolTargets.File => new(null, @"C:\Program Files\Example App\example.exe"),
        _ => new(null, null),
    };
}

/// <summary>A resolved command: the program, its arguments (one string each) and where to run it.</summary>
internal sealed record ToolCommand(string Program, IReadOnlyList<string> Arguments, string WorkingDirectory, bool Console, bool Raw = false)
{
    private static readonly string Wsl = System.IO.Path.Combine(Environment.SystemDirectory, "wsl.exe");
    private static readonly char[] CmdSpecial = ['%', '!', '^', '&', '|', '<', '>', '"', '\r', '\n'];

    /// <summary>The command line as Windows will see it (arguments quoted, unless the action asked for them as typed).</summary>
    public string ArgumentLine => string.Join(' ', Raw ? Arguments : Arguments.Select(Quote));

    public string Display => $"{Quote(Program)} {ArgumentLine}".TrimEnd();

    /// <summary>
    /// Builds the command. <paramref name="toWsl"/> converts a Windows path for a WSL tool; pass
    /// null to show the conversion as "wslpath(...)" (the preview doesn't start WSL).
    /// </summary>
    /// <exception cref="ToolCommandException">The action can't run on this target, with a reason.</exception>
    public static ToolCommand Build(string toolPath, ToolAction action, ToolTarget target, Func<string, string>? toWsl)
    {
        bool wsl = WslTool.TryParse(toolPath, out var distro, out var command);
        if (action.Target == ToolTargets.Process && target.Pid is null)
        {
            throw new ToolCommandException("This action needs a process.");
        }

        if (action.Target == ToolTargets.File && string.IsNullOrEmpty(target.Path))
        {
            throw new ToolCommandException("This action needs a file, and the path isn't known (try running the panel as administrator).");
        }

        string Convert(string windowsPath) => !wsl ? windowsPath : toWsl?.Invoke(windowsPath) ?? $"wslpath({windowsPath})";

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["pid"] = target.Pid?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["path"] = target.Path is { } p ? Convert(p) : null,
            ["dir"] = target.Path is { } d && System.IO.Path.GetDirectoryName(d) is { } dir ? Convert(dir) : null,
            ["name"] = target.Path is { } n ? System.IO.Path.GetFileName(n) : null,
        };

        var arguments = new List<string>();
        foreach (var token in Split(action.Args))
        {
            arguments.Add(Fill(token, values));
        }

        bool console = action.Run == ToolRunModes.Console || wsl;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (wsl)
        {
            return new ToolCommand(Wsl, ["-d", distro, "--exec", command, .. arguments], home, console);
        }

        string extension = System.IO.Path.GetExtension(toolPath);
        if ((extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
            && arguments.Any(a => a.IndexOfAny(CmdSpecial) >= 0))
        {
            throw new ToolCommandException("A batch file can't safely get an argument with % ! ^ & | < > or quotes in it.");
        }

        string workingDirectory = console ? home : System.IO.Path.GetDirectoryName(toolPath) ?? home;
        // "As typed" only for window actions: the console runner always passes an argument list.
        return new ToolCommand(toolPath, arguments, workingDirectory, console, Raw: !action.Quote && !console);
    }

    /// <summary>Splits like a command line: whitespace separates, "double quotes" group (and are removed).</summary>
    public static List<string> Split(string template)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in template)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }

        if (any)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    private static string Fill(string token, Dictionary<string, string?> values)
    {
        var result = new StringBuilder();
        for (int i = 0; i < token.Length; i++)
        {
            int close = token[i] == '{' ? token.IndexOf('}', i + 1) : -1;
            string word = close > i ? token[(i + 1)..close] : string.Empty;
            if (close > i && values.TryGetValue(word, out var value))
            {
                result.Append(value ?? throw new ToolCommandException(Missing(word)));
                i = close;
            }
            else if (word.Length > 0 && word.All(char.IsAsciiLetter))
            {
                // A typo like {file} would otherwise reach the tool as literal text.
                throw new ToolCommandException($"{{{word}}} isn't a placeholder. Use {{pid}}, {{path}}, {{dir}} or {{name}}.");
            }
            else
            {
                result.Append(token[i]); // other braces ({0}, JSON) stay as typed
            }
        }

        return result.ToString();
    }

    private static string Missing(string word) => word.ToLowerInvariant() == "pid"
        ? "{pid} only works when the action runs on a process (a file or module has no PID)."
        : $"{{{word}}} has no value here: the file's path isn't known.";

    /// <summary>Quotes one argument so CommandLineToArgvW / the MSVC runtime read it back unchanged.</summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes before a quote are doubled, and the quote itself is escaped.
            quoted.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            backslashes = 0;
            quoted.Append(c);
        }

        quoted.Append('\\', backslashes * 2); // before the closing quote
        return quoted.Append('"').ToString();
    }

    /// <summary>Converts a Windows path with the distro's own wslpath (honors custom mount points).</summary>
    public static string WslPath(string distro, string windowsPath)
    {
        var start = new ProcessStartInfo(Wsl)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-d", distro, "--exec", "wslpath", "-a", windowsPath })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new ToolCommandException("Couldn't start WSL.");
        string output = process.StandardOutput.ReadToEnd().Trim();
        if (!process.WaitForExit(10_000) || process.ExitCode != 0 || output.Length == 0)
        {
            throw new ToolCommandException($"WSL ({distro}) couldn't convert {windowsPath}.");
        }

        return output;
    }
}

internal sealed class ToolCommandException(string message) : Exception(message);
