using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Contracts;
using static AuditCanonical;

internal static class StackStatePatcher
{
    // Frozen candidate03: appended state preserves definition tokens, and InitLocals
    // supplies zeroing. No experimental nested-type or redundant-initobj variant.
    internal static byte[] Emit(AssemblyDefinition assembly)
    {
  var module=assembly.MainModule;var allTypes=Types(module).ToArray();var old=allTypes.Single(t=>t.FullName==Original);var owner=allTypes.Single(t=>t.FullName==Owner);
  var entry=owner.Methods.Single(m=>m.Name=="DrawSingleTile");var helpers=owner.Methods.Where(m=>m.Parameters.Any(p=>p.ParameterType.FullName==Original)).ToArray();var closed=new[]{entry}.Concat(helpers).ToArray();
  var allMethods=allTypes.SelectMany(t=>t.Methods).ToArray();
  var helperTokens=helpers.Select(m=>m.MetadataToken.ToUInt32()).ToHashSet();
  Require(helperTokens.SetEquals(HelperTokens) && entry.MetadataToken.ToInt32()==EntryToken,"closed definition tokens differ");
  var literals=allMethods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions.Where(i=>i.OpCode==OpCodes.Ldstr && (i.Operand is string s && (s.Contains("TileDrawInfo",StringComparison.Ordinal) || closed.Any(c=>s.Contains(c.Name,StringComparison.Ordinal))))).Select(i=>new{method=m.FullName,offset=i.Offset,value=(string)i.Operand})).ToArray();
  Require(literals.Length==0,"matching reflection name strings require separate audit");
  var ctor=old.Methods.Single();var ctorIns=ctor.Body.Instructions;
  Require(ctorIns.Count==7 && ctorIns[0].OpCode==OpCodes.Ldarg_0 && ctorIns[1].OpCode==OpCodes.Ldc_I4_S && (sbyte)ctorIns[1].Operand==9 && ctorIns[2].OpCode==OpCodes.Newarr && ctorIns[2].Operand is TypeReference vector && vector.FullName=="Microsoft.Xna.Framework.Vector3" && ctorIns[3].OpCode==OpCodes.Stfld && ((FieldReference)ctorIns[3].Operand).Name=="colorSlices" && ctorIns[4].OpCode==OpCodes.Ldarg_0 && ctorIns[5].OpCode==OpCodes.Call && ((MethodReference)ctorIns[5].Operand).FullName=="System.Void System.Object::.ctor()" && ctorIns[6].OpCode==OpCodes.Ret,"ctor contract differs");
  var state=new TypeDefinition("Terraria.GameContent.Drawing","TileDrawState54",TypeAttributes.NotPublic|TypeAttributes.SequentialLayout|TypeAttributes.Sealed|TypeAttributes.BeforeFieldInit,new TypeReference("System","ValueType",module,module.TypeSystem.CoreLibrary));
  module.Types.Add(state);
  foreach(var field in old.Fields) state.Fields.Add(new FieldDefinition(field.Name,FieldAttributes.Public,field.FieldType));
  var fields=state.Fields.ToDictionary(f=>f.Name);var originalEntry=entry.Body.Instructions.ToArray();
  Require(entry.Body.InitLocals && entry.Body.Variables[0].VariableType.FullName==Original && originalEntry[0].OpCode==OpCodes.Newobj && ((MethodReference)originalEntry[0].Operand).FullName==ctor.FullName && originalEntry[1].OpCode==OpCodes.Stloc_0,"entry allocation contract differs");
  Require(!originalEntry.SelectMany(i=>i.Operand is Instruction t?new[]{t}:i.Operand is Instruction[] ts?ts:Array.Empty<Instruction>()).Any(t=>t==originalEntry[0]||t==originalEntry[1]),"initialization is branch target");
  Require(originalEntry.Count(i=>i.OpCode==OpCodes.Stloc_0 || i.OpCode.Code is Code.Stloc or Code.Stloc_S && i.Operand is VariableDefinition v && v.Index==0)==1,"scratch reassigns");
  entry.Body.Variables[0].VariableType=state;int ownerLoads=0,fieldRefs=0,callRefs=0;
  for(int n=2;n<originalEntry.Length;n++) if(Local0(originalEntry[n])) {originalEntry[n].OpCode=OpCodes.Ldloca;originalEntry[n].Operand=entry.Body.Variables[0];ownerLoads++;}
  foreach(var helper in helpers) foreach(var p in helper.Parameters.Where(p=>p.ParameterType.FullName==Original)) p.ParameterType=new ByReferenceType(state);
  foreach(var m in closed) foreach(var ins in m.Body.Instructions) {
   if(ins.Operand is FieldReference f && f.DeclaringType.FullName==Original) {Require(ins.OpCode.Code is Code.Ldfld or Code.Ldflda or Code.Stfld,"unsupported field opcode");ins.Operand=fields[f.Name];fieldRefs++;}
   if(ins.Operand is MethodReference call && helperTokens.Contains(call.MetadataToken.ToUInt32())) {Require(call is MethodDefinition && helpers.Contains(call),"unshared helper reference");callRefs++;}
  }
  Require(ownerLoads==238 && fieldRefs==1123 && callRefs==8,"state rewrite counts differ");
  var il=entry.Body.GetILProcessor();il.Remove(originalEntry[0]);il.Remove(originalEntry[1]);
  var prefix=new[]{Instruction.Create(OpCodes.Ldloca,entry.Body.Variables[0]),Instruction.Create(OpCodes.Ldc_I4_S,(sbyte)9),Instruction.Create(OpCodes.Newarr,(TypeReference)ctorIns[2].Operand),Instruction.Create(OpCodes.Stfld,fields["colorSlices"])};
  foreach(var ins in prefix)il.InsertBefore(originalEntry[2],ins);
  foreach(var ins in entry.Body.Instructions) ins.OpCode=ins.OpCode.Code switch {Code.Br_S=>OpCodes.Br,Code.Brfalse_S=>OpCodes.Brfalse,Code.Brtrue_S=>OpCodes.Brtrue,Code.Beq_S=>OpCodes.Beq,Code.Bne_Un_S=>OpCodes.Bne_Un,Code.Bge_S=>OpCodes.Bge,Code.Bge_Un_S=>OpCodes.Bge_Un,Code.Bgt_S=>OpCodes.Bgt,Code.Bgt_Un_S=>OpCodes.Bgt_Un,Code.Ble_S=>OpCodes.Ble,Code.Ble_Un_S=>OpCodes.Ble_Un,Code.Blt_S=>OpCodes.Blt,Code.Blt_Un_S=>OpCodes.Blt_Un,Code.Leave_S=>OpCodes.Leave,_=>ins.OpCode};
  module.Mvid=Guid.Empty;using(var stream=new MemoryStream()){assembly.Write(stream,new WriterParameters{Timestamp=0});module.Mvid=new Guid(SHA256.HashData(stream.ToArray()).AsSpan(0,16));}
  byte[] candidate;using(var stream=new MemoryStream()){assembly.Write(stream,new WriterParameters{Timestamp=0});candidate=stream.ToArray();}
  return candidate;
    }
}
