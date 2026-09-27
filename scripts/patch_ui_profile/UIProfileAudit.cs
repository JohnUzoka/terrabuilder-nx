using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;
using MethodDefinition = Mono.Cecil.MethodDefinition;
using TypeDefinition = Mono.Cecil.TypeDefinition;
using TypeReference = Mono.Cecil.TypeReference;
using MemberReference = Mono.Cecil.MemberReference;
using FieldDefinition = Mono.Cecil.FieldDefinition;
using ModuleDefinition = Mono.Cecil.ModuleDefinition;
using ModuleReference = Mono.Cecil.ModuleReference;
using GenericParameter = Mono.Cecil.GenericParameter;
using TypeSpecification = Mono.Cecil.TypeSpecification;

internal static class UIProfileAudit
{
    internal static object Verify(byte[] originalBytes, byte[] candidateBytes, UIProfilePatcher.Receipt[] receipts, string inputPath)
    {
        Require(Sha(originalBytes) == Program.InputHash, "UI51 audit requires exact adopted50 bytes");
        using var resolver = UIProfilePatcher.Resolver(inputPath);
        using var original = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes), new ReaderParameters { AssemblyResolver = resolver });
        using var candidate = AssemblyDefinition.ReadAssembly(new MemoryStream(candidateBytes), new ReaderParameters { AssemblyResolver = resolver });
        using var expected = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes), new ReaderParameters { AssemblyResolver = resolver });
        var generatedReceipts = UIProfilePatcher.Inject(expected.MainModule);
        Require(JsonSerializer.Serialize(receipts) == JsonSerializer.Serialize(generatedReceipts), "UI51 splice receipts differ from pinned plan");
        // Compare qualified metadata and all generated code BEFORE any runtime fixture mapping.
        // A FullName-only host rebinding must never make a malformed candidate appear valid.
        using var expectedBuffer = new MemoryStream(); expected.Write(expectedBuffer, new WriterParameters { Timestamp = 0 });
        using var serializedExpected = AssemblyDefinition.ReadAssembly(new MemoryStream(expectedBuffer.ToArray()), new ReaderParameters { AssemblyResolver = resolver });
        var qualified = CompareQualified(serializedExpected.MainModule, candidate.MainModule);
        var preservation = Preserve(original, candidate, receipts);
        var inverse = receipts.Select(r => Inverse(original.MainModule, candidate.MainModule, r)).ToArray();
        var originals = Types(original.MainModule).SelectMany(t => t.Methods).ToArray();
        var gate = originals.Single(m => m.Name == "DrawSingleTile" && m.Body.Instructions.Count == 2312);
        var afterGate = Types(candidate.MainModule).SelectMany(t => t.Methods).Single(m => m.FullName == gate.FullName);
        Require(Body(gate) == Body(afterGate), "inherited50 Single gate body changed");
        var native = PeResources.Fingerprints(originalBytes);
        Require(native.SequenceEqual(PeResources.Fingerprints(candidateBytes)), "native resource bytes changed");
        var wrappers = WrapperInventory(original.MainModule, candidate.MainModule, receipts);
        var raw = Signatures(candidateBytes, receipts.Select(r => r.Method).ToHashSet(StringComparer.Ordinal));
        var sdk = Sdk(candidate.MainModule, original.MainModule);
        Require(!candidate.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("Patch", StringComparison.Ordinal)), "patch-time assembly reference escaped");
        Require(!candidate.MainModule.GetTypeReferences().Any(t => t.Name.StartsWith("UI51", StringComparison.Ordinal) || t.Name.EndsWith("Shape", StringComparison.Ordinal)), "view/template type escaped");
        return new {
            passed = true, qualified, preservation, inverse, wrappers, rawSignatures = raw, sdkReferences = sdk, nativeResources = native,
            inherited50 = new { method = gate.FullName, fingerprint = Fingerprint(gate), instructions = gate.Body.Instructions.Count, exactBody = true, allThreeGateEntriesUntouched = true },
            changedMethods = receipts.Select(r => new { r.Method, r.Kind, r.ScopeId, r.BeforeHash, r.AfterHash, originalInstructions = r.OriginalIndices.Length, r.OriginalLocals, r.OriginalHandlers, fontCallsites = r.Calls.Length }),
            limits = "Instrumentation adds observer and typed-wrapper frames and finally regions. Original application calls, evaluated arguments, return values, byrefs, exception objects, catches and cleanup order are retained. Generated-fault stack traces, profiler/debugger views, timing, stack exhaustion, asynchronous interruption and arbitrary stack-sensitive code are not claimed equivalent. Measurements are inclusive/exclusive sampled wall-clock costs, not pure CPU or Switch GPU proof."
        };
    }

    internal static object NegativeProof(byte[] originalBytes, byte[] candidateBytes, UIProfilePatcher.Receipt[] receipts, string inputPath, string output)
    {
        Directory.CreateDirectory(output);
        var cases = new (string Name, Action<ModuleDefinition> Mutate)[] {
            ("wrong-vector-scope", m => m.GetTypeReferences().Single(t => t.FullName == "Microsoft.Xna.Framework.Vector2").Scope = m.AssemblyReferences.Single(r => r.Name == "mscorlib")),
            ("original-single-gate", m => {
                var target = Types(m).SelectMany(t => t.Methods).Single(x => x.Name == "DrawSingleTile" && x.Body.Instructions.Count == 2312);
                var branch = target.Body.Instructions.Single(i => i.Offset == 0x0715);
                Require(branch.OpCode == OpCodes.Brfalse, "negative gate fixture boundary changed"); branch.OpCode = OpCodes.Brtrue;
            }),
            ("missing-frame-end", m => {
                var target = Program.Method(m, Program.MainDrawName);
                var hook = target.Body.Instructions.Single(i => i.Operand is MethodReference r && r.DeclaringType.FullName == UIProfilePatcher.HelperName && r.Name == "FrameEnd");
                hook.OpCode = OpCodes.Nop; hook.Operand = null;
            }),
            ("wrong-ui-scope", m => {
                var target = Program.Method(m, Program.InterfaceName);
                var hook = target.Body.Instructions.Single(i => i.Operand is MethodReference r && r.DeclaringType.FullName == UIProfilePatcher.HelperName && r.Name == "Enter");
                Require(hook.Previous.OpCode == OpCodes.Ldc_I4, "negative scope fixture missing id"); hook.Previous.Operand = 7;
            }),
            ("wrong-wrapper-signature", m => {
                var wrapper = Types(m).Where(UIProfilePatcher.IsAdded).SelectMany(t => t.Methods).First(x => x.Name.StartsWith("Font", StringComparison.Ordinal));
                wrapper.Parameters[0].ParameterType = m.TypeSystem.Object;
            })
        };
        var rows = new List<object>();
        foreach (var test in cases) {
            using var resolver = UIProfilePatcher.Resolver(inputPath);
            using var changed = AssemblyDefinition.ReadAssembly(new MemoryStream(candidateBytes), new ReaderParameters { AssemblyResolver = resolver });
            test.Mutate(changed.MainModule);
            using var stream = new MemoryStream(); changed.Write(stream, new WriterParameters { Timestamp = 0 });
            byte[] bytes = stream.ToArray(); string path = Path.Combine(output, test.Name + ".exe");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes);
            string? rejection = null;
            try { Verify(originalBytes, bytes, receipts, inputPath); }
            catch (InvalidDataException error) { rejection = error.Message; }
            Require(rejection != null && rejection.StartsWith("qualified ", StringComparison.Ordinal), "malformed candidate reached inverse/fixture stage: " + test.Name);
            rows.Add(new { test.Name, candidatePath = path, sha256 = Sha(bytes), rejection, beforeFixtureRemapping = true });
        }
        var raw = Invoke("RawSignatures", "VerifyMalformedRejection", output)!;
        var result = new { passed = true, candidateSha256 = Sha(candidateBytes), rejectedCandidates = rows, malformedRawPrimitive = raw };
        Json(Path.Combine(output, "static-negative-proof.json"), result);
        return result;
    }

    static object Preserve(AssemblyDefinition original, AssemblyDefinition candidate, UIProfilePatcher.Receipt[] receipts)
    {
        var a = original.MainModule; var b = candidate.MainModule;
        string Meta(string name, object value) => (string)Invoke("Preservation", name, value)!;
        Require(a.Name == b.Name && a.Kind == b.Kind && a.Architecture == b.Architecture && a.Attributes == b.Attributes && a.RuntimeVersion == b.RuntimeVersion && a.EntryPoint?.FullName == b.EntryPoint?.FullName && original.Name.FullName == candidate.Name.FullName, "assembly identity/runtime changed");
        Require(a.AssemblyReferences.Select(r => r.FullName).SequenceEqual(b.AssemblyReferences.Select(r => r.FullName)), "assembly references changed");
        Require(a.ModuleReferences.Select(r => r.Name).SequenceEqual(b.ModuleReferences.Select(r => r.Name)), "module references changed");
        Require(Meta("Attributes", original) == Meta("Attributes", candidate) && Meta("Security", original) == Meta("Security", candidate) && Meta("Attributes", a) == Meta("Attributes", b), "assembly/module attributes changed");
        var before = Types(a).ToArray(); var after = Types(b).Where(t => !UIProfilePatcher.IsAdded(t)).ToArray();
        Require(before.Select(t => t.FullName).SequenceEqual(after.Select(t => t.FullName)), "original type order/count changed");
        var changed = receipts.Select(r => r.Method).ToHashSet(StringComparer.Ordinal);
        var originalMetadata = QualifiedUses(a, false); var candidateMetadata = QualifiedUses(b, false);
        foreach (var (key, value) in originalMetadata) Require(candidateMetadata.TryGetValue(key, out var actual) && actual == value, "original qualified metadata changed: " + key);
        int methods = 0, fields = 0;
        for (int index = 0; index < before.Length; index++) {
            var old = before[index]; var current = after[index];
            Require(Meta("TypeMetadata", old) == Meta("TypeMetadata", current), "original type metadata changed: " + old.FullName);
            Require(old.Fields.Select(f => f.FullName).SequenceEqual(current.Fields.Select(f => f.FullName)) && old.Methods.Select(m => m.FullName).SequenceEqual(current.Methods.Select(m => m.FullName)), "original members changed: " + old.FullName);
            for (int n = 0; n < old.Fields.Count; n++) { Require(Meta("FieldMetadata", old.Fields[n]) == Meta("FieldMetadata", current.Fields[n]), "original field changed: " + old.Fields[n].FullName); fields++; }
            for (int n = 0; n < old.Methods.Count; n++) {
                var m = old.Methods[n]; var c = current.Methods[n];
                Require(Meta("MethodMetadata", m) == Meta("MethodMetadata", c), "original method metadata changed: " + m.FullName);
                if (!changed.Contains(m.FullName)) { Require(Body(m) == Body(c) && (!m.HasBody || m.Body.MaxStackSize == c.Body.MaxStackSize), "unaffected original body changed: " + m.FullName); methods++; }
            }
        }
        Require(a.Resources.Select(r => Meta("ResourceMetadata", r)).SequenceEqual(b.Resources.Select(r => Meta("ResourceMetadata", r))), "managed resources changed");
        var helper = Types(b).Single(t => t.FullName == UIProfilePatcher.HelperName);
        Require(!helper.IsBeforeFieldInit && !helper.Methods.Any(m => m.IsConstructor), "runtime eager initialization introduced");
        return new { unchangedTypes = before.Length, unchangedFields = fields, unchangedMethods = methods, changedMethods = changed.Count, managedResources = a.Resources.Count, noEagerRuntimeConstructor = true };
    }

    static MethodDefinition Clone(MethodDefinition method)
    {
        var result = new MethodDefinition(method.Name, method.Attributes, method.ReturnType) { ImplAttributes = method.ImplAttributes, CallingConvention = method.CallingConvention };
        foreach (var p in method.Parameters) result.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        UIProfilePatcher.CopyBody(method, result, t => t, o => o); return result;
    }

    internal static object Inverse(ModuleDefinition original, ModuleDefinition candidate, UIProfilePatcher.Receipt receipt)
    {
        var before = Types(original).SelectMany(t => t.Methods).Single(m => m.FullName == receipt.Method);
        var after = Types(candidate).SelectMany(t => t.Methods).Single(m => m.FullName == receipt.Method);
        var scratch = Clone(after); var all = scratch.Body.Instructions.ToArray();
        Require(receipt.OriginalIndices.Length == before.Body.Instructions.Count && receipt.OriginalIndices.Distinct().Count() == receipt.OriginalIndices.Length && receipt.OriginalIndices.All(i => i >= 0 && i < all.Length), "invalid inverse instruction receipt");
        var kept = receipt.OriginalIndices.Select(i => all[i]).ToArray(); var keepSet = kept.ToHashSet();
        var source = before.Body.Instructions.ToArray(); var callIndices = receipt.Calls.Select(c => c.OriginalIndex).ToHashSet();
        var returns = receipt.ReturnIndices.ToHashSet();
        Require(scratch.Body.Variables.Count == receipt.OriginalLocals + (receipt.Kind is "frame" or "layer" or "scope" ? (before.ReturnType.MetadataType == MetadataType.Void ? 2 : 3) : 0), "unexpected injected local count");
        Require(scratch.Body.ExceptionHandlers.Count == receipt.OriginalHandlers + (receipt.Kind is "frame" or "layer" or "scope" ? 1 : 0), "unexpected observer EH count");
        for (int index = 0; index < kept.Length; index++) {
            var instruction = kept[index]; var originalInstruction = source[index];
            if (returns.Contains(index) && receipt.Kind is "frame" or "layer" or "scope") {
                Require(instruction.OpCode == (before.ReturnType.MetadataType == MetadataType.Void ? OpCodes.Ldc_I4_1 : OpCodes.Stloc), "return prologue changed");
                instruction.OpCode = OpCodes.Ret; instruction.Operand = null;
            } else if (callIndices.Contains(index)) {
                var row = receipt.Calls.Single(c => c.OriginalIndex == index);
                Require(instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference wrapper && wrapper.FullName == row.Wrapper &&
                    originalInstruction.Operand is MethodReference callee && MemberIdentity(callee) == row.Target && originalInstruction.OpCode.Name == row.Opcode, "font replacement receipt mismatch");
                instruction.OpCode = originalInstruction.OpCode; instruction.Operand = originalInstruction.Operand;
            } else if (originalInstruction.OpCode.OperandType == OperandType.ShortInlineBrTarget) {
                Require(instruction.OpCode == originalInstruction.OpCode || instruction.OpCode.Name == originalInstruction.OpCode.Name[..^2], "original branch not widened faithfully");
                instruction.OpCode = originalInstruction.OpCode;
            }
            foreach (var target in instruction.Operand is Instruction branch ? new[] { branch } : instruction.Operand is Instruction[] branches ? branches : Array.Empty<Instruction>())
                Require(keepSet.Contains(target), "original branch retargeted outside original return prologue");
        }
        foreach (var instruction in all) if (!keepSet.Contains(instruction)) scratch.Body.Instructions.Remove(instruction);
        while (scratch.Body.ExceptionHandlers.Count > receipt.OriginalHandlers) scratch.Body.ExceptionHandlers.RemoveAt(scratch.Body.ExceptionHandlers.Count - 1);
        for (int n = 0; n < receipt.OriginalHandlers; n++) {
            var old = before.Body.ExceptionHandlers[n]; var restored = scratch.Body.ExceptionHandlers[n];
            if (old.TryEnd == null) restored.TryEnd = null;
            if (old.HandlerEnd == null) restored.HandlerEnd = null;
        }
        while (scratch.Body.Variables.Count > receipt.OriginalLocals) scratch.Body.Variables.RemoveAt(scratch.Body.Variables.Count - 1);
        scratch.Body.InitLocals = receipt.OriginalInitLocals; scratch.Body.MaxStackSize = receipt.OriginalMaxStack;
        Require(Body(before) == Body(scratch), "exact inverse failed for original instruction/local/handler: " + receipt.Method);
        Require(QualifiedBody(before) == QualifiedBody(scratch), "qualified inverse failed: " + receipt.Method);
        return new { receipt.Method, restoredInstructions = source.Length, restoredLocals = receipt.OriginalLocals, restoredHandlers = receipt.OriginalHandlers, restoredCalls = receipt.Calls.Length, originalReturnPrologues = receipt.ReturnIndices.Length, exact = true };
    }

    static object WrapperInventory(ModuleDefinition original, ModuleDefinition candidate, UIProfilePatcher.Receipt[] receipts)
    {
        var added = Types(candidate).Where(UIProfilePatcher.IsAdded).SelectMany(t => t.Methods).Where(m => m.Name.StartsWith("Font", StringComparison.Ordinal)).ToArray();
        var rows = new List<object>();
        foreach (var wrapper in added) {
            var references = receipts.SelectMany(r => r.Calls.Select(c => (r.Method, Call: c))).Where(x => x.Call.Wrapper == wrapper.FullName).ToArray();
            Require(references.Length > 0 && references.Select(x => x.Call.Target + x.Call.Opcode).Distinct().Count() == 1, "wrapper is unused or merges incompatible call signatures");
            var originalMethod = Types(original).SelectMany(t => t.Methods).Single(m => m.FullName == references[0].Method);
            var source = originalMethod.Body.Instructions[references[0].Call.OriginalIndex]; var target = (MethodReference)source.Operand;
            Require(!wrapper.HasThis && wrapper.IsStatic && TypeIdentity(wrapper.ReturnType) == TypeIdentity(target.ReturnType), "wrapper receiver/return signature changed");
            var arguments = (target.HasThis ? new[] { target.DeclaringType } : Array.Empty<TypeReference>()).Concat(target.Parameters.Select(p => p.ParameterType));
            Require(wrapper.Parameters.Select(p => TypeIdentity(p.ParameterType)).SequenceEqual(arguments.Select(TypeIdentity)), "wrapper value/ref parameter signature changed");
            var external = wrapper.Body.Instructions.Where(i => i.Operand is MethodReference r && UIProfilePatcher.FontMetric(r) >= 0).ToArray();
            Require(external.Length == 2 && external.All(i => i.OpCode == source.OpCode && MemberIdentity((MethodReference)i.Operand) == MemberIdentity(target)), "wrapper does not retain both exact original opcode/callee paths");
            Require(wrapper.Body.Instructions[0].OpCode == OpCodes.Ldsfld && wrapper.Body.Instructions[0].Operand is FieldReference active && active.Name == "Active" && wrapper.Body.Instructions[1].OpCode == OpCodes.Brtrue, "wrapper lacks noActive fast path");
            rows.Add(new { wrapper = wrapper.FullName, original = target.FullName, qualifiedOriginal = MemberIdentity(target), opcode = source.OpCode.Name, callsites = references.Select(x => new { method = x.Method, originalInstruction = x.Call.OriginalIndex }), explicitReceiver = target.HasThis, directFastPathNoClocks = true });
        }
        Require(added.Length == 8 && receipts.Sum(r => r.Calls.Length) == 230, "font wrapper completeness failed");
        return new { wrappers = added.Length, originalGameCallsites = 230, originalGameCallers = receipts.Count(r => r.Calls.Length > 0), directFnaSpriteFontCallsites = 0, rows };
    }

    static string Scope(IMetadataScope? scope) => scope switch {
        AssemblyNameReference assembly => "assembly:" + assembly.FullName,
        ModuleDefinition module => "module:" + module.Name + "@" + module.Assembly.Name.FullName,
        ModuleReference module => "module-ref:" + module.Name, null => "none", _ => throw new InvalidDataException("unknown scope")
    };
    internal static string TypeIdentity(TypeReference? type) => type switch {
        null => "none", GenericParameter p => (p.Type == GenericParameterType.Method ? "!!" : "!") + p.Position,
        GenericInstanceType g => "generic:" + TypeIdentity(g.ElementType) + "<" + string.Join(',', g.GenericArguments.Select(TypeIdentity)) + ">",
        ArrayType a => "array:" + a.Rank + ":" + a.IsVector + "[" + string.Join(',', a.Dimensions.Select(d => d.LowerBound + ":" + d.UpperBound)) + "](" + TypeIdentity(a.ElementType) + ")",
        OptionalModifierType m => "optional:" + TypeIdentity(m.ModifierType) + "(" + TypeIdentity(m.ElementType) + ")",
        RequiredModifierType m => "required:" + TypeIdentity(m.ModifierType) + "(" + TypeIdentity(m.ElementType) + ")",
        FunctionPointerType f => "function:" + Signature(f), TypeSpecification s => s.GetType().Name + "(" + TypeIdentity(s.ElementType) + ")",
        _ => type.FullName + "@" + (type.DeclaringType == null ? Scope(type.Scope) : TypeIdentity(type.DeclaringType))
    };
    static string Signature(IMethodSignature method) => method.CallingConvention + ":" + method.HasThis + ":" + method.ExplicitThis + ":" + TypeIdentity(method.ReturnType) + "(" + string.Join(',', method.Parameters.Select(p => TypeIdentity(p.ParameterType))) + ")";
    internal static string MemberIdentity(MemberReference member) => member switch {
        TypeReference type => TypeIdentity(type), GenericInstanceMethod g => "method-spec:" + MemberIdentity(g.ElementMethod) + "<" + string.Join(',', g.GenericArguments.Select(TypeIdentity)) + ">",
        MethodReference m => TypeIdentity(m.DeclaringType) + "::" + m.Name + "``" + m.GenericParameters.Count + ":" + Signature(m),
        FieldReference f => TypeIdentity(f.DeclaringType) + "::" + f.Name + ":" + TypeIdentity(f.FieldType), _ => throw new InvalidDataException("unknown member")
    };
    static string QualifiedBody(MethodDefinition method)
    {
        if (!method.HasBody) return "none";
        var indices = method.Body.Instructions.Select((i,n) => (i,n)).ToDictionary(x => x.i, x => x.n);
        string Operand(object? value) => value switch {
            null => "", Instruction i => "@" + indices[i], Instruction[] a => string.Join(',', a.Select(i => "@" + indices[i])),
            VariableDefinition v => "local:" + v.Index, ParameterDefinition p => "arg:" + p.Index,
            MemberReference r => MemberIdentity(r), CallSite c => "call-site:" + Signature(c),
            float n => BitConverter.SingleToInt32Bits(n).ToString("x8", CultureInfo.InvariantCulture), double n => BitConverter.DoubleToInt64Bits(n).ToString("x16", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
        };
        string At(Instruction? i) => i == null ? "end" : "@" + indices[i];
        return method.Body.InitLocals + "\n" + string.Join('\n', method.Body.Variables.Select(v => TypeIdentity(v.VariableType))) + "\n" +
            string.Join('\n', method.Body.Instructions.Select(i => i.OpCode.Name + " " + Operand(i.Operand))) + "\n" +
            string.Join('\n', method.Body.ExceptionHandlers.Select(h => h.HandlerType + ":" + At(h.TryStart) + ":" + At(h.TryEnd) + ":" + At(h.HandlerStart) + ":" + At(h.HandlerEnd) + ":" + At(h.FilterStart) + ":" + TypeIdentity(h.CatchType)));
    }

    static string Constant(bool present, object? value) => !present ? "absent" : value switch {
        null => "null", byte[] bytes => "bytes:" + Convert.ToHexString(bytes),
        float number => "single:" + BitConverter.SingleToInt32Bits(number).ToString("x8", CultureInfo.InvariantCulture),
        double number => "double:" + BitConverter.DoubleToInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture),
        _ => value.GetType().FullName + ":" + Convert.ToString(value, CultureInfo.InvariantCulture)
    };
    static string Marshal(MarshalInfo? info)
    {
        if (info == null) return "none";
        // Include every descriptor property, not just NativeType (array extents,
        // parameter indices, custom marshaler type/cookie, fixed strings, safearrays).
        return info.GetType().FullName + ":" + string.Join(';', info.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(p => p.GetIndexParameters().Length == 0).OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.Name + "=" + (p.GetValue(info) is TypeReference type ? TypeIdentity(type) : Constant(true, p.GetValue(info)))));
    }

    static Dictionary<string,string> QualifiedUses(ModuleDefinition module, bool includeBodies = true)
    {
        var rows = new Dictionary<string,string>(StringComparer.Ordinal);
        void Attr(string key, Mono.Cecil.ICustomAttributeProvider owner) => rows.Add(key + ":attributes", string.Join('\n', owner.CustomAttributes.Select(a => MemberIdentity(a.Constructor) + ":" + Convert.ToHexString(a.GetBlob()))));
        void Generic(string key, IEnumerable<GenericParameter> parameters) {
            foreach (var parameter in parameters) {
                string id = key + ":generic:" + parameter.Position; Attr(id, parameter);
                rows.Add(id, parameter.Attributes + ":" + string.Join(',', parameter.Constraints.Select(c => TypeIdentity(c.ConstraintType))));
                for (int n=0;n<parameter.Constraints.Count;n++) Attr(id + ":constraint:" + n, parameter.Constraints[n]);
            }
        }
        Attr("assembly", module.Assembly); Attr("module", module);
        rows.Add("assembly:name-details", module.Assembly.Name.Attributes + ":" + module.Assembly.Name.HashAlgorithm + ":" + Convert.ToHexString(module.Assembly.Name.PublicKey));
        rows.Add("module:characteristics", module.Characteristics.ToString());
        foreach (var reference in module.AssemblyReferences) rows.Add("assembly-reference:" + reference.FullName, reference.Attributes + ":" + reference.HashAlgorithm + ":" + Convert.ToHexString(reference.PublicKey) + ":" + Convert.ToHexString(reference.Hash));
        foreach (var exported in module.ExportedTypes) rows.Add("exported:" + exported.FullName, exported.Attributes + ":" + exported.Identifier + ":" + Scope(exported.Scope) + ":" + exported.DeclaringType?.FullName);
        foreach (var type in Types(module)) {
            string key = "type:" + type.FullName; Attr(key, type); Generic(key, type.GenericParameters);
            rows.Add(key, TypeIdentity(type) + ":" + TypeIdentity(type.BaseType) + ":" + string.Join(',', type.Interfaces.Select(i => TypeIdentity(i.InterfaceType))));
            for(int n=0;n<type.Interfaces.Count;n++) Attr(key + ":interface:" + n,type.Interfaces[n]);
            foreach (var field in type.Fields) { string f = "field:" + field.FullName; rows.Add(f,MemberIdentity(field)); Attr(f,field); rows.Add(f + ":constant-marshal", Constant(field.HasConstant, field.Constant) + ":" + Marshal(field.HasMarshalInfo ? field.MarshalInfo : null)); }
            foreach (var property in type.Properties) {
                string p = "property:" + property.FullName; rows.Add(p,TypeIdentity(property.PropertyType) + ":" + string.Join(',',property.Parameters.Select(a => TypeIdentity(a.ParameterType)))); Attr(p,property);
                rows.Add(p + ":constant", Constant(property.HasConstant, property.Constant));
                for (int n = 0; n < property.Parameters.Count; n++) { var parameter = property.Parameters[n]; Attr(p + ":arg:" + n, parameter); rows.Add(p + ":arg-details:" + n, parameter.Attributes + ":" + Constant(parameter.HasConstant, parameter.Constant) + ":" + Marshal(parameter.HasMarshalInfo ? parameter.MarshalInfo : null)); }
            }
            foreach (var item in type.Events) { string e = "event:" + item.FullName; rows.Add(e,TypeIdentity(item.EventType) + ":" + string.Join(',',item.OtherMethods.Select(MemberIdentity))); Attr(e,item); }
            foreach (var method in type.Methods) {
                string m = "method:" + method.FullName; rows.Add(m,MemberIdentity(method)); Attr(m,method); Attr(m + ":return",method.MethodReturnType); Generic(m,method.GenericParameters);
                rows.Add(m + ":overrides",string.Join('\n',method.Overrides.Select(MemberIdentity)));
                rows.Add(m + ":return-details", method.MethodReturnType.Attributes + ":" + Constant(method.MethodReturnType.HasConstant, method.MethodReturnType.Constant) + ":" + Marshal(method.MethodReturnType.HasMarshalInfo ? method.MethodReturnType.MarshalInfo : null));
                for(int n=0;n<method.Parameters.Count;n++) { var parameter = method.Parameters[n]; Attr(m + ":arg:" + n,parameter); rows.Add(m + ":arg-details:" + n, parameter.Attributes + ":" + Constant(parameter.HasConstant, parameter.Constant) + ":" + Marshal(parameter.HasMarshalInfo ? parameter.MarshalInfo : null)); }
                if (includeBodies) rows.Add(m + ":body",QualifiedBody(method));
            }
        }
        return rows;
    }
    static object CompareQualified(ModuleDefinition expected, ModuleDefinition candidate)
    {
        Require(expected.GetTypeReferences().Select(TypeIdentity).ToHashSet().SetEquals(candidate.GetTypeReferences().Select(TypeIdentity)), "qualified TypeRef scope changed");
        Require(expected.GetMemberReferences().Select(MemberIdentity).ToHashSet().SetEquals(candidate.GetMemberReferences().Select(MemberIdentity)), "qualified MemberRef binding changed");
        var before = QualifiedUses(expected); var after = QualifiedUses(candidate);
        Require(before.Count == after.Count, "qualified metadata/use-site count changed");
        foreach(var (key,value) in before) Require(after.TryGetValue(key,out var actual) && actual == value,"qualified candidate differs at " + key);
        string Meta(string name, object value) => (string)Invoke("Preservation",name,value)!;
        var newTypes = Types(candidate).Where(UIProfilePatcher.IsAdded).ToDictionary(t=>t.FullName);
        foreach(var type in Types(expected).Where(UIProfilePatcher.IsAdded)) {
            var current = newTypes[type.FullName]; Require(Meta("TypeMetadata",type)==Meta("TypeMetadata",current),"added type metadata mismatch");
            foreach(var field in type.Fields) Require(Meta("FieldMetadata",field)==Meta("FieldMetadata",current.Fields.Single(f=>f.FullName==field.FullName)),"added field metadata mismatch");
            foreach(var method in type.Methods) Require(Meta("MethodMetadata",method)==Meta("MethodMetadata",current.Methods.Single(m=>m.FullName==method.FullName)),"added method metadata mismatch");
        }
        return new { passed = true, qualifiedUseSiteRows = before.Count, beforeAnyFixtureRemapping = true, tableOrderAndDedupIndependent = true, selfModuleMvidIntentionallyExcluded = true };
    }

    internal static object Signatures(byte[] bytes, HashSet<string> changed)
    {
        using var stream = new MemoryStream(bytes); using var pe = new PEReader(stream); var reader = pe.GetMetadataReader();
        var decoder = (ISignatureTypeProvider<string,object?>)Activator.CreateInstance(Support.GetType("RawSignatures+Decoder",true)!,true)!;
        using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        var methods = Types(game.MainModule).SelectMany(t=>t.Methods).Where(m=>m.HasBody && (UIProfilePatcher.IsAdded(m.DeclaringType) || changed.Contains(m.FullName))).ToArray();
        string Sig(MethodSignature<string> sig) => sig.ReturnType + "(" + string.Join(',',sig.ParameterTypes) + ")";
        var rows = new List<object>();
        foreach(var method in methods) {
            var d=reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle((int)method.MetadataToken.RID));
            rows.Add(new { kind="MethodDef", name=method.FullName, raw=Convert.ToHexString(reader.GetBlobBytes(d.Signature)), decoded=Sig(d.DecodeSignature(decoder,(object?)null)) });
            var body=pe.GetMethodBody(d.RelativeVirtualAddress);
            if(!body.LocalSignature.IsNil) { var l=reader.GetStandaloneSignature(body.LocalSignature); rows.Add(new { kind="Locals", name=method.FullName, raw=Convert.ToHexString(reader.GetBlobBytes(l.Signature)), decoded=string.Join(',',l.DecodeLocalSignature(decoder,(object?)null)) }); }
        }
        foreach(var field in Types(game.MainModule).Where(UIProfilePatcher.IsAdded).SelectMany(t=>t.Fields)) {
            var d=reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle((int)field.MetadataToken.RID)); rows.Add(new { kind="FieldDef",name=field.FullName,raw=Convert.ToHexString(reader.GetBlobBytes(d.Signature)),decoded=d.DecodeSignature(decoder,(object?)null) });
        }
        foreach(var reference in methods.SelectMany(m=>m.Body.Instructions).Select(i=>i.Operand).OfType<MemberReference>().Where(r=>r.MetadataToken.TokenType==TokenType.MemberRef).DistinctBy(r=>r.MetadataToken.ToInt32())) {
            var d=reader.GetMemberReference(MetadataTokens.MemberReferenceHandle((int)reference.MetadataToken.RID)); rows.Add(new { kind="MemberRef",name=reference.FullName,raw=Convert.ToHexString(reader.GetBlobBytes(d.Signature)),decoded=d.GetKind()==MemberReferenceKind.Method ? Sig(d.DecodeMethodSignature(decoder,(object?)null)) : d.DecodeFieldSignature(decoder,(object?)null) });
        }
        return new { passed=true,independentlyDecodedRawMetadata=true,primitiveClassOrValueTypeAliasesAllowed=false,rows };
    }

    static object Sdk(ModuleDefinition candidate, ModuleDefinition original)
    {
        Require(Sha(File.ReadAllBytes(UIProfilePatcher.PinnedResolver.Core))==Program.CoreHash,"pinned SDK CoreLib hash mismatch");
        var previous=original.GetMemberReferences().Select(MemberIdentity).ToHashSet(StringComparer.Ordinal);
        var members=Types(candidate).Where(UIProfilePatcher.IsAdded).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Select(i=>i.Operand).OfType<MemberReference>().Where(r=>r is MethodReference or FieldReference).DistinctBy(MemberIdentity).ToArray();
        var rows=new List<object>();
        foreach(var reference in members) {
            if(reference.DeclaringType.Namespace=="Terraria" && reference.DeclaringType.Name=="NXUIProfile51" || reference.DeclaringType.DeclaringType?.FullName==UIProfilePatcher.HelperName) continue;
            IMemberDefinition? resolved=reference switch { MethodReference m=>m.Resolve(), FieldReference f=>f.Resolve(), _=>null };
            Require(resolved!=null,"unresolved added member: "+MemberIdentity(reference));
            Require(resolved is MethodDefinition { IsPublic:true } or FieldDefinition { IsPublic:true } || resolved!.DeclaringType.Module.Assembly.Name.Name==candidate.Assembly.Name.Name && (resolved is MethodDefinition { IsAssembly:true } or FieldDefinition { IsAssembly:true }),"added reference inaccessible: "+reference.FullName);
            var owner=resolved!.DeclaringType.Module;
            if(reference.DeclaringType.Namespace.StartsWith("System",StringComparison.Ordinal)) Require(owner.FileName.StartsWith("/mono-nx/",StringComparison.Ordinal),"host BCL fallback detected");
            rows.Add(new { reference=reference.FullName,qualified=MemberIdentity(reference),resolved=resolved.FullName,image=owner.FileName,sha256=string.IsNullOrEmpty(owner.FileName)?Program.InputHash:Sha(File.ReadAllBytes(owner.FileName)),newReference=!previous.Contains(MemberIdentity(reference)) });
        }
        return new { passed=true,hostFallbackAllowed=false,coreSha256=Program.CoreHash,rows };
    }
}
