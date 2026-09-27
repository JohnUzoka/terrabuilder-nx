using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
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

internal static class HintAudit
{
    internal static object Verify(byte[] originalGame, byte[] candidateGame, byte[] originalReLogic, byte[] candidateReLogic, HintPatcher.HintReceipt receipt, string inputPath)
    {
        Require(Sha(originalGame) == Program.InputHash && Sha(originalReLogic) == Program.ReLogicHash, "Hint52 audit requires pinned original pair");
        using var resolver = HintPatcher.Resolver(inputPath);
        AssemblyDefinition Read(byte[] bytes) => AssemblyDefinition.ReadAssembly(new MemoryStream(bytes), new ReaderParameters { AssemblyResolver = resolver });
        using var oldGame = Read(originalGame); using var game = Read(candidateGame);
        using var oldFont = Read(originalReLogic); using var font = Read(candidateReLogic);
        using var expectedGame = Read(originalGame); using var expectedFont = Read(originalReLogic);
        var expectedReceipt = HintPatcher.Inject(expectedGame.MainModule, expectedFont.MainModule);
        Require(JsonSerializer.Serialize(receipt) == JsonSerializer.Serialize(expectedReceipt), "Hint52 receipt differs from pinned generated plan");
        using var generatedGame = Read(Serialize(expectedGame)); using var generatedFont = Read(Serialize(expectedFont));
        // These comparisons precede all inverse operations and all runtime probe rebinding.
        var gameQualified = CompareQualified(generatedGame.MainModule, game.MainModule);
        var fontQualified = CompareQualified(generatedFont.MainModule, font.MainModule);
        var gamePreserved = Preserve(oldGame, game, true);
        var fontPreserved = Preserve(oldFont, font, false);
        var inverse = Inverse(oldGame.MainModule, game.MainModule, oldFont.MainModule, font.MainModule, receipt);
        var single = Types(oldGame.MainModule).SelectMany(t => t.Methods).Single(m => m.Name == "DrawSingleTile" && m.HasBody && m.Body.Instructions.Count == 2312);
        Require(QualifiedBody(single) == QualifiedBody(Program.Method(game.MainModule, single.FullName)), "inherited50 Single changed");
        var gameResources = PeResources.Fingerprints(originalGame); var fontResources = PeResources.Fingerprints(originalReLogic);
        Require(gameResources.SequenceEqual(PeResources.Fingerprints(candidateGame)) && fontResources.SequenceEqual(PeResources.Fingerprints(candidateReLogic)), "native PE resources changed");
        var gameRaw = Signatures(candidateGame); var fontRaw = Signatures(candidateReLogic);
        var sdk = Sdk(game.MainModule, font.MainModule, oldGame.MainModule, oldFont.MainModule);
        return new {
            passed = true, gameQualified, fontQualified, gamePreserved, fontPreserved, inverse, gameRaw, fontRaw, sdk,
            nativeResources = new { game = gameResources, relogic = fontResources },
            inherited50 = new { single.FullName, fingerprint = Fingerprint(single), exact = true },
            addedGameMethods = receipt.GameHelpers, addedFontMethods = receipt.FontHelpers,
            limits = "Static emitted-pair checks only; actual helper/root execution is reported separately. Resource exhaustion, races, native corruption and arbitrary runtime method detours are outside the guarded contract. No hardware/FPS claim."
        };
    }

    internal static byte[] Serialize(AssemblyDefinition assembly)
    {
        using var stream = new MemoryStream(); assembly.Write(stream, new WriterParameters { Timestamp = 0 }); return stream.ToArray();
    }

    static string Meta(string name, object value) => (string)Invoke("Preservation", name, value)!;
    static object Preserve(AssemblyDefinition original, AssemblyDefinition candidate, bool game)
    {
        var a = original.MainModule; var b = candidate.MainModule;
        Require(a.Mvid != b.Mvid, "candidate MVID unchanged");
        Require(a.Name == b.Name && a.Kind == b.Kind && a.Architecture == b.Architecture && a.Attributes == b.Attributes && a.RuntimeVersion == b.RuntimeVersion && a.EntryPoint?.FullName == b.EntryPoint?.FullName && original.Name.FullName == candidate.Name.FullName, "assembly identity/runtime changed");
        Require(a.AssemblyReferences.Select(r => r.FullName).SequenceEqual(b.AssemblyReferences.Select(r => r.FullName)) && a.ModuleReferences.Select(r => r.Name).SequenceEqual(b.ModuleReferences.Select(r => r.Name)), "assembly/module references changed");
        Require(Meta("Attributes", original) == Meta("Attributes", candidate) && Meta("Security", original) == Meta("Security", candidate) && Meta("Attributes", a) == Meta("Attributes", b), "assembly/module metadata changed");
        var before = Types(a).ToArray(); var after = Types(b).ToArray();
        Require(before.Select(t => t.FullName).SequenceEqual(after.Select(t => t.FullName)), "type order/count changed");
        var beforeUses = QualifiedUses(a, false); var afterUses = QualifiedUses(b, false);
        foreach (var (key, value) in beforeUses) Require(afterUses.TryGetValue(key, out var actual) && actual == value, "original qualified metadata changed: " + key);
        int methods = 0, fields = 0;
        for (int n = 0; n < before.Length; n++)
        {
            var old = before[n]; var current = after[n];
            Require(Meta("TypeMetadata", old) == Meta("TypeMetadata", current), "original type metadata changed: " + old.FullName);
            Require(old.Fields.Select(f => f.FullName).SequenceEqual(current.Fields.Select(f => f.FullName)), "original fields changed: " + old.FullName);
            Require(old.Methods.Select(m => m.FullName).SequenceEqual(current.Methods.Where(m => !HintPatcher.IsAdded(m)).Select(m => m.FullName)), "original methods changed: " + old.FullName);
            foreach (var field in old.Fields) { Require(Meta("FieldMetadata", field) == Meta("FieldMetadata", current.Fields.Single(f => f.FullName == field.FullName)), "original field metadata changed: " + field.FullName); fields++; }
            for (int slot = 0; slot < old.Methods.Count; slot++)
            {
                var method = old.Methods[slot]; var target = current.Methods[slot];
                Require(Meta("MethodMetadata", method) == Meta("MethodMetadata", target), "original method metadata changed: " + method.FullName);
                if (game && method.FullName == Program.TargetName) continue;
                Require(QualifiedBody(method) == QualifiedBody(target), "original body changed: " + method.FullName); methods++;
            }
        }
        Require(a.Resources.Select(r => Meta("ResourceMetadata", r)).SequenceEqual(b.Resources.Select(r => Meta("ResourceMetadata", r))), "managed resources changed");
        Require(!b.GetTypeReferences().Any(t => t.Name.StartsWith("Hint52", StringComparison.Ordinal) || t.Name.EndsWith("GuardTemplate", StringComparison.Ordinal)), "patch-time view escaped");
        var additions = Types(b).SelectMany(t => t.Methods).Where(HintPatcher.IsAdded).ToArray();
        Require(additions.All(m => m.DeclaringType.FullName == (game ? HintPatcher.ChatName : HintPatcher.FontName) && m.IsStatic && !m.IsConstructor && m.HasBody), "helper escaped approved owner");
        return new { unchangedTypes = before.Length, unchangedFields = fields, unchangedMethods = methods, originalBodyChanges = game ? 1 : 0, addedMethods = additions.Length, managedResources = a.Resources.Count, noNewFieldsTypesConstructors = true };
    }

    static object Inverse(ModuleDefinition oldGame, ModuleDefinition game, ModuleDefinition oldFont, ModuleDefinition font, HintPatcher.HintReceipt receipt)
    {
        var before = Program.Target(oldGame); var after = Program.Target(game);
        Require(before.Body.Instructions.Count == after.Body.Instructions.Count && before.Body.CodeSize == after.Body.CodeSize && before.Body.MaxStackSize == after.Body.MaxStackSize, "root shape changed");
        var site = after.Body.Instructions[receipt.RootInstruction];
        Require(site.Offset == 0x007a && site.OpCode == OpCodes.Call && site.Operand is MethodReference r && r.Name == "_NXHint52Measure" && r.DeclaringType.FullName == HintPatcher.ChatName, "root replacement site changed");
        var replacement = site.Operand;
        try
        {
            site.Operand = before.Body.Instructions[receipt.RootInstruction].Operand;
            Require(QualifiedBody(before) == QualifiedBody(after), "root inverse did not recover every qualified instruction/local/handler");
            string[] OriginalCalls(ModuleDefinition m) => Types(m).SelectMany(t => t.Methods).Where(method => !HintPatcher.IsAdded(method) && method.HasBody)
                .SelectMany(method => method.Body.Instructions.Select((instruction, index) => (method, instruction, index)))
                .Where(x => x.instruction.OpCode.FlowControl == FlowControl.Call)
                .Select(x => m.Assembly.Name.FullName + "|" + x.method.FullName + "|" + x.index + "|" + x.instruction.OpCode.Name + "|" + MemberIdentity((MemberReference)x.instruction.Operand)).ToArray();
            var calls = OriginalCalls(game).Concat(OriginalCalls(font)).ToArray();
            var originals = HintPatcher.Calls(oldGame).Concat(HintPatcher.Calls(oldFont)).ToArray();
            Require(calls.SequenceEqual(originals) && calls.Length == receipt.OriginalCallsiteCount && Sha(Encoding.UTF8.GetBytes(string.Join('\n', calls))) == receipt.OriginalCallsitesSha256, "original qualified callsite inventory changed");
        }
        finally { site.Operand = replacement; }
        return new { passed = true, originalRootInstructions = before.Body.Instructions.Count, originalRootLocals = before.Body.Variables.Count, originalRootHandlers = before.Body.ExceptionHandlers.Count, changedOperands = 1, changedOpcodes = 0, receipt.OriginalCallsiteCount, receipt.OriginalCallsitesSha256, allOriginalQualifiedCallsitesRecovered = true, originalFontAccessAndZeroMultiplyAndScaleSwapRetained = true };
    }

    static string Scope(IMetadataScope? scope) => scope switch {
        AssemblyNameReference a => "assembly:" + a.FullName, ModuleDefinition m => "module:" + m.Name + "@" + m.Assembly.Name.FullName,
        ModuleReference m => "module-ref:" + m.Name, null => "none", _ => throw new InvalidDataException("unknown scope")
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
    static string Signature(IMethodSignature m) => m.CallingConvention + ":" + m.HasThis + ":" + m.ExplicitThis + ":" + TypeIdentity(m.ReturnType) + "(" + string.Join(',', m.Parameters.Select(p => TypeIdentity(p.ParameterType))) + ")";
    internal static string MemberIdentity(MemberReference member) => member switch {
        TypeReference t => TypeIdentity(t), GenericInstanceMethod g => "method-spec:" + MemberIdentity(g.ElementMethod) + "<" + string.Join(',', g.GenericArguments.Select(TypeIdentity)) + ">",
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
        float n => "single:" + BitConverter.SingleToInt32Bits(n).ToString("x8", CultureInfo.InvariantCulture),
        double n => "double:" + BitConverter.DoubleToInt64Bits(n).ToString("x16", CultureInfo.InvariantCulture),
        _ => value.GetType().FullName + ":" + Convert.ToString(value, CultureInfo.InvariantCulture)
    };
    static string Marshal(MarshalInfo? info) => info == null ? "none" : info.GetType().FullName + ":" + string.Join(';', info.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
        .Where(p => p.GetIndexParameters().Length == 0).OrderBy(p => p.Name, StringComparer.Ordinal)
        .Select(p => p.Name + "=" + (p.GetValue(info) is TypeReference type ? TypeIdentity(type) : Constant(true, p.GetValue(info)))));

    static Dictionary<string, string> QualifiedUses(ModuleDefinition module, bool bodies = true)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        void Attr(string key, Mono.Cecil.ICustomAttributeProvider owner) => rows.Add(key + ":attributes", string.Join('\n', owner.CustomAttributes.Select(a => MemberIdentity(a.Constructor) + ":" + Convert.ToHexString(a.GetBlob()))));
        void Generic(string key, IEnumerable<GenericParameter> parameters) {
            foreach (var p in parameters) {
                string id = key + ":generic:" + p.Position; Attr(id, p);
                rows.Add(id, p.Attributes + ":" + string.Join(',', p.Constraints.Select(c => TypeIdentity(c.ConstraintType))));
                for (int n = 0; n < p.Constraints.Count; n++) Attr(id + ":constraint:" + n, p.Constraints[n]);
            }
        }
        Attr("assembly", module.Assembly); Attr("module", module);
        rows.Add("assembly:name-details", module.Assembly.Name.Attributes + ":" + module.Assembly.Name.HashAlgorithm + ":" + Convert.ToHexString(module.Assembly.Name.PublicKey));
        rows.Add("module:characteristics", module.Characteristics.ToString());
        foreach (var r in module.AssemblyReferences) rows.Add("assembly-reference:" + r.FullName, r.Attributes + ":" + r.HashAlgorithm + ":" + Convert.ToHexString(r.PublicKey) + ":" + Convert.ToHexString(r.Hash));
        foreach (var e in module.ExportedTypes) rows.Add("exported:" + e.FullName, e.Attributes + ":" + e.Identifier + ":" + Scope(e.Scope) + ":" + e.DeclaringType?.FullName);
        foreach (var type in Types(module)) {
            string key = "type:" + type.FullName; Attr(key, type); Generic(key, type.GenericParameters);
            rows.Add(key, TypeIdentity(type) + ":" + TypeIdentity(type.BaseType) + ":" + string.Join(',', type.Interfaces.Select(i => TypeIdentity(i.InterfaceType))));
            for (int n = 0; n < type.Interfaces.Count; n++) Attr(key + ":interface:" + n, type.Interfaces[n]);
            foreach (var field in type.Fields) { string f = "field:" + field.FullName; rows.Add(f, MemberIdentity(field)); Attr(f, field); rows.Add(f + ":constant-marshal", Constant(field.HasConstant, field.Constant) + ":" + Marshal(field.HasMarshalInfo ? field.MarshalInfo : null)); }
            foreach (var property in type.Properties) {
                string p = "property:" + property.FullName; rows.Add(p, TypeIdentity(property.PropertyType) + ":" + string.Join(',', property.Parameters.Select(a => TypeIdentity(a.ParameterType)))); Attr(p, property);
                rows.Add(p + ":constant", Constant(property.HasConstant, property.Constant));
                for (int n = 0; n < property.Parameters.Count; n++) { var a = property.Parameters[n]; Attr(p + ":arg:" + n, a); rows.Add(p + ":arg-details:" + n, a.Attributes + ":" + Constant(a.HasConstant, a.Constant) + ":" + Marshal(a.HasMarshalInfo ? a.MarshalInfo : null)); }
            }
            foreach (var e in type.Events) { string keyEvent = "event:" + e.FullName; rows.Add(keyEvent, TypeIdentity(e.EventType) + ":" + string.Join(',', e.OtherMethods.Select(MemberIdentity))); Attr(keyEvent, e); }
            foreach (var method in type.Methods) {
                // The pinned ReLogic contains duplicate FullName methods. Owner-local
                // slots distinguish them without relying on moving metadata tokens.
                string m = "method:" + method.FullName + ":slot:" + type.Methods.IndexOf(method); rows.Add(m, MemberIdentity(method)); Attr(m, method); Attr(m + ":return", method.MethodReturnType); Generic(m, method.GenericParameters);
                rows.Add(m + ":overrides", string.Join('\n', method.Overrides.Select(MemberIdentity)));
                rows.Add(m + ":return-details", method.MethodReturnType.Attributes + ":" + Constant(method.MethodReturnType.HasConstant, method.MethodReturnType.Constant) + ":" + Marshal(method.MethodReturnType.HasMarshalInfo ? method.MethodReturnType.MarshalInfo : null));
                for (int n = 0; n < method.Parameters.Count; n++) { var p = method.Parameters[n]; Attr(m + ":arg:" + n, p); rows.Add(m + ":arg-details:" + n, p.Attributes + ":" + Constant(p.HasConstant, p.Constant) + ":" + Marshal(p.HasMarshalInfo ? p.MarshalInfo : null)); }
                if (bodies) rows.Add(m + ":body", QualifiedBody(method));
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
        foreach (var (key, value) in before) Require(after.TryGetValue(key, out var actual) && actual == value, "qualified candidate differs at " + key);
        foreach (var method in Types(expected).SelectMany(t => t.Methods).Where(HintPatcher.IsAdded))
            Require(Meta("MethodMetadata", method) == Meta("MethodMetadata", Program.Method(candidate, method.FullName)), "qualified added method metadata changed");
        return new { passed = true, qualifiedUseSiteRows = before.Count, exactGeneratedHelpers = true, beforeAnyFixtureRemapping = true, tableOrderAndDedupIndependent = true, selfModuleMvidIntentionallyExcluded = true };
    }

    internal static object Signatures(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes); using var pe = new PEReader(stream); var reader = pe.GetMetadataReader();
        var decoder = (ISignatureTypeProvider<string, object?>)Activator.CreateInstance(Support.GetType("RawSignatures+Decoder", true)!, true)!;
        using var module = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        var methods = Types(module.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody && (HintPatcher.IsAdded(m) || m.FullName == Program.TargetName)).ToArray();
        string Sig(MethodSignature<string> sig) => sig.ReturnType + "(" + string.Join(',', sig.ParameterTypes) + ")";
        var rows = new List<object>();
        foreach (var method in methods) {
            var d = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle((int)method.MetadataToken.RID));
            rows.Add(new { kind = "MethodDef", name = method.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(d.Signature)), decoded = Sig(d.DecodeSignature(decoder, (object?)null)) });
            var body = pe.GetMethodBody(d.RelativeVirtualAddress);
            if (!body.LocalSignature.IsNil) { var l = reader.GetStandaloneSignature(body.LocalSignature); rows.Add(new { kind = "Locals", name = method.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(l.Signature)), decoded = string.Join(',', l.DecodeLocalSignature(decoder, (object?)null)) }); }
        }
        foreach (var r in methods.SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MemberReference>().Where(r => r.MetadataToken.TokenType == TokenType.MemberRef).DistinctBy(r => r.MetadataToken.ToInt32())) {
            var d = reader.GetMemberReference(MetadataTokens.MemberReferenceHandle((int)r.MetadataToken.RID));
            rows.Add(new { kind = "MemberRef", name = r.FullName, raw = Convert.ToHexString(reader.GetBlobBytes(d.Signature)), decoded = d.GetKind() == MemberReferenceKind.Method ? Sig(d.DecodeMethodSignature(decoder, (object?)null)) : d.DecodeFieldSignature(decoder, (object?)null) });
        }
        return new { passed = true, independentlyDecodedRawMetadata = true, primitiveClassOrValueTypeAliasesAllowed = false, rows };
    }

    static object Sdk(ModuleDefinition game, ModuleDefinition font, ModuleDefinition oldGame, ModuleDefinition oldFont)
    {
        Require(Sha(File.ReadAllBytes(HintPatcher.PinnedResolver.Core)) == Program.CoreHash, "pinned CoreLib hash mismatch");
        var pairs = new[] { (Module: game, Original: oldGame), (Module: font, Original: oldFont) };
        var rows = new List<object>();
        foreach (var pair in pairs) foreach (var method in Types(pair.Module).SelectMany(t => t.Methods).Where(HintPatcher.IsAdded))
        {
            var members = method.Body.Instructions.Select(i => i.Operand).OfType<MemberReference>().DistinctBy(MemberIdentity);
            foreach (var reference in members)
            {
                TypeDefinition ResolveType(TypeReference type) {
                    string name = type.GetElementType().FullName;
                    if (type.Scope is AssemblyNameReference scope && scope.FullName == font.Assembly.Name.FullName) return Types(font).Single(t => t.FullName == name);
                    if (type.Scope is AssemblyNameReference gameScope && gameScope.FullName == game.Assembly.Name.FullName) return Types(game).Single(t => t.FullName == name);
                    return type.Resolve() ?? throw new InvalidDataException("unresolved guard type " + TypeIdentity(type));
                }
                var owner = reference is TypeReference typeReference ? ResolveType(typeReference) : ResolveType(reference.DeclaringType);
                bool SameAssembly(TypeDefinition t) => t.Module.Assembly.Name.FullName == pair.Module.Assembly.Name.FullName;
                bool AccessibleType(TypeDefinition t) => t.DeclaringType == null ? t.IsPublic || SameAssembly(t) : AccessibleType(t.DeclaringType) && (t.IsNestedPublic || SameAssembly(t) && (t.IsNestedAssembly || t.IsNestedFamilyOrAssembly) || t.DeclaringType.FullName == method.DeclaringType.FullName && SameAssembly(t));
                Require(AccessibleType(owner), "guard referenced inaccessible type: " + TypeIdentity(owner));
                IMemberDefinition resolved = reference switch {
                    TypeReference => owner,
                    MethodReference m when m.Name.StartsWith(HintPatcher.Prefix, StringComparison.Ordinal) => owner.Methods.Single(x => x.FullName == m.FullName),
                    MethodReference m => m.Resolve() ?? throw new InvalidDataException("unresolved guard method " + MemberIdentity(m)),
                    FieldReference f => f.Resolve() ?? throw new InvalidDataException("unresolved guard field " + MemberIdentity(f)),
                    _ => throw new InvalidDataException("unsupported guard operand")
                };
                bool sameOwner = owner.FullName == method.DeclaringType.FullName && SameAssembly(owner);
                Require(resolved is TypeDefinition || resolved is MethodDefinition { IsPublic: true } or FieldDefinition { IsPublic: true } || sameOwner || SameAssembly(owner) && resolved is MethodDefinition { IsAssembly: true } or FieldDefinition { IsAssembly: true }, "guard member inaccessible: " + reference.FullName);
                if (owner.Namespace.StartsWith("System", StringComparison.Ordinal)) Require(owner.Module.FileName.StartsWith("/mono-nx/", StringComparison.Ordinal), "host BCL fallback detected");
                rows.Add(new { caller = method.FullName, reference = MemberIdentity(reference), resolved = resolved.FullName, ownerAssembly = owner.Module.Assembly.Name.FullName, image = owner.Module.FileName, sameOwnerPrivateAccess = sameOwner && resolved is FieldDefinition { IsPrivate: true }, sdkOnly = owner.Namespace.StartsWith("System", StringComparison.Ordinal) });
            }
        }
        return new { passed = true, coreSha256 = Program.CoreHash, noHostFallback = true, exactCrossAssemblyFontHelper = true, rows };
    }

    internal static object NegativeProof(byte[] originalGame, byte[] candidateGame, byte[] originalReLogic, byte[] candidateReLogic, HintPatcher.HintReceipt receipt, string inputPath, string output)
    {
        Directory.CreateDirectory(output);
        MethodDefinition Helper(ModuleDefinition m, string name) => Types(m).SelectMany(t => t.Methods).Single(x => x.Name == name);
        void IntegerBound(ModuleDefinition m, int from, int to) { var i = Helper(m, "_NXHint52CanSkip").Body.Instructions.First(i => i.OpCode == OpCodes.Ldc_I4 && (int)i.Operand == from); i.Operand = to; }
        var cases = new (string Name, bool Font, Action<ModuleDefinition> Mutate)[] {
            ("wrong-vector-scope", false, m => m.GetTypeReferences().Single(t => t.FullName == "Microsoft.Xna.Framework.Vector2").Scope = m.AssemblyReferences.Single(r => r.Name == "mscorlib")),
            ("wrong-root-call", false, m => Program.Target(m).Body.Instructions[receipt.RootInstruction].Operand = Program.Method(m, Program.SizeName)),
            ("wrong-helper-signature", false, m => Helper(m, "_NXHint52Measure").Parameters[0].ParameterType = m.TypeSystem.Object),
            ("wrong-font-helper-binding", false, m => { var call = (MethodReference)Helper(m, "_NXHint52CanSkip").Body.Instructions.Single(i => i.Operand is MethodReference r && r.Name == "_NXHint52MetricsSafe").Operand; call.DeclaringType = new TypeReference("ReLogic.Graphics", "DynamicSpriteFont", m, m.AssemblyReferences.Single(r => r.Name == "FNA")); }),
            ("missing-original-fallback", false, m => Helper(m, "_NXHint52Measure").Body.Instructions.Single(i => i.Operand is MethodReference r && r.Name == "GetStringSize").Operand = Program.Method(m, Program.SizeName)),
            ("wrong-population-bound", false, m => IntegerBound(m, 256, 257)),
            ("wrong-text-bound", false, m => IntegerBound(m, 4096, 4097)),
            ("wrong-glyph-bound", false, m => Helper(m, "_NXHint52CanSkip").Body.Instructions.Single(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 4f).Operand = float.MaxValue),
            ("wrong-font-metric-bound", true, m => Helper(m, "_NXHint52MetricInRange").Body.Instructions.Single(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 1024f).Operand = float.MaxValue),
            ("unconditional-fast-path", false, m => { var method = Helper(m, "_NXHint52CanSkip"); method.Body.Instructions.Clear(); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); }),
            ("changed-original-font-getter", true, m => { var method = Types(m).Single(t => t.FullName == HintPatcher.FontName).Methods.Single(x => x.Name == "get_LineSpacing"); method.Body.Instructions[0].OpCode = OpCodes.Ldnull; method.Body.Instructions[0].Operand = null; })
        };
        var rows = new List<object>();
        foreach (var test in cases)
        {
            using var resolver = HintPatcher.Resolver(inputPath);
            using var changed = AssemblyDefinition.ReadAssembly(new MemoryStream(test.Font ? candidateReLogic : candidateGame), new ReaderParameters { AssemblyResolver = resolver });
            test.Mutate(changed.MainModule); byte[] bytes = Serialize(changed);
            string path = Path.Combine(output, test.Name + (test.Font ? ".dll" : ".exe"));
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes);
            string? rejection = null;
            try { Verify(originalGame, test.Font ? candidateGame : bytes, originalReLogic, test.Font ? bytes : candidateReLogic, receipt, inputPath); }
            catch (InvalidDataException e) { rejection = e.Message; }
            Require(rejection != null && rejection.StartsWith("qualified ", StringComparison.Ordinal), "negative candidate not rejected before inverse/fixture: " + test.Name);
            rows.Add(new { test.Name, mutatedAssembly = test.Font ? "ReLogic" : "Terraria", candidatePath = path, sha256 = Sha(bytes), rejection, beforeFixtureMapping = true });
        }
        var raw = Invoke("RawSignatures", "VerifyMalformedRejection", output)!;
        var result = new { passed = true, gameSha256 = Sha(candidateGame), relogicSha256 = Sha(candidateReLogic), rejectedCandidates = rows, malformedRawPrimitive = raw };
        Json(Path.Combine(output, "static-negative-proof.json"), result); return result;
    }
}
