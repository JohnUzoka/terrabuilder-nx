namespace StackStateProof;

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class StackStateProofCommon
{
    internal static void Require(bool ok,string message) { if(!ok)throw new InvalidDataException(message); }
    internal static string Sha(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static void Json(string path,object value)=>File.WriteAllText(path,JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true})+"\n");
    internal static IEnumerable<TypeDefinition> Types(ModuleDefinition module)=>module.Types.SelectMany(Flatten);
    static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)=>new[]{type}.Concat(type.NestedTypes.SelectMany(Flatten));
    internal static TypeReference Primitive(ModuleDefinition m,string name)=>name switch {
        "System.Void"=>m.TypeSystem.Void,"System.Boolean"=>m.TypeSystem.Boolean,"System.Char"=>m.TypeSystem.Char,
        "System.SByte"=>m.TypeSystem.SByte,"System.Byte"=>m.TypeSystem.Byte,"System.Int16"=>m.TypeSystem.Int16,"System.UInt16"=>m.TypeSystem.UInt16,
        "System.Int32"=>m.TypeSystem.Int32,"System.UInt32"=>m.TypeSystem.UInt32,"System.Int64"=>m.TypeSystem.Int64,"System.UInt64"=>m.TypeSystem.UInt64,
        "System.Single"=>m.TypeSystem.Single,"System.Double"=>m.TypeSystem.Double,"System.String"=>m.TypeSystem.String,"System.Object"=>m.TypeSystem.Object,
        "System.IntPtr"=>m.TypeSystem.IntPtr,"System.UIntPtr"=>m.TypeSystem.UIntPtr,
        _=>throw new InvalidDataException("unsupported primitive "+name)
    };
    internal static void Define(MethodDefinition source,TypeDefinition owner,Func<TypeReference,TypeReference> typeMap)
    {
        Require(!source.HasGenericParameters,"generic proof clone declaration unsupported");
        var target=new MethodDefinition(source.Name,source.Attributes,typeMap(source.ReturnType)){
            ImplAttributes=source.ImplAttributes,CallingConvention=source.CallingConvention,HasThis=source.HasThis,ExplicitThis=source.ExplicitThis
        };
        foreach(var p in source.Parameters)target.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,typeMap(p.ParameterType)));
        owner.Methods.Add(target);
    }
    internal static void CopyBody(MethodDefinition source,MethodDefinition target,Func<TypeReference,TypeReference> typeMap,Func<object,object> memberMap)
    {
        Require(source.HasBody,"source has no proof body "+source.FullName);
        target.Body=new Mono.Cecil.Cil.MethodBody(target){InitLocals=source.Body.InitLocals,MaxStackSize=source.Body.MaxStackSize};
        foreach(var v in source.Body.Variables)target.Body.Variables.Add(new VariableDefinition(typeMap(v.VariableType)));
        var instructions=new Dictionary<Instruction,Instruction>();
        foreach(var original in source.Body.Instructions)
        {
            var copy=Instruction.Create(OpCodes.Nop);copy.OpCode=original.OpCode;instructions.Add(original,copy);target.Body.Instructions.Add(copy);
        }
        Instruction? MapInstruction(Instruction? value)=>value==null?null:instructions[value];
        int receiver=source.HasThis&&!target.HasThis?1:0;
        foreach(var original in source.Body.Instructions)
        {
            object? value=original.Operand;
            instructions[original].Operand=value switch {
                null=>null,
                Instruction branch=>instructions[branch],
                Instruction[] branches=>branches.Select(b=>instructions[b]).ToArray(),
                VariableDefinition local=>target.Body.Variables[local.Index],
                ParameterDefinition p when p.Index<0=>target.HasThis?target.Body.ThisParameter:target.Parameters[0],
                ParameterDefinition p=>target.Parameters[p.Index+receiver],
                MemberReference member=>memberMap(member),
                CallSite=>throw new InvalidDataException("calli unsupported in bounded proof"),
                _=>value
            };
        }
        foreach(var e in source.Body.ExceptionHandlers)
            target.Body.ExceptionHandlers.Add(new ExceptionHandler(e.HandlerType){TryStart=MapInstruction(e.TryStart),TryEnd=MapInstruction(e.TryEnd),HandlerStart=MapInstruction(e.HandlerStart),HandlerEnd=MapInstruction(e.HandlerEnd),FilterStart=MapInstruction(e.FilterStart),CatchType=e.CatchType==null?null:typeMap(e.CatchType)});
    }
    static readonly Dictionary<string,OpCode> Opcodes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(o=>o.Name,StringComparer.Ordinal);
    internal static void Widen(MethodDefinition method)
    {
        foreach(var i in method.Body.Instructions)if(i.OpCode.OperandType==OperandType.ShortInlineBrTarget)i.OpCode=Opcodes[i.OpCode.Name[..^2]];
    }
    internal static string Body(MethodDefinition method)
    {
        var sb=new StringBuilder();sb.Append(method.FullName).Append('|').Append(method.Body.InitLocals).AppendLine();
        foreach(var v in method.Body.Variables)sb.Append(v.Index).Append(':').Append(Probe.Qualified(v.VariableType)).AppendLine();
        int Index(Instruction? i)=>i==null?-1:method.Body.Instructions.IndexOf(i);
        foreach(var i in method.Body.Instructions)
        {
            string operand=i.Operand switch {
                null=>"",Instruction branch=>"i:"+Index(branch),Instruction[] branches=>"is:"+string.Join(",",branches.Select(Index)),
                ParameterDefinition p=>"p:"+p.Index,VariableDefinition v=>"v:"+v.Index,MemberReference m=>"m:"+Probe.Qualified(m),
                string s=>"s:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(s)),
                float f=>"f:"+BitConverter.SingleToInt32Bits(f),double d=>"d:"+BitConverter.DoubleToInt64Bits(d),
                _=>Convert.ToString(i.Operand,CultureInfo.InvariantCulture)??""
            };
            sb.Append(i.OpCode.Name).Append('|').Append(operand).AppendLine();
        }
        foreach(var e in method.Body.ExceptionHandlers)sb.Append(e.HandlerType).Append('|').Append(Index(e.TryStart)).Append('|').Append(Index(e.TryEnd)).Append('|').Append(Index(e.HandlerStart)).Append('|').Append(Index(e.HandlerEnd)).Append('|').Append(Index(e.FilterStart)).Append('|').Append(e.CatchType==null?"":Probe.Qualified(e.CatchType)).AppendLine();
        return sb.ToString();
    }
    internal static string Fingerprint(MethodDefinition method)=>Sha(Encoding.UTF8.GetBytes(Body(method)));
}
