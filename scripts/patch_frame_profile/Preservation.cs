using Mono.Cecil;
using static Program;
internal static class Preservation
{
    internal static object Verify(AssemblyDefinition original, AssemblyDefinition changed)
    {
        var a = original.MainModule; var b = changed.MainModule;
        Require(a.Mvid != b.Mvid, "MVID unchanged");
        Require(original.Name.FullName == changed.Name.FullName && a.Name == b.Name && a.Kind == b.Kind && a.Architecture == b.Architecture && a.Attributes == b.Attributes && a.RuntimeVersion == b.RuntimeVersion && a.EntryPoint?.FullName == b.EntryPoint?.FullName, "assembly identity/runtime changed");
        Require(a.AssemblyReferences.Select(r => r.FullName).SequenceEqual(b.AssemblyReferences.Select(r => r.FullName)), "assembly references changed");
        Require(a.ModuleReferences.Select(r => r.Name).SequenceEqual(b.ModuleReferences.Select(r => r.Name)), "module references changed");
        Require(Attributes(original) == Attributes(changed) && Attributes(a) == Attributes(b) && Security(original) == Security(changed), "assembly/module metadata changed");
        var oldTypes = Types(a).ToArray(); var newTypes = Types(b).ToArray();
        Require(oldTypes.Select(t => t.FullName).SequenceEqual(newTypes.Where(t => t.Name != "NXFrameProfile43").Select(t => t.FullName)), "unexpected type change");
        int bodies = 0, fields = 0;
        foreach (var before in oldTypes)
        {
            var after = newTypes.Single(t => t.FullName == before.FullName);
            Require(TypeMetadata(before) == TypeMetadata(after), "type metadata changed: " + before.FullName);
            foreach (var f in before.Fields) { Require(FieldMetadata(f) == FieldMetadata(after.Fields.Single(x => x.Name == f.Name)), "field changed: " + f.FullName); fields++; }
            Require(after.Fields.Count == before.Fields.Count + (before.FullName is "Terraria.TimeLogger/TimeLogData" or "Terraria.TimeLogger/DataSeries" ? 1 : 0), "unexpected new field");
            foreach (var m in before.Methods)
            {
                var n = after.Methods.Single(x => x.FullName == m.FullName);
                Require(MethodMetadata(m) == MethodMetadata(n), "method metadata changed: " + m.FullName);
                if (Patcher.Expected.ContainsKey(m.FullName)) VerifySplice(m, n);
                else { Require(Body(m) == Body(n), "unrelated body changed: " + m.FullName); bodies++; }
            }
            Require(after.Methods.Count == before.Methods.Count + (before.FullName == "Terraria.TimeLogger/DataSeries" ? 1 : 0), "unexpected new method");
        }
        var resources = a.Resources.Select(ResourceMetadata).ToArray();
        Require(resources.SequenceEqual(b.Resources.Select(ResourceMetadata)), "managed resources changed");
        var privateAccess = new HashSet<string>();
        var originalFields = oldTypes.SelectMany(t => t.Fields).ToDictionary(f => f.FullName);
        foreach (var method in newTypes.SelectMany(t => t.Methods).Where(m => m.HasBody && (m.DeclaringType.Name == "NXFrameProfile43" || m.Name == "NXProfileRead")))
        foreach (var reference in method.Body.Instructions.Select(i => i.Operand).OfType<FieldReference>())
        {
            if (!originalFields.TryGetValue(reference.FullName, out var field) || !field.IsPrivate) continue;
            TypeDefinition? owner = method.DeclaringType;
            while (owner != null && owner.FullName != field.DeclaringType.FullName) owner = owner.DeclaringType;
            Require(owner != null, "illegal added private-field access: " + method.FullName + " -> " + field.FullName);
            privateAccess.Add(method.DeclaringType.FullName + " -> " + field.FullName);
        }
        return new { unchangedBodies = bodies, unchangedFields = fields, unchangedTypes = oldTypes.Length, unchangedResources = resources.Length, unchangedAssemblyReferences = a.AssemblyReferences.Count, resources, observerOnlySplices = Patcher.Expected.Keys, privateFieldAccessVerified = privateAccess, preservedNoOptimizationMethods = oldTypes.SelectMany(t => t.Methods).Where(m => (m.ImplAttributes & MethodImplAttributes.NoOptimization) != 0).Select(m => m.FullName).ToArray() };
    }
    // Remove only the enumerated hooks from a temporary clone, then compare all
    // original instructions, locals, targets and handlers with long-branch normalization.
    static void VerifySplice(MethodDefinition before, MethodDefinition after)
    {
        var scratch = new MethodDefinition(after.Name, after.Attributes, after.ReturnType);
        foreach (var p in after.Parameters) scratch.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        Patcher.CopyBody(after, scratch, t => t, o => o);
        var il = scratch.Body.Instructions;
        foreach (var i in il.ToArray())
        {
            if (i.Operand is not MethodReference m || m.DeclaringType.Name != "NXFrameProfile43") continue;
            int at = il.IndexOf(i);
            int start = at, length = 1;
            if (m.Name == "Begin") { start--; length++; }
            if (m.Name == "StageEnd") { start--; length++; }
            if (m.Name == "Register") { start -= 4; length = 6; }
            var removed = il.Skip(start).Take(length).ToArray();
            Require(!il.Any(x => x.Operand is Mono.Cecil.Cil.Instruction target && removed.Contains(target)), "observer insertion changed an original branch target");
            foreach (var x in removed) il.Remove(x);
        }
        foreach (var i in il.Where(i => i.Operand is FieldReference f && f.Name == "_nxProfileReset").ToArray())
        {
            int at = il.IndexOf(i);
            Require(at >= 2 && il[at - 2].OpCode == Mono.Cecil.Cil.OpCodes.Ldarg_0 && il[at - 1].OpCode == Mono.Cecil.Cil.OpCodes.Ldc_I4, "unexpected reset observer shape");
            il.RemoveAt(at); il.RemoveAt(at - 1); il.RemoveAt(at - 2);
        }
        Patcher.WidenBranches(scratch);
        var baseline = new MethodDefinition(before.Name, before.Attributes, before.ReturnType);
        foreach (var p in before.Parameters) baseline.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        Patcher.CopyBody(before, baseline, t => t, o => o); Patcher.WidenBranches(baseline);
        Require(Body(baseline) == Body(scratch), "non-observer change in " + before.FullName);
    }
    static string Attributes(ICustomAttributeProvider p) => string.Join(";", p.CustomAttributes.Select(a => a.AttributeType.FullName + ":" + Convert.ToHexString(a.GetBlob())));
    static string Security(ISecurityDeclarationProvider p) => string.Join(";", p.SecurityDeclarations.Select(s => s.Action + ":" + Convert.ToHexString(s.GetBlob())));
    static string GenericMetadata(GenericParameter p) => $"{p.Name}:{p.Attributes}:{Attributes(p)}:{string.Join(',', p.Constraints.Select(c => c.ConstraintType.FullName + ':' + Attributes(c)))}";
    static string TypeMetadata(TypeDefinition t) => $"{t.Attributes}|{t.BaseType?.FullName}|{t.PackingSize}|{t.ClassSize}|{Attributes(t)}|{Security(t)}|{string.Join(';', t.Interfaces.Select(i => i.InterfaceType.FullName + ':' + Attributes(i)))}|{string.Join(';', t.GenericParameters.Select(GenericMetadata))}|{string.Join(';', t.Properties.Select(p => p.FullName + ':' + p.Attributes + ':' + Attributes(p) + ':' + p.GetMethod?.FullName + ':' + p.SetMethod?.FullName + ':' + string.Join(',', p.OtherMethods.Select(m => m.FullName))))}|{string.Join(';', t.Events.Select(e => e.FullName + ':' + e.Attributes + ':' + Attributes(e) + ':' + e.AddMethod?.FullName + ':' + e.RemoveMethod?.FullName + ':' + e.InvokeMethod?.FullName))}";
    static string FieldMetadata(FieldDefinition f) => $"{f.FullName}|{f.Attributes}|{f.Offset}|{f.HasConstant}:{f.Constant}|{Convert.ToHexString(f.InitialValue)}|{Attributes(f)}|{f.HasMarshalInfo}:{(f.HasMarshalInfo ? f.MarshalInfo.NativeType : null)}";
    static string MethodMetadata(MethodDefinition m) => $"{m.FullName}|{m.Attributes}|{m.ImplAttributes}|{m.CallingConvention}|{m.SemanticsAttributes}|{Attributes(m)}|{Security(m)}|{Attributes(m.MethodReturnType)}|{string.Join(';', m.Parameters.Select(p => p.Name + ':' + p.Attributes + ':' + p.HasConstant + ':' + p.Constant + ':' + Attributes(p)))}|{string.Join(';', m.GenericParameters.Select(GenericMetadata))}|{string.Join(';', m.Overrides.Select(o => o.FullName))}|{(m.HasPInvokeInfo ? m.PInvokeInfo.Attributes + ":" + m.PInvokeInfo.EntryPoint + ":" + m.PInvokeInfo.Module.Name : "")}";
    static string ResourceMetadata(Resource r) => $"{r.Name}|{r.ResourceType}|{r.Attributes}|" + (r switch { EmbeddedResource e => Sha(e.GetResourceData()), LinkedResource l => l.File + ":" + Convert.ToHexString(l.Hash), AssemblyLinkedResource a => a.Assembly.FullName, _ => throw new InvalidDataException("unsupported resource") });
}
