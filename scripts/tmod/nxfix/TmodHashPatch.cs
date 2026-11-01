using Mono.Cecil;
using Mono.Cecil.Cil;
using static NxIl;

// Mod file hash check. ModOrganizer.EnsureHashesAreValid runs TmodFile.VerifyHash on every enabled
// mod before loading it: _VerifyHash reads the .tmod after its header and compares
// SHA1.Create().ComputeHash(stream) with the stored hash. On the Switch, SHA1.Create() is the
// managed SHA1 of System.Security.Cryptography.dll, which is not AOT-compiled, so the whole hash
// runs in the Mono interpreter, reading the file in 4 KB chunks. In run 42 that was 73.7 s of the
// 127.5 s mod load (18:24:41.6 to 18:25:55.3) for 80.6 MB of .tmod files: about 66 s of
// interpreted hashing and 7 s of SD reads on the load thread.
// The patch adds Terraria.ModLoader.Core.NxSha1, a plain SHA-1 (FIPS 180-4) over Stream.Read with a
// 1 MiB buffer whose IL is copied from NxSha1Template below. It lives in tModLoader.dll and is
// AOT-compiled with it. _VerifyHash calls NxSha1.ComputeHash(fs) instead; the comparison, the
// stored hash and every caller are unchanged, so the same files pass and fail as before.
// Before patching, NxFix checks the template against .NET's SHA1 on edge-case lengths and
// short reads, and the patch rejects any template IL that references anything but primitive
// arrays and Stream.Read.
static class TmodHashPatch
{
    public static void Apply(ModuleDefinition module)
    {
        SelfTest();

        var tmodFile = module.GetType("Terraria.ModLoader.Core.TmodFile")
            ?? throw new InvalidOperationException("Terraria.ModLoader.Core.TmodFile not found");
        var verify = tmodFile.Methods.Single(m => m.Name == "_VerifyHash" && m.HasBody);
        var expected = new[] {
            "ldarg.0", "ldfld System.Int64 Terraria.ModLoader.Core.TmodFile::hashStartPos", "brtrue",
            "ldc.i4.0", "ret",
            "ldarg.0", "ldfld System.String Terraria.ModLoader.Core.TmodFile::path",
            "call System.IO.FileStream System.IO.File::OpenRead(System.String)", "stloc.0",
            "ldloc.0", "ldarg.0", "ldfld System.Int64 Terraria.ModLoader.Core.TmodFile::hashStartPos",
            "callvirt System.Void System.IO.Stream::set_Position(System.Int64)",
            "ldarg.0", "call System.Byte[] Terraria.ModLoader.Core.TmodFile::get_Hash()",
            "call System.Security.Cryptography.SHA1 System.Security.Cryptography.SHA1::Create()",
            "ldloc.0",
            "callvirt System.Byte[] System.Security.Cryptography.HashAlgorithm::ComputeHash(System.IO.Stream)",
            "call System.Boolean System.Linq.Enumerable::SequenceEqual<System.Byte>(System.Collections.Generic.IEnumerable`1<!!0>,System.Collections.Generic.IEnumerable`1<!!0>)",
            "stloc.1", "leave",
            "ldloc.0", "brfalse", "ldloc.0", "callvirt System.Void System.IDisposable::Dispose()", "endfinally",
            "ldloc.1", "ret",
        };
        var text = Text(verify);
        if (verify.IsStatic || verify.Parameters.Count != 0 || verify.ReturnType.MetadataType != MetadataType.Boolean
            || !text.SequenceEqual(expected) || verify.Body.ExceptionHandlers.Count != 1
            || verify.Body.ExceptionHandlers[0].HandlerType != ExceptionHandlerType.Finally)
            throw new InvalidOperationException("unexpected TmodFile._VerifyHash shape:\n  " + string.Join("\n  ", text));
        if (module.GetType("Terraria.ModLoader.Core.NxSha1") != null)
            throw new InvalidOperationException("Terraria.ModLoader.Core.NxSha1 already exists (input already patched?)");

        var computeHash = CloneTemplate(module);

        var create = verify.Body.Instructions[15];
        var hashCall = verify.Body.Instructions[17];
        if (verify.Body.Instructions.Any(i => i.Operand == create || i.Operand is Instruction[] targets && targets.Contains(create))
            || verify.Body.ExceptionHandlers.Any(h => h.TryStart == create || h.TryEnd == create || h.HandlerStart == create || h.HandlerEnd == create))
            throw new InvalidOperationException("TmodFile._VerifyHash: SHA1.Create() is a branch or handler target");
        verify.Body.GetILProcessor().Remove(create);
        hashCall.OpCode = OpCodes.Call;
        hashCall.Operand = computeHash;
        VerifyPatched(verify, Text(verify).Count(t => t.Contains("System.Security.Cryptography")) == 0
            && Text(verify)[16] == "call System.Byte[] Terraria.ModLoader.Core.NxSha1::ComputeHash(System.IO.Stream)",
            "SHA1 call not replaced");
        Console.WriteLine("FIX TmodFile._VerifyHash: SHA1.Create().ComputeHash(fs) -> NxSha1.ComputeHash(fs) (AOT-compiled SHA-1, 1 MiB reads; was interpreted, 4 KB reads)");
    }

    // Copies NxSha1Template's methods into a new static class Terraria.ModLoader.Core.NxSha1.
    static MethodDefinition CloneTemplate(ModuleDefinition module)
    {
        var self = AssemblyDefinition.ReadAssembly(typeof(TmodHashPatch).Assembly.Location).MainModule;
        var template = self.GetType("NxSha1Template") ?? throw new InvalidOperationException("NxSha1Template not found in NxFix");
        if (template.Fields.Any(f => !f.IsLiteral) || template.HasNestedTypes || template.HasProperties
            || template.Methods.Any(m => !m.IsStatic || !m.HasBody))
            throw new InvalidOperationException("NxSha1Template must be static methods and constants only");

        // The one non-primitive member the template may use, taken from tModLoader's own references.
        var streamRead = module.GetMemberReferences().OfType<MethodReference>()
            .Where(m => m.FullName == "System.Int32 System.IO.Stream::Read(System.Byte[],System.Int32,System.Int32)").ToList();
        var stream = module.GetTypeReferences().Where(t => t.FullName == "System.IO.Stream").ToList();
        if (streamRead.Count != 1 || stream.Count != 1)
            throw new InvalidOperationException($"tModLoader.dll: expected one Stream.Read(byte[], int, int) and one Stream reference, found {streamRead.Count} and {stream.Count}");

        var nxSha1 = new TypeDefinition("Terraria.ModLoader.Core", "NxSha1",
            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit
            | TypeAttributes.AutoLayout | TypeAttributes.AnsiClass | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(nxSha1);

        TypeReference MapType(TypeReference type)
        {
            if (type is ArrayType array && array.IsVector)
                return new ArrayType(MapType(array.ElementType));
            return type.FullName switch
            {
                "System.Void" => module.TypeSystem.Void,
                "System.Boolean" => module.TypeSystem.Boolean,
                "System.Byte" => module.TypeSystem.Byte,
                "System.Int32" => module.TypeSystem.Int32,
                "System.UInt32" => module.TypeSystem.UInt32,
                "System.Int64" => module.TypeSystem.Int64,
                "System.UInt64" => module.TypeSystem.UInt64,
                "System.IO.Stream" => stream[0],
                _ => throw new InvalidOperationException("NxSha1Template uses unsupported type " + type.FullName),
            };
        }

        var methods = new Dictionary<MethodDefinition, MethodDefinition>();
        foreach (var source in template.Methods)
        {
            var clone = new MethodDefinition(source.Name,
                MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, MapType(source.ReturnType))
            {
                ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed,
            };
            foreach (var p in source.Parameters)
                clone.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, MapType(p.ParameterType)));
            nxSha1.Methods.Add(clone);
            methods[source] = clone;
        }

        foreach (var (source, clone) in methods)
        {
            var from = source.Body;
            var to = clone.Body;
            if (from.HasExceptionHandlers)
                throw new InvalidOperationException($"NxSha1Template.{source.Name}: exception handlers are not supported");
            to.InitLocals = from.InitLocals;
            to.MaxStackSize = from.MaxStackSize;
            var variables = new Dictionary<VariableDefinition, VariableDefinition>();
            foreach (var v in from.Variables)
                to.Variables.Add(variables[v] = new VariableDefinition(MapType(v.VariableType)));
            var map = new Dictionary<Instruction, Instruction>();
            foreach (var ins in from.Instructions)
                map[ins] = Instruction.Create(OpCodes.Nop);
            foreach (var ins in from.Instructions)
            {
                var copy = map[ins];
                copy.OpCode = ins.OpCode;
                copy.Operand = ins.Operand switch
                {
                    null => null,
                    Instruction target => map[target],
                    Instruction[] targets => targets.Select(t => map[t]).ToArray(),
                    VariableDefinition v => variables[v],
                    ParameterDefinition p => clone.Parameters[p.Index],
                    TypeReference t => MapType(t),
                    MethodDefinition m when methods.TryGetValue(m, out var local) => local,
                    MethodReference m when m.FullName == streamRead[0].FullName => streamRead[0],
                    MethodReference m => throw new InvalidOperationException($"NxSha1Template.{source.Name} calls unsupported {m.FullName}"),
                    FieldReference f => throw new InvalidOperationException($"NxSha1Template.{source.Name} uses field {f.FullName}"),
                    sbyte or byte or int or long or float or double => ins.Operand,
                    _ => throw new InvalidOperationException($"NxSha1Template.{source.Name}: unsupported operand {ins}"),
                };
                to.Instructions.Add(copy);
            }
            VerifyPatched(clone, to.Instructions.Count == from.Instructions.Count, "clone length differs");
            Console.WriteLine($"FIX NxSha1.{clone.Name}: copied from NxSha1Template ({to.Instructions.Count} instructions)");
        }
        return methods.Values.Single(m => m.Name == "ComputeHash");
    }

    // The template must equal .NET's SHA1 on block-boundary lengths, buffer-boundary lengths and
    // streams that return short reads.
    static void SelfTest()
    {
        var random = new Random(1094);
        const int Buffer = NxSha1Template.BufferSize;
        foreach (int length in new[] { 0, 1, 3, 55, 56, 57, 63, 64, 65, 119, 120, 128, 1000,
            Buffer - 65, Buffer - 64, Buffer - 9, Buffer - 1, Buffer, Buffer + 1, Buffer + 55, Buffer + 56, 3 * Buffer + 77 })
        {
            var data = new byte[length];
            random.NextBytes(data);
            var want = System.Security.Cryptography.SHA1.HashData(data);
            foreach (int maxRead in new[] { int.MaxValue, 1, 7, 4096, 65531 })
            {
                if (maxRead == 1 && length > 100_000)
                    continue;
                var got = NxSha1Template.ComputeHash(new ChunkedStream(data, maxRead));
                if (!got.AsSpan().SequenceEqual(want))
                    throw new InvalidOperationException($"NxSha1Template wrong for length {length}, reads of at most {maxRead}");
            }
        }
    }

    sealed class ChunkedStream(byte[] data, int maxRead) : MemoryStream(data, false)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, maxRead));
    }

    static List<string> Text(MethodDefinition method) => method.Body.Instructions.Select(i => i.Operand is Instruction
        ? i.OpCode.Name.Replace(".s", "") : i.ToString().Substring(i.ToString().IndexOf(':') + 2)).ToList();
}

// SHA-1 (FIPS 180-4) over a stream. TmodHashPatch copies this IL into tModLoader.dll, so it may use
// only primitive arrays, arithmetic and Stream.Read(byte[], int, int): no fields, no other calls.
static class NxSha1Template
{
    internal const int BufferSize = 1 << 20; // a multiple of 64

    internal static byte[] ComputeHash(Stream stream)
    {
        var buffer = new byte[BufferSize];
        var h = new uint[5];
        h[0] = 0x67452301; h[1] = 0xEFCDAB89; h[2] = 0x98BADCFE; h[3] = 0x10325476; h[4] = 0xC3D2E1F0;
        var w = new uint[80];
        long total = 0;
        int filled = 0;
        while (true)
        {
            int read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read <= 0)
                break;
            filled += read;
            total += read;
            if (filled == buffer.Length)
            {
                for (int offset = 0; offset < filled; offset += 64)
                    Compress(h, w, buffer, offset);
                filled = 0;
            }
        }
        int whole = filled & ~63;
        for (int offset = 0; offset < whole; offset += 64)
            Compress(h, w, buffer, offset);
        int rest = filled - whole;
        // All complete blocks are consumed; reuse the read buffer for the final padded block(s).
        int paddedLength = rest < 56 ? 64 : 128;
        if (whole != 0)
            for (int i = 0; i < rest; i++)
                buffer[i] = buffer[whole + i];
        for (int i = rest; i < paddedLength; i++)
            buffer[i] = 0;
        buffer[rest] = 0x80;
        ulong bits = (ulong)total << 3;
        for (int i = paddedLength - 1; i >= paddedLength - 8; i--)
        {
            buffer[i] = (byte)bits;
            bits >>= 8;
        }
        for (int offset = 0; offset < paddedLength; offset += 64)
            Compress(h, w, buffer, offset);
        var hash = new byte[20];
        for (int i = 0; i < 5; i++)
        {
            hash[4 * i] = (byte)(h[i] >> 24);
            hash[4 * i + 1] = (byte)(h[i] >> 16);
            hash[4 * i + 2] = (byte)(h[i] >> 8);
            hash[4 * i + 3] = (byte)h[i];
        }
        return hash;
    }

    static void Compress(uint[] h, uint[] w, byte[] data, int offset)
    {
        for (int t = 0; t < 16; t++, offset += 4)
            w[t] = (uint)data[offset] << 24 | (uint)data[offset + 1] << 16 | (uint)data[offset + 2] << 8 | data[offset + 3];
        for (int t = 16; t < 80; t++)
        {
            uint x = w[t - 3] ^ w[t - 8] ^ w[t - 14] ^ w[t - 16];
            w[t] = x << 1 | x >> 31;
        }
        uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
        for (int t = 0; t < 20; t++)
        {
            uint next = (a << 5 | a >> 27) + ((b & c) | (~b & d)) + e + 0x5A827999 + w[t];
            e = d; d = c; c = b << 30 | b >> 2; b = a; a = next;
        }
        for (int t = 20; t < 40; t++)
        {
            uint next = (a << 5 | a >> 27) + (b ^ c ^ d) + e + 0x6ED9EBA1 + w[t];
            e = d; d = c; c = b << 30 | b >> 2; b = a; a = next;
        }
        for (int t = 40; t < 60; t++)
        {
            uint next = (a << 5 | a >> 27) + ((b & c) | (b & d) | (c & d)) + e + 0x8F1BBCDC + w[t];
            e = d; d = c; c = b << 30 | b >> 2; b = a; a = next;
        }
        for (int t = 60; t < 80; t++)
        {
            uint next = (a << 5 | a >> 27) + (b ^ c ^ d) + e + 0xCA62C1D6 + w[t];
            e = d; d = c; c = b << 30 | b >> 2; b = a; a = next;
        }
        h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e;
    }
}
