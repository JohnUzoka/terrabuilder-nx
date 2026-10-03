using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;

static class TerrariaPatches
{
    public static void Apply(AssemblyDefinition asm, AssemblyDefinition nxCrypto)
    {
        var m = asm.MainModule;
        PatchLinuxLaunch(m); PatchCrypto(m, nxCrypto.MainModule); PatchStartForceLoad(m); PatchEmptyDesktopServices(m); PatchWeGame(m); PatchTcp(m);
        TimeLoggerPatch.Apply(m); DrawGatePatch.Apply(m); AotInliningPatch.Apply(m);
    }
    static void PatchLinuxLaunch(ModuleDefinition m)
    {
        var linux = Type(m,"Terraria.LinuxLaunch"); if (linux.Fields.Any(f=>f.Name=="_cachedReLogicAssembly")) return;
        var asmType = m.ImportReference(typeof(System.Reflection.Assembly));
        var cache = new FieldDefinition("_cachedReLogicAssembly", FieldAttributes.Private|FieldAttributes.Static, asmType); linux.Fields.Add(cache);
        var lambda = Types(m).SelectMany(t=>t.Methods).Single(x=>x.Name=="<Main>b__0_0");
        var call = lambda.Body.Instructions.First(i=>i.OpCode==OpCodes.Call && i.Operand is MethodReference r && r.DeclaringType.FullName=="System.Reflection.Assembly" && r.Name=="Load" && r.Parameters.Count==1 && r.Parameters[0].ParameterType.FullName=="System.Byte[]");
        var originalLoad = call.Previous!; var originalStore = call.Next!; var afterOriginal = originalStore.Next!;
        var resourceField = Types(m).SelectMany(t=>t.Fields).Single(f=>f.Name=="resourceName");
        var strEq = m.ImportReference(typeof(string).GetMethod("op_Equality", new[]{typeof(string),typeof(string)})!);
        var asmLocal = lambda.Body.Variables[4];
        var relogicLoad = Instruction.Create(OpCodes.Ldloc, lambda.Body.Variables[3]);
        var il=lambda.Body.GetILProcessor();
        var inserted = new[]{
            Instruction.Create(OpCodes.Ldloc, lambda.Body.Variables[0]), Instruction.Create(OpCodes.Ldfld, resourceField), Instruction.Create(OpCodes.Ldstr,"ReLogic.dll"), Instruction.Create(OpCodes.Call,strEq), Instruction.Create(OpCodes.Brfalse, originalLoad),
            Instruction.Create(OpCodes.Ldsfld,cache), Instruction.Create(OpCodes.Brfalse, relogicLoad), Instruction.Create(OpCodes.Ldsfld,cache), Instruction.Create(OpCodes.Stloc, asmLocal), Instruction.Create(OpCodes.Br, afterOriginal),
            relogicLoad, Instruction.Create(OpCodes.Call, (MethodReference)call.Operand), Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Stsfld, cache), Instruction.Create(OpCodes.Stloc, asmLocal), Instruction.Create(OpCodes.Br, afterOriginal)
        };
        foreach (var ins in inserted) il.InsertBefore(originalLoad, ins);
        WidenBranches(lambda);
    }
    static void PatchCrypto(ModuleDefinition m, ModuleDefinition nx)
    {
        var nxType = Type(nx,"Terraria.NxCrypto.NxCrypto"); var enc=m.ImportReference(nxType.Methods.Single(x=>x.Name=="CreateEncryptor")); var dec=m.ImportReference(nxType.Methods.Single(x=>x.Name=="CreateDecryptor"));
        var save=Method(m,"System.Void Terraria.Player::InternalSavePlayerFile(Terraria.IO.PlayerFileData)");
        NopNewRijndael(save); var ce=CallsByName(save,"CreateEncryptor").Single(); var ceObj=ce.Previous!.Previous!.Previous!; ceObj.OpCode=OpCodes.Nop; ceObj.Operand=null; ce.OpCode=OpCodes.Call; ce.Operand=enc;
        var load=Method(m,"Terraria.IO.PlayerFileData Terraria.Player::LoadPlayer(System.String,System.Boolean)");
        var newobj=load.Body.Instructions.First(i=>i.OpCode==OpCodes.Newobj && i.Operand is MethodReference r && r.DeclaringType.FullName.Contains("RijndaelManaged")); for(var x=newobj; ; x=x.Next!){ var next=x.Next; x.OpCode=OpCodes.Nop; x.Operand=null; if(next!=null && next.OpCode==OpCodes.Callvirt && next.Operand is MethodReference r && r.Name=="set_Padding") { next.OpCode=OpCodes.Nop; next.Operand=null; break; } }
        var cd=CallsByName(load,"CreateDecryptor").Single(); var cdObj=cd.Previous!.Previous!.Previous!; cdObj.OpCode=OpCodes.Nop; cdObj.Operand=null; cd.OpCode=OpCodes.Call; cd.Operand=dec;
    }
    static IEnumerable<Instruction> CallsByName(MethodDefinition m,string name)=>m.Body.Instructions.Where(i=>(i.OpCode==OpCodes.Call||i.OpCode==OpCodes.Callvirt)&&i.Operand is MethodReference r&&r.Name==name);
    static void NopNewRijndael(MethodDefinition m){ var n=m.Body.Instructions.First(i=>i.OpCode==OpCodes.Newobj && i.Operand is MethodReference r && r.DeclaringType.FullName.Contains("RijndaelManaged")); n.OpCode=OpCodes.Nop; n.Operand=null; n.Next!.OpCode=OpCodes.Nop; n.Next.Operand=null; }
    static void PatchStartForceLoad(ModuleDefinition m){ var meth=Method(m,"System.Void Terraria.Program::StartForceLoad()"); var skip=Type(m,"Terraria.Program").Fields.Single(f=>f.Name=="LoadedEverything"); meth.Body.ExceptionHandlers.Clear(); meth.Body.Variables.Clear(); meth.Body.Instructions.Clear(); meth.Body.MaxStackSize=8; var il=meth.Body.GetILProcessor(); il.Append(il.Create(OpCodes.Ldc_I4_1)); il.Append(il.Create(OpCodes.Stsfld,skip)); il.Append(il.Create(OpCodes.Ret)); }
    static void PatchEmptyDesktopServices(ModuleDefinition m){ Empty(Method(m,"System.Void Terraria.Initializers.ChromaInitializer::Load()")); Empty(Method(m,"System.Void Terraria.Initializers.ChromaInitializer::UpdateEvents()")); Empty(Method(m,"System.Void Terraria.Testing.WindowsPerformanceDiagnostics::.cctor()")); }
    static void PatchWeGame(ModuleDefinition m){ var t=Type(m,"Terraria.Social.WeGame.CurrentThreadRunner"); var f=t.Fields.Single(x=>x.Name=="_dsipatcher"); f.FieldType=m.TypeSystem.Object; Empty(t.Methods.Single(x=>x.Name==".ctor"), false); foreach(var i in t.Methods.Single(x=>x.Name=="Run").Body.Instructions.Where(i=>i.Operand is FieldReference fr && fr.Name=="_dsipatcher")) i.Operand=f; }
    static void PatchTcp(ModuleDefinition m)
    {
        var t=Type(m,"Terraria.Net.Sockets.TcpSocket"); var method=t.Methods.Single(x=>x.Name=="Terraria.Net.Sockets.ISocket.IsConnected");
        var fConn=(FieldReference)method.Body.Instructions[1].Operand; var fDebug=(FieldReference)t.Fields.Single(f=>f.Name=="_debugStream"); var getClient=(MethodReference)method.Body.Instructions.First(i=>i.Operand is MethodReference r&&r.Name=="get_Client").Operand; var getConn=(MethodReference)method.Body.Instructions.First(i=>i.Operand is MethodReference r&&r.Name=="get_Connected").Operand; var hasBuf=(MethodReference)method.Body.Instructions.First(i=>i.Operand is MethodReference r&&r.Name=="get_HasBufferedData").Operand; var poll=(MethodReference)method.Body.Instructions.First(i=>i.Operand is MethodReference r&&r.Name=="Poll").Operand; var avail=(MethodReference)method.Body.Instructions.First(i=>i.Operand is MethodReference r&&r.Name=="get_Available").Operand;
        method.Body.Instructions.Clear(); method.Body.Variables.Clear(); method.Body.ExceptionHandlers.Clear(); method.Body.InitLocals=true; var vC=new VariableDefinition(fConn.FieldType); var vS=new VariableDefinition(getClient.ReturnType); var vD=new VariableDefinition(fDebug.FieldType); var vR=new VariableDefinition(m.TypeSystem.Boolean); method.Body.Variables.Add(vC); method.Body.Variables.Add(vS); method.Body.Variables.Add(vD); method.Body.Variables.Add(vR); var il=method.Body.GetILProcessor(); var retFalse=il.Create(OpCodes.Ldc_I4_0); var retRes=il.Create(OpCodes.Ldloc,vR); var pollI=il.Create(OpCodes.Ldloc,vS); var notReadable=il.Create(OpCodes.Ldc_I4_0); var negate=il.Create(OpCodes.Ldc_I4_0); var handler=il.Create(OpCodes.Pop); var tryStart=il.Create(OpCodes.Ldloc,vD);
        foreach(var ins in new[]{il.Create(OpCodes.Ldarg_0),il.Create(OpCodes.Ldfld,fConn),il.Create(OpCodes.Stloc,vC),il.Create(OpCodes.Ldloc,vC),il.Create(OpCodes.Brfalse,retFalse),il.Create(OpCodes.Ldloc,vC),il.Create(OpCodes.Callvirt,getClient),il.Create(OpCodes.Stloc,vS),il.Create(OpCodes.Ldloc,vS),il.Create(OpCodes.Brfalse,retFalse),il.Create(OpCodes.Ldloc,vC),il.Create(OpCodes.Callvirt,getConn),il.Create(OpCodes.Brfalse,retFalse),il.Create(OpCodes.Ldarg_0),il.Create(OpCodes.Ldfld,fDebug),il.Create(OpCodes.Stloc,vD),tryStart,il.Create(OpCodes.Brfalse,pollI),il.Create(OpCodes.Ldloc,vD),il.Create(OpCodes.Callvirt,hasBuf),il.Create(OpCodes.Brfalse,pollI),il.Create(OpCodes.Ldc_I4_1),il.Create(OpCodes.Stloc,vR),il.Create(OpCodes.Leave,retRes),pollI,il.Create(OpCodes.Ldc_I4_0),il.Create(OpCodes.Ldc_I4_0),il.Create(OpCodes.Callvirt,poll),il.Create(OpCodes.Brfalse,notReadable),il.Create(OpCodes.Ldloc,vS),il.Create(OpCodes.Callvirt,avail),il.Create(OpCodes.Ldc_I4_0),il.Create(OpCodes.Ceq),il.Create(OpCodes.Br,negate),notReadable,negate,il.Create(OpCodes.Ceq),il.Create(OpCodes.Stloc,vR),il.Create(OpCodes.Leave,retRes),handler,il.Create(OpCodes.Ldc_I4_0),il.Create(OpCodes.Stloc,vR),il.Create(OpCodes.Leave,retRes),retRes,il.Create(OpCodes.Ret),retFalse,il.Create(OpCodes.Ret)}) il.Append(ins);
        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch){CatchType=new TypeReference("System","ObjectDisposedException",m,m.TypeSystem.CoreLibrary),TryStart=tryStart,TryEnd=handler,HandlerStart=handler,HandlerEnd=retRes});
    }
}
