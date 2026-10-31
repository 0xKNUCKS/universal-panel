// What a process was built with, from a subset of Detect It Easy's PE signatures (db/PE/*.sg):
// compiler_Go, compiler_Rust, compiler_Delphi, compiler_Nim, compiler_Zig, compiler_MinGW,
// packer_PyInstaller, packer_Nuitka, library_Python, library_Qt, library_ElectronApp,
// format_AutoIt, plus the Rich header DIE uses to tell MSVC-linked images apart.
// https://github.com/horsicq/Detect-It-Easy @ adc0617abd4d35e7449c767f62cf6ae276c67762
// Simplified: markers are searched in whole sections instead of DIE's exact offsets, and
// runtime DLLs come from the live module list instead of the import table. Where a rule
// misfired on this machine it is tightened (noted at the rule): see PlatformDetect in REFERENCES.md.

using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace UniversalPanel.Native;

internal static partial class PlatformDetect
{
    private const int MaxSectionBytes = 64 * 1024 * 1024;

    // compiler_Go.4.sg: "ff20'Go build ID: '"; compiler_Delphi.4.sg: "0708'TControl'" (VCL RTTI).
    private static readonly byte[] GoBuildId = [0xFF, 0x20, .. "Go build ID: "u8.ToArray()];
    private static readonly byte[] DelphiControl = [0x07, 0x08, .. "TControl"u8.ToArray()];

    /// <summary>e.g. "Rust (MSVC)", ".NET (CoreCLR)", "C/C++ (MSVC), Qt 6"; null if the image can't be read.</summary>
    public static string? Describe(string? imagePath, IReadOnlyCollection<string> moduleNames)
    {
        bool Loaded(string name) => moduleNames.Any(m => m.Equals(name, StringComparison.OrdinalIgnoreCase));
        var extras = new List<string>();
        if (moduleNames.FirstOrDefault(m => QtCore().IsMatch(m)) is { } qt)
        {
            extras.Add($"Qt {QtCore().Match(qt).Groups[1].Value}");
        }

        // A managed process may run from a native host (apphost, dotnet.exe), so the runtime decides.
        if (Loaded("coreclr.dll"))
        {
            return Join(".NET (CoreCLR)", extras);
        }

        if (Loaded("clr.dll") || Loaded("mscorwks.dll"))
        {
            return Join(".NET Framework", extras);
        }

        if (imagePath is null)
        {
            return null;
        }

        Image image;
        try
        {
            image = Image.Read(imagePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }

        if (image.IsManaged)
        {
            return Join(".NET", extras);
        }

        // library_ElectronApp.4.sg: CPADinfo is Chromium's section, so it marks any Chromium
        // embedder (Edge, CEF games); Electron is told apart by its ELECTRON_RUN_AS_NODE switch.
        if (image.Has(".rdata", "CPADinfo") && image.Section("CPADinfo").IndexOf("daPC"u8) >= 0)
        {
            extras.Add(image.Section(".rdata").IndexOf("ELECTRON_RUN_AS_NODE"u8) >= 0 ? "Electron" : "Chromium");
        }

        string? python = moduleNames.Select(m => PythonDll().Match(m)).FirstOrDefault(m => m.Success) is { } py
            ? $"Python {py.Groups[1].Value}.{py.Groups[2].Value}"
            : null;
        var rdata = image.Section(".rdata");
        var text = image.First();

        string language =
            text.IndexOf(GoBuildId) >= 0 ? "Go"
            : HasRustSources(rdata) || moduleNames.Any(m => RustStd().IsMatch(m)) ? image.Rich ? "Rust (MSVC)" : "Rust"
            : text.IndexOf(DelphiControl) >= 0 ? "Delphi (VCL)"
            : image.ZlibOverlay && rdata.IndexOf("PyInstaller: FormatMessageW failed."u8) >= 0 ? $"{python ?? "Python"} (PyInstaller)"
            : (python is not null && rdata.IndexOf("__nuitka_version__"u8) >= 0) || rdata.IndexOf("NUITKA_ONEFILE_PARENT"u8) >= 0 ? $"{python ?? "Python"} (Nuitka)"
            : python is not null ? python
            : IsAutoIt(imagePath, image) ? "AutoIt"
            : Loaded("jvm.dll") ? "Java"
            : !image.Rich && (rdata.IndexOf("io.nim"u8) >= 0 || rdata.IndexOf("fatal.nim"u8) >= 0) ? "Nim"
            : rdata.IndexOf(Encoding.Unicode.GetBytes("ZIG_DEBUG_COLOR")) >= 0 ? "Zig"
            : !image.Rich && (rdata.IndexOf("GCC: "u8) >= 0 || rdata.IndexOf("gcc-"u8) >= 0) ? "C/C++ (MinGW)"
            : image.Rich ? "C/C++ (MSVC)"
            : "Native";
        return Join(language, extras);
    }

    private static string Join(string language, List<string> extras) =>
        extras.Count == 0 ? language : $"{language}, {string.Join(", ", extras)}";

    // compiler_Rust.4.sg: a "mod.rs" source path (after a path separator) in the first 0x5000
    // bytes of .rdata. Only the start: C++ apps that embed Rust crates (Chromium) have them later.
    private static bool HasRustSources(ReadOnlySpan<byte> rdata)
    {
        var head = rdata[..Math.Min(rdata.Length, 0x5000)];
        return head.IndexOf("\\mod.rs"u8) >= 0 || head.IndexOf("/mod.rs"u8) >= 0;
    }

    // format_AutoIt.3.sg: a resource named SCRIPT, or the compiler's file description. DIE reads the
    // resource directory; this looks for the name in .rsrc and also wants AutoIt's "AU3!" script
    // header, so a stray "SCRIPT" string alone doesn't count.
    private static bool IsAutoIt(string path, Image image)
    {
        if (image.Section(".rsrc").IndexOf(Encoding.Unicode.GetBytes("SCRIPT")) >= 0
            && image.Section(".rsrc").IndexOf("AU3!"u8) >= 0)
        {
            return true;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileDescription == "Compiled AutoIt Script";
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^Qt(\d)Core\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex QtCore();

    [GeneratedRegex(@"^python(\d)(\d+)\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex PythonDll();

    [GeneratedRegex(@"^std-[0-9a-f]{8,16}\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex RustStd();

    /// <summary>The parts of the PE file the rules look at: section bytes and the Rich header.</summary>
    private sealed class Image
    {
        private readonly Dictionary<string, byte[]> _sections = new(StringComparer.Ordinal);
        private byte[] _first = [];

        public bool IsManaged { get; private set; }

        /// <summary>Data appended after the last section starts with a zlib header (78 DA).</summary>
        public bool ZlibOverlay { get; private set; }

        /// <summary>The undocumented "Rich" block MSVC's linker writes after the DOS stub.</summary>
        public bool Rich { get; private set; }

        public static Image Read(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new PEReader(stream);
            var headers = reader.PEHeaders;
            var image = new Image { IsManaged = headers.CorHeader is not null };

            var stub = new byte[Math.Clamp(headers.PEHeaderStartOffset, 0, 0x1000)];
            stream.Position = 0;
            stream.ReadExactly(stub);
            image.Rich = stub.AsSpan().IndexOf("Rich"u8) >= 0x40;

            foreach (var section in headers.SectionHeaders)
            {
                if (section.SizeOfRawData <= 0 || image._sections.ContainsKey(section.Name))
                {
                    continue;
                }

                var bytes = new byte[Math.Min(section.SizeOfRawData, MaxSectionBytes)];
                stream.Position = section.PointerToRawData;
                int read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                Array.Resize(ref bytes, read);
                image._sections[section.Name] = bytes;
                if (image._first.Length == 0)
                {
                    image._first = bytes;
                }
            }

            long overlay = headers.SectionHeaders.Max(h => (long)h.PointerToRawData + h.SizeOfRawData);
            if (overlay + 2 <= stream.Length)
            {
                Span<byte> magic = stackalloc byte[2];
                stream.Position = overlay;
                stream.ReadExactly(magic);
                image.ZlibOverlay = magic[0] == 0x78 && magic[1] == 0xDA;
            }

            return image;
        }

        public bool Has(params string[] names) => names.All(_sections.ContainsKey);

        public ReadOnlySpan<byte> Section(string name) => _sections.TryGetValue(name, out var bytes) ? bytes : [];

        public ReadOnlySpan<byte> First() => _first;
    }
}
