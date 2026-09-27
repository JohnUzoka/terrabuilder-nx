using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;

internal static class Program
{
    internal const string InputHash = "0ce46870f3f384b10f37da068bdbbbeeb567cb58aa69bb0ad0d6511305689ab6";
    internal const string InputMvid = "b17425f9-1fbc-4be3-9b13-ecda72163fe0";
    internal const string TargetName = "System.Object System.ComponentModel.ReflectPropertyDescriptor::GetValue(System.Object)";
    internal static MethodDefinition Target(ModuleDefinition module) => Types(module).SelectMany(t => t.Methods).Single(m => m.FullName == TargetName);
    internal static void Require(bool value, string message) => Common.Require(value, message);
    internal static string Sha(byte[] bytes) => Common.Sha(bytes);

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    static extern int MakeDirectory(string path, uint mode);

    static string SafePath(string path)
    {
        Require(!path.Split(Path.DirectorySeparatorChar).Contains(".."), "parent traversal forbidden");
        string full = Path.GetFullPath(path);
        for (string? part = full; part != null; part = Path.GetDirectoryName(part))
            Require(new FileInfo(part).LinkTarget == null && new DirectoryInfo(part).LinkTarget == null, "symlink path forbidden: " + part);
        return full;
    }

    static void FreshOutput(string output, string input)
    {
        Require(!Path.Exists(output), "existing or aliased output forbidden");
        Require(!output.StartsWith(Path.GetDirectoryName(input)! + Path.DirectorySeparatorChar, StringComparison.Ordinal), "output in source runtime directory forbidden");
        Require(Directory.Exists(Path.GetDirectoryName(output)), "output parent must already exist");
        Require(MakeDirectory(output, 0x1c0) == 0, "exclusive fresh output creation failed: errno=" + Marshal.GetLastPInvokeError());
    }

    static int Main(string[] args)
    {
        try
        {
            Require(OperatingSystem.IsLinux(), "run in the pinned Linux monobuild environment");
            Require(args.Length == 3 && args[0] is "inspect" or "accept", "usage: PatchPropertyDiagnostics <inspect|accept> <clean42 System.ComponentModel.TypeConverter.dll> <fresh-output>");
            string input = SafePath(args[1]), output = SafePath(args[2]);
            Require(!Path.Exists(output), "existing or aliased output forbidden");
            byte[] originalBytes = File.ReadAllBytes(input);
            Require(originalBytes.Length == 355840 && Sha(originalBytes) == InputHash, "unsupported or already-patched input");
            using var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
            var reader = new ReaderParameters { AssemblyResolver = resolver };
            using var original = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes), reader);
            Require(original.MainModule.Mvid == Guid.Parse(InputMvid) && original.Name.FullName == "System.ComponentModel.TypeConverter, Version=9.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", "input identity changed");
            ValidateShape(Target(original.MainModule));
            var signing = PropertyDiagnosticAudit.Signing(originalBytes);
            FreshOutput(output, input);
            if (args[0] == "inspect")
            {
                Json(Path.Combine(output, "inspection.json"), new { inputSha256 = InputHash, inputMvid = InputMvid, identity = original.Name.FullName, signing, target = Body(Target(original.MainModule)), deletion = "39 instructions at original IL_0001..IL_006e; retain IL_0000 and IL_006f onward" });
                Require(Sha(File.ReadAllBytes(input)) == InputHash, "source input changed during inspection");
                Console.WriteLine("INSPECTED " + input + " sha256=" + InputHash);
                return 0;
            }
            Accept(input, output, originalBytes, original, reader);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void ValidateShape(MethodDefinition method)
    {
        Require(method.MetadataToken.ToInt32() == 0x06000328 && method.HasBody && method.Body.Instructions.Count == 161 && !method.Body.InitLocals && method.Body.MaxStackSize == 4 && method.Body.Variables.Count == 11 && method.Body.ExceptionHandlers.Count == 1 && method.Body.ExceptionHandlers[0].HandlerType == ExceptionHandlerType.Catch, "unsupported target shape");
        var instructions = method.Body.Instructions;
        Require(instructions[0].Offset == 0 && instructions[0].OpCode == OpCodes.Nop && instructions[1].Offset == 1 && instructions[1].OpCode == OpCodes.Ldloca_S && instructions[39].Offset == 0x6e && instructions[39].OpCode == OpCodes.Nop && instructions[40].Offset == 0x6f && instructions[40].OpCode == OpCodes.Ldarg_0, "pinned deletion boundaries changed");
        Require(instructions[38].Offset == 0x69 && instructions[38].OpCode == OpCodes.Call && instructions[38].Operand is MethodReference call && call.FullName == "System.Void System.Diagnostics.Debug::WriteLine(System.String)", "entry diagnostic endpoint changed");
    }

    static void Accept(string input, string output, byte[] originalBytes, AssemblyDefinition original, ReaderParameters reader)
    {
        using var image = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes), reader);
        var method = Target(image.MainModule);
        var removed = method.Body.Instructions.Skip(1).Take(39).ToHashSet();
        Require(removed.All(i => i.Offset >= 1 && i.Offset <= 0x6e), "deletion extends outside pinned prefix");
        foreach (var instruction in method.Body.Instructions.Where(i => !removed.Contains(i)))
            Require(!(instruction.Operand is Instruction target && removed.Contains(target)) && !(instruction.Operand is Instruction[] targets && targets.Any(removed.Contains)), "retained branch enters removed prefix");
        foreach (var handler in method.Body.ExceptionHandlers)
            Require(new[] { handler.TryStart, handler.TryEnd, handler.HandlerStart, handler.HandlerEnd, handler.FilterStart }.All(i => i == null || !removed.Contains(i)), "exception boundary enters removed prefix");
        var il = method.Body.GetILProcessor();
        foreach (var instruction in removed) il.Remove(instruction);
        // Do not simplify/widen branches, remove locals, or suppress any other diagnostic.
        image.MainModule.Mvid = Guid.Empty;
        using (var identity = new MemoryStream())
        {
            image.Write(identity, new WriterParameters { Timestamp = 0 });
            image.MainModule.Mvid = new Guid(SHA256.HashData(identity.ToArray()).AsSpan(0, 16));
        }
        byte[] bytes;
        using (var stream = new MemoryStream()) { image.Write(stream, new WriterParameters { Timestamp = 0 }); bytes = stream.ToArray(); }
        using var changed = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes), reader);
        var preservation = PropertyDiagnosticAudit.Preservation(original, changed);
        var resources = PeResources.Fingerprints(originalBytes);
        Require(resources.SequenceEqual(PeResources.Fingerprints(bytes)), "native resources changed");
        var signatures = PropertyDiagnosticAudit.Signatures(originalBytes, bytes);
        var signing = PropertyDiagnosticAudit.SigningPreservation(originalBytes, bytes);
        string candidate = Path.Combine(output, "candidate-for-proof.dll");
        using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write)) stream.Write(bytes);
        var proof = DescriptorDiagnosticProof.Run(input, Path.Combine(output, "proof"), candidate);
        Require(Sha(File.ReadAllBytes(candidate)) == Sha(bytes), "candidate changed during proof");
        Require(Sha(File.ReadAllBytes(input)) == InputHash, "source input changed during acceptance");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(candidate, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        else throw new PlatformNotSupportedException("accepted output requires Linux readonly mode");
        string destination = Path.Combine(output, "System.ComponentModel.TypeConverter.dll");
        File.Move(candidate, destination, false);
        Json(Path.Combine(output, "acceptance.json"), new
        {
            accepted = true, inputSha256 = InputHash, inputMvid = InputMvid, outputSha256 = Sha(bytes), outputMvid = changed.MainModule.Mvid,
            identity = changed.Name.FullName, changedMethods = new[] { TargetName }, removedInstructions = 39, retainedInstructions = 122,
            preservation, resources, signatures, signing, proof,
            scope = "Patch-time diagnostic-only candidate. Only unconditional GetValue entry Name/type-name access, formatting and Debug.WriteLine (including diagnostic-only failures/side effects) disappear. Property logic, assertions and later diagnostics remain. No valid cryptographic signature, runtime-loader, AOT, hardware, gameplay or performance acceptance claim. Never deploy without separate dependency-GUID/AOT and loader acceptance."
        });
        Console.WriteLine("ACCEPTED " + destination + " sha256=" + Sha(bytes) + " mvid=" + changed.MainModule.Mvid);
    }
}
