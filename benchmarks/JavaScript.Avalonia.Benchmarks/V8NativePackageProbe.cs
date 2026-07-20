using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace JavaScript.Avalonia.Benchmarks;

/// <summary>
/// Product-independent packaging contract for the optional patched ClearScript V8 native.
/// It prevents a RID package from accidentally carrying stock or cross-RID assets.
/// </summary>
internal static class V8NativePackageProbe
{
    private const string PatchEntry =
        "build/clearscript-patches/ClearScript-7.5.1-SharedContextSecurityToken.patch";
    private const string TypedManagedAbiPatchEntry =
        "build/clearscript-patches/ClearScript-7.5.1-TypedManagedAbi.patch";
    private const string NativeHashEntry = "build/clearscript-native.sha256";
    private const string PatchHashEntry = "build/clearscript-patch.sha256";
    private const string ProvenanceEntry = "build/clearscript-provenance.txt";

    private static readonly IReadOnlyDictionary<string, string> NativeNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["win-x86"] = "ClearScriptV8.win-x86.dll",
            ["win-x64"] = "ClearScriptV8.win-x64.dll",
            ["win-arm64"] = "ClearScriptV8.win-arm64.dll",
            ["linux-x64"] = "ClearScriptV8.linux-x64.so",
            ["linux-arm"] = "ClearScriptV8.linux-arm.so",
            ["linux-arm64"] = "ClearScriptV8.linux-arm64.so",
            ["osx-x64"] = "ClearScriptV8.osx-x64.dylib",
            ["osx-arm64"] = "ClearScriptV8.osx-arm64.dylib"
        };

    internal static int Run(string[] args)
    {
        var packagePath = ReadOption(args, "--package");
        var rid = ReadOption(args, "--rid");
        if (string.IsNullOrWhiteSpace(packagePath) || string.IsNullOrWhiteSpace(rid))
        {
            Console.Error.WriteLine("Usage: probe v8nativepackage --package <nupkg> --rid <rid>");
            return 1;
        }

        if (!NativeNames.TryGetValue(rid, out var nativeName))
        {
            Console.Error.WriteLine($"Unsupported ClearScript native RID '{rid}'.");
            return 1;
        }

        if (!File.Exists(packagePath))
        {
            Console.Error.WriteLine($"Package does not exist: {packagePath}");
            return 1;
        }

        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var expectedNativeEntry = $"runtimes/{rid}/native/{nativeName}";
            var nativeEntries = archive.Entries
                .Where(entry => Path.GetFileName(entry.FullName)
                    .StartsWith("ClearScriptV8.", StringComparison.Ordinal))
                .ToArray();

            Require(nativeEntries.Length == 1,
                $"expected one ClearScript V8 native entry, found {nativeEntries.Length}");
            Require(string.Equals(nativeEntries[0].FullName, expectedNativeEntry, StringComparison.Ordinal),
                $"native entry '{nativeEntries[0].FullName}' does not match '{expectedNativeEntry}'");
            ValidateNativeArchitecture(nativeEntries[0], rid);

            var patch = RequireEntry(archive, PatchEntry);
            var typedManagedAbiPatch = RequireEntry(archive, TypedManagedAbiPatchEntry);
            var nativeHash = ReadHash(RequireEntry(archive, NativeHashEntry));
            var patchHashes = ReadNamedHashes(RequireEntry(archive, PatchHashEntry));
            var provenance = ReadText(RequireEntry(archive, ProvenanceEntry));

            Require(string.Equals(ComputeSha256(nativeEntries[0]), nativeHash, StringComparison.OrdinalIgnoreCase),
                "native SHA-256 does not match package metadata");
            Require(
                patchHashes.TryGetValue(Path.GetFileName(PatchEntry), out var patchHash)
                && string.Equals(ComputeSha256(patch), patchHash, StringComparison.OrdinalIgnoreCase),
                "security-token patch SHA-256 does not match package metadata");
            Require(
                patchHashes.TryGetValue(
                    Path.GetFileName(TypedManagedAbiPatchEntry),
                    out var typedManagedAbiPatchHash)
                && string.Equals(
                    ComputeSha256(typedManagedAbiPatch),
                    typedManagedAbiPatchHash,
                    StringComparison.OrdinalIgnoreCase),
                "typed managed ABI patch SHA-256 does not match package metadata");
            Require(provenance.Contains("ClearScript source tag: 7.5.1", StringComparison.Ordinal),
                "provenance does not pin ClearScript 7.5.1");
            Require(provenance.Contains("V8 revision: 14.7.173.23", StringComparison.Ordinal),
                "provenance does not pin the tested V8 revision");
            Require(provenance.Contains($"RID: {rid}", StringComparison.Ordinal),
                "provenance RID does not match the package RID");

            Console.WriteLine(
                $"V8 native package: pass; rid={rid}, native={nativeName}, " +
                $"bytes={nativeEntries[0].Length}, sha256={nativeHash}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"V8 native package: fail; {exception.Message}");
            return 1;
        }
    }

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index + 1 < args.Count; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static ZipArchiveEntry RequireEntry(ZipArchive archive, string name)
        => archive.GetEntry(name)
           ?? throw new InvalidDataException($"required package entry is missing: {name}");

    private static string ReadHash(ZipArchiveEntry entry)
    {
        var text = ReadText(entry);
        var token = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return !string.IsNullOrWhiteSpace(token)
            ? token
            : throw new InvalidDataException($"hash file is empty: {entry.FullName}");
    }

    private static IReadOnlyDictionary<string, string> ReadNamedHashes(ZipArchiveEntry entry)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in ReadText(entry).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length != 2 || tokens[0].Length != 64)
            {
                throw new InvalidDataException($"invalid named hash in {entry.FullName}: {line}");
            }
            hashes.Add(tokens[1], tokens[0]);
        }
        return hashes;
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ComputeSha256(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void ValidateNativeArchitecture(ZipArchiveEntry entry, string rid)
    {
        var header = new byte[4096];
        using var stream = entry.Open();
        var count = 0;
        while (count < header.Length)
        {
            var read = stream.Read(header, count, header.Length - count);
            if (read == 0)
            {
                break;
            }
            count += read;
        }

        Require(count >= 64, "native binary header is truncated");
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            Require(BinaryPrimitives.ReadUInt16LittleEndian(header) == 0x5A4D,
                "Windows native is missing the MZ header");
            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x3C));
            Require(peOffset >= 0 && peOffset + 6 <= count,
                "Windows native has an invalid PE header offset");
            Require(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(peOffset)) == 0x00004550,
                "Windows native is missing the PE signature");
            var machine = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(peOffset + 4));
            var expectedMachine = rid switch
            {
                "win-x86" => 0x014C,
                "win-x64" => 0x8664,
                "win-arm64" => 0xAA64,
                _ => throw new InvalidDataException($"unsupported Windows RID: {rid}")
            };
            Require(machine == expectedMachine,
                $"Windows PE machine 0x{machine:X4} does not match RID '{rid}'");
            return;
        }

        if (rid.StartsWith("linux-", StringComparison.Ordinal))
        {
            Require(header[0] == 0x7F && header[1] == (byte)'E'
                                      && header[2] == (byte)'L' && header[3] == (byte)'F',
                "Linux native is missing the ELF header");
            Require(header[5] == 1, "only little-endian ClearScript ELF binaries are supported");
            var machine = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18));
            var expectedMachine = rid switch
            {
                "linux-x64" => 0x003E,
                "linux-arm" => 0x0028,
                "linux-arm64" => 0x00B7,
                _ => throw new InvalidDataException($"unsupported Linux RID: {rid}")
            };
            Require(machine == expectedMachine,
                $"ELF machine 0x{machine:X4} does not match RID '{rid}'");
            return;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        Require(magic == 0xFEEDFACF, "macOS native is not a little-endian 64-bit Mach-O binary");
        var cpuType = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        var expectedCpuType = rid switch
        {
            "osx-x64" => 0x01000007,
            "osx-arm64" => 0x0100000C,
            _ => throw new InvalidDataException($"unsupported macOS RID: {rid}")
        };
        Require(cpuType == expectedCpuType,
            $"Mach-O CPU type 0x{cpuType:X8} does not match RID '{rid}'");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
