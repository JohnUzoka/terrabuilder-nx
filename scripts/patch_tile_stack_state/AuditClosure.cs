using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

using static Contracts;

internal static class AuditClosure
{
    internal static object Verify(AssemblyDefinition assembly, string output)
    {
const string ScratchName = Original;
IEnumerable<TypeDefinition> Flatten(TypeDefinition type)=>new[]{type}.Concat(type.NestedTypes.SelectMany(Flatten));
var module=assembly.MainModule;
var types=module.Types.SelectMany(Flatten).ToArray();
var methods=types.SelectMany(t=>t.Methods).ToArray();
var scratch=types.Single(t=>t.FullName==ScratchName);
var drawing=types.Single(t=>t.FullName=="Terraria.GameContent.Drawing.TileDrawing");
var entry=drawing.Methods.Single(m=>m.Name=="DrawSingleTile");
var helpers=methods.Where(m=>m.Parameters.Any(p=>p.ParameterType.FullName==ScratchName)).ToArray();
var closure=new[]{entry}.Concat(helpers).ToArray();
Require(helpers.Length==7 && closure.All(m=>m.IsPrivate && !m.IsVirtual && m.DeclaringType==drawing && m.ReturnType.MetadataType==MetadataType.Void && m.HasBody && m.Body.ExceptionHandlers.Count==0),"closure shape differs");
Require(scratch.Fields.Count==19 && scratch.Methods.Count==1,"public scratch shape differs");
bool Contains(TypeReference? type)=>type!=null && (type.FullName==ScratchName || type is TypeSpecification s && Contains(s.ElementType) || type is GenericInstanceType g && g.GenericArguments.Any(Contains));
Require(!types.Where(t=>t!=scratch).SelectMany(t=>t.Fields).Any(f=>Contains(f.FieldType)),"typed scratch field outside class");
var typedUses=methods.Where(m=>m.DeclaringType==scratch || Contains(m.ReturnType) || m.Parameters.Any(p=>Contains(p.ParameterType)) || m.HasBody && (m.Body.Variables.Any(v=>Contains(v.VariableType)) || m.Body.Instructions.Any(i=>i.Operand is TypeReference t && Contains(t) || i.Operand is MemberReference r && r.DeclaringType?.FullName==ScratchName || i.Operand is MethodReference c && (Contains(c.ReturnType)||c.Parameters.Any(p=>Contains(p.ParameterType)))))).ToArray();
Require(typedUses.ToHashSet().SetEquals(closure.Concat(scratch.Methods)),"unexpected typed scratch use");
var calls=new List<object>();
foreach(var caller in methods.Where(m=>m.HasBody)) foreach(var ins in caller.Body.Instructions) if(ins.Operand is MethodReference target && helpers.Contains(target.Resolve())) {
 Require(ins.OpCode==OpCodes.Call && closure.Contains(caller),"private helper has indirect/outside caller");
 calls.Add(new{caller=caller.FullName,offset=ins.Offset,target=target.FullName});
}
Directory.CreateDirectory(output);
var records=new List<object>(); var sinks=new List<object>(); var work=new Queue<(MethodDefinition Method,int[] Args)>(); var visited=new HashSet<string>();
foreach(var method in closure) work.Enqueue((method,Enumerable.Range(0,method.Parameters.Count+(method.HasThis?1:0)).Select(n=>n>0 && method.Parameters[n-1].ParameterType.FullName==ScratchName?1:0).ToArray()));
int LocalIndex(Instruction i)=>i.OpCode.Code switch {Code.Ldloc_0 or Code.Stloc_0=>0,Code.Ldloc_1 or Code.Stloc_1=>1,Code.Ldloc_2 or Code.Stloc_2=>2,Code.Ldloc_3 or Code.Stloc_3=>3,_=>((VariableDefinition)i.Operand).Index};
int ArgIndex(MethodDefinition m,Instruction i)=>i.OpCode.Code switch {Code.Ldarg_0=>0,Code.Ldarg_1=>1,Code.Ldarg_2=>2,Code.Ldarg_3=>3,_=>((ParameterDefinition)i.Operand).Index+(m.HasThis?1:0)};
int PopCount(StackBehaviour b)=>b switch {StackBehaviour.Pop0=>0,StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref=>1,StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi=>2,StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref=>3,_=>throw new InvalidDataException("unsupported pop "+b)};
int PushCount(StackBehaviour b)=>b switch {StackBehaviour.Push0=>0,StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref=>1,StackBehaviour.Push1_push1=>2,_=>throw new InvalidDataException("unsupported push "+b)};
while(work.Count>0) {
 var (method,seed)=work.Dequeue(); string key=method.Module.Assembly.Name.Name+":"+method.FullName+":"+string.Join(",",seed);
 if(!visited.Add(key)) continue;
 Require(method.HasBody && method.Body.ExceptionHandlers.Count==0,"opaque/EH byref callee: "+key);
 var insns=method.Body.Instructions.ToArray(); var states=new Dictionary<Instruction,(int[] Stack,int[] Locals,int[] Args)>(); var pending=new Queue<Instruction>();
 states.Add(insns[0],(Array.Empty<int>(),new int[method.Body.Variables.Count],seed)); pending.Enqueue(insns[0]);
 var accepted=new HashSet<string>(); int steps=0;
 void Merge(Instruction? target,int[] stack,int[] locals,int[] arguments) {
  if(target==null) return;
  if(!states.TryGetValue(target,out var old)) { states.Add(target,((int[])stack.Clone(),(int[])locals.Clone(),(int[])arguments.Clone())); pending.Enqueue(target); return; }
  Require(old.Stack.Length==stack.Length,"stack join differs "+key+" "+target);
  bool changed=false;
  foreach(var pair in new[]{(old.Stack,stack),(old.Locals,locals),(old.Args,arguments)}) for(int n=0;n<pair.Item1.Length;n++) { int merged=pair.Item1[n]|pair.Item2[n]; if(merged!=pair.Item1[n]) {pair.Item1[n]=merged;changed=true;} }
  if(changed) pending.Enqueue(target);
 }
 while(pending.Count>0) {
  var ins=pending.Dequeue(); var state=states[ins]; var stack=state.Stack.ToList(); var locals=(int[])state.Locals.Clone(); var arguments=(int[])state.Args.Clone(); var code=ins.OpCode.Code; steps++;
  string At()=>method.FullName+" IL_"+ins.Offset.ToString("x4")+" "+ins;
  int Pop() {Require(stack.Count>0,"stack underflow "+At());int x=stack[^1];stack.RemoveAt(stack.Count-1);return x;}
  void Check(bool ok,string reason) {Require(ok,reason+": "+At());}
  if(code is Code.Ldarg or Code.Ldarg_S or Code.Ldarg_0 or Code.Ldarg_1 or Code.Ldarg_2 or Code.Ldarg_3) stack.Add(arguments[ArgIndex(method,ins)]);
  else if(code is Code.Ldarga or Code.Ldarga_S) {Check(arguments[ArgIndex(method,ins)]==0,"address of tracked argument");stack.Add(0);}
  else if(code is Code.Starg or Code.Starg_S) {int value=Pop();Check(value==0 && arguments[ArgIndex(method,ins)]==0,"tracked argument replacement");}
  else if(code is Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3) stack.Add(locals[LocalIndex(ins)]);
  else if(code is Code.Ldloca or Code.Ldloca_S) {Check(locals[LocalIndex(ins)]==0,"address of tracked local");stack.Add(0);}
  else if(code is Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3) {
   int value=Pop(), index=LocalIndex(ins); Check((value&1)==0 || method==entry && index==0 && ins==insns[1],"object-erased scratch store");locals[index]=value;
  }
  else if(code==Code.Dup) {int value=Pop();stack.Add(value);stack.Add(value);}
  else if(code is Code.Ldfld or Code.Ldflda) {
   int receiver=Pop();var field=(FieldReference)ins.Operand;
   Check((receiver&1)==0 || field.Resolve().DeclaringType==scratch,"scratch receiver wrong field");
   if(receiver!=0) accepted.Add(ins.Offset.ToString("x4")+" "+code+" "+field.FullName);
   stack.Add(code==Code.Ldflda && receiver!=0?2:0);
  }
  else if(code==Code.Stfld) {
   int value=Pop(),receiver=Pop(); Check(value==0,"tracked reference stored in field");Check((receiver&1)==0 || ((FieldReference)ins.Operand).Resolve().DeclaringType==scratch,"scratch write wrong field");
   if(receiver!=0) accepted.Add(ins.Offset.ToString("x4")+" stfld "+ins.Operand);
  }
  else if(code is Code.Call or Code.Callvirt or Code.Newobj) {
   var target=(MethodReference)ins.Operand; int count=target.Parameters.Count+(target.HasThis && code!=Code.Newobj?1:0); var operands=new int[count]; for(int n=count-1;n>=0;n--) operands[n]=Pop();
   bool raw=operands.Any(v=>(v&1)!=0),address=operands.Any(v=>(v&2)!=0);
   if(raw) {
    Check(code==Code.Call && helpers.Contains(target.Resolve()),"object-erased scratch call");
    for(int n=0;n<count;n++) if((operands[n]&1)!=0) Check(n>0 && target.Parameters[n-1].ParameterType.Resolve()==scratch,"scratch wrong call argument");
    accepted.Add(ins.Offset.ToString("x4")+" helper-call "+target.FullName);
   }
   if(address) {
    Check(code==Code.Call || code==Code.Callvirt && target.DeclaringType.IsValueType,"unknown byref virtual dispatch");
    var resolved=target.Resolve();Check(resolved!=null,"unresolved byref callee");
    sinks.Add(new{caller=method.FullName,offset=ins.Offset,callee=target.FullName,arguments=operands});
    work.Enqueue((resolved!,operands.Select(v=>(v&2)!=0?2:0).ToArray()));
   }
   if(code==Code.Newobj) stack.Add(target.DeclaringType.Resolve()==scratch?1:0);
   else if(target.ReturnType.MetadataType!=MetadataType.Void) {Check(!address || !target.ReturnType.IsByReference && !target.ReturnType.IsPointer,"byref result needs alias analysis");stack.Add(0);}
  }
  else if(code==Code.Ret) {if(method.ReturnType.MetadataType!=MetadataType.Void) Check(Pop()==0,"tracked reference returned");Check(stack.Count==0,"nonempty return stack");}
  else if(code==Code.Initobj) {Check((Pop()&1)==0,"initobj on object scratch");}
  else if(code==Code.Ldobj || ins.OpCode.Name.StartsWith("ldind.")) {Check((Pop()&1)==0,"raw scratch indirect load");stack.Add(0);}
  else if(code==Code.Stobj || ins.OpCode.Name.StartsWith("stind.")) {int value=Pop(),address=Pop();Check(value==0 && (address&1)==0,"tracked indirect store");}
  else {
   int pop=PopCount(ins.OpCode.StackBehaviourPop);for(int n=0;n<pop;n++) Check(Pop()==0,"unsupported tracked consumption (identity/null/escape/unsafe)");
   int push=PushCount(ins.OpCode.StackBehaviourPush);for(int n=0;n<push;n++) stack.Add(0);
  }
  if(ins.Operand is Instruction branch) Merge(branch,stack.ToArray(),locals,arguments);
  if(ins.Operand is Instruction[] branches) foreach(var target in branches) Merge(target,stack.ToArray(),locals,arguments);
  if(ins.OpCode.FlowControl is not (FlowControl.Branch or FlowControl.Return or FlowControl.Throw)) Merge(ins.Next,stack.ToArray(),locals,arguments);
 }
 Require(states.Count==insns.Length,"unreachable IL not audited: "+key);
 string file=method.Module.Assembly.Name.Name+"-"+method.MetadataToken.ToUInt32().ToString("x8")+".il";
 File.WriteAllText(Path.Combine(output,file),method.FullName+"\n"+string.Join("\n",insns.Select(i=>i.ToString()))+"\n");
 records.Add(new{method=method.FullName,assembly=method.Module.Assembly.Name.Name,token=method.MetadataToken.ToUInt32(),file,seeds=seed,instructions=insns.Length,reachable=states.Count,steps,accepted=accepted.Order().ToArray()});
}
Require(records.Count==17, "actual-method closure differs");
var report=new{inspectionOnly=true,inputSha256=InputHash,inputMvid=module.Mvid,typedUseMethods=typedUses.Length,closedMethods=closure.Length,fields=scratch.Fields.Count,helperCalls=calls,flowRecords=records,fieldAddressCalls=sinks,verdict="All reachable scratch-reference uses are field receivers or closed direct helper arguments; all tracked field addresses audited transitively. No object-erased/identity/null/boxing/unsafe escaping consumption accepted. This does not establish private reflection-hook compatibility or game equivalence."};
File.WriteAllText(Path.Combine(output,"audit.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true})+"\n");
Console.WriteLine($"AUDIT PASS methods={records.Count} typed={typedUses.Length} helperCalls={calls.Count} byrefCalls={sinks.Count}");
return report;
    }
}
