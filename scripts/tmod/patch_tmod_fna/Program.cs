using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class Program
{
    static readonly string[] Suppress = {
        "FNA3D_DrawIndexedPrimitives", "FNA3D_DrawPrimitives", "FNA3D_SetViewport", "FNA3D_SetScissorRect", "FNA3D_SetBlendFactor",
        "FNA3D_SetMultiSampleMask", "FNA3D_SetReferenceStencil", "FNA3D_SetBlendState", "FNA3D_SetDepthStencilState", "FNA3D_ApplyRasterizerState",
        "FNA3D_VerifySampler", "FNA3D_VerifyVertexSampler", "FNA3D_ApplyVertexBufferBindings", "FNA3D_ApplyEffect", "FNA3D_BeginPassRestore", "FNA3D_EndPassRestore"
    };

    static int Main(string[] args)
    {
        try {
            if (args.Length != 2) throw new Exception("usage: TModFnaPatch <input-FNA.dll> <output-FNA.dll>");
            Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
            return 0;
        } catch (Exception e) { Console.Error.WriteLine("TModFnaPatch: " + e); return 1; }
    }

    static void Run(string input, string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
        resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        using var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { ReadSymbols = false, AssemblyResolver = resolver });
        Require(asm.Name.Name == "FNA", "not FNA");
        int suppressFound = PatchSuppressGc(asm.MainModule);
        bool sharedAudioChanged = PatchSharedAudio(asm.MainModule);
        asm.Write(output, new WriterParameters { Timestamp = 0 });
        var receipt = new {
            input = new { path = input, sha256 = Sha(input), mvid = asm.MainModule.Mvid.ToString() },
            output = new { path = output, sha256 = Sha(output), mvid = AssemblyDefinition.ReadAssembly(output).MainModule.Mvid.ToString() },
            suppressGcTransitionTargets = suppressFound,
            sharedAudioContextPatched = sharedAudioChanged,
            scope = "tModLoader FNA-only native interop/audio patches; no Terraria/ReLogic IL patches"
        };
        File.WriteAllText(output + ".receipt.json", JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine(JsonSerializer.Serialize(receipt));
    }

    static int PatchSuppressGc(ModuleDefinition module)
    {
        var corelib = module.AssemblyReferences.FirstOrDefault(r => r.Name == "System.Private.CoreLib")
            ?? new AssemblyNameReference("System.Private.CoreLib", new Version(9, 0, 0, 0)) { PublicKeyToken = new byte[] { 0x7c, 0xec, 0x85, 0xd7, 0xbe, 0xa7, 0x79, 0x8e } };
        if (!module.AssemblyReferences.Contains(corelib)) module.AssemblyReferences.Add(corelib);
        var attrType = new TypeReference("System.Runtime.InteropServices", "SuppressGCTransitionAttribute", module, corelib);
        var ctor = new MethodReference(".ctor", module.TypeSystem.Void, attrType) { HasThis = true };
        var wanted = Suppress.ToHashSet();
        int found = 0;
        foreach (var m in Types(module).SelectMany(t => t.Methods).Where(m => m.HasPInvokeInfo && m.PInvokeInfo.Module.Name == "FNA3D" && wanted.Contains(m.PInvokeInfo.EntryPoint))) {
            found++;
            if (!m.CustomAttributes.Any(a => a.AttributeType.FullName == attrType.FullName))
                m.CustomAttributes.Add(new CustomAttribute(ctor));
        }
        Require(found == Suppress.Length, "missing FNA3D SuppressGCTransition targets: " + found);
        return found;
    }

    static bool PatchSharedAudio(ModuleDefinition module)
    {
        var engine = Type(module, "Microsoft.Xna.Framework.Audio.AudioEngine");
        var sound = Type(module, "Microsoft.Xna.Framework.Audio.SoundEffect");
        var ctx = sound.NestedTypes.Single(t => t.Name == "FAudioContext");
        var fa = Type(module, "FAudio");
        var parms = fa.NestedTypes.Single(t => t.Name == "FACTRuntimeParameters");
        var device = sound.Methods.Single(m => m.Name == "Device" && m.IsStatic);
        var addRef = fa.Methods.Single(m => m.Name == "FAudio_AddRef");
        var handle = ctx.Fields.Single(f => f.Name == "Handle");
        var master = ctx.Fields.Single(f => f.Name == "MasterVoice");
        var pxa = parms.Fields.Single(f => f.Name == "pXAudio2");
        var pm = parms.Fields.Single(f => f.Name == "pMasteringVoice");
        var ctor = engine.Methods.Single(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 3);
        if (ctor.Body.Instructions.Any(i => IsCall(i, "FAudio_AddRef"))) return false;
        var init = ctor.Body.Instructions.Single(i => IsCall(i, "FACTAudioEngine_Initialize"));
        var ploc = (VariableDefinition)init.Previous!.Operand;
        var shared = new VariableDefinition(ctx);
        ctor.Body.Variables.Add(shared);
        var il = ctor.Body.GetILProcessor();
        foreach (var ins in new[] {
            il.Create(OpCodes.Call, device), il.Create(OpCodes.Stloc, shared),
            il.Create(OpCodes.Ldloc, shared), il.Create(OpCodes.Ldfld, handle), il.Create(OpCodes.Call, addRef), il.Create(OpCodes.Pop),
            il.Create(OpCodes.Ldloca, ploc), il.Create(OpCodes.Ldloc, shared), il.Create(OpCodes.Ldfld, handle), il.Create(OpCodes.Stfld, pxa),
            il.Create(OpCodes.Ldloca, ploc), il.Create(OpCodes.Ldloc, shared), il.Create(OpCodes.Ldfld, master), il.Create(OpCodes.Stfld, pm)
        }) il.InsertBefore(init, ins);
        ctor.Body.MaxStackSize = Math.Max(ctor.Body.MaxStackSize, 4);
        var disp = engine.Methods.Single(m => m.Name == "Dispose" && m.Parameters.Count == 1);
        foreach (var name in new[] { "FACTAudioEngine_ShutDown", "FACTAudioEngine_Release" }) {
            var call = disp.Body.Instructions.Single(i => IsCall(i, name));
            foreach (var ins in new[] { call.Previous!.Previous!, call.Previous!, call, call.Next! }) { ins.OpCode = OpCodes.Nop; ins.Operand = null; }
        }
        return true;
    }

    static bool IsCall(Instruction i, string name) => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference r && r.Name == name;
    static TypeDefinition Type(ModuleDefinition m, string fullName) => Types(m).Single(t => t.FullName == fullName);
    static IEnumerable<TypeDefinition> Types(ModuleDefinition m) => m.Types.SelectMany(Flatten);
    static IEnumerable<TypeDefinition> Flatten(TypeDefinition t) { yield return t; foreach (var n in t.NestedTypes.SelectMany(Flatten)) yield return n; }
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
