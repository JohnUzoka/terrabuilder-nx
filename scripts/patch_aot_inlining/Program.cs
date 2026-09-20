using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private const string InputSha256 = "34e3b59f5736ec5ea3d590f87cf9d09aa8f7e8e67ed16d7f7f59b8df8d3344f3";
    private static readonly Guid InputMvid = new("7b2a493d-1dc3-66fe-a3d1-620d76453f0c");
    private const string MvidDomain = "Terraria.AotInlining.SetDisplayMode.TryMovingToScreen.NoOptimization.v1\n";
    // Mono method-to-ir.c sets cfg.disable_inline from this caller implementation flag.
    private const ushort NoOptimization = 0x0040;
    private sealed record Target(string Namespace, string Type, string Method, string Name, int Token,
        int Attributes, string Signature, int IlLength, int MaxStack, int LocalsToken, string Locals,
        string Header, string IlSha256, string BodySha256);
    private static readonly Target[] Targets =
    {
        new("Terraria", "Main", "SetDisplayMode", "Terraria.Main.SetDisplayMode(System.Int32,System.Int32,System.Boolean)",
            0x06001116, 0x0096, "000301080802", 1253, 3, 0x11000579, "0708021280E9080802021183DD1183E1",
            "13300300E504000079050011", "dc6fa0fccca4af832f27668172faf8dc3b736c67722bbfe8952bd7a573d10e47",
            "f6dea10223366c36102d156a3b3426e29394e70ad7e3d4be08071a73ea650de7"),
        new("Terraria.Graphics", "WindowStateController", "TryMovingToScreen", "Terraria.Graphics.WindowStateController.TryMovingToScreen(System.String)",
            0x06002b42, 0x0086, "2001010E", 132, 5, 0x11000a82, "07021183C91280E9",
            "1330050084000000820A0011", "4d49f2c483d9fed4fc96601d95738b55e009f40cdfcb3e6c14f324a480192c6d",
            "98ae9c98e550c85f1de5693bc0e4e7d2c4d51b8b907f5e83826da1e3a4e3c2a7")
    };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record Layout(Guid Mvid, int MetadataOffset, int MethodTableOffset, int MethodRowSize,
        int GuidHeapOffset, int GuidHeapSize, int GuidIndex, int MvidOffset, MethodLayout[] Methods);
    private sealed record MethodLayout(int Token, string Name, int MethodRow, int ImplFlagsOffset,
        ushort ImplFlags, int BodyOffset, int BodySize, string Signature,
        string IlSha256, string BodySha256, string LocalSignature);
    private sealed record Difference(int Offset, string Before, string After);
    private sealed record ByteDifference(int Offset, byte Before, byte After);

    public static int Main(string[] args)
    {
        try
        {
            Require(args.Length == 3 && args[0] == "accept",
                "usage: PatchAotInlining accept INPUT_EXE NEW_OUTPUT_DIRECTORY");
            Accept(args[1], args[2]);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"REJECT: {error.Message}");
            return 1;
        }
    }

    private static void Accept(string inputArgument, string outputArgument)
    {
        string input = Path.GetFullPath(inputArgument);
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputArgument));
        Require(!string.Equals(input, output, StringComparison.Ordinal), "output aliases input");
        // A fresh directory is the transaction boundary. Existing directories, files and even
        // dangling links are rejected; no overwrite switch or in-place patching exists.
        Require(!Exists(output), "output already exists or aliases an existing path");
        string parent = Path.GetDirectoryName(output) ?? throw new InvalidDataException("output needs a parent directory");
        Require(Directory.Exists(parent), "output parent must already exist");
        for (string? path = parent; path != null; path = Path.GetDirectoryName(path))
            Require(new DirectoryInfo(path).LinkTarget == null, "output parent contains a symbolic link");

        byte[] original = File.ReadAllBytes(input);
        Layout before = Inspect(original, patched: false);
        string inputSha = Hash(original);
        Require(inputSha == InputSha256, "input SHA differs from pinned build41");
        Require(before.Mvid == InputMvid, "input MVID differs from pinned build41");

        byte[] image = (byte[])original.Clone();
        foreach (var method in before.Methods)
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(method.ImplFlagsOffset, 2),
                (ushort)(method.ImplFlags | NoOptimization));
        Guid outputMvid = DeterministicMvid();
        Require(outputMvid != InputMvid, "new MVID must differ");
        Require(outputMvid.TryWriteBytes(image.AsSpan(before.MvidOffset, 16)), "MVID write failed");

        Layout after = Inspect(image, patched: true);
        Require(after with { Mvid = before.Mvid, Methods = before.Methods } == before,
            "module metadata readback differs outside requested MVID");
        Require(after.Mvid == outputMvid && after.Methods.Length == before.Methods.Length,
            "patched metadata did not read back exactly");
        for (int i = 0; i < before.Methods.Length; i++)
        {
            Require(after.Methods[i] with { ImplFlags = before.Methods[i].ImplFlags } == before.Methods[i],
                "method metadata readback differs outside requested flags");
            Require(after.Methods[i].ImplFlags == (before.Methods[i].ImplFlags | NoOptimization) &&
                (after.Methods[i].ImplFlags & ~NoOptimization) == before.Methods[i].ImplFlags,
                "other implementation flags changed");
        }
        Require(image.Length == original.Length, "image length changed");

        var diffBytes = new List<ByteDifference>();
        for (int offset = 0; offset < original.Length; offset++)
        {
            if (original[offset] == image[offset]) continue;
            Require(before.Methods.Any(method => InRange(offset, method.ImplFlagsOffset, 2)) || InRange(offset, before.MvidOffset, 16),
                $"unpermitted change at file offset {offset}");
            diffBytes.Add(new(offset, original[offset], image[offset]));
        }
        foreach (var method in before.Methods)
            Require(diffBytes.Single(d => InRange(d.Offset, method.ImplFlagsOffset, 2))
                == new ByteDifference(method.ImplFlagsOffset, (byte)method.ImplFlags, (byte)(method.ImplFlags | NoOptimization)),
                "expected only the NoOptimization bit in each ImplFlags field");
        var diffRanges = new List<Difference>();
        for (int i = 0; i < diffBytes.Count;)
        {
            int start = diffBytes[i].Offset;
            int end = start + 1;
            while (++i < diffBytes.Count && diffBytes[i].Offset == end) end++;
            diffRanges.Add(new(start, Convert.ToHexString(original.AsSpan(start, end - start)),
                Convert.ToHexString(image.AsSpan(start, end - start))));
        }
        string outputSha = Hash(image);
        var manifest = new
        {
            schemaVersion = 2,
            patch = "Only the two named Windows display callers: MethodDef.ImplFlags |= NoOptimization",
            input = new { sha256 = inputSha, mvid = before.Mvid, length = original.Length },
            output = new { sha256 = outputSha, mvid = after.Mvid, length = image.Length },
            deterministicMvid = new { algorithm = "SHA-256 UTF-8(domain + lowercase input SHA); first 16 bytes in Guid byte order; UUID version 8 and RFC variant bits", domain = MvidDomain },
            methods = before.Methods.Select((method, i) => new
            {
                token = $"0x{method.Token:X8}", name = method.Name,
                method.Signature, method.IlSha256, method.BodySha256, method.LocalSignature, method.BodyOffset, method.BodySize,
                oldImplFlags = $"0x{method.ImplFlags:X4}", newImplFlags = $"0x{after.Methods[i].ImplFlags:X4}"
            }),
            offsets = before,
            permittedRanges = before.Methods.Select(method => new { offset = method.ImplFlagsOffset, length = 2, field = method.Name + ".ImplFlags" })
                .Append(new { offset = before.MvidOffset, length = 16, field = "Module.Mvid GUID heap entry" }),
            changedByteCount = diffBytes.Count,
            diffBytes,
            diffRanges,
            verification = new { metadataReadback = true, everyOtherByteIdentical = true,
                ilSignaturesResourcesUnchanged = true, unsignedImage = true, noRuntimeDependencyAdded = true }
        };
        byte[] manifestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonOptions) + "\n");
        string staging = Path.Combine(parent, "." + Path.GetFileName(output) + ".pending-" + Guid.NewGuid().ToString("N"));
        bool ownStaging = false;
        try
        {
            Require(!Exists(staging), "staging path already exists");
            Directory.CreateDirectory(staging);
            ownStaging = true;
            string game = Path.Combine(staging, "Terraria.exe");
            using (var stream = new FileStream(game, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(image);
                stream.Flush(flushToDisk: true);
            }
            Require(File.ReadAllBytes(game).AsSpan().SequenceEqual(image), "published image bytes failed readback");
            string report = Path.Combine(staging, "manifest.json");
            using (var stream = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(manifestBytes);
                stream.Flush(flushToDisk: true);
            }
            Require(File.ReadAllBytes(report).AsSpan().SequenceEqual(manifestBytes), "manifest bytes failed readback");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(game, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                File.SetUnixFileMode(report, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            // Directory.Move cannot replace an existing destination. The input is never opened for write.
            Directory.Move(staging, output);
            ownStaging = false;
        }
        finally
        {
            if (ownStaging) Directory.Delete(staging, recursive: true);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { accepted = true, image = Path.Combine(output, "Terraria.exe"),
            sha256 = outputSha, mvid = outputMvid, manifest = Path.Combine(output, "manifest.json") }));
    }

    private static Layout Inspect(byte[] bytes, bool patched)
    {
        using var pe = new PEReader(new MemoryStream(bytes, writable: false));
        Require(pe.HasMetadata, "not a managed PE image");
        var headers = pe.PEHeaders;
        var cor = headers.CorHeader ?? throw new InvalidDataException("missing CLI header");
        var optional = headers.PEHeader ?? throw new InvalidDataException("missing PE optional header");
        var md = pe.GetMetadataReader(MetadataReaderOptions.None);
        Require(md.IsAssembly, "expected an assembly");
        var assembly = md.GetAssemblyDefinition();
        Require((cor.Flags & CorFlags.StrongNameSigned) == 0 &&
            cor.StrongNameSignatureDirectory.Size == 0 && cor.StrongNameSignatureDirectory.RelativeVirtualAddress == 0 &&
            optional.CertificateTableDirectory.Size == 0 && optional.CertificateTableDirectory.RelativeVirtualAddress == 0 &&
            assembly.PublicKey.IsNil && (assembly.Flags & AssemblyFlags.PublicKey) == 0,
            "signed or signing-reserved image is unsupported");
        Require(optional.CheckSum == 0, "nonzero PE checksum is unsupported");
        Require(cor.ManagedNativeHeaderDirectory.Size == 0, "native managed image is unsupported");
        Require(md.GetTableRowCount(TableIndex.Module) == 1 && md.GetTableRowCount(TableIndex.CustomDebugInformation) == 0,
            "unexpected module or portable debug metadata");
        var module = md.GetModuleDefinition();
        Require(module.Generation == 0 && module.GenerationId.IsNil && module.BaseGenerationId.IsNil,
            "edit-and-continue module is unsupported");
        int metadataOffset = headers.MetadataStartOffset;
        int tableOffset = md.GetTableMetadataOffset(TableIndex.MethodDef);
        int rowSize = md.GetTableRowSize(TableIndex.MethodDef);
        int guidHeapOffset = md.GetHeapMetadataOffset(HeapIndex.Guid);
        int guidSize = md.GetHeapSize(HeapIndex.Guid);
        // Unlike Blob/String handles, the supported GetHeapOffset(GuidHandle) returns a 1-based GUID index.
        // https://learn.microsoft.com/dotnet/api/system.reflection.metadata.ecma335.metadatatokens.getheapoffset
        int guidIndex = MetadataTokens.GetHeapOffset(module.Mvid);
        Require(!module.Mvid.IsNil && guidIndex == 1 && guidSize == 16, "unexpected MVID GUID heap layout");
        int mvidOffset = checked(metadataOffset + guidHeapOffset + (guidIndex - 1) * 16);
        Guid mvid = md.GetGuid(module.Mvid);
        Require(new Guid(bytes.AsSpan(mvidOffset, 16)) == mvid &&
            bytes.AsSpan(mvidOffset, 16).SequenceEqual(mvid.ToByteArray()), "GUID heap offset/raw MVID readback mismatch");
        Require(mvidOffset >= metadataOffset && mvidOffset + 16 <= metadataOffset + headers.MetadataSize,
            "MVID patch range outside metadata");

        var inspected = new MethodLayout[Targets.Length];
        for (int index = 0; index < Targets.Length; index++)
        {
            var target = Targets[index];
            var types = md.TypeDefinitions.Where(handle =>
            {
                var type = md.GetTypeDefinition(handle);
                return md.GetString(type.Namespace) == target.Namespace && md.GetString(type.Name) == target.Type;
            }).ToArray();
            Require(types.Length == 1, $"expected exactly one {target.Namespace}.{target.Type} type");
            var methods = md.GetTypeDefinition(types[0]).GetMethods()
                .Where(handle => md.GetString(md.GetMethodDefinition(handle).Name) == target.Method).ToArray();
            Require(methods.Length == 1 && MetadataTokens.GetToken(methods[0]) == target.Token,
                $"unexpected {target.Method} method identity");
            var method = md.GetMethodDefinition(methods[0]);
            ushort implFlags = (ushort)method.ImplAttributes;
            Require(patched ? (implFlags & NoOptimization) != 0 : (implFlags & NoOptimization) == 0,
                patched ? "NoOptimization missing on readback" : $"{target.Method} already has NoOptimization");
            Require(implFlags == (patched ? NoOptimization : 0) && (int)method.Attributes == target.Attributes &&
                method.GetGenericParameters().Count == 0 && method.RelativeVirtualAddress != 0,
                $"unexpected {target.Method} flags or method shape");
            string signature = Convert.ToHexString(md.GetBlobBytes(method.Signature));
            Require(signature == target.Signature, $"unexpected {target.Method} method signature");
            var body = pe.GetMethodBody(method.RelativeVirtualAddress);
            byte[] il = body.GetILBytes() ?? throw new InvalidDataException($"missing {target.Method} IL");
            string ilSha = Hash(il);
            Require(il.Length == target.IlLength && ilSha == target.IlSha256 && body.Size == target.IlLength + 12 &&
                body.MaxStack == target.MaxStack && body.LocalVariablesInitialized &&
                MetadataTokens.GetToken(body.LocalSignature) == target.LocalsToken && body.ExceptionRegions.Length == 0,
                $"unexpected {target.Method} method body");
            string locals = Convert.ToHexString(md.GetBlobBytes(md.GetStandaloneSignature(body.LocalSignature).Signature));
            Require(locals == target.Locals, $"unexpected {target.Method} local signature");
            int row = MetadataTokens.GetRowNumber(methods[0]);
            int rowOffset = checked(metadataOffset + tableOffset + (row - 1) * rowSize);
            // ECMA-335 II.22.26: RVA (4 bytes), ImplFlags (2), Flags (2), followed by heap/table indices.
            int flagsOffset = checked(rowOffset + sizeof(uint));
            Require(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(rowOffset, 4)) == method.RelativeVirtualAddress &&
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(flagsOffset, 2)) == implFlags &&
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(flagsOffset + 2, 2)) == (ushort)method.Attributes,
                "MethodDef offset API/raw field readback mismatch");
            Require(flagsOffset + 2 <= mvidOffset || mvidOffset + 16 <= flagsOffset, "patch ranges overlap");
            Require(flagsOffset >= metadataOffset && flagsOffset + 2 <= metadataOffset + headers.MetadataSize,
                "method patch range outside metadata");
            for (int previous = 0; previous < index; previous++)
                Require(flagsOffset + 2 <= inspected[previous].ImplFlagsOffset ||
                    inspected[previous].ImplFlagsOffset + 2 <= flagsOffset, "method patch ranges overlap");
            int bodyOffset = RvaOffset(headers, method.RelativeVirtualAddress);
            string bodySha = Hash(bytes.AsSpan(bodyOffset, body.Size));
            Require(Convert.ToHexString(bytes.AsSpan(bodyOffset, 12)) == target.Header &&
                bytes.AsSpan(bodyOffset + 12, il.Length).SequenceEqual(il) && bodySha == target.BodySha256,
                "fat method header/raw body readback mismatch");
            inspected[index] = new(target.Token, target.Name, row, flagsOffset, implFlags, bodyOffset, body.Size,
                signature, ilSha, bodySha, locals);
        }
        return new(mvid, metadataOffset, tableOffset, rowSize, guidHeapOffset, guidSize, guidIndex, mvidOffset, inspected);
    }

    private static int RvaOffset(PEHeaders headers, int rva)
    {
        var sections = headers.SectionHeaders.Where(section => rva >= section.VirtualAddress &&
            (long)rva < (long)section.VirtualAddress + section.SizeOfRawData).ToArray();
        Require(sections.Length == 1, "RVA must resolve to exactly one file-backed section");
        return checked(sections[0].PointerToRawData + rva - sections[0].VirtualAddress);
    }

    private static Guid DeterministicMvid()
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(MvidDomain + InputSha256));
        digest[7] = (byte)((digest[7] & 0x0f) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest.AsSpan(0, 16));
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget != null;
    private static bool InRange(int offset, int start, int length) => offset >= start && offset - start < length;
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
