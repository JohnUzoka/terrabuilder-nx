using Mono.Cecil;
using Mono.Cecil.Cil;
using FAttr=Mono.Cecil.FieldAttributes;
using MAttr=Mono.Cecil.MethodAttributes;
using static Common;

static class TimeLoggerPatch
{
    const string SeriesName = "Terraria.TimeLogger/DataSeries";
    public static void Apply(ModuleDefinition module)
    {
        var target = Type(module, SeriesName);
        if (target.Fields.Any(f=>f.Name=="_ordered")) return;
        using var tmplAsm = AssemblyDefinition.ReadAssembly(typeof(OptimizedTemplate).Assembly.Location);
        var tmpl = tmplAsm.MainModule.Types.Single(t=>t.Name==nameof(OptimizedTemplate));
        var ordered = new FieldDefinition("_ordered", FAttr.Private, new ArrayType(module.TypeSystem.Int32));
        var orderedCount = new FieldDefinition("_orderedCount", FAttr.Private, module.TypeSystem.Int32);
        target.Fields.Add(ordered); target.Fields.Add(orderedCount);
        foreach (var name in new[]{"OrderedInsert","OrderedRemove"}) { var src=tmpl.Methods.Single(m=>m.Name==name); var add=new MethodDefinition(name, MAttr.Private|MAttr.HideBySig, module.TypeSystem.Void); add.Parameters.Add(new ParameterDefinition("value", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Int32)); target.Methods.Add(add); }
        TypeReference MT(TypeReference t)=>t.FullName switch { "System.Int32"=>module.TypeSystem.Int32, "System.Boolean"=>module.TypeSystem.Boolean, "System.Void"=>module.TypeSystem.Void, _=>throw new InvalidDataException("template type "+t.FullName)};
        object MM(object o) { if (o is FieldReference f) return target.Fields.Single(x=>x.Name==f.Name && x.FieldType.FullName==f.FieldType.FullName); if (o is MethodReference m) { if (m.DeclaringType.FullName==tmpl.FullName) return target.Methods.Single(x=>x.Name==m.Name && x.Parameters.Count==m.Parameters.Count); return module.ImportReference(m); } if (o is TypeReference tr) return MT(tr); return o; }
        foreach (var name in new[]{"OrderedInsert","OrderedRemove","StartNextFrame"}) CopyBody(tmpl.Methods.Single(m=>m.Name==name), target.Methods.Single(m=>m.Name==name), MT, MM);
        AppendBeforeReturn(target.Methods.Single(m=>m.Name==".ctor"), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld,target.Fields.Single(f=>f.Name=="values")), Instruction.Create(OpCodes.Ldlen), Instruction.Create(OpCodes.Conv_I4), Instruction.Create(OpCodes.Newarr,module.TypeSystem.Int32), Instruction.Create(OpCodes.Stfld,ordered));
        AppendBeforeReturn(target.Methods.Single(m=>m.Name=="Reset"), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Stfld,orderedCount));
        var cctor = target.Methods.FirstOrDefault(m=>m.Name==".cctor"); if (cctor!=null) target.Methods.Remove(cctor);
        var sort = target.Fields.FirstOrDefault(f=>f.Name=="_sort"); if (sort!=null) target.Fields.Remove(sort);
    }
    static void AppendBeforeReturn(MethodDefinition m, params Instruction[] ins) { var ret=m.Body.Instructions.Last(i=>i.OpCode==OpCodes.Ret); var il=m.Body.GetILProcessor(); foreach(var x in ins) il.InsertBefore(ret,x); }
}
