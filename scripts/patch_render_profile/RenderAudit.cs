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

internal static class RenderAudit
{
    const string CorePath = "/mono-nx/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug/System.Private.CoreLib.dll";
    static readonly int[] Tokens = { 0x060010c3, 0x06004530, 0x0600453a, 0x06000db5, 0x06002b60, 0x06002b62, 0x06002b64 };
    static readonly int[] Counts = { 27, 815, 2312, 56, 22, 114, 186 };
    static readonly int[] LocalDeltas = { 2, 2, 1, 0, 3, 0, 0 };
    static readonly int[] HandlerDeltas = { 1, 1, 0, 0, 1, 0, 0 };
    static readonly string[] Kinds = { "frame", "tile_draw", "tile_single", "run", "batch_end", "calls", "calls" };
    static readonly string[] WrapperNames = { "BatchUpload000", "BatchUpload001", "BatchSubmit002" };

    internal static object Verify(byte[] originalBytes, byte[] candidateBytes, RenderPatcher.Receipt[] receipts, string inputPath)
    {
        Require(Sha(originalBytes) == Program.InputHash, "render53 audit requires exact accepted52 bytes");
        string directory = Path.GetDirectoryName(Path.GetFullPath(inputPath))!;
        Require(Sha(File.ReadAllBytes(inputPath)) == Program.InputHash, "render53 resolver source differs from accepted52");
        Require(Sha(File.ReadAllBytes(Path.Combine(directory, "FNA.dll"))) == Program.FnaHash, "render53 accepted52 FNA pairing changed");
        Require(Sha(File.ReadAllBytes(Path.Combine(directory, "ReLogic.dll"))) == Program.ReLogicHash, "render53 accepted52 ReLogic pairing changed");
        using var resolver = RenderPatcher.Resolver(inputPath);
        using var original = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes), new ReaderParameters { AssemblyResolver = resolver });
        using var candidate = AssemblyDefinition.ReadAssembly(new MemoryStream(candidateBytes), new ReaderParameters { AssemblyResolver = resolver });
        using var expected = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes), new ReaderParameters { AssemblyResolver = resolver });
        Require(original.MainModule.Mvid == Guid.Parse("df61a8d1-0622-9555-82c2-a2118b6c9bc4"), "render53 original52 MVID changed");
        var generated = RenderPatcher.Inject(expected.MainModule);
        Require(JsonSerializer.Serialize(receipts) == JsonSerializer.Serialize(generated), "render53 splice receipts differ from pinned plan");
        Require(receipts.Length == 7 && receipts.Select(r => r.Method).SequenceEqual(Program.ChangedNames) && receipts.Select(r => r.Kind).SequenceEqual(Kinds), "render53 requires seven ordered original-body receipts");
        // The comparison is deliberately ahead of inverse normalization and all host
        // fixture rebinding. Otherwise a wrong qualified callee can be normalized away.
        byte[] expectedBytes = Serialize(expected);
        using var serializedExpected = AssemblyDefinition.ReadAssembly(new MemoryStream(expectedBytes), new ReaderParameters { AssemblyResolver = resolver });
        var qualified = CompareQualified(serializedExpected.MainModule, candidate.MainModule);
        var rawUses = RenderRawSignatures.Compare(expectedBytes, candidateBytes, serializedExpected.MainModule, candidate.MainModule);
        var raw = Signatures(candidateBytes, receipts.Select(r => r.Method).ToHashSet(StringComparer.Ordinal), inputPath);
        var preservation = Preserve(original, candidate, receipts);
        var inverse = receipts.Select(r => Inverse(original.MainModule, candidate.MainModule, r)).ToArray();
        var wrappers = WrapperInventory(original.MainModule, candidate.MainModule, receipts);
        var native = PeResources.Fingerprints(originalBytes);
        Require(native.SequenceEqual(PeResources.Fingerprints(candidateBytes)), "native resource bytes changed");
        var sdk = Sdk(candidate.MainModule, original.MainModule);
        Require(!candidate.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("Patch", StringComparison.Ordinal)), "patch-time assembly reference escaped");
        Require(!candidate.MainModule.GetTypeReferences().Any(t => t.Name == "Render53Template" || t.Name.EndsWith("Shape", StringComparison.Ordinal)), "view/template type escaped");
        return new {
            passed = true, qualified, preservation, inverse, wrappers, rawSignatureUses = rawUses, rawSignatures = raw, sdkReferences = sdk, nativeResources = native,
            inherited52 = new { exactOriginalReLogicSha256 = Program.ReLogicHash, exactOriginalFnaSha256 = Program.FnaHash, hintHelpersAndRootUnchanged = true },
            inherited50 = new { method = Program.SingleName, instructions = 2312, exactInverseIncludingRelocatedGate = true },
            changedMethods = receipts.Select(r => new { r.Method, r.Kind, r.ScopeId, r.BeforeHash, r.AfterHash, originalInstructions = r.OriginalIndices.Length, r.OriginalLocals, r.OriginalHandlers, batchCallsites = r.Calls.Length }),
            limits = "Static qualified preservation and exact generated-code checks, not execution or GPU proof. Observer/wrapper frames and finally regions are added. Stack traces, profiler/debugger views, stack exhaustion, asynchronous interruption and arbitrary stack-sensitive code are not claimed equivalent. Runtime semantic and timing evidence is separate."
        };
    }

    static byte[] Serialize(AssemblyDefinition assembly)
    {
        using var stream = new MemoryStream(); assembly.Write(stream, new WriterParameters { Timestamp = 0 }); return stream.ToArray();
    }

    static string Meta(string name, object value) => (string)Invoke("Preservation", name, value)!;

    static object Preserve(AssemblyDefinition original, AssemblyDefinition candidate, RenderPatcher.Receipt[] receipts)
    {
        var a = original.MainModule; var b = candidate.MainModule;
        Require(original.Modules.Count == 1 && candidate.Modules.Count == 1, "assembly module count changed");
        Require(a.Name == b.Name && a.Kind == b.Kind && a.Architecture == b.Architecture && a.Attributes == b.Attributes && a.RuntimeVersion == b.RuntimeVersion && a.EntryPoint?.FullName == b.EntryPoint?.FullName && original.Name.FullName == candidate.Name.FullName, "assembly identity/runtime changed");
        Require(a.AssemblyReferences.Select(r => r.FullName).SequenceEqual(b.AssemblyReferences.Select(r => r.FullName)), "assembly references changed");
        Require(a.ModuleReferences.Select(r => r.Name).SequenceEqual(b.ModuleReferences.Select(r => r.Name)), "module references changed");
        Require(Meta("Attributes", original) == Meta("Attributes", candidate) && Meta("Security", original) == Meta("Security", candidate) && Meta("Attributes", a) == Meta("Attributes", b), "assembly/module attributes changed");
        var before = Types(a).ToArray(); var after = Types(b).Where(t => !RenderPatcher.IsAdded(t)).ToArray();
        Require(before.Select(t => t.FullName).SequenceEqual(after.Select(t => t.FullName)), "original type order/count changed");
        var originalMetadata = QualifiedUses(a, false); var candidateMetadata = QualifiedUses(b, false);
        foreach (var (key, value) in originalMetadata) Require(candidateMetadata.TryGetValue(key, out var actual) && actual == value, "original qualified metadata changed: " + key);
        var changed = receipts.Select(r => Program.Method(a, r.Method)).ToHashSet();
        int methods = 0, fields = 0, changedBodies = 0;
        for (int index = 0; index < before.Length; index++) {
            var old = before[index]; var current = after[index];
            Require(Meta("TypeMetadata", old) == Meta("TypeMetadata", current), "original type metadata changed: " + old.FullName);
            Require(old.Fields.Select(f => f.FullName).SequenceEqual(current.Fields.Select(f => f.FullName)) && old.Methods.Select(m => m.FullName).SequenceEqual(current.Methods.Select(m => m.FullName)), "original member order/count changed: " + old.FullName);
            for (int n = 0; n < old.Fields.Count; n++) { Require(Meta("FieldMetadata", old.Fields[n]) == Meta("FieldMetadata", current.Fields[n]), "original field changed: " + old.Fields[n].FullName); fields++; }
            for (int n = 0; n < old.Methods.Count; n++) {
                var m = old.Methods[n]; var c = current.Methods[n];
                Require(Meta("MethodMetadata", m) == Meta("MethodMetadata", c), "original method metadata changed: " + m.FullName);
                if (!changed.Contains(m)) { Require(QualifiedBody(m) == QualifiedBody(c), "unaffected original body changed: " + m.FullName); methods++; }
                else { Require(QualifiedBody(m) != QualifiedBody(c), "planned original body did not change: " + m.FullName); changedBodies++; }
            }
        }
        Require(changedBodies == 7, "not exactly seven changed original bodies");
        Require(a.Resources.Select(r => Meta("ResourceMetadata", r)).SequenceEqual(b.Resources.Select(r => Meta("ResourceMetadata", r))), "managed resources changed");
        var added = Types(b).Where(RenderPatcher.IsAdded).ToArray();
        var helper = added.Single(t => t.FullName == RenderPatcher.HelperName);
        Require(helper.DeclaringType == null && !helper.IsPublic && !helper.IsBeforeFieldInit && !helper.Methods.Any(m => m.IsConstructor), "runtime visibility/eager initialization changed");
        Require(added.All(t => t == helper || t.IsNestedAssembly && t.DeclaringType == helper) && added.All(t => !t.Methods.Any(m => m.IsConstructor)), "only internal runtime helper tree may be added");
        return new { unchangedTypes = before.Length, unchangedFields = fields, unchangedMethods = methods, changedMethods = changedBodies, managedResources = a.Resources.Count, noEagerRuntimeConstructor = true, duplicateFullNamesMatchedByOwnerAndOrder = true };
    }

    static MethodDefinition Clone(MethodDefinition method)
    {
        var result = new MethodDefinition(method.Name, method.Attributes, method.ReturnType) { ImplAttributes = method.ImplAttributes, CallingConvention = method.CallingConvention };
        foreach (var p in method.Parameters) result.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        RenderPatcher.CopyBody(method, result, t => t, o => o); return result;
    }

    // Private: callers cannot use observer-target normalization without Verify's
    // preceding exact qualified expected-generation comparison.
    static object Inverse(ModuleDefinition original, ModuleDefinition candidate, RenderPatcher.Receipt receipt)
    {
        int slot = Array.IndexOf(Program.ChangedNames, receipt.Method);
        Require(slot >= 0, "unknown inverse receipt");
        var before = Program.Method(original, receipt.Method); var after = Program.Method(candidate, receipt.Method);
        Require(before.MetadataToken.ToInt32() == Tokens[slot] && before.Body.Instructions.Count == Counts[slot], "original52 target boundary changed");
        var scratch = Clone(after); var all = scratch.Body.Instructions.ToArray();
        Require(receipt.OriginalIndices.Length == before.Body.Instructions.Count && receipt.OriginalIndices.SequenceEqual(receipt.OriginalIndices.Order()) && receipt.OriginalIndices.Distinct().Count() == receipt.OriginalIndices.Length && receipt.OriginalIndices.All(i => i >= 0 && i < all.Length), "invalid inverse instruction receipt");
        Require(receipt.OriginalLocals == before.Body.Variables.Count && receipt.OriginalHandlers == before.Body.ExceptionHandlers.Count && receipt.OriginalInitLocals == before.Body.InitLocals && receipt.OriginalMaxStack == before.Body.MaxStackSize, "invalid inverse original body receipt");
        Require(scratch.Body.Variables.Count == receipt.OriginalLocals + LocalDeltas[slot] && scratch.Body.ExceptionHandlers.Count == receipt.OriginalHandlers + HandlerDeltas[slot] && scratch.Body.InitLocals == receipt.OriginalInitLocals, "unexpected injected local/EH/init-locals shape");
        var kept = receipt.OriginalIndices.Select(i => all[i]).ToArray(); var keepSet = kept.ToHashSet();
        var source = before.Body.Instructions.ToArray(); var callIndices = receipt.Calls.Select(c => c.OriginalIndex).ToHashSet();
        var returns = receipt.ReturnIndices.ToHashSet();
        var expectedReturns = source.Select((i, n) => (i, n)).Where(x => slot is 1 or 4 && x.i.OpCode == OpCodes.Ret).Select(x => x.n);
        Require(receipt.ReturnIndices.SequenceEqual(expectedReturns) && callIndices.Count == receipt.Calls.Length, "return/call receipt differs from original52");
        Instruction RecoverTarget(Instruction target) {
            // Existing49 entry blocks intentionally redirect original branches. The
            // generated code was already checked; remove only the leading observers.
            while (!keepSet.Contains(target)) { Require(target.Next != null, "observer redirect has no preserved successor"); target = target.Next; }
            return target;
        }
        for (int index = 0; index < kept.Length; index++) {
            var instruction = kept[index]; var originalInstruction = source[index];
            if (returns.Contains(index)) {
                Require(instruction.OpCode == (slot == 1 ? OpCodes.Leave : OpCodes.Stloc), "return observer prologue changed");
                instruction.OpCode = OpCodes.Ret; instruction.Operand = null;
            } else if (callIndices.Contains(index)) {
                var row = receipt.Calls.Single(c => c.OriginalIndex == index);
                Require(instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference wrapper && wrapper.FullName == row.Wrapper && originalInstruction.Operand is MethodReference callee && MemberIdentity(callee) == row.Target && originalInstruction.OpCode.Name == row.Opcode, "batch replacement receipt mismatch");
                instruction.OpCode = originalInstruction.OpCode; instruction.Operand = originalInstruction.Operand;
            } else if (originalInstruction.OpCode.OperandType == OperandType.ShortInlineBrTarget) {
                Require(instruction.OpCode == originalInstruction.OpCode || instruction.OpCode.Name == originalInstruction.OpCode.Name[..^2], "original branch not widened faithfully");
                instruction.OpCode = originalInstruction.OpCode;
            }
            if (instruction.Operand is Instruction branch) instruction.Operand = RecoverTarget(branch);
            else if (instruction.Operand is Instruction[] branches) instruction.Operand = branches.Select(RecoverTarget).ToArray();
        }
        while (scratch.Body.ExceptionHandlers.Count > receipt.OriginalHandlers) scratch.Body.ExceptionHandlers.RemoveAt(scratch.Body.ExceptionHandlers.Count - 1);
        for (int n = 0; n < receipt.OriginalHandlers; n++) {
            var old = before.Body.ExceptionHandlers[n]; var h = scratch.Body.ExceptionHandlers[n];
            h.TryStart = RecoverTarget(h.TryStart); h.HandlerStart = RecoverTarget(h.HandlerStart);
            h.TryEnd = old.TryEnd == null ? null : RecoverTarget(h.TryEnd);
            h.HandlerEnd = old.HandlerEnd == null ? null : RecoverTarget(h.HandlerEnd);
            if (h.FilterStart != null) h.FilterStart = RecoverTarget(h.FilterStart);
        }
        foreach (var instruction in all) if (!keepSet.Contains(instruction)) scratch.Body.Instructions.Remove(instruction);
        while (scratch.Body.Variables.Count > receipt.OriginalLocals) scratch.Body.Variables.RemoveAt(scratch.Body.Variables.Count - 1);
        scratch.Body.MaxStackSize = receipt.OriginalMaxStack;
        Require(Body(before) == Body(scratch) && QualifiedBody(before) == QualifiedBody(scratch), "exact qualified inverse failed: " + receipt.Method);
        return new { receipt.Method, restoredInstructions = source.Length, restoredLocals = receipt.OriginalLocals, restoredHandlers = receipt.OriginalHandlers, restoredCalls = receipt.Calls.Length, originalReturnPrologues = receipt.ReturnIndices.Length, exact = true };
    }

    static object WrapperInventory(ModuleDefinition original, ModuleDefinition candidate, RenderPatcher.Receipt[] receipts)
    {
        var helper = Types(candidate).Single(t => t.FullName == RenderPatcher.HelperName);
        var wrappers = helper.Methods.Where(m => WrapperNames.Contains(m.Name)).ToArray();
        Require(wrappers.Select(m => m.Name).SequenceEqual(WrapperNames) && receipts.Sum(r => r.Calls.Length) == 4 && receipts.Take(5).All(r => r.Calls.Length == 0) && receipts.Skip(5).All(r => r.Calls.Length == 2), "batch wrapper/callsite completeness failed");
        var rows = new List<object>();
        foreach (var wrapper in wrappers) {
            var references = receipts.SelectMany(r => r.Calls.Select(c => (r.Method, Call: c))).Where(x => x.Call.Wrapper == wrapper.FullName).ToArray();
            Require(references.Length == (wrapper.Name == "BatchSubmit002" ? 2 : 1) && references.Select(x => x.Call.Target + x.Call.Opcode).Distinct().Count() == 1, "wrapper unused or merges incompatible call signatures");
            var source = Program.Method(original, references[0].Method).Body.Instructions[references[0].Call.OriginalIndex]; var target = (MethodReference)source.Operand;
            Require(target.HasThis && source.OpCode == OpCodes.Callvirt && !wrapper.HasThis && wrapper.IsStatic && !wrapper.HasGenericParameters && TypeIdentity(wrapper.ReturnType) == TypeIdentity(RenderPatcher.ClosedType(target.ReturnType, target)), "wrapper receiver/return signature changed");
            var arguments = new[] { target.DeclaringType }.Concat(target.Parameters.Select(p => RenderPatcher.ClosedType(p.ParameterType, target))).ToArray();
            Require(wrapper.Parameters.Select(p => TypeIdentity(p.ParameterType)).SequenceEqual(arguments.Select(TypeIdentity)) && wrapper.Parameters.Count == (wrapper.Name == "BatchUpload000" ? 5 : 7), "wrapper closed value/ref/array signature changed");
            if (wrapper.Name.StartsWith("BatchUpload", StringComparison.Ordinal)) {
                Require(target is GenericInstanceMethod g && g.GenericArguments.Count == 1 && g.GenericArguments[0].FullName == "Microsoft.Xna.Framework.Graphics.VertexPositionColorTexture" && target.Name == "SetData" && target.DeclaringType.FullName == "Microsoft.Xna.Framework.Graphics.DynamicVertexBuffer", "wrong closed SetData target");
                Require(wrapper.Parameters.Count(p => p.ParameterType is ArrayType a && a.IsVector && a.ElementType.FullName == "Microsoft.Xna.Framework.Graphics.VertexPositionColorTexture") == 1, "wrapper must bind the original concrete vertex array");
            } else Require(target.Name == "DrawIndexedPrimitives" && target.DeclaringType.FullName == "Microsoft.Xna.Framework.Graphics.GraphicsDevice", "wrong indexed-submit target");
            var calls = wrapper.Body.Instructions.Where(i => i.Operand is MethodReference).ToArray();
            var external = calls.Where(i => ((MethodReference)i.Operand).DeclaringType.FullName != RenderPatcher.HelperName).ToArray();
            Require(external.Length == 1 && external[0].OpCode == OpCodes.Callvirt && MemberIdentity((MethodReference)external[0].Operand) == MemberIdentity(target), "wrapper must retain exactly one original qualified callvirt");
            int at = wrapper.Body.Instructions.IndexOf(external[0]);
            Require(at >= wrapper.Parameters.Count, "wrapper argument block truncated");
            for (int n = 0; n < wrapper.Parameters.Count; n++) Require(ArgumentIndex(wrapper, wrapper.Body.Instructions[at - wrapper.Parameters.Count + n]) == n, "wrapper receiver/argument order changed");
            var begin = calls.Single(i => ((MethodReference)i.Operand).Name == "BatchBegin"); var end = calls.Single(i => ((MethodReference)i.Operand).Name == "BatchEnd");
            Require(calls.Length == 3 && begin.OpCode == OpCodes.Call && end.OpCode == OpCodes.Call && Integer(begin.Previous) == (wrapper.Name == "BatchSubmit002" ? 2 : 1), "wrapper scope/hook changed");
            Require(wrapper.Body.ExceptionHandlers.Count == 1 && wrapper.Body.ExceptionHandlers[0].HandlerType == ExceptionHandlerType.Finally && wrapper.Body.Variables.Count == 2 && wrapper.Body.Variables[0].VariableType.MetadataType == MetadataType.Int32 && wrapper.Body.Variables[1].VariableType.MetadataType == MetadataType.Boolean, "wrapper cookie/completion finally changed");
            rows.Add(new { wrapper = wrapper.FullName, qualifiedOriginal = MemberIdentity(target), opcode = source.OpCode.Name, callsites = references.Select(x => new { method = x.Method, originalInstruction = x.Call.OriginalIndex }), explicitReceiver = true, originalCallsPerWrapper = 1, closedArrayWithoutCopies = true });
        }
        return new { wrappers = wrappers.Length, originalGameCallsites = 4, originalGameCallers = 2, exactGeneratedCodeCheckedBeforeInverse = true, rows };
    }

    static int ArgumentIndex(MethodDefinition method, Instruction instruction) => instruction.OpCode.Code switch {
        Code.Ldarg_0 => 0, Code.Ldarg_1 => 1, Code.Ldarg_2 => 2, Code.Ldarg_3 => 3,
        Code.Ldarg or Code.Ldarg_S when instruction.Operand is ParameterDefinition p => p.Index + (method.HasThis ? 1 : 0), _ => -1
    };
    static int? Integer(Instruction instruction) => instruction.OpCode.Code switch {
        Code.Ldc_I4_M1 => -1, Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2, Code.Ldc_I4_3 => 3, Code.Ldc_I4_4 => 4, Code.Ldc_I4_5 => 5, Code.Ldc_I4_6 => 6, Code.Ldc_I4_7 => 7, Code.Ldc_I4_8 => 8,
        Code.Ldc_I4 => (int)instruction.Operand, Code.Ldc_I4_S => (sbyte)instruction.Operand, _ => null
    };

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
        _ => type.MetadataType + ":value=" + type.IsValueType + ":" + type.FullName + "@" + (type.DeclaringType == null ? Scope(type.Scope) : TypeIdentity(type.DeclaringType))
    };
    static string Signature(IMethodSignature method) => method.CallingConvention + ":" + method.HasThis + ":" + method.ExplicitThis + ":" + TypeIdentity(method.ReturnType) + "(" + string.Join(',', method.Parameters.Select(p => TypeIdentity(p.ParameterType))) + ")";
    static string DefinitionOwner(TypeDefinition type) => type.DeclaringType == null ? "type:" + type.Module.Types.IndexOf(type) : DefinitionOwner(type.DeclaringType) + ":nested:" + type.DeclaringType.NestedTypes.IndexOf(type);
    internal static string MemberIdentity(MemberReference member) => member switch {
        TypeReference type => TypeIdentity(type), GenericInstanceMethod g => "method-spec:" + MemberIdentity(g.ElementMethod) + "<" + string.Join(',', g.GenericArguments.Select(TypeIdentity)) + ">",
        MethodDefinition m => DefinitionOwner(m.DeclaringType) + ":method:" + m.DeclaringType.Methods.IndexOf(m) + ":" + TypeIdentity(m.DeclaringType) + "::" + m.Name + "``" + m.GenericParameters.Count + ":" + Signature(m),
        FieldDefinition f => DefinitionOwner(f.DeclaringType) + ":field:" + f.DeclaringType.Fields.IndexOf(f) + ":" + TypeIdentity(f.DeclaringType) + "::" + f.Name + ":" + TypeIdentity(f.FieldType),
        MethodReference m => TypeIdentity(m.DeclaringType) + "::" + m.Name + "``" + m.GenericParameters.Count + ":" + Signature(m),
        FieldReference f => TypeIdentity(f.DeclaringType) + "::" + f.Name + ":" + TypeIdentity(f.FieldType), _ => throw new InvalidDataException("unknown member")
    };
    internal static string QualifiedBody(MethodDefinition method)
    {
        if (!method.HasBody) return "none";
        var indices = method.Body.Instructions.Select((i, n) => (i, n)).ToDictionary(x => x.i, x => x.n);
        string Operand(object? value) => value switch {
            null => "", Instruction i => "@" + indices[i], Instruction[] a => string.Join(',', a.Select(i => "@" + indices[i])),
            VariableDefinition v => "local:" + v.Index, ParameterDefinition p => "arg:" + p.Index,
            MemberReference r => MemberIdentity(r), CallSite c => "call-site:" + Signature(c),
            float n => BitConverter.SingleToInt32Bits(n).ToString("x8", CultureInfo.InvariantCulture), double n => BitConverter.DoubleToInt64Bits(n).ToString("x16", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
        };
        string At(Instruction? i) => i == null ? "end" : "@" + indices[i];
        return method.Body.InitLocals + ":" + method.Body.MaxStackSize + "\n" + string.Join('\n', method.Body.Variables.Select(v => TypeIdentity(v.VariableType))) + "\n" +
            string.Join('\n', method.Body.Instructions.Select(i => i.OpCode.Name + " " + Operand(i.Operand))) + "\n" +
            string.Join('\n', method.Body.ExceptionHandlers.Select(h => h.HandlerType + ":" + At(h.TryStart) + ":" + At(h.TryEnd) + ":" + At(h.HandlerStart) + ":" + At(h.HandlerEnd) + ":" + At(h.FilterStart) + ":" + TypeIdentity(h.CatchType)));
    }

    static string Constant(bool present, object? value) => !present ? "absent" : value switch {
        null => "null", byte[] bytes => "bytes:" + Convert.ToHexString(bytes),
        float n => "single:" + BitConverter.SingleToInt32Bits(n).ToString("x8", CultureInfo.InvariantCulture), double n => "double:" + BitConverter.DoubleToInt64Bits(n).ToString("x16", CultureInfo.InvariantCulture),
        _ => value.GetType().FullName + ":" + Convert.ToString(value, CultureInfo.InvariantCulture)
    };
    static string Marshal(MarshalInfo? info) => info == null ? "none" : info.GetType().FullName + ":" + string.Join(';', info.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
        .Where(p => p.GetIndexParameters().Length == 0).OrderBy(p => p.Name, StringComparer.Ordinal)
        .Select(p => p.Name + "=" + (p.GetValue(info) is TypeReference type ? TypeIdentity(type) : Constant(true, p.GetValue(info)))));

    static Dictionary<string, string> QualifiedUses(ModuleDefinition module, bool includeBodies = true)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        void Attr(string key, Mono.Cecil.ICustomAttributeProvider owner) => rows.Add(key + ":attributes", string.Join('\n', owner.CustomAttributes.Select(a => MemberIdentity(a.Constructor) + ":" + Convert.ToHexString(a.GetBlob()))));
        void Generic(string key, IEnumerable<GenericParameter> parameters) {
            foreach (var p in parameters) {
                string id = key + ":generic:" + p.Position; Attr(id, p);
                rows.Add(id, p.Name + ":" + p.Attributes + ":" + string.Join(',', p.Constraints.Select(c => TypeIdentity(c.ConstraintType))));
                for (int n = 0; n < p.Constraints.Count; n++) Attr(id + ":constraint:" + n, p.Constraints[n]);
            }
        }
        Attr("assembly", module.Assembly); Attr("module", module);
        rows.Add("assembly:identity", module.Assembly.Name.FullName + ":" + module.Assembly.Name.Attributes + ":" + module.Assembly.Name.HashAlgorithm + ":" + Convert.ToHexString(module.Assembly.Name.PublicKey) + ":" + Meta("Security", module.Assembly));
        rows.Add("module:identity", module.Name + ":" + module.Kind + ":" + module.Architecture + ":" + module.Attributes + ":" + module.RuntimeVersion + ":" + module.Characteristics + ":" + module.EntryPoint?.FullName);
        for (int n = 0; n < module.AssemblyReferences.Count; n++) { var r = module.AssemblyReferences[n]; rows.Add("assembly-reference:" + n, r.FullName + ":" + r.Attributes + ":" + r.HashAlgorithm + ":" + Convert.ToHexString(r.PublicKey) + ":" + Convert.ToHexString(r.Hash)); }
        for (int n = 0; n < module.ModuleReferences.Count; n++) rows.Add("module-reference:" + n, module.ModuleReferences[n].Name);
        for (int n = 0; n < module.ExportedTypes.Count; n++) { var e = module.ExportedTypes[n]; rows.Add("exported:" + n, e.FullName + ":" + e.Attributes + ":" + e.Identifier + ":" + Scope(e.Scope) + ":" + e.DeclaringType?.FullName); }
        for (int n = 0; n < module.Resources.Count; n++) rows.Add("resource:" + n, Meta("ResourceMetadata", module.Resources[n]));
        void Visit(TypeDefinition type, string key) {
            Attr(key, type); Generic(key, type.GenericParameters);
            rows.Add(key, TypeIdentity(type) + ":" + TypeIdentity(type.BaseType) + ":" + Meta("TypeMetadata", type));
            for (int n = 0; n < type.Interfaces.Count; n++) { Attr(key + ":interface:" + n, type.Interfaces[n]); rows.Add(key + ":interface-type:" + n, TypeIdentity(type.Interfaces[n].InterfaceType)); }
            for (int n = 0; n < type.Fields.Count; n++) { var f = type.Fields[n]; string id = key + ":field:" + n; rows.Add(id, MemberIdentity(f) + ":" + Meta("FieldMetadata", f)); Attr(id, f); rows.Add(id + ":constant-marshal", Constant(f.HasConstant, f.Constant) + ":" + Marshal(f.HasMarshalInfo ? f.MarshalInfo : null)); }
            string Accessor(MethodDefinition? m) => m == null ? "none" : type.Methods.IndexOf(m) + ":" + MemberIdentity(m);
            for (int n = 0; n < type.Properties.Count; n++) {
                var p = type.Properties[n]; string id = key + ":property:" + n; Attr(id, p);
                rows.Add(id, p.Name + ":" + p.Attributes + ":" + TypeIdentity(p.PropertyType) + ":" + string.Join(',', p.Parameters.Select(a => TypeIdentity(a.ParameterType))) + ":" + Accessor(p.GetMethod) + ":" + Accessor(p.SetMethod) + ":" + string.Join(',', p.OtherMethods.Select(Accessor)));
                rows.Add(id + ":constant", Constant(p.HasConstant, p.Constant));
                for (int j = 0; j < p.Parameters.Count; j++) { var parameter = p.Parameters[j]; Attr(id + ":arg:" + j, parameter); rows.Add(id + ":arg-details:" + j, parameter.Name + ":" + parameter.Attributes + ":" + Constant(parameter.HasConstant, parameter.Constant) + ":" + Marshal(parameter.HasMarshalInfo ? parameter.MarshalInfo : null)); }
            }
            for (int n = 0; n < type.Events.Count; n++) { var e = type.Events[n]; string id = key + ":event:" + n; Attr(id, e); rows.Add(id, e.Name + ":" + e.Attributes + ":" + TypeIdentity(e.EventType) + ":" + Accessor(e.AddMethod) + ":" + Accessor(e.RemoveMethod) + ":" + Accessor(e.InvokeMethod) + ":" + string.Join(',', e.OtherMethods.Select(Accessor))); }
            // Owner path + member order, never FullName alone: accepted assemblies
            // may contain distinct MethodDefs with the very same display signature.
            for (int n = 0; n < type.Methods.Count; n++) {
                var m = type.Methods[n]; string id = key + ":method:" + n;
                rows.Add(id, MemberIdentity(m) + ":" + Meta("MethodMetadata", m)); Attr(id, m); Attr(id + ":return", m.MethodReturnType); Generic(id, m.GenericParameters);
                rows.Add(id + ":overrides", string.Join('\n', m.Overrides.Select(MemberIdentity)));
                rows.Add(id + ":return-details", m.MethodReturnType.Attributes + ":" + Constant(m.MethodReturnType.HasConstant, m.MethodReturnType.Constant) + ":" + Marshal(m.MethodReturnType.HasMarshalInfo ? m.MethodReturnType.MarshalInfo : null));
                for (int j = 0; j < m.Parameters.Count; j++) { var p = m.Parameters[j]; Attr(id + ":arg:" + j, p); rows.Add(id + ":arg-details:" + j, p.Name + ":" + p.Attributes + ":" + Constant(p.HasConstant, p.Constant) + ":" + Marshal(p.HasMarshalInfo ? p.MarshalInfo : null)); }
                if (includeBodies) rows.Add(id + ":body", QualifiedBody(m));
            }
            for (int n = 0; n < type.NestedTypes.Count; n++) Visit(type.NestedTypes[n], key + ":nested:" + n);
        }
        for (int n = 0; n < module.Types.Count; n++) Visit(module.Types[n], "type:" + n);
        return rows;
    }

    static object CompareQualified(ModuleDefinition expected, ModuleDefinition candidate)
    {
        Require(expected.GetTypeReferences().Select(TypeIdentity).Order(StringComparer.Ordinal).SequenceEqual(candidate.GetTypeReferences().Select(TypeIdentity).Order(StringComparer.Ordinal)), "qualified TypeRef scope/count changed");
        Require(expected.GetMemberReferences().Select(MemberIdentity).Order(StringComparer.Ordinal).SequenceEqual(candidate.GetMemberReferences().Select(MemberIdentity).Order(StringComparer.Ordinal)), "qualified MemberRef binding/count changed");
        var before = QualifiedUses(expected); var after = QualifiedUses(candidate);
        Require(before.Count == after.Count, "qualified metadata/use-site count changed");
        foreach (var (key, value) in before) Require(after.TryGetValue(key, out var actual) && actual == value, "qualified candidate differs at " + key);
        return new { passed = true, qualifiedUseSiteRows = before.Count, exactGeneratedHelpersWrappersAndMaxStack = true, beforeAnyFixtureRemapping = true, duplicateFullNamesMatchedByOwnerAndOrder = true, referenceTableOrderIndependent = true, selfModuleMvidIntentionallyExcluded = true };
    }

    internal static object Signatures(byte[] bytes, HashSet<string> changed, string inputPath)
    {
        using var stream = new MemoryStream(bytes); using var pe = new PEReader(stream); var reader = pe.GetMetadataReader();
        using var resolver = RenderPatcher.Resolver(inputPath);
        using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes), new ReaderParameters { AssemblyResolver = resolver });
        var decoder = new RenderRawSignatures.NamedKindDecoder(game.MainModule);
        var methods = Types(game.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody && (RenderPatcher.IsAdded(m.DeclaringType) || changed.Contains(m.FullName))).ToArray();
        string Sig(MethodSignature<string> sig) => sig.Header + ":" + sig.GenericParameterCount + ":" + sig.ReturnType + "(" + string.Join(',', sig.ParameterTypes) + ")";
        var rows = new List<object>();
        foreach (var method in methods) {
            var d = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle((int)method.MetadataToken.RID));
            rows.Add(new { kind = "MethodDef", name = method.FullName, token = method.MetadataToken.ToInt32(), raw = Convert.ToHexString(reader.GetBlobBytes(d.Signature)), decoded = Sig(d.DecodeSignature(decoder, (object?)null)) });
            var body = pe.GetMethodBody(d.RelativeVirtualAddress);
            if (!body.LocalSignature.IsNil) { var l = reader.GetStandaloneSignature(body.LocalSignature); rows.Add(new { kind = "Locals", name = method.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(l.Signature)), decoded = string.Join(',', l.DecodeLocalSignature(decoder, (object?)null)) }); }
        }
        foreach (var field in Types(game.MainModule).Where(RenderPatcher.IsAdded).SelectMany(t => t.Fields)) {
            var d = reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle((int)field.MetadataToken.RID)); rows.Add(new { kind = "FieldDef", name = field.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(d.Signature)), decoded = d.DecodeSignature(decoder, (object?)null) });
        }
        var visited = new HashSet<int>();
        void DecodeReference(MemberReference reference) {
            if (!visited.Add(reference.MetadataToken.ToInt32())) return;
            if (reference.MetadataToken.TokenType == TokenType.MethodSpec) {
                var d = reader.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle((int)reference.MetadataToken.RID));
                rows.Add(new { kind = "MethodSpec", name = reference.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(d.Signature)), decoded = string.Join(',', d.DecodeSignature(decoder, (object?)null)) });
                Require(reference is GenericInstanceMethod, "raw method-spec not a closed generic method");
                DecodeReference(((GenericInstanceMethod)reference).ElementMethod);
            } else if (reference.MetadataToken.TokenType == TokenType.MemberRef) {
                var d = reader.GetMemberReference(MetadataTokens.MemberReferenceHandle((int)reference.MetadataToken.RID));
                rows.Add(new { kind = "MemberRef", name = reference.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(d.Signature)), decoded = d.GetKind() == MemberReferenceKind.Method ? Sig(d.DecodeMethodSignature(decoder, (object?)null)) : d.DecodeFieldSignature(decoder, (object?)null) });
                if (d.Parent.Kind == HandleKind.TypeSpecification) {
                    var t = reader.GetTypeSpecification((TypeSpecificationHandle)d.Parent);
                    rows.Add(new { kind = "TypeSpec", name = reference.DeclaringType.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(t.Signature)), decoded = t.DecodeSignature(decoder, (object?)null) });
                }
            } else if (reference.MetadataToken.TokenType == TokenType.TypeSpec) {
                var t = reader.GetTypeSpecification(MetadataTokens.TypeSpecificationHandle((int)reference.MetadataToken.RID));
                rows.Add(new { kind = "TypeSpec", name = reference.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(t.Signature)), decoded = t.DecodeSignature(decoder, (object?)null) });
            }
        }
        foreach (var reference in methods.SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MemberReference>()) DecodeReference(reference);
        foreach (var wrapper in methods.Where(m => WrapperNames.Take(2).Contains(m.Name))) Require(wrapper.Body.Instructions.Any(i => i.Operand is GenericInstanceMethod g && visited.Contains(g.MetadataToken.ToInt32()) && visited.Contains(g.ElementMethod.MetadataToken.ToInt32())), "closed upload MethodSpec/MemberRef not raw-audited");
        return new { passed = true, independentlyDecodedRawMetadata = true, primitiveClassOrValueTypeAliasesAllowed = false, closedGenericMethodSpecsAndElementMemberRefsAudited = true, rows };
    }

    static object Sdk(ModuleDefinition candidate, ModuleDefinition original)
    {
        Require(Sha(File.ReadAllBytes(CorePath)) == Program.CoreHash, "pinned SDK CoreLib hash mismatch");
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal) { [CorePath] = Program.CoreHash };
        string ImageHash(string path) { if (string.IsNullOrEmpty(path)) return Program.InputHash; if (!hashes.TryGetValue(path, out var hash)) hashes.Add(path, hash = Sha(File.ReadAllBytes(path))); return hash; }
        var typeRows = new List<object>(); var checkedTypes = new HashSet<string>(StringComparer.Ordinal);
        void CheckType(TypeReference? type) {
            if (type == null || type is GenericParameter || !checkedTypes.Add(TypeIdentity(type))) return;
            if (type is GenericInstanceType generic) foreach (var argument in generic.GenericArguments) CheckType(argument);
            if (type is OptionalModifierType optional) CheckType(optional.ModifierType);
            if (type is RequiredModifierType required) CheckType(required.ModifierType);
            if (type is FunctionPointerType function) { CheckType(function.ReturnType); foreach (var parameter in function.Parameters) CheckType(parameter.ParameterType); return; }
            if (type is TypeSpecification specification) { CheckType(specification.ElementType); return; }
            var resolved = type.Resolve(); Require(resolved != null, "unresolved generated type: " + TypeIdentity(type));
            if (RenderPatcher.IsAdded(resolved!)) return;
            var owner = resolved!.Module;
            if (type.Namespace.StartsWith("System", StringComparison.Ordinal)) Require(owner.FileName.StartsWith("/mono-nx/", StringComparison.Ordinal), "host BCL type fallback detected");
            if (owner.Assembly.Name.Name is "FNA" or "ReLogic") Require(ImageHash(owner.FileName) == (owner.Assembly.Name.Name == "FNA" ? Program.FnaHash : Program.ReLogicHash), "resolved generated type differs from accepted52 pair");
            typeRows.Add(new { type = TypeIdentity(type), resolved = resolved.FullName, image = owner.FileName, sha256 = ImageHash(owner.FileName) });
        }
        foreach (var type in Types(candidate).Where(RenderPatcher.IsAdded)) {
            CheckType(type.BaseType);
            foreach (var field in type.Fields) CheckType(field.FieldType);
            foreach (var method in type.Methods) {
                CheckType(method.ReturnType); foreach (var parameter in method.Parameters) CheckType(parameter.ParameterType);
                if (!method.HasBody) continue;
                foreach (var variable in method.Body.Variables) CheckType(variable.VariableType);
                foreach (var handler in method.Body.ExceptionHandlers) CheckType(handler.CatchType);
                foreach (var operand in method.Body.Instructions.Select(i => i.Operand)) {
                    if (operand is TypeReference t) CheckType(t);
                    else if (operand is MemberReference member) {
                        CheckType(member.DeclaringType);
                        if (member is FieldReference field) CheckType(field.FieldType);
                        if (member is MethodReference callee) { CheckType(callee.ReturnType); foreach (var parameter in callee.Parameters) CheckType(parameter.ParameterType); if (callee is GenericInstanceMethod closed) foreach (var argument in closed.GenericArguments) CheckType(argument); }
                    }
                }
            }
        }
        var previous = original.GetMemberReferences().Select(MemberIdentity).ToHashSet(StringComparer.Ordinal);
        var members = Types(candidate).Where(RenderPatcher.IsAdded).SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MemberReference>().Where(r => r is MethodReference or FieldReference).DistinctBy(MemberIdentity).ToArray();
        var rows = new List<object>();
        foreach (var reference in members) {
            if (reference.DeclaringType.FullName == RenderPatcher.HelperName || reference.DeclaringType.DeclaringType?.FullName == RenderPatcher.HelperName) continue;
            IMemberDefinition? resolved = reference switch { MethodReference m => m.Resolve(), FieldReference f => f.Resolve(), _ => null };
            Require(resolved != null, "unresolved added member: " + MemberIdentity(reference));
            Require(resolved is MethodDefinition { IsPublic: true } or FieldDefinition { IsPublic: true } || resolved!.DeclaringType.Module.Assembly.Name.Name == candidate.Assembly.Name.Name && (resolved is MethodDefinition { IsAssembly: true } or FieldDefinition { IsAssembly: true }), "added reference inaccessible: " + reference.FullName);
            var owner = resolved!.DeclaringType.Module;
            if (reference.DeclaringType.Namespace.StartsWith("System", StringComparison.Ordinal)) Require(owner.FileName.StartsWith("/mono-nx/", StringComparison.Ordinal), "host BCL fallback detected");
            if (owner.Assembly.Name.Name is "FNA" or "ReLogic") Require(ImageHash(owner.FileName) == (owner.Assembly.Name.Name == "FNA" ? Program.FnaHash : Program.ReLogicHash), "resolved framework differs from accepted52 pair");
            rows.Add(new { reference = reference.FullName, qualified = MemberIdentity(reference), resolved = resolved.FullName, image = owner.FileName, sha256 = ImageHash(owner.FileName), newReference = !previous.Contains(MemberIdentity(reference)) });
        }
        return new { passed = true, hostFallbackAllowed = false, coreSha256 = Program.CoreHash, typeRows, rows };
    }

    internal static object NegativeProof(byte[] originalBytes, byte[] candidateBytes, RenderPatcher.Receipt[] receipts, string inputPath, string output)
    {
        Directory.CreateDirectory(output);
        Verify(originalBytes, candidateBytes, receipts, inputPath);
        MethodDefinition Helper(ModuleDefinition m, string name) => Types(m).Single(t => t.FullName == RenderPatcher.HelperName).Methods.Single(x => x.Name == name);
        Instruction Hook(MethodDefinition m, string name) => m.Body.Instructions.First(i => i.Operand is MethodReference r && r.DeclaringType.FullName == RenderPatcher.HelperName && r.Name == name);
        var cases = new (string Name, Action<ModuleDefinition> Mutate)[] {
            ("wrong-vector-scope", m => m.GetTypeReferences().First(t => t.FullName == "Microsoft.Xna.Framework.Vector2").Scope = m.AssemblyReferences.Single(r => r.Name == "mscorlib")),
            ("existing52-hint-guard", m => { var target = Types(m).SelectMany(t => t.Methods).Single(x => x.Name == "_NXHint52CanSkip"); var i = target.Body.Instructions.Single(i => Integer(i) == 256); i.OpCode = OpCodes.Ldc_I4; i.Operand = 257; }),
            ("existing50-relocated-gate", m => {
                using var original = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes));
                var source = Program.Method(original.MainModule, Program.SingleName); var branch = source.Body.Instructions.Single(i => i.Offset == 0x0715);
                Require(branch.OpCode == OpCodes.Brfalse, "negative relocated gate boundary changed");
                var receipt = receipts.Single(r => r.Method == Program.SingleName); Program.Method(m, Program.SingleName).Body.Instructions[receipt.OriginalIndices[source.Body.Instructions.IndexOf(branch)]].OpCode = OpCodes.Brtrue;
            }),
            ("wrong-batch-scope", m => { var i = Hook(Helper(m, "BatchUpload000"), "BatchBegin").Previous; Require(Integer(i) == 1, "negative batch scope boundary changed"); i.OpCode = OpCodes.Ldc_I4; i.Operand = 2; }),
            ("wrong-tile-hook-id", m => { var i = Hook(Program.Method(m, Program.SingleName), "BeginOp").Previous; Require(Integer(i).HasValue, "negative tile hook boundary changed"); i.OpCode = OpCodes.Ldc_I4; i.Operand = 99; }),
            ("equivalent-runtime-binding", m => Hook(Program.Method(m, Program.SingleName), "BeginOp").Operand = Helper(m, "EndOp")),
            ("batch-return-corruption", m => { var target = Program.Method(m, Program.BatchEndName); var load = target.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret).Previous; Require(load.OpCode.Code is Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3, "negative batch return boundary changed"); load.OpCode = OpCodes.Ldc_I4; load.Operand = -731; }),
            ("wrapper-callvirt-mutation", m => { var i = Helper(m, "BatchUpload000").Body.Instructions.Single(i => i.OpCode == OpCodes.Callvirt); i.OpCode = OpCodes.Call; }),
            ("wrapper-argument-order", m => {
                var w = Helper(m, "BatchSubmit002"); var call = w.Body.Instructions.Single(i => i.OpCode == OpCodes.Callvirt); int at = w.Body.Instructions.IndexOf(call) - w.Parameters.Count;
                Require(TypeIdentity(w.Parameters[2].ParameterType) == TypeIdentity(w.Parameters[3].ParameterType), "negative integer argument boundary changed");
                var a = w.Body.Instructions[at + 2]; var b = w.Body.Instructions[at + 3]; (a.OpCode, b.OpCode) = (b.OpCode, a.OpCode); (a.Operand, b.Operand) = (b.Operand, a.Operand);
            }),
            ("wrapper-closed-generic-scope", m => { var g = (GenericInstanceMethod)Helper(m, "BatchUpload000").Body.Instructions.Single(i => i.OpCode == OpCodes.Callvirt).Operand; var t = g.GenericArguments[0]; g.GenericArguments[0] = new TypeReference(t.Namespace, t.Name, m, m.AssemblyReferences.Single(r => r.Name == "ReLogic"), t.IsValueType); }),
            ("extra-helper-method", m => { var h = Types(m).Single(t => t.FullName == RenderPatcher.HelperName); var extra = new MethodDefinition("UnplannedObserver", Mono.Cecil.MethodAttributes.Private | Mono.Cecil.MethodAttributes.Static, m.TypeSystem.Void); extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); h.Methods.Add(extra); }),
            ("unrelated-original-body", m => { var method = Types(m).Where(t => !RenderPatcher.IsAdded(t)).SelectMany(t => t.Methods).First(x => x.HasBody && !Program.ChangedNames.Contains(x.FullName) && x.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4)); var i = method.Body.Instructions.First(i => i.OpCode == OpCodes.Ldc_I4); i.Operand = unchecked((int)i.Operand + 1); }),
            ("original-member-order", m => { var t = Program.Method(m, Program.BatchEndName).DeclaringType; (t.Methods[0], t.Methods[1]) = (t.Methods[1], t.Methods[0]); }),
            ("extra-assembly-reference", m => m.AssemblyReferences.Add(new AssemblyNameReference("UnplannedRenderDependency", new Version(1, 0))))
        };
        var rows = new List<object>();
        foreach (var test in cases) {
            using var resolver = RenderPatcher.Resolver(inputPath);
            using var changed = AssemblyDefinition.ReadAssembly(new MemoryStream(candidateBytes), new ReaderParameters { AssemblyResolver = resolver });
            test.Mutate(changed.MainModule); byte[] bytes = Serialize(changed); string path = Path.Combine(output, test.Name + ".exe");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes);
            string? rejection = null;
            try { Verify(originalBytes, bytes, receipts, inputPath); } catch (InvalidDataException error) { rejection = error.Message; }
            Require(rejection != null && rejection.StartsWith("qualified ", StringComparison.Ordinal), "mutant not rejected before inverse/fixture mapping: " + test.Name);
            rows.Add(new { test.Name, candidatePath = path, sha256 = Sha(bytes), rejection, beforeFixtureRemapping = true, executionEquivalenceNotClaimed = true });
        }
        var badReceipts = JsonSerializer.Deserialize<RenderPatcher.Receipt[]>(JsonSerializer.Serialize(receipts))!;
        badReceipts[0].OriginalIndices[0]++;
        string receiptPath = Path.Combine(output, "tampered-receipts.json"); Json(receiptPath, badReceipts);
        string? receiptRejection = null;
        try { Verify(originalBytes, candidateBytes, badReceipts, inputPath); } catch (InvalidDataException error) { receiptRejection = error.Message; }
        Require(receiptRejection == "render53 splice receipts differ from pinned plan", "tampered receipts reached inverse stage");
        var supportRaw = Invoke("RawSignatures", "VerifyMalformedRejection", output)!;
        var rawRows = new List<object>();
        foreach (string kind in new[] { "wrapper-parameter", "closed-method-spec", "generic-element-member-ref" }) {
            using var resolver = RenderPatcher.Resolver(inputPath);
            using var changed = AssemblyDefinition.ReadAssembly(new MemoryStream(candidateBytes), new ReaderParameters { AssemblyResolver = resolver });
            var m = changed.MainModule; var wrapper = Helper(m, "BatchUpload000");
            var g = (GenericInstanceMethod)wrapper.Body.Instructions.Single(i => i.OpCode == OpCodes.Callvirt).Operand;
            TypeReference Alias(string name, bool valueType) => new("System", name, m, m.TypeSystem.CoreLibrary, valueType);
            if (kind == "wrapper-parameter") wrapper.Parameters.First(p => p.ParameterType.MetadataType == MetadataType.Int32).ParameterType = Alias("Int32", false);
            else if (kind == "closed-method-spec") g.GenericArguments[0] = Alias("Int32", true);
            else g.ElementMethod.ReturnType = Alias("Void", false);
            byte[] bytes = Serialize(changed); string path = Path.Combine(output, "raw-" + kind + ".exe");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes);
            string? rejection = null;
            try { Signatures(bytes, Program.ChangedNames.ToHashSet(StringComparer.Ordinal), inputPath); } catch (InvalidDataException error) { rejection = error.Message; }
            Require(rejection != null && rejection.StartsWith("invalid raw primitive encoding:", StringComparison.Ordinal), "raw alias escaped targeted signature decoder: " + kind);
            rawRows.Add(new { kind, candidatePath = path, sha256 = Sha(bytes), rejection, beforeFixtureRemapping = true });
        }
        var namedKinds = NamedKindNegatives(candidateBytes, inputPath, output);
        var result = new { passed = true, candidateSha256 = Sha(candidateBytes), rejectedCandidates = rows, receiptTampering = new { receiptPath, rejection = receiptRejection, beforeFixtureRemapping = true }, malformedRawPrimitive = supportRaw, actualCandidateRawAliases = rawRows, namedKinds };
        Json(Path.Combine(output, "static-negative-proof.json"), result); return result;
    }

    static object NamedKindNegatives(byte[] candidateBytes, string inputPath, string output)
    {
        var cases = new[] { "wrapper-parameter-kind", "wrapper-array-kind", "method-spec-kind", "element-member-kind", "helper-field-kind", "local-kind", "original-parameter-kind", "original-field-kind" };
        var rows = new List<object>();
        foreach (string name in cases) {
            using var resolver = RenderPatcher.Resolver(inputPath);
            using var original = AssemblyDefinition.ReadAssembly(new MemoryStream(candidateBytes), new ReaderParameters { AssemblyResolver = resolver });
            using var changed = AssemblyDefinition.ReadAssembly(new MemoryStream(candidateBytes), new ReaderParameters { AssemblyResolver = resolver });
            var module = changed.MainModule;
            var helper = Types(module).Single(t => t.FullName == RenderPatcher.HelperName);
            var wrapper = helper.Methods.Single(m => m.Name == "BatchUpload000");
            var method = (GenericInstanceMethod)wrapper.Body.Instructions.Single(i => i.OpCode == OpCodes.Callvirt).Operand;
            TypeReference AsClass(TypeReference type) {
                Require(type.IsValueType && type.MetadataType == MetadataType.ValueType && type is not TypeSpecification, "named-kind fixture requires named value type");
                return new TypeReference(type.Namespace, type.Name, module, type.Scope, false) { DeclaringType = type.DeclaringType };
            }
            byte[]? directBytes = null;
            if (name == "wrapper-parameter-kind") { var p = wrapper.Parameters.Single(p => p.ParameterType.FullName == "Microsoft.Xna.Framework.Graphics.SetDataOptions"); p.ParameterType = AsClass(p.ParameterType); }
            else if (name == "wrapper-array-kind") { var p = wrapper.Parameters.Single(p => p.ParameterType is ArrayType); p.ParameterType = new ArrayType(AsClass(((ArrayType)p.ParameterType).ElementType)); }
            else if (name == "method-spec-kind") method.GenericArguments[0] = AsClass(method.GenericArguments[0]);
            else if (name == "element-member-kind") { var p = method.ElementMethod.Parameters.Single(p => p.ParameterType.FullName == "Microsoft.Xna.Framework.Graphics.SetDataOptions"); p.ParameterType = AsClass(p.ParameterType); }
            else if (name == "helper-field-kind") { var f = helper.Fields.Single(f => f.Name == "WindowBatch"); directBytes = RenderRawSignatures.FlipKindForProof(candidateBytes, f.MetadataToken.ToInt32(), f.FieldType.MetadataToken.ToInt32(), false); }
            else if (name == "local-kind") { var owner = Program.Draw(module); var local = owner.Body.Variables.Single(v => v.VariableType.FullName.EndsWith("/Render53Pass", StringComparison.Ordinal)); directBytes = RenderRawSignatures.FlipKindForProof(candidateBytes, owner.MetadataToken.ToInt32(), local.VariableType.MetadataToken.ToInt32(), true); }
            else if (name == "original-parameter-kind") {
                var p = Types(module).Where(t => !RenderPatcher.IsAdded(t)).SelectMany(t => t.Methods).Where(m => !Program.ChangedNames.Contains(m.FullName)).SelectMany(m => m.Parameters).First(p => p.ParameterType.MetadataType == MetadataType.ValueType && p.ParameterType is not TypeSpecification && p.ParameterType.DeclaringType == null && p.ParameterType.Scope.Name == "FNA");
                p.ParameterType = AsClass(p.ParameterType);
            } else {
                var f = Types(module).Where(t => !RenderPatcher.IsAdded(t)).SelectMany(t => t.Fields).First(f => f.FieldType.MetadataType == MetadataType.ValueType && f.FieldType is not TypeSpecification && f.FieldType.DeclaringType == null && f.FieldType.Scope.Name == "FNA");
                f.FieldType = AsClass(f.FieldType);
            }
            byte[] bytes = directBytes ?? Serialize(changed); string path = Path.Combine(output, "raw-named-" + name + ".exe");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes);
            using var reread = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes), new ReaderParameters { AssemblyResolver = resolver });
            string? useRejection = null;
            try { RenderRawSignatures.Compare(candidateBytes, bytes, original.MainModule, reread.MainModule); } catch (InvalidDataException error) { useRejection = error.Message; }
            Require(useRejection != null && useRejection.StartsWith("raw signature use differs:", StringComparison.Ordinal), "per-use raw census missed named kind: " + name);
            string? intrinsicRejection = null;
            if (!name.StartsWith("original-", StringComparison.Ordinal)) {
                try { Signatures(bytes, Program.ChangedNames.ToHashSet(StringComparer.Ordinal), inputPath); } catch (InvalidDataException error) { intrinsicRejection = error.Message; }
                Require(intrinsicRejection != null && intrinsicRejection.StartsWith("invalid raw named type kind:", StringComparison.Ordinal), "pinned TypeDef kind check missed: " + name + ": " + (intrinsicRejection ?? "accepted"));
            }
            rows.Add(new { name, candidatePath = path, sha256 = Sha(bytes), useRejection, intrinsicRejection, beforeFixtureRemapping = true });
        }
        return new { passed = true, cases = rows, perUseNotSharedCecilFlags = true };
    }
}
