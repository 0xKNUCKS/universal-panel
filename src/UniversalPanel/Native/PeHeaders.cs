using System.IO;
using System.Reflection.PortableExecutable;

namespace UniversalPanel.Native;

/// <summary>Headers of a PE file on disk, read with the runtime's own PEReader.</summary>
internal sealed record PeHeaders(DateTime? LinkTime, string Machine, ulong PreferredBase, uint EntryPointRva, bool IsDotNet)
{
    /// <summary>Reads the file's headers; null if it isn't a readable PE file.</summary>
    public static PeHeaders? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new PEReader(stream);
            var headers = reader.PEHeaders;
            var coff = headers.CoffHeader;
            var optional = headers.PEHeader;
            if (optional is null)
            {
                return null;
            }

            // Reproducible builds store a hash, not a time, in TimeDateStamp; only show plausible dates.
            DateTime? linkTime = DateTimeOffset.FromUnixTimeSeconds((uint)coff.TimeDateStamp).UtcDateTime;
            if (linkTime < new DateTime(1995, 1, 1) || linkTime > DateTime.UtcNow.AddDays(1))
            {
                linkTime = null;
            }

            string machine = coff.Machine switch
            {
                System.Reflection.PortableExecutable.Machine.Amd64 => "x64",
                System.Reflection.PortableExecutable.Machine.I386 => "x86",
                System.Reflection.PortableExecutable.Machine.Arm64 => "ARM64",
                System.Reflection.PortableExecutable.Machine.ArmThumb2 => "ARM",
                var m => m.ToString(),
            };

            return new PeHeaders(linkTime, machine, optional.ImageBase, (uint)optional.AddressOfEntryPoint, headers.CorHeader is not null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }
    }
}
