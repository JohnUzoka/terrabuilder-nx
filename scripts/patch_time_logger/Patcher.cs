using System.Reflection;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Program;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

internal static class Patcher
{
    const string InputHash = "34f4421e9cee7e92270961f40608f895d8dcd0d3b428946a0802f8be6cc50332";
    const string FnaHash = "15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f";
    static readonly Dictionary<string, string> ExpectedMethods = new()
    {
        ["get_HasData"] = "68472d9f9b0ed1cf584dbd599503cbc7c578e49f97e50dcdc6ab67e19a748a6d",
        ["get_frequency"] = "87876119f06c6f7c8c47106deb85aa16fe9eca30206180ee72031148ee9eb078",
        ["Add"] = "964c8799c79e0236fee6e292353976d7927c2556c321d00730c398af3599568d",
        ["StartNextFrame"] = "16b2c0a6f1ddd6977ea31bad359c8dcba130085a1ea0e15ea01f2f0ef38fbd27",
        ["Reset"] = "9a1075069c2e7e5a788a65de0da3415cf0a7df5147ce56a9601406719dc222d8",
        ["Quantile"] = "ee7a1922f006b8f5c76c30b7105617ce8a5e1227c1b64064220d5b259bc312ce",
        [".ctor"] = "2a7892e69301e2dd384d935a4666ca4a311b8ef12562993104606df9fd44eb65",
        [".cctor"] = "225daf79b0fdcb3ec0d0c2c9a48a6210e58b7fd3d0dc6b66a30bdef18db1c80d"
    };
    static readonly HashSet<string> ChangedMethods = new() { ".ctor", "Reset", "StartNextFrame" };
    static readonly HashSet<string> InjectedMethods = new() { "OrderedInsert", "OrderedRemove" };
    internal static void Accept(string input, string fna, string output, AssemblyDefinition game, AssemblyDefinition framework)
    {
        Require(Sha(File.ReadAllBytes(input)) == InputHash, "input SHA differs from reviewed patched Terraria; refusing an unreviewed shape");
        Require(Sha(File.ReadAllBytes(fna)) == FnaHash, "FNA SHA differs from reviewed MathHelper implementation");
        var series = Types(game.MainModule).Single(t => t.FullName == SeriesName);
        Require(series.Methods.Count == ExpectedMethods.Count && series.Fields.Count == 10, "unexpected original DataSeries shape");
        foreach (var method in series.Methods)
            Require(ExpectedMethods.TryGetValue(method.Name, out var hash) && Fingerprint(method) == hash, "original method differs: " + method.FullName);
        var scratchUsers = Types(game.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody)
            .Where(m => m.Body.Instructions.Any(i => i.Operand is FieldReference f && f.DeclaringType.FullName == SeriesName && f.Name == "_sort")).ToArray();
        Require(scratchUsers.Length == 2 && scratchUsers.All(m => m.DeclaringType == series && m.Name is "StartNextFrame" or ".cctor"), "shared scratch has another observer");
        foreach (var method in Types(game.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody && m.DeclaringType != series))
            Require(!method.Body.Instructions.Any(i => i.Operand is FieldReference f && f.DeclaringType.FullName == SeriesName && f.Name is "values" or "used" or "count" or "next" or "usedCount"), "private history has an outside observer: " + method.FullName);

        // Keep source assembly untouched. The candidate is serialized and checked in memory.
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
        resolver.AddSearchDirectory(Path.GetDirectoryName(fna)!);
        resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        using var candidate = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(input)), new ReaderParameters { AssemblyResolver = resolver });
        var target = Types(candidate.MainModule).Single(t => t.FullName == SeriesName);
        using var templateAssembly = AssemblyDefinition.ReadAssembly(typeof(OptimizedTemplate).Assembly.Location);
        Inject(target, templateAssembly.MainModule.Types.Single(t => t.Name == nameof(OptimizedTemplate)));
        var identity = Encoding.UTF8.GetBytes(InputHash + "\n" + string.Join("\n", target.Fields.Select(f => f.FullName)) + "\n" + string.Join("\n", target.Methods.Select(m => m.FullName + "\n" + Body(m))));
        candidate.MainModule.Mvid = new Guid(System.Security.Cryptography.SHA256.HashData(identity).AsSpan(0, 16));
        using var candidateStream = new MemoryStream();
        candidate.Write(candidateStream, new WriterParameters { Timestamp = 0 });
        var candidateBytes = candidateStream.ToArray();
        using var reloaded = AssemblyDefinition.ReadAssembly(new MemoryStream(candidateBytes));
        object preservation = VerifyPreservation(game, reloaded);
        var nativeResources = PeResources.Fingerprints(File.ReadAllBytes(input));
        Require(nativeResources.SequenceEqual(PeResources.Fingerprints(candidateBytes)), "native PE resources changed");

        var lerp = Types(framework.MainModule).Single(t => t.FullName == "Microsoft.Xna.Framework.MathHelper").Methods.Single(m => m.Name == "Lerp");
        Require(Fingerprint(lerp) == "6ce5b9aea087a49465dea15577e104df3787983e86264eee3dfb0ef570a46546", "unexpected FNA Lerp");
        var probeBytes = BuildProbe(series, Types(reloaded.MainModule).Single(t => t.FullName == SeriesName), lerp);
        File.WriteAllBytes(Path.Combine(output, "TimeLoggerProbe.dll"), probeBytes);
        var probe = Assembly.Load(probeBytes);
        var baselineType = probe.GetType("Probe.Baseline", true)!;
        var optimizedType = probe.GetType("Probe.Optimized", true)!;
        Console.WriteLine("Running exact extracted-IL behavior comparison");
        var behavior = Harness.Verify(baselineType, optimizedType);
        Json(Path.Combine(output, "behavior.json"), behavior);
        Console.WriteLine(JsonSerializer.Serialize(behavior));
        Console.WriteLine("Running host benchmark (not Switch performance)");
        var benchmark = Harness.Benchmark(baselineType, optimizedType);
        Json(Path.Combine(output, "benchmark.json"), benchmark);
        using var measured = JsonDocument.Parse(JsonSerializer.Serialize(benchmark));
        var steady = measured.RootElement.GetProperty("Cases").EnumerateArray()
            .SelectMany(c => c.GetProperty("Phases").EnumerateArray()).Where(p => p.GetProperty("Name").GetString() == "steady").ToArray();
        Require(steady.Length == 6 && steady.All(p => p.GetProperty("MedianSpeedup").GetDouble() > 1.25), "meaningful host benefit not established in every steady workload; no production output");
        Require(steady.All(p => p.GetProperty("Optimized").GetProperty("MedianAllocatedBytes").GetInt64() == 0), "optimized steady frame path allocated");
        Require(Sha(File.ReadAllBytes(input)) == InputHash && Sha(File.ReadAllBytes(fna)) == FnaHash, "original input changed during acceptance");
        var destination = Path.Combine(output, "Terraria.exe");
        if (File.Exists(destination)) Require(File.ReadAllBytes(destination).AsSpan().SequenceEqual(candidateBytes), "different existing output; use a clean directory");
        else File.WriteAllBytes(destination, candidateBytes);
        Json(Path.Combine(output, "acceptance.json"), new
        {
            accepted = true, input, inputSha256 = InputHash, inputMvid = game.MainModule.Mvid,
            output = destination, outputSha256 = Sha(candidateBytes), outputMvid = reloaded.MainModule.Mvid,
            fnaSha256 = FnaHash, probeSha256 = Sha(probeBytes),
            changedMethods = target.Methods.Where(m => ChangedMethods.Contains(m.Name)).Select(m => new { m.FullName, hash = Fingerprint(m) }),
            addedMethods = target.Methods.Where(m => InjectedMethods.Contains(m.Name)).Select(m => new { m.FullName, hash = Fingerprint(m) }),
            addedFields = target.Fields.Where(f => f.Name.StartsWith("_ordered", StringComparison.Ordinal)).Select(f => f.FullName),
            removed = new[] { "System.Int32[] " + SeriesName + "::_sort", "System.Void " + SeriesName + "::.cctor()" },
            preservation,
            nativeResources,
            aotConsequence = "New Terraria MVID and shifted method/field tokens require recompiling Terraria and every AOT module whose dependency table names Terraria. Restage this exact output; regenerate input/dependency manifests; preserve unchanged FNA/CoreLib identities. Do not reuse old Terraria AOT object or link old embedded bytes.",
            limitation = "Actual baseline and patched IL plus exact FNA Lerp execute on this host CLR, not Switch Mono full-AOT. Host timings are not FPS and do not establish causal TimeLogger cost on hardware. Static private scratch contents deliberately removed after complete reference audit; all original instance fields and getters preserved. No simulation/pacing changes."
        });
        Console.WriteLine($"ACCEPTED {destination} sha256={Sha(candidateBytes)} mvid={reloaded.MainModule.Mvid}");
    }

    static void Inject(TypeDefinition target, TypeDefinition template)
    {
        var module = target.Module;
        var ordered = new FieldDefinition("_ordered", FieldAttributes.Private, new ArrayType(module.TypeSystem.Int32));
        var orderedCount = new FieldDefinition("_orderedCount", FieldAttributes.Private, module.TypeSystem.Int32);
        target.Fields.Add(ordered);
        target.Fields.Add(orderedCount);
        foreach (var name in InjectedMethods)
        {
            var source = template.Methods.Single(m => m.Name == name);
            var added = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.HideBySig, module.TypeSystem.Void);
            added.Parameters.Add(new ParameterDefinition("value", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Int32));
            target.Methods.Add(added);
            Require(source.Parameters.Count == 1 && source.Parameters[0].ParameterType.FullName == "System.Int32", "template helper shape");
        }
        TypeReference MapType(TypeReference type) => type.FullName switch
        {
            "System.Int32" => module.TypeSystem.Int32,
            "System.Boolean" => module.TypeSystem.Boolean,
            "System.Void" => module.TypeSystem.Void,
            _ => throw new InvalidDataException("unapproved template type: " + type.FullName)
        };
        object MapMember(object operand)
        {
            if (operand is FieldReference field)
            {
                Require(field.DeclaringType.FullName == template.FullName, "unapproved template field");
                return target.Fields.Single(f => f.Name == field.Name && f.FieldType.FullName == field.FieldType.FullName && !f.IsStatic);
            }
            if (operand is MethodReference method)
            {
                if (method.DeclaringType.FullName == template.FullName)
                    return target.Methods.Single(m => m.Name == method.Name && m.ReturnType.FullName == method.ReturnType.FullName && m.HasThis == method.HasThis && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(method.Parameters.Select(p => p.ParameterType.FullName)));
                Require(method.FullName == "System.Void System.Array::Copy(System.Array,System.Int32,System.Array,System.Int32,System.Int32)", "unapproved template call: " + method.FullName);
                // Reuse an existing reference, never import System.Runtime from this tool.
                return Types(module).SelectMany(t => t.Methods).Where(m => m.HasBody)
                    .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>().First(m => m.FullName == method.FullName);
            }
            if (operand is TypeReference type) return MapType(type);
            return operand;
        }
        foreach (var name in InjectedMethods.Append("StartNextFrame"))
            CopyBody(template.Methods.Single(m => m.Name == name), target.Methods.Single(m => m.Name == name), MapType, MapMember);
        var ctor = target.Methods.Single(m => m.Name == ".ctor");
        AppendBeforeReturn(ctor, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, target.Fields.Single(f => f.Name == "values")), Instruction.Create(OpCodes.Ldlen), Instruction.Create(OpCodes.Conv_I4),
            Instruction.Create(OpCodes.Newarr, module.TypeSystem.Int32), Instruction.Create(OpCodes.Stfld, ordered));
        AppendBeforeReturn(target.Methods.Single(m => m.Name == "Reset"), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Stfld, orderedCount));
        target.Methods.Remove(target.Methods.Single(m => m.Name == ".cctor"));
        target.Fields.Remove(target.Fields.Single(f => f.Name == "_sort"));
        Require(!Types(module).SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
            .Any(i => i.Operand is MemberReference m && m.DeclaringType?.FullName == SeriesName && m.Name is "_sort" or ".cctor"), "orphaned removed member reference");
    }

    static void AppendBeforeReturn(MethodDefinition method, params Instruction[] additions)
    {
        Require(method.Body.Instructions.Count(i => i.OpCode == OpCodes.Ret) == 1 && method.Body.Instructions.Last().OpCode == OpCodes.Ret, "unexpected early return");
        var end = method.Body.Instructions.Last();
        Require(!method.Body.Instructions.Any(i => ReferenceEquals(i.Operand, end)), "unexpected return branch");
        foreach (var instruction in additions) method.Body.GetILProcessor().InsertBefore(end, instruction);
    }

    internal static void CopyBody(MethodDefinition source, MethodDefinition target, Func<TypeReference, TypeReference> typeMap, Func<object, object> memberMap)
    {
        Require(source.HasBody && !source.HasGenericParameters, "unsupported source body");
        target.Body = new Mono.Cecil.Cil.MethodBody(target) { InitLocals = source.Body.InitLocals, MaxStackSize = source.Body.MaxStackSize };
        foreach (var local in source.Body.Variables) target.Body.Variables.Add(new VariableDefinition(typeMap(local.VariableType)));
        var instructions = source.Body.Instructions.ToDictionary(i => i, _ => Instruction.Create(OpCodes.Nop));
        foreach (var instruction in source.Body.Instructions)
        {
            var copy = instructions[instruction];
            copy.OpCode = instruction.OpCode;
            copy.Operand = instruction.Operand switch
            {
                null => null,
                Instruction branch => instructions[branch],
                Instruction[] branches => branches.Select(i => instructions[i]).ToArray(),
                VariableDefinition local => target.Body.Variables[local.Index],
                ParameterDefinition arg => target.Parameters[arg.Index],
                var other => memberMap(other)
            };
            target.Body.Instructions.Add(copy);
        }
        foreach (var handler in source.Body.ExceptionHandlers)
            target.Body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
            {
                TryStart = instructions[handler.TryStart], TryEnd = handler.TryEnd is null ? null : instructions[handler.TryEnd],
                HandlerStart = instructions[handler.HandlerStart], HandlerEnd = handler.HandlerEnd is null ? null : instructions[handler.HandlerEnd],
                FilterStart = handler.FilterStart is null ? null : instructions[handler.FilterStart], CatchType = handler.CatchType is null ? null : typeMap(handler.CatchType)
            });
    }

    static byte[] BuildProbe(TypeDefinition original, TypeDefinition optimized, MethodDefinition lerp)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("TimeLoggerExactProbe", new Version(1, 0)), "TimeLoggerExactProbe", ModuleKind.Dll);
        var module = assembly.MainModule;
        var support = new TypeDefinition("Probe", "Support", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
        module.Types.Add(support);
        var frameCount = new FieldDefinition("FrameCount", FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Int32);
        support.Fields.Add(frameCount);
        var initializer = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
        support.Methods.Add(initializer);
        initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 300));
        initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, frameCount));
        initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        var extractedLerp = new MethodDefinition("Lerp", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Single);
        foreach (var parameter in lerp.Parameters) extractedLerp.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, module.TypeSystem.Single));
        support.Methods.Add(extractedLerp);
        CopyBody(lerp, extractedLerp, module.ImportReference, operand => operand is TypeReference t ? module.ImportReference(t) : operand);
        foreach (var (source, name) in new[] { (original, "Baseline"), (optimized, "Optimized") })
        {
            var dest = new TypeDefinition("Probe", name, TypeAttributes.Public | TypeAttributes.BeforeFieldInit, module.TypeSystem.Object);
            module.Types.Add(dest);
            var fields = source.Fields.ToDictionary(f => f.Name, f => new FieldDefinition(f.Name, f.Attributes, module.ImportReference(f.FieldType)));
            foreach (var field in fields.Values) dest.Fields.Add(field);
            var methods = new Dictionary<string, MethodDefinition>();
            foreach (var method in source.Methods)
            {
                var copy = new MethodDefinition(method.Name, method.Attributes, module.ImportReference(method.ReturnType)) { ImplAttributes = method.ImplAttributes };
                foreach (var parameter in method.Parameters) copy.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, module.ImportReference(parameter.ParameterType)));
                dest.Methods.Add(copy);
                methods.Add(method.Name, copy);
            }
            object Map(object operand) => operand switch
            {
                FieldReference f when f.DeclaringType.FullName == SeriesName => fields[f.Name],
                FieldReference f when f.FullName == "System.Int32 Terraria.TimeLogger::FrameCount" => frameCount,
                MethodReference m when m.DeclaringType.FullName == SeriesName => methods[m.Name],
                MethodReference m when m.FullName == lerp.FullName => extractedLerp,
                MethodReference m => module.ImportReference(m),
                TypeReference t => module.ImportReference(t),
                MemberReference member => throw new InvalidDataException("unsupported probe member: " + member.FullName),
                _ => operand
            };
            foreach (var method in source.Methods) CopyBody(method, methods[method.Name], module.ImportReference, Map);
        }
        var identity = Encoding.UTF8.GetBytes(string.Join("\n", Types(module).SelectMany(t => t.Methods).Select(m => m.FullName + "\n" + Body(m))));
        module.Mvid = new Guid(System.Security.Cryptography.SHA256.HashData(identity).AsSpan(0, 16));
        using var stream = new MemoryStream();
        assembly.Write(stream, new WriterParameters { Timestamp = 0 });
        return stream.ToArray();
    }

    static object VerifyPreservation(AssemblyDefinition original, AssemblyDefinition changed)
    {
        var oldModule = original.MainModule;
        var newModule = changed.MainModule;
        Require(oldModule.Mvid != newModule.Mvid, "MVID was not changed");
        Require(original.Name.FullName == changed.Name.FullName && oldModule.Name == newModule.Name && oldModule.Kind == newModule.Kind && oldModule.Architecture == newModule.Architecture && oldModule.Attributes == newModule.Attributes && oldModule.RuntimeVersion == newModule.RuntimeVersion && oldModule.EntryPoint?.FullName == newModule.EntryPoint?.FullName, "assembly identity/runtime changed");
        Require(oldModule.AssemblyReferences.Select(r => r.FullName).SequenceEqual(newModule.AssemblyReferences.Select(r => r.FullName)), "assembly references changed");
        Require(oldModule.ModuleReferences.Select(r => r.Name).SequenceEqual(newModule.ModuleReferences.Select(r => r.Name)), "module references changed");
        Require(Attributes(original) == Attributes(changed) && Attributes(oldModule) == Attributes(newModule), "assembly/module custom attributes changed");
        var oldTypes = Types(oldModule).ToArray();
        var newTypes = Types(newModule).ToArray();
        Require(oldTypes.Select(t => t.FullName).SequenceEqual(newTypes.Select(t => t.FullName)), "type set/order changed");
        int preservedMethods = 0, preservedFields = 0;
        foreach (var (before, after) in oldTypes.Zip(newTypes))
        {
            Require(TypeMetadata(before) == TypeMetadata(after), "type metadata changed: " + before.FullName);
            foreach (var field in before.Fields)
            {
                if (before.FullName == SeriesName && field.Name == "_sort") continue;
                var updated = after.Fields.Single(f => f.Name == field.Name);
                Require(FieldMetadata(field) == FieldMetadata(updated), "field changed: " + field.FullName);
                preservedFields++;
            }
            Require(after.Fields.Count == before.Fields.Count + (before.FullName == SeriesName ? 1 : 0), "field count changed unexpectedly");
            foreach (var method in before.Methods)
            {
                if (before.FullName == SeriesName && method.Name == ".cctor") continue;
                var updated = after.Methods.Single(m => m.FullName == method.FullName);
                Require(MethodMetadata(method) == MethodMetadata(updated), "method metadata changed: " + method.FullName);
                if (before.FullName == SeriesName && ChangedMethods.Contains(method.Name)) continue;
                Require(Body(method) == Body(updated), "non-target body changed: " + method.FullName);
                preservedMethods++;
            }
            Require(after.Methods.Count == before.Methods.Count + (before.FullName == SeriesName ? 1 : 0), "method count changed unexpectedly");
        }
        var oldResources = oldModule.Resources.Select(ResourceMetadata).ToArray();
        var newResources = newModule.Resources.Select(ResourceMetadata).ToArray();
        Require(oldResources.SequenceEqual(newResources), "resource changed");
        Require(!newModule.AssemblyReferences.Any(r => r.Name is "PatchTimeLogger" or "TimeLoggerExactProbe"), "tool dependency leaked");
        return new { unchangedMethods = preservedMethods, unchangedFields = preservedFields, unchangedTypes = oldTypes.Length, unchangedResources = oldResources.Length, unchangedAssemblyReferences = oldModule.AssemblyReferences.Count, resources = oldResources };
    }

    static string Attributes(Mono.Cecil.ICustomAttributeProvider provider) => string.Join(";", provider.CustomAttributes.Select(a => a.AttributeType.FullName + ":" + Convert.ToHexString(a.GetBlob())));
    static string Security(Mono.Cecil.ISecurityDeclarationProvider provider) => string.Join(";", provider.SecurityDeclarations.Select(s => s.Action + ":" + Convert.ToHexString(s.GetBlob())));
    static string TypeMetadata(TypeDefinition t) => $"{t.Attributes}|{t.BaseType?.FullName}|{t.PackingSize}|{t.ClassSize}|{Attributes(t)}|{Security(t)}|{string.Join(';', t.Interfaces.Select(i => i.InterfaceType.FullName + ':' + Attributes(i)))}|{string.Join(';', t.GenericParameters.Select(GenericMetadata))}|{string.Join(';', t.Properties.Select(p => p.FullName + ':' + p.Attributes + ':' + Attributes(p) + ':' + p.GetMethod?.FullName + ':' + p.SetMethod?.FullName + ':' + string.Join(',', p.OtherMethods.Select(m => m.FullName))))}|{string.Join(';', t.Events.Select(e => e.FullName + ':' + e.Attributes + ':' + Attributes(e) + ':' + e.AddMethod?.FullName + ':' + e.RemoveMethod?.FullName + ':' + e.InvokeMethod?.FullName))}";
    static string GenericMetadata(GenericParameter p) => $"{p.Name}:{p.Attributes}:{Attributes(p)}:{string.Join(',', p.Constraints.Select(c => c.ConstraintType.FullName + ':' + Attributes(c)))}";
    static string FieldMetadata(FieldDefinition f) => $"{f.FullName}|{f.Attributes}|{f.Offset}|{f.HasConstant}:{f.Constant}|{Convert.ToHexString(f.InitialValue)}|{Attributes(f)}|{f.HasMarshalInfo}:{(f.HasMarshalInfo ? f.MarshalInfo.NativeType : null)}";
    static string MethodMetadata(MethodDefinition m) => $"{m.FullName}|{m.Attributes}|{m.ImplAttributes}|{m.CallingConvention}|{m.SemanticsAttributes}|{Attributes(m)}|{Security(m)}|{Attributes(m.MethodReturnType)}|{string.Join(';', m.Parameters.Select(p => p.Name + ':' + p.Attributes + ':' + p.HasConstant + ':' + p.Constant + ':' + Attributes(p)))}|{string.Join(';', m.GenericParameters.Select(GenericMetadata))}|{string.Join(';', m.Overrides.Select(o => o.FullName))}|{(m.HasPInvokeInfo ? m.PInvokeInfo.Attributes + ":" + m.PInvokeInfo.EntryPoint + ":" + m.PInvokeInfo.Module.Name : "")}";
    static string ResourceMetadata(Resource r) => $"{r.Name}|{r.ResourceType}|{r.Attributes}|" + (r switch
    {
        EmbeddedResource e => Sha(e.GetResourceData()),
        LinkedResource l => l.File + ":" + Convert.ToHexString(l.Hash),
        AssemblyLinkedResource a => a.Assembly.FullName,
        _ => throw new InvalidDataException("unsupported resource")
    });
}
