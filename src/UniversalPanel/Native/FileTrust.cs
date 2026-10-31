// Mirrors System Informer (MIT), phlib/verify.c PhVerifyFile:
//   1. WinVerifyTrust(WINTRUST_ACTION_GENERIC_VERIFY_V2) on the file's embedded signature;
//   2. if unsigned, look the file's hash up in the Windows catalogs (CryptCATAdminAcquireContext2
//      with SHA256, falling back to SHA1; CalcHashFromFileHandle2; EnumCatalogFromHash) and verify
//      the catalog member with DRIVER_ACTION_VERIFY.
//   No network: WTD_REVOKE_NONE + WTD_CACHE_ONLY_URL_RETRIEVAL (SI's PH_VERIFY_PREVENT_NETWORK_ACCESS).
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d
// The Win32 structs and functions are declared by hand from wintrust.h / mscat.h.

using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace UniversalPanel.Native;

internal enum TrustState { Signed, SignedCatalog, Unsigned, Invalid }

internal sealed record FileTrustInfo(TrustState State, string? Signer)
{
    public bool IsTrusted => State is TrustState.Signed or TrustState.SignedCatalog;

    public string Text => State switch
    {
        TrustState.Signed or TrustState.SignedCatalog => Signer ?? "Signed",
        TrustState.Unsigned => "Unsigned",
        _ => "Invalid signature",
    };
}

internal static unsafe partial class FileTrust
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private static readonly Guid DriverActionVerify = new("F750E6C3-38EE-11d1-85E5-00C04FC295EE");
    private const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1, WTD_CHOICE_CATALOG = 2;
    private const uint WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_SAFER_FLAG = 0x100, WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000, WTD_DISABLE_MD2_MD4 = 0x2000;
    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);

    private static readonly Dictionary<(string, DateTime), FileTrustInfo> Cache = [];
    private static readonly Lock Gate = new();

    /// <summary>Verifies a file; results are cached per path and last-write time. Any thread.</summary>
    public static FileTrustInfo Verify(string path)
    {
        DateTime stamp;
        try
        {
            stamp = System.IO.File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return new(TrustState.Invalid, null);
        }

        lock (Gate)
        {
            if (Cache.TryGetValue((path, stamp), out var cached))
            {
                return cached;
            }
        }

        var result = VerifyEmbedded(path) ?? VerifyCatalog(path) ?? new FileTrustInfo(TrustState.Unsigned, null);
        lock (Gate)
        {
            Cache[(path, stamp)] = result;
        }

        return result;
    }

    /// <summary>Null when the file carries no embedded signature (then try catalogs).</summary>
    private static FileTrustInfo? VerifyEmbedded(string path)
    {
        fixed (char* file = path)
        {
            var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = file };
            int status = RunWinVerifyTrust(GenericVerifyV2, WTD_CHOICE_FILE, &fileInfo);
            if (status is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN)
            {
                return null;
            }

            return status == 0 ? new(TrustState.Signed, SignerOf(path)) : new(TrustState.Invalid, SignerOf(path));
        }
    }

    private static FileTrustInfo? VerifyCatalog(string path)
    {
        using var stream = TryOpen(path);
        if (stream is null)
        {
            return null;
        }

        byte* hash = stackalloc byte[64]; // SHA256 needs 32, SHA1 20
        foreach (var algorithm in new[] { "SHA256", null })
        {
            nint admin = 0;
            var policy = new CERT_STRONG_SIGN_PARA { cbSize = (uint)sizeof(CERT_STRONG_SIGN_PARA), dwInfoChoice = 2 /* OID */ };
            fixed (byte* oid = "1.3.6.1.4.1.311.72.1.1\0"u8) // szOID_CERT_STRONG_SIGN_OS_CURRENT
            fixed (char* alg = algorithm)
            {
                policy.pszOID = oid;
                var actionId = DriverActionVerify;
                if (!CryptCATAdminAcquireContext2(&admin, &actionId, alg, &policy, 0))
                {
                    continue;
                }
            }

            try
            {
                uint hashLength = 0;
                nint fileHandle = stream.SafeFileHandle.DangerousGetHandle();
                CryptCATAdminCalcHashFromFileHandle2(admin, fileHandle, &hashLength, null, 0);
                if (hashLength == 0 || hashLength > 64)
                {
                    continue;
                }

                if (!CryptCATAdminCalcHashFromFileHandle2(admin, fileHandle, &hashLength, hash, 0))
                {
                    continue;
                }

                nint catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, hashLength, 0, null);
                if (catalog == 0)
                {
                    continue;
                }

                try
                {
                    var info = new CATALOG_INFO { cbStruct = (uint)sizeof(CATALOG_INFO) };
                    if (!CryptCATCatalogInfoFromContext(catalog, &info, 0))
                    {
                        continue;
                    }

                    string catalogPath = new(info.wszCatalogFile);
                    string memberTag = Convert.ToHexString(new ReadOnlySpan<byte>(hash, (int)hashLength));
                    fixed (char* catalogFile = catalogPath)
                    fixed (char* member = path)
                    fixed (char* tag = memberTag)
                    {
                        var catalogInfo = new WINTRUST_CATALOG_INFO
                        {
                            cbStruct = (uint)sizeof(WINTRUST_CATALOG_INFO),
                            pcwszCatalogFilePath = catalogFile,
                            pcwszMemberFilePath = member,
                            pcwszMemberTag = tag,
                            hMemberFile = fileHandle,
                            pbCalculatedFileHash = hash,
                            cbCalculatedFileHash = hashLength,
                            hCatAdmin = admin,
                        };
                        int status = RunWinVerifyTrust(DriverActionVerify, WTD_CHOICE_CATALOG, &catalogInfo);
                        return status == 0
                            ? new(TrustState.SignedCatalog, SignerOf(catalogPath))
                            : new(TrustState.Invalid, SignerOf(catalogPath));
                    }
                }
                finally
                {
                    CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
                }
            }
            finally
            {
                CryptCATAdminReleaseContext(admin, 0);
            }
        }

        return null;
    }

    private static int RunWinVerifyTrust(Guid action, uint choice, void* union)
    {
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)sizeof(WINTRUST_DATA),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = choice,
            pUnion = union,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_SAFER_FLAG | WTD_DISABLE_MD2_MD4 | WTD_CACHE_ONLY_URL_RETRIEVAL,
        };
        int status = WinVerifyTrust(-1, &action, &data);
        data.dwStateAction = WTD_STATEACTION_CLOSE;
        _ = WinVerifyTrust(-1, &action, &data); // releases the state; its result carries nothing
        return status;
    }

    /// <summary>Leaf signer's common name from a file's (or catalog's) embedded signature.</summary>
    private static string? SignerOf(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // No replacement API reads the Authenticode signer of a PE/catalog.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static System.IO.FileStream? TryOpen(string path)
    {
        try
        {
            return new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public char* pcwszFilePath;
        public nint hFile;
        public Guid* pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public char* pcwszCatalogFilePath;
        public char* pcwszMemberTag;
        public char* pcwszMemberFilePath;
        public nint hMemberFile;
        public byte* pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public void* pcCatalogContext;
        public nint hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public void* pPolicyCallbackData;
        public void* pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public void* pUnion;
        public uint dwStateAction;
        public nint hWVTStateData;
        public char* pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public void* pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CERT_STRONG_SIGN_PARA
    {
        public uint cbSize;
        public uint dwInfoChoice;
        public byte* pszOID; // union with pSerializedInfo / pszOID
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CATALOG_INFO
    {
        public uint cbStruct;
        public fixed char wszCatalogFile[260];
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint hwnd, Guid* actionId, WINTRUST_DATA* data);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminAcquireContext2(nint* catAdmin, Guid* subsystem, char* hashAlgorithm, CERT_STRONG_SIGN_PARA* strongHashPolicy, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminCalcHashFromFileHandle2(nint catAdmin, nint file, uint* hashLength, byte* hash, uint flags);

    [LibraryImport("wintrust.dll")]
    private static partial nint CryptCATAdminEnumCatalogFromHash(nint catAdmin, byte* hash, uint hashLength, uint flags, nint* prevCatInfo);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATCatalogInfoFromContext(nint catInfo, CATALOG_INFO* info, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminReleaseCatalogContext(nint catAdmin, nint catInfo, uint flags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminReleaseContext(nint catAdmin, uint flags);
}
