using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class Common
{
    public static void Require(bool c, string m) { if (!c) throw new InvalidDataException(m); }
    public static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    public static IEnumerable<TypeDefinition> Types(ModuleDefinition m) => m.Types.SelectMany(Walk);
    static IEnumerable<TypeDefinition> Walk(TypeDefinition t) { yield return t; foreach (var n in t.NestedTypes) foreach (var x in Walk(n)) yield return x; }
    public static TypeDefinition Type(ModuleDefinition m, string full) => Types(m).Single(t => t.FullName == full);
    public static MethodDefinition Method(ModuleDefinition m, string full) => Types(m).SelectMany(t => t.Methods).Single(x => x.FullName == full);
    public static MethodDefinition Method(TypeDefinition t, string name, string ret, params string[] pars) => t.Methods.Single(m => m.Name == name && m.ReturnType.FullName == ret && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(pars));
    public static void Empty(MethodDefinition m, bool callBase = false)
    {
        Require(m.HasBody, "missing body " + m.FullName);
        m.Body.ExceptionHandlers.Clear(); m.Body.Variables.Clear(); m.Body.Instructions.Clear(); m.Body.InitLocals = false; m.Body.MaxStackSize = 8;
        var il = m.Body.GetILProcessor();
        if (callBase)
        {
            var objCtor = m.Module.ImportReference(typeof(object).GetConstructor(System.Type.EmptyTypes)!);
            il.Append(il.Create(OpCodes.Ldarg_0)); il.Append(il.Create(OpCodes.Call, objCtor));
        }
        il.Append(il.Create(OpCodes.Ret));
    }
    public static Instruction[] Calls(MethodDefinition m, string type, string name) => m.Body.Instructions.Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference r && r.DeclaringType.FullName == type && r.Name == name).ToArray();
    public static int IntConst(Instruction i) => i.OpCode.Code switch { Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2, Code.Ldc_I4_3 => 3, Code.Ldc_I4_4 => 4, Code.Ldc_I4_5 => 5, Code.Ldc_I4_6 => 6, Code.Ldc_I4_7 => 7, Code.Ldc_I4_8 => 8, Code.Ldc_I4_S => (sbyte)i.Operand, Code.Ldc_I4 => (int)i.Operand, _ => int.MinValue };
    public static void WidenBranches(MethodDefinition m)
    {
        bool changed;
        do { changed = false; var off = new Dictionary<Instruction,int>(); int o = 0; foreach (var i in m.Body.Instructions) { off[i]=o; o += i.GetSize(); }
            foreach (var i in m.Body.Instructions.Where(x => x.OpCode.OperandType == OperandType.ShortInlineBrTarget)) { int d = off[(Instruction)i.Operand] - off[i] - i.GetSize(); if (d >= sbyte.MinValue && d <= sbyte.MaxValue) continue; i.OpCode = i.OpCode.Code switch { Code.Br_S=>OpCodes.Br, Code.Brfalse_S=>OpCodes.Brfalse, Code.Brtrue_S=>OpCodes.Brtrue, Code.Beq_S=>OpCodes.Beq, Code.Bge_S=>OpCodes.Bge, Code.Bgt_S=>OpCodes.Bgt, Code.Ble_S=>OpCodes.Ble, Code.Blt_S=>OpCodes.Blt, Code.Bne_Un_S=>OpCodes.Bne_Un, Code.Bge_Un_S=>OpCodes.Bge_Un, Code.Bgt_Un_S=>OpCodes.Bgt_Un, Code.Ble_Un_S=>OpCodes.Ble_Un, Code.Blt_Un_S=>OpCodes.Blt_Un, Code.Leave_S=>OpCodes.Leave, _=>throw new InvalidDataException("short branch "+i.OpCode) }; changed = true; }
        } while (changed);
    }
    public static void CopyBody(MethodDefinition source, MethodDefinition target, Func<TypeReference,TypeReference> mapType, Func<object,object> mapMember)
    {
        target.Body = new MethodBody(target){ InitLocals = source.Body.InitLocals, MaxStackSize = source.Body.MaxStackSize };
        foreach (var v in source.Body.Variables) target.Body.Variables.Add(new VariableDefinition(mapType(v.VariableType)));
        var map = source.Body.Instructions.ToDictionary(i=>i, _=>Instruction.Create(OpCodes.Nop));
        foreach (var s in source.Body.Instructions) { var c = map[s]; c.OpCode = s.OpCode; c.Operand = s.Operand switch { null=>null, Instruction b=>map[b], Instruction[] bs=>bs.Select(b=>map[b]).ToArray(), VariableDefinition v=>target.Body.Variables[v.Index], ParameterDefinition p=>target.Parameters[p.Index], var o=>mapMember(o) }; target.Body.Instructions.Add(c); }
        foreach (var h in source.Body.ExceptionHandlers) target.Body.ExceptionHandlers.Add(new ExceptionHandler(h.HandlerType){TryStart=map[h.TryStart],TryEnd=h.TryEnd==null?null:map[h.TryEnd],HandlerStart=map[h.HandlerStart],HandlerEnd=h.HandlerEnd==null?null:map[h.HandlerEnd],FilterStart=h.FilterStart==null?null:map[h.FilterStart],CatchType=h.CatchType==null?null:mapType(h.CatchType)});
    }
}
