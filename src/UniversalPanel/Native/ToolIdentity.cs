// Identifies known reverse-engineering tools from what the file says about itself (version-info
// company / product, Ghidra's application.properties) and, where the publisher signs, from the
// Authenticode signer. Values were read from real installs (IDA Free 9.3, IDA 7.5, DIE 3.10,
// x64dbg 2025-08-19 snapshot, System Informer 4.0, PE-bear 0.7.1, CFF Explorer 8) and from
// Ghidra's repository (Ghidra/application.properties). Entries for tools not installed here
// match on product name only, which is what their version info is known to carry.
//
// Verified   = identity matches and the file is validly signed by the expected publisher.
// Identified = identity matches; the publisher doesn't sign (IDA, DIE, Ghidra, PE-bear ...).
// Warning    = identity matches but the signature is broken or from someone else.
// A file can claim any version info, so Identified is a label, not proof; only Verified is.

using System.Diagnostics;
using System.IO;

namespace UniversalPanel.Native;

internal enum ToolTrust { Unknown, Identified, Verified, Warning }

internal sealed record ToolIdentity(string Name, string? Version, ToolTrust Trust, string Detail, string? Publisher);

internal static class ToolCatalog
{
    private sealed record Known(string Name, Func<FileVersionInfo, bool> Matches, string? Signer = null, bool VersionFromBuildDate = false);

    private static bool Has(string? value, string text) => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    private static readonly Known[] Tools =
    [
        new("IDA", v => v.CompanyName == "Hex-Rays SA" && Has(v.ProductName, "Interactive Disassembler")),
        new("Detect It Easy", v => v.CompanyName == "NTInfo" && v.ProductName == "Detect It Easy"),
        new("x64dbg", v => v.ProductName == "x64dbg", Signer: "Duncan Ogilvie", VersionFromBuildDate: true),
        new("System Informer", v => v.ProductName == "System Informer", Signer: "Winsider Seminars & Solutions"),
        new("Process Hacker", v => v.ProductName == "Process Hacker"),
        new("PE-bear", v => v.ProductName == "PE-bear"),
        new("CFF Explorer", v => v.ProductName == "CFF Explorer"),
        new("HxD", v => Has(v.ProductName, "HxD")),
        new("Binary Ninja", v => Has(v.ProductName, "Binary Ninja"), Signer: "Vector 35"),
        new("dnSpy", v => Has(v.ProductName, "dnSpy")),
        new("ILSpy", v => Has(v.ProductName, "ILSpy")),
        new("ImHex", v => Has(v.ProductName, "ImHex")),
        new("Cutter", v => Has(v.ProductName, "Cutter")),
        new("WinDbg", v => Has(v.ProductName, "WinDbg") || Has(v.FileDescription, "WinDbg"), Signer: "Microsoft"),
        new("Sysinternals", v => Has(v.CompanyName, "Sysinternals"), Signer: "Microsoft"),
        new("Wireshark", v => v.ProductName == "Wireshark", Signer: "Wireshark Foundation"),
        new("Postman", v => v.ProductName == "Postman", Signer: "Postman"),
    ];

    /// <summary>What the file is; never throws (unreadable files come back Unknown).</summary>
    public static ToolIdentity Identify(string path)
    {
        try
        {
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
            var known = Tools.FirstOrDefault(k => k.Matches(info));
            string? version = Clean(info.ProductVersion) ?? Clean(info.FileVersion);
            if (known is null)
            {
                string name = Clean(info.FileDescription) ?? Clean(info.ProductName) ?? Path.GetFileNameWithoutExtension(path);
                return new(name, version, ToolTrust.Unknown, trust.Text, Clean(info.CompanyName));
            }

            if (known.VersionFromBuildDate && PeHeaders.Read(path)?.LinkTime is { } built)
            {
                version = $"{built:yyyy-MM-dd}"; // snapshots all report 0.0.2.5; the build date is the real version
            }

            // Sysinternals tools are one entry per tool; keep their own name.
            string display = known.Name == "Sysinternals" ? Clean(info.FileDescription) ?? known.Name : known.Name;
            var (state, detail) = Judge(known, trust);
            return new(display, version, state, detail, Clean(info.CompanyName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(Path.GetFileNameWithoutExtension(path), null, ToolTrust.Unknown, "Can't read the file", null);
        }
    }

    private static (ToolTrust, string) Judge(Known known, FileTrustInfo trust)
    {
        if (trust.State == TrustState.Invalid)
        {
            return (ToolTrust.Warning, "Signature is broken: the file was modified after signing");
        }

        if (!trust.IsTrusted)
        {
            return known.Signer is null
                ? (ToolTrust.Identified, "Identified from its version info (the publisher doesn't sign it)")
                : (ToolTrust.Identified, $"Identified from its version info, but unsigned (official builds are signed by {known.Signer})");
        }

        if (known.Signer is null || Has(trust.Signer, known.Signer))
        {
            return (ToolTrust.Verified, $"Verified: signed by {trust.Signer}");
        }

        return (ToolTrust.Warning, $"Signed by {trust.Signer}, not by {known.Signer}");
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

        return new("Ghidra", version, ToolTrust.Identified, "Identified from Ghidra\\application.properties (Ghidra isn't signed)", "NSA");
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
