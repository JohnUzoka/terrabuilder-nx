using System.Reflection;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;

internal static class Program
{
    internal const string InputHash = "d4c767a545d5f1a4cacbb6b0d3b16be68b6d1b9af1962d0cd439c3b7946b7298";
    internal const string FnaHash = "15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f";
    internal const string CoreHash = "ddcbdd24a642d9bc923aab3df519c68803b51afccfe252b5846e9898eea191fd";
    internal const string TargetName = "System.Void Terraria.GameContent.Drawing.TileDrawing::GetTileDrawData(System.Int32,System.Int32,Terraria.Tile,System.UInt16,System.Int16&,System.Int16&,System.Int32&,System.Int32&,System.Int32&,System.Int32&,System.Int32&,System.Int32&,Microsoft.Xna.Framework.Graphics.SpriteEffects&,Microsoft.Xna.Framework.Graphics.Texture2D&,Microsoft.Xna.Framework.Rectangle&,Microsoft.Xna.Framework.Color&)";
    internal static MethodDefinition Target(ModuleDefinition module) => Types(module).SelectMany(t => t.Methods).Single(m => m.FullName == TargetName);
    internal static void Require(bool value, string message) => Common.Require(value, message);
    internal static string Sha(byte[] bytes) => Common.Sha(bytes);

    static int Main(string[] args)
    {
        try
        {
            Require(!OperatingSystem.IsWindows(), "run in the pinned Linux monobuild environment");
            Require(args.Length == 4 && args[0] is "inspect" or "accept", "usage: PatchLightLookup <inspect|accept> <clean42 Terraria.exe> <FNA.dll> <fresh-output>");
            string input = Path.GetFullPath(args[1]), fna = Path.GetFullPath(args[2]), output = Path.GetFullPath(args[3]);
            Require(!Path.Exists(output) && new DirectoryInfo(output).LinkTarget == null, "existing or aliased output forbidden");
            byte[] originalBytes = File.ReadAllBytes(input);
            Require(Sha(originalBytes) == InputHash, "unsupported or already-patched input");
            Require(Sha(File.ReadAllBytes(fna)) == FnaHash, "unsupported FNA");
            const string core = "/mono-nx/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug/System.Private.CoreLib.dll";
            Require(Sha(File.ReadAllBytes(core)) == CoreHash, "target CoreLib changed");
            using var original = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes));
            Require(original.MainModule.Mvid == Guid.Parse("2a9040da-3f4b-844f-a14b-3056cdda95ba"), "input identity changed");
            Directory.CreateDirectory(output);
            if (args[0] == "inspect")
            {
                Json(Path.Combine(output, "inspection.json"), new { inputSha256 = InputHash, target = Body(Target(original.MainModule)), requiredTypes = new[] { 637, 638 }, scope = "Candidate only; initialized built-in render-state semantics require separate audit" });
                return 0;
            }
            Accept(input, output, originalBytes, original);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void Accept(string input, string output, byte[] originalBytes, AssemblyDefinition original)
    {
        var resolverType = Support.GetType("ReferenceAudit+PinnedResolver", true)!;
        using var resolver = (IAssemblyResolver)Activator.CreateInstance(resolverType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { Path.GetDirectoryName(input)! }, null)!;
        using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes), new ReaderParameters { AssemblyResolver = resolver });
        var method = Target(game.MainModule);
        Require(method.Body.Instructions.Count == 5140 && method.Body.Variables.Count == 101 && method.Body.ExceptionHandlers.Count == 0, "unsupported target shape");
        var first = method.Body.Instructions.Single(i => i.Offset == 0x44);
        var next = method.Body.Instructions.Single(i => i.Offset == 0x4c);
        Require(first.OpCode == OpCodes.Ldarg_1 && first.Next.OpCode == OpCodes.Ldarg_2 && first.Next.Next.OpCode == OpCodes.Call && first.Next.Next.Operand is MethodReference lookup && lookup.FullName == "Microsoft.Xna.Framework.Color Terraria.Lighting::GetColor(System.Int32,System.Int32)" && first.Next.Next.Next.OpCode == OpCodes.Stloc_0 && first.Next.Next.Next.Next == next, "original lookup location changed");
        Require(method.Parameters[3].Name == "typeCache" && method.Parameters[3].ParameterType.MetadataType == MetadataType.UInt16, "guard parameter changed");
        var il = method.Body.GetILProcessor();
        foreach (var instruction in new[] { Instruction.Create(OpCodes.Ldarg, method.Parameters[3]), Instruction.Create(OpCodes.Ldc_I4, 637), Instruction.Create(OpCodes.Sub), Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Bgt_Un, next) }) il.InsertBefore(first, instruction);
        game.MainModule.Mvid = Guid.Empty;
        using (var identity = new MemoryStream())
        {
            game.Write(identity, new WriterParameters { Timestamp = 0 });
            game.MainModule.Mvid = new Guid(SHA256.HashData(identity.ToArray()).AsSpan(0, 16));
        }
        byte[] bytes;
        using (var stream = new MemoryStream()) { game.Write(stream, new WriterParameters { Timestamp = 0 }); bytes = stream.ToArray(); }
        using var changed = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        var preservation = LightLookupAudit.Preservation(original, changed);
        var resources = PeResources.Fingerprints(originalBytes);
        Require(resources.SequenceEqual(PeResources.Fingerprints(bytes)), "native resources changed");
        var signatures = LightLookupAudit.Signatures(originalBytes, bytes);
        string candidate = Path.Combine(output, "candidate-for-proof.exe");
        using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write)) stream.Write(bytes);
        var proof = LightLookupProof.Run(input, Path.Combine(output, "proof"), candidate);
        Require(Sha(File.ReadAllBytes(input)) == InputHash, "source input changed during acceptance");
        string destination = Path.Combine(output, "Terraria.exe");
        File.Move(candidate, destination);
        File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Json(Path.Combine(output, "acceptance.json"), new
        {
            accepted = true, inputSha256 = InputHash, outputSha256 = Sha(bytes), outputMvid = changed.MainModule.Mvid,
            changedMethods = new[] { TargetName }, requiredTypes = new[] { 637, 638 }, insertedInstructions = 5,
            preservation, resources, signatures, proof, targetCorelibSha256 = CoreHash,
            scope = "Clean42-derived guarded lookup experiment for initialized built-in rendering. Required reads retain original ordering. Invalid-state failures and stateful custom-hook call counts are not equivalent. No hardware performance or arbitrary mod-compatibility claim."
        });
        Console.WriteLine("ACCEPTED " + destination + " sha256=" + Sha(bytes) + " mvid=" + changed.MainModule.Mvid);
    }
}
