using Mono.Cecil;
using Mono.Cecil.Cil;

// usage: SharedAudio <in FNA.dll> <out FNA.dll>
//
// FNA's XACT AudioEngine lets FACT create its own FAudio instance and mastering voice,
// which opens a second SDL audio device. The Switch SDL audio driver supports only one
// open device, so music (XACT) and sound effects (SoundEffect) cannot both start.
// This patch makes AudioEngine reuse SoundEffect's FAudio context:
//   * .ctor: before FACTAudioEngine_Initialize, take SoundEffect.Device(), AddRef its
//     FAudio handle, and pass Handle/MasterVoice as pXAudio2/pMasteringVoice.
//   * Dispose(bool): skip FACTAudioEngine_ShutDown/Release. FACT would otherwise stop
//     the shared engine and destroy the shared mastering voice, which SoundEffect still
//     owns. The engine lives for the whole process, so nothing is freed underneath
//     either side at exit; the extra reference keeps FAudio alive until the process ends.
// The module MVID is unchanged.
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: SharedAudio <in FNA.dll> <out FNA.dll>");
    return 2;
}
var assembly = AssemblyDefinition.ReadAssembly(args[0]);
var module = assembly.MainModule;
TypeDefinition Type(string name) => module.GetType(name) ?? throw new Exception("type not found: " + name);
var engine = Type("Microsoft.Xna.Framework.Audio.AudioEngine");
var soundEffect = Type("Microsoft.Xna.Framework.Audio.SoundEffect");
var context = soundEffect.NestedTypes.Single(t => t.Name == "FAudioContext");
var faudio = Type("FAudio");
var parameters = faudio.NestedTypes.Single(t => t.Name == "FACTRuntimeParameters");

var device = soundEffect.Methods.Single(m => m.Name == "Device" && m.IsStatic && m.Parameters.Count == 0);
var addRef = faudio.Methods.Single(m => m.Name == "FAudio_AddRef");
var handle = context.Fields.Single(f => f.Name == "Handle");
var master = context.Fields.Single(f => f.Name == "MasterVoice");
var pXAudio2 = parameters.Fields.Single(f => f.Name == "pXAudio2");
var pMastering = parameters.Fields.Single(f => f.Name == "pMasteringVoice");

static bool IsCall(Instruction i, string name) =>
    (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference m && m.Name == name;

var ctor = engine.Methods.Single(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 3);
if (ctor.Body.Instructions.Any(i => i.Operand is FieldReference f && (f.Name == "pXAudio2" || f.Name == "pMasteringVoice")))
    throw new Exception("AudioEngine..ctor already sets pXAudio2/pMasteringVoice; patch not needed");
var init = ctor.Body.Instructions.Single(i => IsCall(i, "FACTAudioEngine_Initialize"));
// Stack at the call: engine handle, &parameters. The parameters local is the ldloca just before it.
var paramsLoad = init.Previous;
if (paramsLoad.OpCode != OpCodes.Ldloca_S && paramsLoad.OpCode != OpCodes.Ldloca)
    throw new Exception("unexpected FACTAudioEngine_Initialize argument shape");
var paramsLocal = (VariableDefinition)paramsLoad.Operand;
var shared = new VariableDefinition(context);
ctor.Body.Variables.Add(shared);
var il = ctor.Body.GetILProcessor();
foreach (var instruction in new[]
{
    il.Create(OpCodes.Call, device),
    il.Create(OpCodes.Stloc, shared),
    il.Create(OpCodes.Ldloc, shared),
    il.Create(OpCodes.Ldfld, handle),
    il.Create(OpCodes.Call, addRef),
    il.Create(OpCodes.Pop),
    il.Create(OpCodes.Ldloca, paramsLocal),
    il.Create(OpCodes.Ldloc, shared),
    il.Create(OpCodes.Ldfld, handle),
    il.Create(OpCodes.Stfld, pXAudio2),
    il.Create(OpCodes.Ldloca, paramsLocal),
    il.Create(OpCodes.Ldloc, shared),
    il.Create(OpCodes.Ldfld, master),
    il.Create(OpCodes.Stfld, pMastering),
})
    il.InsertBefore(init, instruction);
ctor.Body.MaxStackSize = Math.Max(ctor.Body.MaxStackSize, 4);

var dispose = engine.Methods.Single(m => m.Name == "Dispose" && m.Parameters.Count == 1);
int removed = 0;
foreach (var name in new[] { "FACTAudioEngine_ShutDown", "FACTAudioEngine_Release" })
{
    // ldarg.0; ldfld handle; call X; pop  ->  four nops (keeps any branch targets valid)
    var call = dispose.Body.Instructions.Single(i => IsCall(i, name));
    var sequence = new[] { call.Previous.Previous, call.Previous, call, call.Next };
    if (sequence[0].OpCode != OpCodes.Ldarg_0 || sequence[1].OpCode != OpCodes.Ldfld || sequence[3].OpCode != OpCodes.Pop)
        throw new Exception("unexpected AudioEngine.Dispose shape around " + name);
    foreach (var instruction in sequence)
    {
        instruction.OpCode = OpCodes.Nop;
        instruction.Operand = null;
    }
    removed++;
}
assembly.Write(args[1]);
Console.WriteLine($"shared FAudio context: AudioEngine..ctor patched; Dispose FACT calls removed={removed}; mvid {module.Mvid}");
return 0;
