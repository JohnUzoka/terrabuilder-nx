using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;

static class FnaPatches
{
    static readonly string[] Suppress = {
        "FNA3D_DrawIndexedPrimitives", "FNA3D_DrawPrimitives", "FNA3D_SetViewport", "FNA3D_SetScissorRect", "FNA3D_SetBlendFactor",
        "FNA3D_SetMultiSampleMask", "FNA3D_SetReferenceStencil", "FNA3D_SetBlendState", "FNA3D_SetDepthStencilState", "FNA3D_ApplyRasterizerState",
        "FNA3D_VerifySampler", "FNA3D_VerifyVertexSampler", "FNA3D_ApplyVertexBufferBindings", "FNA3D_ApplyEffect", "FNA3D_BeginPassRestore", "FNA3D_EndPassRestore"
    };

    public static void Apply(AssemblyDefinition asm, AssemblyDefinition diag)
    {
        var module = asm.MainModule;
        Require(asm.Name.Name == "FNA", "not FNA");
        var fna3d = Type(module, "Microsoft.Xna.Framework.Graphics.FNA3D");
        fna3d.IsPublic = true;
        fna3d.Methods.Single(m => m.Name == "FNA3D_LinkedVersion").IsPublic = true;
        PatchDispose(module);
        PatchRootsAndInput(asm, diag);
        PatchSuppressGc(module);
        PatchSharedAudio(module);
    }

    static void PatchDispose(ModuleDefinition module)
    {
        var rt = Type(module, "Microsoft.Xna.Framework.Graphics.RenderTarget2D");
        var dispose = Common.Method(rt, "Dispose", "System.Void", "System.Boolean");
        var throwI = dispose.Body.Instructions.Single(i => i.OpCode == OpCodes.Throw);
        var newEx = throwI.Previous!; var message = newEx.Previous!; var cont = throwI.Next!;
        var getDevice = Common.Method(Type(module, "Microsoft.Xna.Framework.Graphics.GraphicsResource"), "get_GraphicsDevice", "Microsoft.Xna.Framework.Graphics.GraphicsDevice");
        var setTarget = Common.Method(Type(module, "Microsoft.Xna.Framework.Graphics.GraphicsDevice"), "SetRenderTarget", "System.Void", "Microsoft.Xna.Framework.Graphics.RenderTarget2D");
        message.OpCode = OpCodes.Ldarg_0; message.Operand = null;
        newEx.OpCode = OpCodes.Call; newEx.Operand = getDevice;
        throwI.OpCode = OpCodes.Ldnull; throwI.Operand = null;
        var il = dispose.Body.GetILProcessor(); var unbind = Instruction.Create(OpCodes.Callvirt, setTarget);
        il.InsertAfter(throwI, unbind); il.InsertAfter(unbind, Instruction.Create(OpCodes.Br, cont));
    }

    static void PatchRootsAndInput(AssemblyDefinition asm, AssemblyDefinition diag)
    {
        var module = asm.MainModule;
        var content = Type(module, "Microsoft.Xna.Framework.Content.ContentManager");
        Replace(Common.Method(content, "get_RootDirectory", "System.String"), "romfs:/Content");
        Replace(Common.Method(content, "get_RootDirectoryFullPath", "System.String"), "romfs:/Content");
        var platform = Type(module, "Microsoft.Xna.Framework.SDL2_FNAPlatform");
        Replace(Common.Method(platform, "GetBaseDirectory", "System.String"), "romfs:/");
        var helperType = module.ImportReference(Type(diag.MainModule, "Terraria.NxInputDiag.InputDiagnostics"));
        MethodReference H(string name, TypeReference ret, params TypeReference[] ps) { var r = new MethodReference(name, ret, helperType){HasThis=false}; foreach (var p in ps) r.Parameters.Add(new ParameterDefinition(p)); return r; }
        var install = H("InstallSwitchMapping", module.TypeSystem.Void);
        var getButton = H("GetButton", module.TypeSystem.Byte, module.TypeSystem.IntPtr, module.TypeSystem.Int32);
        var phases = new[]{"BeginTick","EndTick","BeginUpdate","EndUpdate","BeginDraw","EndDraw"}.ToDictionary(n=>n, n=>H(n,module.TypeSystem.Void));
        var init = Common.Method(platform, "ProgramInit", "System.String", "Microsoft.Xna.Framework.LaunchParameters");
        var sdlInit = Calls(init, "SDL2.SDL", "SDL_Init").Single(); init.Body.GetILProcessor().InsertAfter(sdlInit, Instruction.Create(OpCodes.Call, install));
        var getState = Common.Method(platform, "GetGamePadState", "Microsoft.Xna.Framework.Input.GamePadState", "System.Int32", "Microsoft.Xna.Framework.Input.GamePadDeadZone");
        foreach (var c in Calls(getState, "SDL2.SDL", "SDL_GameControllerGetButton")) c.Operand = getButton;
        var tick = Common.Method(Type(module, "Microsoft.Xna.Framework.Game"), "Tick", "System.Void");
        var il = tick.Body.GetILProcessor(); il.InsertBefore(tick.Body.Instructions[0], Instruction.Create(OpCodes.Call, phases["BeginTick"]));
        foreach (var c in Calls(tick, "Microsoft.Xna.Framework.Game", "Update")) { il.InsertBefore(c, Instruction.Create(OpCodes.Call, phases["BeginUpdate"])); il.InsertAfter(c, Instruction.Create(OpCodes.Call, phases["EndUpdate"])); }
        foreach (var c in Calls(tick, "Microsoft.Xna.Framework.Game", "Draw")) { il.InsertBefore(c, Instruction.Create(OpCodes.Call, phases["BeginDraw"])); il.InsertAfter(c, Instruction.Create(OpCodes.Call, phases["EndDraw"])); }
        foreach (var ret in tick.Body.Instructions.Where(i=>i.OpCode==OpCodes.Ret).ToArray()) { ret.OpCode = OpCodes.Call; ret.Operand = phases["EndTick"]; il.InsertAfter(ret, Instruction.Create(OpCodes.Ret)); }
        foreach (var m in new[]{init,tick}) WidenBranches(m);
    }
    static void Replace(MethodDefinition m, string s) { m.Body.Variables.Clear(); m.Body.ExceptionHandlers.Clear(); m.Body.Instructions.Clear(); m.Body.InitLocals=false; m.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr,s)); m.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); }

    static void PatchSuppressGc(ModuleDefinition module)
    {
        var corlib = module.AssemblyReferences.FirstOrDefault(r=>r.Name=="System.Private.CoreLib") ?? new AssemblyNameReference("System.Private.CoreLib", new Version(9,0,0,0)){PublicKeyToken=new byte[]{0x7c,0xec,0x85,0xd7,0xbe,0xa7,0x79,0x8e}};
        if (!module.AssemblyReferences.Contains(corlib)) module.AssemblyReferences.Add(corlib);
        var attrType = new TypeReference("System.Runtime.InteropServices","SuppressGCTransitionAttribute",module,corlib);
        var ctor = new MethodReference(".ctor", module.TypeSystem.Void, attrType){HasThis=true};
        var wanted = Suppress.ToHashSet(); int n=0;
        foreach (var m in Types(module).SelectMany(t=>t.Methods).Where(m=>m.HasPInvokeInfo && m.PInvokeInfo.Module.Name=="FNA3D" && wanted.Contains(m.PInvokeInfo.EntryPoint))) { if (!m.CustomAttributes.Any(a=>a.AttributeType.Name==attrType.Name)) m.CustomAttributes.Add(new CustomAttribute(ctor)); n++; }
        Require(n == Suppress.Length, "missing FNA3D SuppressGCTransition targets: " + n);
    }

    static void PatchSharedAudio(ModuleDefinition module)
    {
        var engine = Type(module, "Microsoft.Xna.Framework.Audio.AudioEngine"); var sound = Type(module, "Microsoft.Xna.Framework.Audio.SoundEffect"); var ctx = sound.NestedTypes.Single(t=>t.Name=="FAudioContext"); var fa = Type(module,"FAudio"); var parms = fa.NestedTypes.Single(t=>t.Name=="FACTRuntimeParameters");
        var device = sound.Methods.Single(m=>m.Name=="Device" && m.IsStatic); var addRef = fa.Methods.Single(m=>m.Name=="FAudio_AddRef"); var handle = ctx.Fields.Single(f=>f.Name=="Handle"); var master = ctx.Fields.Single(f=>f.Name=="MasterVoice"); var pxa = parms.Fields.Single(f=>f.Name=="pXAudio2"); var pm = parms.Fields.Single(f=>f.Name=="pMasteringVoice");
        bool IsCall(Instruction i,string n)=>(i.OpCode==OpCodes.Call||i.OpCode==OpCodes.Callvirt)&&i.Operand is MethodReference r&&r.Name==n;
        var ctor = engine.Methods.Single(m=>m.IsConstructor && !m.IsStatic && m.Parameters.Count==3); var init = ctor.Body.Instructions.Single(i=>IsCall(i,"FACTAudioEngine_Initialize")); var ploc = (VariableDefinition)init.Previous!.Operand; var shared = new VariableDefinition(ctx); ctor.Body.Variables.Add(shared); var il=ctor.Body.GetILProcessor();
        foreach (var ins in new[]{il.Create(OpCodes.Call,device),il.Create(OpCodes.Stloc,shared),il.Create(OpCodes.Ldloc,shared),il.Create(OpCodes.Ldfld,handle),il.Create(OpCodes.Call,addRef),il.Create(OpCodes.Pop),il.Create(OpCodes.Ldloca,ploc),il.Create(OpCodes.Ldloc,shared),il.Create(OpCodes.Ldfld,handle),il.Create(OpCodes.Stfld,pxa),il.Create(OpCodes.Ldloca,ploc),il.Create(OpCodes.Ldloc,shared),il.Create(OpCodes.Ldfld,master),il.Create(OpCodes.Stfld,pm)}) il.InsertBefore(init,ins);
        ctor.Body.MaxStackSize = Math.Max(ctor.Body.MaxStackSize,4);
        var disp = engine.Methods.Single(m=>m.Name=="Dispose" && m.Parameters.Count==1);
        foreach (var name in new[]{"FACTAudioEngine_ShutDown","FACTAudioEngine_Release"}) { var call = disp.Body.Instructions.Single(i=>IsCall(i,name)); foreach (var ins in new[]{call.Previous!.Previous!, call.Previous!, call, call.Next!}) { ins.OpCode=OpCodes.Nop; ins.Operand=null; } }
    }
}
