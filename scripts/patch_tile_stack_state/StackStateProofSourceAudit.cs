namespace StackStateProof;

using System.Globalization;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static StackStateProofCommon;

internal static class SourceAudit
{
    internal static void Check(MethodDefinition source,MethodDefinition target,Func<TypeReference,TypeReference> typeMap,Func<object,object> memberMap)
    {
        Require(source.HasBody&&target.HasBody,"missing source/target body");
        Require(source.Body.InitLocals==target.Body.InitLocals&&source.Body.Instructions.Count==target.Body.Instructions.Count&&source.Body.Variables.Count==target.Body.Variables.Count&&source.Body.ExceptionHandlers.Count==target.Body.ExceptionHandlers.Count,"source clone body shape differs");
        Require(typeMap(source.ReturnType).FullName==target.ReturnType.FullName,"source return signature differs");
        int extra=source.HasThis&&!target.HasThis?1:0;
        Require(target.Parameters.Count==source.Parameters.Count+extra,"source parameter count differs");
        if(extra!=0)Require(typeMap(source.DeclaringType).FullName==target.Parameters[0].ParameterType.FullName,"source receiver mapping differs");
        foreach(var p in source.Parameters)Require(typeMap(p.ParameterType).FullName==target.Parameters[p.Index+extra].ParameterType.FullName,"source parameter mapping differs");
        foreach(var v in source.Body.Variables)Require(typeMap(v.VariableType).FullName==target.Body.Variables[v.Index].VariableType.FullName,"source local mapping differs");
        string InstructionKey(Instruction i,MethodDefinition owner,bool remap)
        {
            string op=i.OpCode.Name;
            if(op.EndsWith(".s",StringComparison.Ordinal))op=op[..^2];
            object? value=i.Operand;
            foreach(var family in new[]{"ldarg","ldloc","stloc","ldc.i4"})
            {
                if(op.StartsWith(family+".",StringComparison.Ordinal))
                {
                    string suffix=op[(family.Length+1)..];
                    if(suffix=="m1"||int.TryParse(suffix,out _)){value=suffix=="m1"?-1:int.Parse(suffix,CultureInfo.InvariantCulture);op=family;break;}
                }
            }
            string operand=value switch {
                null=>"",
                Instruction branch=>"instruction:"+owner.Body.Instructions.IndexOf(branch),
                Instruction[] branches=>"instructions:"+string.Join(",",branches.Select(b=>owner.Body.Instructions.IndexOf(b))),
                ParameterDefinition p=>(p.Index+(owner.HasThis?1:0)).ToString(CultureInfo.InvariantCulture),
                VariableDefinition v=>v.Index.ToString(CultureInfo.InvariantCulture),
                MemberReference m=>Probe.Qualified((MemberReference)(remap?memberMap(m):m)),
                float f=>"float-bits:"+BitConverter.SingleToInt32Bits(f),
                double d=>"double-bits:"+BitConverter.DoubleToInt64Bits(d),
                string s=>"string:"+s,
                _=>Convert.ToString(value,CultureInfo.InvariantCulture)??""
            };
            return op+" "+operand;
        }
        for(int i=0;i<source.Body.Instructions.Count;i++)
        {
            string a=InstructionKey(source.Body.Instructions[i],source,true),b=InstructionKey(target.Body.Instructions[i],target,false);
            Require(a==b,$"source instruction differs {source.FullName} index={i}: {a} != {b}");
        }
        for(int i=0;i<source.Body.ExceptionHandlers.Count;i++)
        {
            var a=source.Body.ExceptionHandlers[i];var b=target.Body.ExceptionHandlers[i];
            Require(a.HandlerType==b.HandlerType&&source.Body.Instructions.IndexOf(a.TryStart)==target.Body.Instructions.IndexOf(b.TryStart)&&source.Body.Instructions.IndexOf(a.TryEnd)==target.Body.Instructions.IndexOf(b.TryEnd)&&source.Body.Instructions.IndexOf(a.HandlerStart)==target.Body.Instructions.IndexOf(b.HandlerStart)&&source.Body.Instructions.IndexOf(a.HandlerEnd)==target.Body.Instructions.IndexOf(b.HandlerEnd)&&source.Body.Instructions.IndexOf(a.FilterStart)==target.Body.Instructions.IndexOf(b.FilterStart)&&(a.CatchType==null?b.CatchType==null:typeMap(a.CatchType).FullName==b.CatchType?.FullName),"source EH mapping differs");
        }
    }
}
