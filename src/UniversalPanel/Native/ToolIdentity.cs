// Identifies known tools from what the file says about itself (version-info fields, Ghidra's
// application.properties) and, where the publisher signs, from the Authenticode signer. The rules
// live in Catalog/tools.json (see docs/TOOLS.md), not in code, so adding a tool is a data change.
// Values were read from real installs (IDA Free 9.3, IDA 7.5, DIE 3.10, x64dbg 2025-08-19,
// System Informer 4.0, PE-bear 0.7.1, CFF Explorer 8, a Kali WSL distro) and from Ghidra's
// repository (Ghidra/application.properties).
//
// Verified   = identity matches and the file is validly signed by the expected publisher.
// Identified = identity matches; the publisher doesn't sign (IDA, DIE, Ghidra, PE-bear, WSL tools).
// Warning    = identity matches but the signature is broken or from someone else.
// A file can claim any version info, so Identified is a label, not proof; only Verified is.

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using UniversalPanel.Diagnostics;

namespace UniversalPanel.Native;

internal enum ToolTrust { Unknown, Identified, Verified, Warning }

/// <param name="Known">The catalog entry it matched (its "id"), or null.</param>
internal sealed record ToolIdentity(string Name, string? Version, ToolTrust Trust, string Detail, string? Publisher, string? Known = null);

/// <summary>One action a tool can run, e.g. "Attach x64dbg" with "-p {pid}".</summary>
internal sealed class ToolAction
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>"process" (needs {pid}), "file" ({path}, {dir}, {name}) or "none".</summary>
    public string Target { get; set; } = ToolTargets.None;

    /// <summary>"window" (a normal app) or "console" (output shown in a console window).</summary>
    public string Run { get; set; } = ToolRunModes.Window;

    public string Args { get; set; } = string.Empty;

    public ToolAction Clone() => (ToolAction)MemberwiseClone();
}

internal static class ToolTargets
{
    public const string Process = "process", File = "file", None = "none";
}

internal static class ToolRunModes
{
    public const string Window = "window", Console = "console";
}

/// <summary>One entry of Catalog/tools.json.</summary>
internal sealed class CatalogTool
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> Categories { get; set; } = [];
    public Dictionary<string, string> Match { get; set; } = [];
    public string? Signer { get; set; }
    public string? Version { get; set; }
    public string? Detect { get; set; }
    public string? NameFrom { get; set; }
    public List<string> Files { get; set; } = [];
    public string? Wsl { get; set; }
    public List<ToolAction> Actions { get; set; } = [];
}

internal static class ToolCatalog
{
    private static readonly Lazy<List<CatalogTool>> Catalog = new(Load);

    public static IReadOnlyList<CatalogTool> Tools => Catalog.Value;

    public static CatalogTool? Find(string? id) => id is null ? null : Tools.FirstOrDefault(t => t.Id == id);

    /// <summary>The sections a tool goes in by default (the user can change them per tool).</summary>
    public static IReadOnlyList<string> DefaultCategories(string? known) =>
        Find(known) is { Categories.Count: > 0 } tool ? tool.Categories : [ToolCategories.Other];

    /// <summary>The catalog's default actions for a tool (empty for tools it doesn't know).</summary>
    public static IReadOnlyList<ToolAction> DefaultActions(string? known) => Find(known)?.Actions ?? [];

    private static List<CatalogTool> Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("tools.catalog.json")
                ?? throw new FileNotFoundException("tools.catalog.json is missing from the build");
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var root = JsonSerializer.Deserialize<CatalogFile>(stream, options);
            return root?.Tools.Where(t => t.Id.Length > 0 && t.Name.Length > 0).ToList() ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Error("Tools", "Couldn't read the tool catalog; no tools will be recognized.", ex);
            return [];
        }
    }

    private sealed class CatalogFile
    {
        public List<CatalogTool> Tools { get; set; } = [];
    }

    /// <summary>What the file (or "wsl:distro:command" entry) is; never throws.</summary>
    public static ToolIdentity Identify(string path)
    {
        try
        {
            if (WslTool.TryParse(path, out var distro, out var command))
            {
                return IdentifyWsl(distro, command);
            }

            if (Path.GetFileName(path).Equals("ghidraRun.bat", StringComparison.OrdinalIgnoreCase))
            {
                return IdentifyGhidra(path);
            }

            if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return new(Path.GetFileNameWithoutExtension(path), null, ToolTrust.Unknown, "Not an executable", null);
            }

            var info = FileVersionInfo.GetVersionInfo(path);
            var trust = FileTrust.Verify(path);
            var tool = Tools.FirstOrDefault(t => t.Match.Count > 0 && t.Match.All(m => Matches(Field(info, m.Key), m.Value)));
            string? version = Clean(info.ProductVersion) ?? Clean(info.FileVersion);
            if (tool is null)
            {
                string name = Clean(info.FileDescription) ?? Clean(info.ProductName) ?? Path.GetFileNameWithoutExtension(path);
                return new(name, version, ToolTrust.Unknown, trust.Text, Clean(info.CompanyName));
            }

            if (tool.Version == "buildDate" && PeHeaders.Read(path)?.LinkTime is { } built)
            {
                version = $"{built:yyyy-MM-dd}"; // e.g. x64dbg snapshots all report 0.0.2.5
            }

            string display = tool.NameFrom is { } field ? Clean(Field(info, field)) ?? tool.Name : tool.Name;
            var (state, detail) = Judge(tool, trust);
            return new(display, version, state, detail, Clean(info.CompanyName), tool.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(Path.GetFileNameWithoutExtension(path), null, ToolTrust.Unknown, "Can't read the file", null);
        }
    }

    private static string? Field(FileVersionInfo info, string name) => name switch
    {
        "CompanyName" => info.CompanyName,
        "ProductName" => info.ProductName,
        "FileDescription" => info.FileDescription,
        "OriginalFilename" => info.OriginalFilename,
        "InternalName" => info.InternalName,
        "LegalCopyright" => info.LegalCopyright,
        _ => null,
    };

    /// <summary>Case-insensitive match where "*" stands for anything.</summary>
    private static bool Matches(string? value, string pattern) =>
        value is not null && Regex.IsMatch(value.Trim(), "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static (ToolTrust, string) Judge(CatalogTool tool, FileTrustInfo trust)
    {
        if (trust.State == TrustState.Invalid)
        {
            return (ToolTrust.Warning, "Signature is broken: the file was modified after signing");
        }

        if (!trust.IsTrusted)
        {
            return tool.Signer is null
                ? (ToolTrust.Identified, "Identified from its version info (the publisher doesn't sign it)")
                : (ToolTrust.Identified, $"Identified from its version info, but unsigned (official builds are signed by {tool.Signer})");
        }

        if (tool.Signer is null || trust.Signer?.Contains(tool.Signer, StringComparison.OrdinalIgnoreCase) == true)
        {
            return (ToolTrust.Verified, $"Verified: signed by {trust.Signer}");
        }

        return (ToolTrust.Warning, $"Signed by {trust.Signer}, not by {tool.Signer}");
    }

    // A tool inside a WSL distro: identified by name only (there's no Authenticode inside Linux).
    private static ToolIdentity IdentifyWsl(string distro, string command)
    {
        var tool = Tools.FirstOrDefault(t => t.Wsl == command);
        return new(tool?.Name ?? command, distro, tool is null ? ToolTrust.Unknown : ToolTrust.Identified,
            $"In WSL ({distro}); runs with \"wsl --exec\", so nothing passes through a shell", distro, tool?.Id);
    }

    // Ghidra (Java) starts from ghidraRun.bat; its name and version are in Ghidra\application.properties.
    private static ToolIdentity IdentifyGhidra(string path)
    {
        var properties = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "Ghidra", "application.properties");
        if (!File.Exists(properties))
        {
            return new("ghidraRun", null, ToolTrust.Unknown, "No Ghidra\\application.properties next to it", null);
        }

        var values = File.ReadLines(properties).Take(200)
            .Select(line => line.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0].Trim())
            .ToDictionary(g => g.Key, g => g.First()[1].Trim());
        if (values.GetValueOrDefault("application.name") != "Ghidra")
        {
            return new("ghidraRun", null, ToolTrust.Unknown, "application.properties isn't Ghidra's", null);
        }

        string? version = values.GetValueOrDefault("application.version");
        if (values.GetValueOrDefault("application.release.name") is { Length: > 0 } release && release != "PUBLIC")
        {
            version += $" {release}";
        }

        var tool = Tools.FirstOrDefault(t => t.Detect == "ghidra");
        return new(tool?.Name ?? "Ghidra", version, ToolTrust.Identified, "Identified from Ghidra\\application.properties (Ghidra isn't signed)", "NSA", tool?.Id ?? "ghidra");
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>WSL tools are saved as "wsl:distro:command" instead of a file path.</summary>
internal static partial class WslTool
{
    public const string Prefix = "wsl:";

    public static string Make(string distro, string command) => $"{Prefix}{distro}:{command}";

    public static bool TryParse(string? value, out string distro, out string command)
    {
        distro = command = string.Empty;
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = value[Prefix.Length..].Split(':');
        if (parts.Length != 2 || !SafeName().IsMatch(parts[0]) || !SafeName().IsMatch(parts[1]))
        {
            return false;
        }

        (distro, command) = (parts[0], parts[1]);
        return true;
    }

    // Distro and command names: letters, digits and . _ + - only (they become wsl.exe arguments).
    [GeneratedRegex(@"^[A-Za-z0-9._+-]{1,64}$")]
    private static partial Regex SafeName();
}
