using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;

internal static class DescriptorDiagnosticProof
{
    const string SourceHash = "0ce46870f3f384b10f37da068bdbbbeeb567cb58aa69bb0ad0d6511305689ab6";
    const string SourceMvid = "b17425f9-1fbc-4be3-9b13-ecda72163fe0";
    const string Descriptor = "System.ComponentModel.ReflectPropertyDescriptor";
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    delegate object? GetValue(DescriptorProofHost descriptor, object? component);

#if DESCRIPTOR_DIAGNOSTIC_PROOF_STANDALONE
    public static int Main(string[] args)
    {
        try
        {
            Require(args.Length is 2 or 3, "usage: baseline fresh-output-directory [candidate]");
            Run(args[0], args[1], args.Length == 3 ? args[2] : null);
            Console.WriteLine("DESCRIPTOR FULL-METHOD DIFFERENTIAL PROOF PASS; no Switch performance or promotion claim");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
#endif

    internal static object Run(string baselinePath, string freshOutputDirectory, string? candidatePath = null)
    {
        byte[] originalBytes = File.ReadAllBytes(baselinePath);
        Require(Sha(originalBytes) == SourceHash && originalBytes.Length == 355840, "unsupported baseline bytes");
        Require(!Directory.Exists(freshOutputDirectory) && !File.Exists(freshOutputDirectory), "fresh proof output directory required");
        Directory.CreateDirectory(freshOutputDirectory);
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(baselinePath))!);
        var reader = new ReaderParameters { AssemblyResolver = resolver };
        using var original = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes), reader);
        Require(original.MainModule.Mvid.ToString() == SourceMvid && original.Name.Version == new Version(9, 0, 0, 0) && Convert.ToHexString(original.Name.PublicKeyToken).ToLowerInvariant() == "b03f5f7f11d50a3a", "unsupported baseline identity");
        using var experiment = AssemblyDefinition.ReadAssembly(new MemoryStream(originalBytes), reader);
        DeleteEntry(Find(experiment));
        experiment.MainModule.Mvid = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(SourceHash + ":descriptor-entry-proof-only")).AsSpan(0, 16));
        using var buffer = new MemoryStream(); experiment.Write(buffer, new WriterParameters { Timestamp = 0 });
        byte[] independentBytes = buffer.ToArray();
        File.WriteAllBytes(Path.Combine(freshOutputDirectory, "candidate-for-proof-only.dll"), independentBytes);
        using var independent = AssemblyDefinition.ReadAssembly(new MemoryStream(independentBytes), reader);
        var before = Find(original); var independentlyChanged = Find(independent);
        object independentStructural = Validate(before, independentlyChanged);
        byte[] candidateBytes = candidatePath == null ? independentBytes : File.ReadAllBytes(candidatePath);
        using var candidate = AssemblyDefinition.ReadAssembly(new MemoryStream(candidateBytes), reader);
        var after = Find(candidate);
        object candidateStructural = Validate(before, after);
        Require(BodyKey(after) == BodyKey(independentlyChanged), "caller method differs from independent serialized experiment");
        var adversarial = Adversarial(before, after);
        var receipts = new List<object>();
        byte[] executable = BuildExecutable(before, after, receipts);
        File.WriteAllBytes(Path.Combine(freshOutputDirectory, "DescriptorExactMethods.dll"), executable);
        var loaded = Assembly.Load(executable).GetType("Probe.DescriptorMethods", true)!;
        var baseline = loaded.GetMethod("Baseline")!.CreateDelegate<GetValue>();
        var changed = loaded.GetMethod("Candidate")!.CreateDelegate<GetValue>();
        string template = ErrorTemplate(original);
        CultureInfo savedCulture = CultureInfo.CurrentCulture, savedUiCulture = CultureInfo.CurrentUICulture;
        object dynamic, allocation;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture; CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            dynamic = ExecuteCases(baseline, changed, template);
            allocation = Allocation(baseline, changed, template);
        }
        finally { CultureInfo.CurrentCulture = savedCulture; CultureInfo.CurrentUICulture = savedUiCulture; }
        Require(Sha(File.ReadAllBytes(baselinePath)) == SourceHash, "baseline input changed");
        if (candidatePath != null) Require(Sha(File.ReadAllBytes(candidatePath)) == Sha(candidateBytes), "caller candidate input changed");
        var result = new
        {
            passed = true, promotionApproved = false, wholeSerializedMethodExecuted = true,
            baselineSha256 = SourceHash, baselineMvid = SourceMvid,
            independentCandidateSha256 = Sha(independentBytes), candidateSha256 = Sha(candidateBytes),
            candidateOrigin = candidatePath == null ? "independently deleted entry prefix, serialized and reread" : "caller candidate checked against independently serialized deletion",
            independentStructural, candidateStructural, adversarial, executableSha256 = Sha(executable), receipts,
            dynamic, allocation,
            intentionalDifferences = new[] {
                "Only entry Name access, interpolated-string construction/formatting and entry Debug.WriteLine disappear.",
                "Failures or listener side effects exclusively inside the removed entry diagnostic disappear too; dedicated cases report these separately and do not claim preserved property errors."
            },
            limitations = new[] {
                "Both entire GetValue bodies, including every branch and exception handler, are copied from actual serialized inputs, then serialized/reread and JIT executed. No handwritten GetValue implementation or method interpreter is used.",
                "Descriptor internals Name, IsExtender, GetMethodValue, GetInvocationTarget and _componentClass use explicit fixtures. This does not prove actual TypeDescriptor provider/association lookup, caching, extender registration or lifetime behavior; substituted targets test the surrounding real IL only.",
                "MethodInfo.Invoke, property getters, IComponent/ISite dispatch, exception construction and real host string formatting are executed. Debug sinks are explicit capture/throw boundaries rather than platform listeners. Resource text is extracted from the original DLL; SR.Format is a string.Format boundary with invariant culture, not a proof of SR resource-key mode or localization infrastructure.",
                "The host interpolated-string-handler wrapper counts and delegates to the real host handler. Host runtime allocation results include these boundaries and do not measure Switch AOT, deployed Mono, graphics, frame time, gameplay, save/protocol behavior or material/mod behavior.",
                "Exception stack traces, generated exception object identity and native/JIT lifetime scheduling are not compared. Fixture-thrown exception and returned-object identities are checked directly. The entry type-name-null fallback is structurally retained in the original but cannot be forced with an ordinary real System.Type.",
                "This is a targeted method proof, not a whole-assembly preservation, strong-name validity, AOT loader or hardware acceptance proof."
            }
        };
        File.WriteAllText(Path.Combine(freshOutputDirectory, "descriptor-diagnostic-proof.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return result;
    }

    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("descriptor proof: " + message); }
    static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static MethodDefinition Find(AssemblyDefinition image) => image.MainModule.Types.Single(t => t.FullName == Descriptor).Methods.Single(m => m.Name == "GetValue" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Object");
    static Instruction At(MethodDefinition method, int offset) => method.Body.Instructions.Single(i => i.Offset == offset);
    static bool Removed(Instruction instruction) => instruction.Offset >= 1 && instruction.Offset <= 0x6e;
    static void DeleteEntry(MethodDefinition method)
    {
        Require(At(method, 0).OpCode.Code == Code.Nop && At(method, 0x6f).OpCode.Code == Code.Ldarg_0, "pinned deletion boundaries");
        Require(At(method, 0x69).Operand is MethodReference m && m.FullName == "System.Void System.Diagnostics.Debug::WriteLine(System.String)", "entry diagnostic endpoint");
        var removed = method.Body.Instructions.Where(Removed).ToHashSet();
        foreach (var i in method.Body.Instructions.Where(i => !removed.Contains(i)))
            Require(!(i.Operand is Instruction target && removed.Contains(target)) && !(i.Operand is Instruction[] targets && targets.Any(removed.Contains)), "retained branch enters removed prefix");
        foreach (var h in method.Body.ExceptionHandlers)
            Require(new[] { h.TryStart, h.TryEnd, h.HandlerStart, h.HandlerEnd, h.FilterStart }.All(i => i == null || !removed.Contains(i)), "exception boundary enters removed prefix");
        var il = method.Body.GetILProcessor(); foreach (var i in removed) il.Remove(i);
    }
    static string TypeKey(TypeReference type) => type.FullName + "@" + type.Scope;
    static string Operand(object? value, Func<Instruction, int> index) => value switch
    {
        null => "", Instruction i => "label:" + index(i), Instruction[] a => "labels:" + string.Join(",", a.Select(index)),
        VariableDefinition v => "local:" + v.Index, ParameterDefinition p => "arg:" + p.Index,
        MethodReference m => "method:" + m.FullName + "@" + m.DeclaringType.Scope,
        FieldReference f => "field:" + f.FullName + "@" + f.DeclaringType.Scope,
        TypeReference t => "type:" + TypeKey(t),
        _ => value.GetType().FullName + ":" + Convert.ToString(value, CultureInfo.InvariantCulture)
    };
    static string Attributes(Mono.Cecil.ICustomAttributeProvider provider) => string.Join(";", provider.CustomAttributes.Select(a => a.Constructor.FullName + "@" + a.Constructor.DeclaringType.Scope + ":" + Convert.ToHexString(a.GetBlob())));
    static string Metadata(MethodDefinition method)
    {
        Require(!method.HasGenericParameters && !method.HasSecurityDeclarations && !method.HasPInvokeInfo && !method.HasOverrides && !method.MethodReturnType.HasMarshalInfo && method.Parameters.All(p => !p.HasMarshalInfo), "unsupported method metadata shape");
        return $"{method.FullName}|{method.MetadataToken}|{method.Attributes}|{method.ImplAttributes}|{method.SemanticsAttributes}|{method.CallingConvention}|{method.HasThis}|{method.ExplicitThis}|{TypeKey(method.ReturnType)}|{Attributes(method)}|{method.MethodReturnType.Attributes}|{Attributes(method.MethodReturnType)}|" +
            string.Join(";", method.Parameters.Select(p => $"{p.Name}:{p.Attributes}:{TypeKey(p.ParameterType)}:{p.HasConstant}:{p.Constant}:{Attributes(p)}"));
    }
    static string Handlers(MethodDefinition method, Func<Instruction, int> index)
    {
        int Label(Instruction? i) => i == null ? -1 : index(i);
        return string.Join(";", method.Body.ExceptionHandlers.Select(h => $"{h.HandlerType}:{(h.CatchType == null ? "" : TypeKey(h.CatchType))}:{Label(h.TryStart)}:{Label(h.TryEnd)}:{Label(h.HandlerStart)}:{Label(h.HandlerEnd)}:{Label(h.FilterStart)}"));
    }
    static string BodyKey(MethodDefinition method)
    {
        var indices = method.Body.Instructions.Select((i, n) => (i, n)).ToDictionary(p => p.i, p => p.n);
        return string.Join("\n", method.Body.Instructions.Select(i => i.OpCode.Code + " " + Operand(i.Operand, x => indices[x]))) +
            "\nlocals:" + string.Join(";", method.Body.Variables.Select(v => TypeKey(v.VariableType))) +
            $"\ninit:{method.Body.InitLocals};stack:{method.Body.MaxStackSize}\nhandlers:" + Handlers(method, x => indices[x]);
    }
    static object Validate(MethodDefinition before, MethodDefinition after)
    {
        var a = before.Body.Instructions; var b = after.Body.Instructions;
        var survivors = a.Where(i => !Removed(i)).ToArray();
        Require(a.Count == 161 && a.Count - survivors.Length == 39 && b.Count == survivors.Length, "exact deletion is 39 of 161 instructions");
        Require(Metadata(before) == Metadata(after), "method signature, token, flags, return/parameter metadata and custom attributes unchanged");
        Require(before.Body.InitLocals == after.Body.InitLocals && before.Body.MaxStackSize == after.Body.MaxStackSize, "initialization/max-stack flags unchanged");
        Require(before.Body.Variables.Select(v => TypeKey(v.VariableType)).SequenceEqual(after.Body.Variables.Select(v => TypeKey(v.VariableType))), "locals unchanged");
        var originalIndices = a.Select((i, n) => (i, n)).ToDictionary(p => p.i, p => p.n);
        var afterIndices = b.Select((i, n) => (i, n)).ToDictionary(p => p.i, p => originalIndices[survivors[p.n]]);
        for (int n = 0; n < survivors.Length; n++)
            Require(survivors[n].OpCode.Code == b[n].OpCode.Code && Operand(survivors[n].Operand, i => originalIndices[i]) == Operand(b[n].Operand, i => afterIndices[i]), "retained instruction/operand/edge changed at original " + survivors[n]);
        Require(Handlers(before, i => originalIndices[i]) == Handlers(after, i => afterIndices[i]), "exception region or catch type changed");
        Require(before.Body.ExceptionHandlers.Count == 1 && before.Body.ExceptionHandlers[0].HandlerType == ExceptionHandlerType.Catch, "one complete original catch region");
        return new { baselineInstructions = a.Count, candidateInstructions = b.Count, removedInstructions = 39, unchangedSurvivingInstructions = survivors.Length, locals = before.Body.Variables.Count, handlers = before.Body.ExceptionHandlers.Count, maxStack = before.Body.MaxStackSize, deletion = "original IL_0001..IL_006e inclusive; IL_0000 and IL_006f onward unchanged", baselineBodySha256 = Sha(Encoding.UTF8.GetBytes(BodyKey(before))), candidateBodySha256 = Sha(Encoding.UTF8.GetBytes(BodyKey(after))) };
    }
    static object Adversarial(MethodDefinition original, MethodDefinition candidate)
    {
        var rejected = new List<string>();
        void Reject(string name, Action mutate, Action restore)
        {
            bool failed = false; mutate(); try { Validate(original, candidate); } catch (InvalidOperationException) { failed = true; } finally { restore(); }
            Require(failed, "adversary accepted: " + name); rejected.Add(name);
        }
        var instructions = candidate.Body.Instructions;
        var branch = instructions.First(i => i.Operand is Instruction); object target = branch.Operand;
        Reject("retargeted retained branch", () => branch.Operand = instructions.Last(), () => branch.Operand = target);
        var assertion = instructions.Single(i => i.Operand is MethodReference m && m.Name == "Assert"); var assertionCode = assertion.OpCode;
        Reject("removed Debug.Assert", () => assertion.OpCode = OpCodes.Nop, () => assertion.OpCode = assertionCode);
        var line = instructions.First(i => i.Operand is MethodReference m && m.Name == "WriteLine"); var lineCode = line.OpCode;
        Reject("removed surviving null-return diagnostic", () => line.OpCode = OpCodes.Nop, () => line.OpCode = lineCode);
        var invoke = instructions.Single(i => i.Operand is MethodReference m && m.Name == "Invoke"); var invokeCode = invoke.OpCode;
        Reject("changed reflection invocation dispatch", () => invoke.OpCode = OpCodes.Call, () => invoke.OpCode = invokeCode);
        var h = candidate.Body.ExceptionHandlers.Single(); var tryStart = h.TryStart;
        Reject("moved exception boundary", () => h.TryStart = tryStart.Next, () => h.TryStart = tryStart);
        var catchType = h.CatchType;
        Reject("narrowed catch type", () => h.CatchType = candidate.Module.ImportReference(typeof(TargetInvocationException)), () => h.CatchType = catchType);
        bool init = candidate.Body.InitLocals;
        Reject("changed InitLocals", () => candidate.Body.InitLocals = !init, () => candidate.Body.InitLocals = init);
        int stack = candidate.Body.MaxStackSize;
        Reject("changed max stack", () => candidate.Body.MaxStackSize++, () => candidate.Body.MaxStackSize = stack);
        var localType = candidate.Body.Variables[0].VariableType;
        Reject("changed retained interpolation local", () => candidate.Body.Variables[0].VariableType = candidate.Module.TypeSystem.Object, () => candidate.Body.Variables[0].VariableType = localType);
        var attributes = candidate.Attributes;
        Reject("changed method flags", () => candidate.Attributes ^= MA.Virtual, () => candidate.Attributes = attributes);
        string parameterName = candidate.Parameters[0].Name;
        Reject("changed parameter metadata", () => candidate.Parameters[0].Name = "changed", () => candidate.Parameters[0].Name = parameterName);
        var extra = Instruction.Create(OpCodes.Nop);
        Reject("extra surviving instruction", () => instructions.Add(extra), () => instructions.Remove(extra));
        bool incomingRejected = false; var incoming = At(original, 0x77); object oldTarget = incoming.Operand;
        try { incoming.Operand = At(original, 1); DeleteEntry(original); } catch (InvalidOperationException) { incomingRejected = true; } finally { incoming.Operand = oldTarget; }
        Require(incomingRejected, "deletion accepted an incoming branch"); rejected.Add("incoming branch into deleted prefix");
        return rejected;
    }

    static string ErrorTemplate(AssemblyDefinition assembly)
    {
        foreach (var resource in assembly.MainModule.Resources.OfType<EmbeddedResource>().Where(r => r.Name.EndsWith(".resources", StringComparison.Ordinal)))
        {
            using var stream = resource.GetResourceStream(); using var reader = new ResourceReader(stream);
            foreach (DictionaryEntry entry in reader) if (entry.Key is string key && key == "ErrorPropertyAccessorException") return (string)entry.Value!;
        }
        throw new InvalidOperationException("missing original error resource");
    }

    static byte[] BuildExecutable(MethodDefinition original, MethodDefinition candidate, List<object> receipts)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("DescriptorExactMethods", new Version(1, 0)), "DescriptorExactMethods", ModuleKind.Dll);
        var module = assembly.MainModule;
        var core = (AssemblyNameReference)module.TypeSystem.CoreLibrary; var hostCore = typeof(object).Assembly.GetName();
        core.Name = hostCore.Name!; core.Version = hostCore.Version!; core.PublicKeyToken = hostCore.GetPublicKeyToken();
        var type = new TypeDefinition("Probe", "DescriptorMethods", TA.Public | TA.Abstract | TA.Sealed, module.TypeSystem.Object); module.Types.Add(type);
        var substitutions = new Dictionary<string, Type>
        {
            [Descriptor] = typeof(DescriptorProofHost), ["System.ComponentModel.MemberDescriptor"] = typeof(DescriptorProofHost),
            ["System.Diagnostics.Debug"] = typeof(DescriptorProofDiagnostics), ["System.SR"] = typeof(DescriptorProofDiagnostics),
            ["System.Runtime.CompilerServices.DefaultInterpolatedStringHandler"] = typeof(DescriptorProofHandler)
        };
        Type HostType(TypeReference source)
        {
            if (substitutions.TryGetValue(source.FullName, out var host)) return host;
            if (source is ArrayType array) return array.Rank == 1 ? HostType(array.ElementType).MakeArrayType() : HostType(array.ElementType).MakeArrayType(array.Rank);
            var real = Type.GetType(source.FullName) ?? typeof(IComponent).Assembly.GetType(source.FullName) ?? typeof(TypeConverter).Assembly.GetType(source.FullName);
            Require(real != null && source.Namespace.StartsWith("System", StringComparison.Ordinal), "unmapped host type " + source.FullName); return real!;
        }
        TypeReference MapType(TypeReference source) => module.ImportReference(HostType(source));
        var expected = new Dictionary<string, string>();
        foreach (var (source, name) in new[] { (original, "Baseline"), (candidate, "Candidate") })
        {
            var target = new MethodDefinition(name, MA.Public | MA.Static, MapType(source.ReturnType)); type.Methods.Add(target);
            target.Parameters.Add(new ParameterDefinition("owner", Mono.Cecil.ParameterAttributes.None, module.ImportReference(typeof(DescriptorProofHost))));
            foreach (var p in source.Parameters) target.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, MapType(p.ParameterType)));
            target.Body.InitLocals = source.Body.InitLocals; target.Body.MaxStackSize = source.Body.MaxStackSize;
            foreach (var local in source.Body.Variables) target.Body.Variables.Add(new VariableDefinition(MapType(local.VariableType)));
            var instructions = source.Body.Instructions.ToDictionary(i => i, _ => Instruction.Create(OpCodes.Nop));
            var originalInstructions = source == original ? original.Body.Instructions.ToArray() : original.Body.Instructions.Where(i => !Removed(i)).ToArray();
            var boundaryReceipts = new List<object>();
            object Member(object value, int offset)
            {
                if (value is TypeReference tr) return MapType(tr);
                if (value is FieldReference field)
                {
                    Require(field.FullName == "System.Type System.ComponentModel.ReflectPropertyDescriptor::_componentClass", "unexpected field boundary");
                    var result = module.ImportReference(typeof(DescriptorProofHost).GetField(field.Name)!);
                    boundaryReceipts.Add(new { originalOffset = offset.ToString("x4"), source = field.FullName, target = result.FullName, substituted = true }); return result;
                }
                if (value is not MethodReference method) return value;
                Type host = HostType(method.DeclaringType);
                string methodName = offset == 0x1b ? "get_EntryName" : offset == 0x69 ? "EntryWriteLine" : method.Name;
                var parameterTypes = method.Parameters.Select(p => HostType(p.ParameterType)).ToArray();
                MethodBase? resolved = methodName == ".ctor" ? host.GetConstructor(parameterTypes) : host.GetMethod(methodName, Flags, null, parameterTypes, null);
                Require(resolved != null && resolved.IsStatic != method.HasThis, "unmapped method boundary " + method.FullName);
                if (resolved is MethodInfo info) Require(info.ReturnType == HostType(method.ReturnType), "boundary return type changed " + method.FullName);
                var imported = module.ImportReference(resolved!);
                boundaryReceipts.Add(new { originalOffset = offset.ToString("x4"), source = method.FullName, target = imported.FullName, substituted = substitutions.ContainsKey(method.DeclaringType.FullName) }); return imported;
            }
            for (int n = 0; n < source.Body.Instructions.Count; n++)
            {
                var i = source.Body.Instructions[n]; var copy = instructions[i]; copy.OpCode = i.OpCode;
                copy.Operand = i.Operand switch
                {
                    null => null, Instruction label => instructions[label], Instruction[] labels => labels.Select(label => instructions[label]).ToArray(),
                    VariableDefinition local => target.Body.Variables[local.Index], ParameterDefinition p => target.Parameters[p.Index + 1],
                    _ => Member(i.Operand, originalInstructions[n].Offset)
                };
                target.Body.Instructions.Add(copy);
            }
            Instruction? Label(Instruction? i) => i == null ? null : instructions[i];
            foreach (var h in source.Body.ExceptionHandlers) target.Body.ExceptionHandlers.Add(new ExceptionHandler(h.HandlerType)
            {
                TryStart = Label(h.TryStart), TryEnd = Label(h.TryEnd), HandlerStart = Label(h.HandlerStart), HandlerEnd = Label(h.HandlerEnd),
                FilterStart = Label(h.FilterStart), CatchType = h.CatchType == null ? null : MapType(h.CatchType)
            });
            expected.Add(name, BodyKey(target));
            receipts.Add(new { method = name, copiedWholeMethod = true, copiedInstructions = source.Body.Instructions.Count, addedInstructions = 0, copiedLocals = source.Body.Variables.Count, copiedHandlers = source.Body.ExceptionHandlers.Count, signatureAdapter = "instance this -> explicit DescriptorProofHost argument; starg component retains slot 1", boundaryReceipts });
        }
        module.Mvid = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", expected.Values))).AsSpan(0, 16));
        using var buffer = new MemoryStream(); assembly.Write(buffer, new WriterParameters { Timestamp = 0 }); byte[] bytes = buffer.ToArray();
        using var reread = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        foreach (var method in reread.MainModule.Types.Single(t => t.Name == type.Name).Methods)
            Require(BodyKey(method) == expected[method.Name], "executable serialization changed instructions/locals/edges/handlers: " + method.Name + "\n" + string.Join("\n", expected[method.Name].Split('\n').Zip(BodyKey(method).Split('\n')).Where(p => p.First != p.Second).Select(p => "expected: " + p.First + "\nactual: " + p.Second)));
        return bytes;
    }

    sealed record Observation(string[] Results, string[] Errors, string[] Trace, string[] Lines, string[] EntryLines, string[] Assertions, int GetterReads, int SiteReads, int SiteNameReads, int EntryNames, int Constructors, int Literals, int Formatted, int Clears, int EntryWrites, int OtherWrites, int Asserts);
    sealed record Scenario(string Name, Action<DescriptorProofContext> Configure, string Target = "component", int Calls = 1, string? ExpectedError = null, int ExpectedReads = 0, string? ExpectedInner = null);

    static Observation Observe(GetValue get, DescriptorProofContext c, object? component, int calls)
    {
        DescriptorProofDiagnostics.Current = c;
        var host = new DescriptorProofHost(c); var values = new List<string>(); var errors = new List<string>();
        for (int n = 0; n < calls; n++)
        {
            try
            {
                object? value = get(host, component);
                values.Add(value == null ? "null" : ReferenceEquals(value, c.Identity) ? "identity" : value is int integer ? "int:" + integer : "unexpected:" + value.GetType().FullName);
                errors.Add("none");
            }
            catch (Exception error)
            {
                values.Add("throw"); errors.Add(ExceptionKey(error, c));
            }
        }
        return new(values.ToArray(), errors.ToArray(), c.Trace.ToArray(), c.Lines.ToArray(), c.EntryLines.ToArray(), c.Assertions.ToArray(), c.GetterReads, c.SiteReads, c.SiteNameReads, c.EntryNames, c.HandlerConstructors, c.Literals, c.Formatted, c.Clears, c.EntryWrites, c.OtherWrites, c.Asserts);
    }
    static string ExceptionKey(Exception e, DescriptorProofContext c)
    {
        // Never call an adversarial Message getter merely to observe a thrown fixture exception.
        string identity = ReferenceEquals(e, c.BoundaryFailure) ? "boundary-identity" : ReferenceEquals(e, c.GetterFailure) ? "getter-identity" : ReferenceEquals(e, c.GetterThrown) ? "thrown-identity" : ReferenceEquals(e, c.LookupFailure) ? "lookup-identity" : "constructed";
        string message = e is DescriptorProofMessageException ? "<adversarial-message-not-read>" : e.Message;
        return e.GetType().FullName + ":" + identity + ":" + message + (e.InnerException == null ? "" : " => " + ExceptionKey(e.InnerException, c));
    }
    static string Preserved(Observation o) => JsonSerializer.Serialize(new
    {
        o.Results, o.Errors, o.Trace, o.Lines, o.Assertions, o.GetterReads, o.SiteReads, o.SiteNameReads, o.OtherWrites, o.Asserts
    });

    static object ExecuteCases(GetValue baseline, GetValue candidate, string template)
    {
        var cases = new List<Scenario>
        {
            new("identity-return", _ => { }, ExpectedReads: 1),
            new("changing-values-no-cache", c => c.ChangingValue = true, Calls: 3, ExpectedReads: 3),
            new("null-property-value", c => c.Value = null, ExpectedReads: 1),
            new("ordinary-property", _ => { }, Target: "ordinary", ExpectedReads: 1),
            new("null-component-assert-and-null-diagnostic", _ => { }, Target: "null"),
            new("repeated-null-preserves-every-diagnostic", _ => { }, Target: "null", Calls: 3),
            new("extender-nonnull", c => c.Extender = true),
            new("extender-null-no-assert", c => c.Extender = true, Target: "null"),
            new("repeated-extender-preserves-every-diagnostic", c => c.Extender = true, Calls: 3),
            new("substituted-component-target", c => c.Substitute = true, Target: "substitute", ExpectedReads: 1),
            new("substituted-ordinary-target", c => c.Substitute = true, Target: "substitute-ordinary", ExpectedReads: 1),
            new("wrong-invocation-target", _ => { }, Target: "wrong", ExpectedError: "System.Reflection.TargetInvocationException"),
            new("substituted-wrong-target", c => { c.Substitute = true; c.InvocationTarget = new object(); }, ExpectedError: "System.Reflection.TargetInvocationException"),
            new("substituted-null-target-error-path-null-dereference", c => c.Substitute = true, ExpectedError: "System.NullReferenceException"),
            new("missing-getter-null", c => c.Getter = null, ExpectedError: "System.Reflection.TargetInvocationException"),
            new("getter-lookup-failure", c => c.LookupFailure = c.BoundaryFailure, ExpectedError: "System.Reflection.TargetInvocationException", ExpectedInner: "boundary-identity"),
            new("throwing-getter-inner-identity", c => c.GetterThrown = c.GetterFailure, ExpectedError: "System.Reflection.TargetInvocationException", ExpectedReads: 1, ExpectedInner: "getter-identity"),
            new("ordinary-throwing-getter", c => c.GetterThrown = c.GetterFailure, Target: "ordinary", ExpectedError: "System.Reflection.TargetInvocationException", ExpectedReads: 1, ExpectedInner: "getter-identity"),
            new("getter-throws-target-invocation-exception-unwrap-once", c => c.GetterThrown = new TargetInvocationException("nested getter wrapper", c.GetterFailure), ExpectedError: "System.Reflection.TargetInvocationException", ExpectedReads: 1, ExpectedInner: "thrown-identity"),
            new("lookup-target-invocation-exception-unwrap", c => c.LookupFailure = new TargetInvocationException("lookup wrapper", c.GetterFailure), ExpectedError: "System.Reflection.TargetInvocationException", ExpectedInner: "getter-identity"),
            new("lookup-target-invocation-exception-null-inner", c => c.LookupFailure = new TargetInvocationException("empty wrapper", null), ExpectedError: "System.NullReferenceException"),
            new("getter-null-message-falls-back-type-name", c => { c.NullMessage = true; c.GetterThrown = new DescriptorProofMessageException(c); }, ExpectedError: "System.Reflection.TargetInvocationException", ExpectedReads: 1, ExpectedInner: "thrown-identity"),
            new("getter-message-callback-failure", c => { c.ThrowMessage = true; c.GetterThrown = new DescriptorProofMessageException(c); }, ExpectedError: "System.InvalidOperationException", ExpectedReads: 1),
            new("named-site-error", c => c.GetterThrown = c.GetterFailure, Target: "site", ExpectedError: "System.Reflection.TargetInvocationException", ExpectedReads: 1, ExpectedInner: "getter-identity"),
            new("null-site-name-falls-back-type", c => { c.GetterThrown = c.GetterFailure; c.SiteNames = new string?[] { null }; }, Target: "site", ExpectedError: "System.Reflection.TargetInvocationException", ExpectedReads: 1),
            new("empty-site-name-is-retained", c => { c.GetterThrown = c.GetterFailure; c.SiteNames = new[] { "" }; }, Target: "site", ExpectedError: "System.Reflection.TargetInvocationException", ExpectedReads: 1),
            new("site-name-read-twice", c => { c.GetterThrown = c.GetterFailure; c.SiteNames = new[] { "first", "second" }; }, Target: "site", ExpectedError: "System.Reflection.TargetInvocationException", ExpectedReads: 1),
            new("second-site-name-null-falls-back-type", c => { c.GetterThrown = c.GetterFailure; c.SiteNames = new string?[] { "first", null }; }, Target: "site", ExpectedError: "System.Reflection.TargetInvocationException", ExpectedReads: 1),
            new("substituted-target-site-used-on-error", c => { c.Substitute = true; c.GetterThrown = c.GetterFailure; }, Target: "substitute-site", ExpectedError: "System.Reflection.TargetInvocationException", ExpectedReads: 1)
        };
        foreach (string boundary in new[] { "is-extender", "assert", "invocation-target", "get-method", "site", "site-name", "name", "error-resource", "error-format" })
        {
            string captured = boundary;
            bool inError = boundary is "site" or "site-name" or "name" or "error-resource" or "error-format";
            cases.Add(new("callback-failure-" + boundary, c => { c.ThrowAt = captured; if (inError) c.GetterThrown = c.GetterFailure; }, Target: inError ? "site" : "component", ExpectedError: boundary == "get-method" ? "System.Reflection.TargetInvocationException" : "System.InvalidOperationException", ExpectedReads: inError ? 1 : 0));
        }
        cases.Add(new("second-site-name-callback-failure", c => { c.GetterThrown = c.GetterFailure; c.ThrowAt = "site-name"; c.ThrowOccurrence = 2; }, Target: "site", ExpectedError: "System.InvalidOperationException", ExpectedReads: 1));
        cases.Add(new("retained-null-diagnostic-failure", c => c.ThrowAt = "write-line", Target: "null", ExpectedError: "System.InvalidOperationException"));
        cases.Add(new("retained-extender-diagnostic-failure", c => { c.Extender = true; c.ThrowAt = "write-line"; }, ExpectedError: "System.InvalidOperationException"));
        cases.Add(new("retained-null-diagnostic-name-failure", c => c.ThrowAt = "name", Target: "null", ExpectedError: "System.InvalidOperationException"));
        var reports = new List<object>();
        (DescriptorProofContext Context, object? Target) Setup(Scenario scenario)
        {
            var c = new DescriptorProofContext { ErrorTemplate = template }; c.Value = c.Identity; scenario.Configure(c);
            var component = new DescriptorProofComponent(c); var ordinary = new DescriptorProofOrdinary(c);
            if (scenario.Target.Contains("ordinary", StringComparison.Ordinal)) c.Getter = typeof(DescriptorProofOrdinary).GetProperty(nameof(DescriptorProofOrdinary.Value))!.GetMethod;
            if (scenario.Target.Contains("site", StringComparison.Ordinal)) c.Site = new DescriptorProofSite(c, component);
            if (scenario.Target.StartsWith("substitute", StringComparison.Ordinal)) c.InvocationTarget = scenario.Target == "substitute-ordinary" ? ordinary : component;
            object? input = scenario.Target switch { "null" => null, "wrong" => new object(), "ordinary" => ordinary, _ when scenario.Target.StartsWith("substitute", StringComparison.Ordinal) => new object(), _ => component };
            return (c, input);
        }
        foreach (var scenario in cases)
        {
            var a = Setup(scenario); var b = Setup(scenario);
            var before = Observe(baseline, a.Context, a.Target, scenario.Calls); var after = Observe(candidate, b.Context, b.Target, scenario.Calls);
            Require(Preserved(before) == Preserved(after), "changed retained result/error/side effects in " + scenario.Name + "\nbaseline=" + Preserved(before) + "\ncandidate=" + Preserved(after));
            Require(before.EntryNames == scenario.Calls && before.Constructors == scenario.Calls && before.Formatted == 2 * scenario.Calls && before.Literals == 3 * scenario.Calls && before.Clears == scenario.Calls && before.EntryWrites == scenario.Calls, "entry operation counts " + scenario.Name);
            Require(after.EntryNames + after.Constructors + after.Formatted + after.Literals + after.Clears + after.EntryWrites == 0, "candidate still performs entry work " + scenario.Name);
            Require(before.EntryLines.Length == scenario.Calls && before.EntryLines.All(line => line == $"[{a.Context.PropertyName}]: GetValue({a.Target?.GetType().Name ?? "(null)"})") && after.EntryLines.Length == 0, "exact entry output removed " + scenario.Name);
            Require(after.GetterReads == scenario.ExpectedReads, "getter side-effect count " + scenario.Name);
            Require(scenario.ExpectedError == null ? after.Errors.All(e => e == "none") : after.Errors.All(e => e.StartsWith(scenario.ExpectedError + ":", StringComparison.Ordinal)), "expected exception class " + scenario.Name);
            if (scenario.ExpectedInner != null) Require(after.Errors.All(e => e.Contains(" => ", StringComparison.Ordinal) && e.Contains(scenario.ExpectedInner, StringComparison.Ordinal)), "exception inner identity " + scenario.Name);
            if (scenario.Name == "changing-values-no-cache") Require(after.Results.SequenceEqual(new[] { "int:1", "int:2", "int:3" }), "values were cached");
            if (scenario.Name is "identity-return" or "ordinary-property" or "substituted-component-target" or "substituted-ordinary-target") Require(after.Results.Single() == "identity", "returned object identity " + scenario.Name);
            if (scenario.Name == "null-property-value") Require(after.Results.Single() == "null", "null getter value");
            if (scenario.Name == "null-component-assert-and-null-diagnostic") Require(after.Assertions.Single() == "False:GetValue must be given a component" && after.OtherWrites == 1, "retained null assertion/diagnostic");
            if (scenario.Name == "extender-null-no-assert") Require(after.Asserts == 0 && after.OtherWrites == 1, "extender precedes assertion");
            if (scenario.Name == "site-name-read-twice") Require(after.SiteNameReads == 2 && after.Errors.Single().Contains("object 'second'", StringComparison.Ordinal), "second site name used");
            if (scenario.Name == "empty-site-name-is-retained") Require(after.Errors.Single().Contains("object ''", StringComparison.Ordinal), "empty site name retained");
            if (scenario.Name == "getter-null-message-falls-back-type-name") Require(after.Errors.Single().Contains("exception:'DescriptorProofMessageException'", StringComparison.Ordinal), "null message type fallback");
            reports.Add(new { scenario.Name, passed = true, baseline = before, candidate = after });
        }
        var diagnosticOnly = new List<object>();
        foreach (string boundary in new[] { "entry-handler", "entry-name", "entry-literal", "entry-formatted", "entry-clear", "entry-write" })
        {
            var scenario = new Scenario("removed-" + boundary, c => c.ThrowAt = boundary);
            var a = Setup(scenario); var b = Setup(scenario);
            var before = Observe(baseline, a.Context, a.Target, 1); var after = Observe(candidate, b.Context, b.Target, 1);
            Require(before.Errors.Single().Contains("boundary-identity", StringComparison.Ordinal) && before.GetterReads == 0 && before.Trace.Length == 0, "baseline diagnostic-only failure escaped before property work: " + boundary);
            Require(after.Errors.Single() == "none" && after.Results.Single() == "identity" && after.GetterReads == 1 && after.EntryWrites == 0, "candidate removed diagnostic-only failure: " + boundary);
            diagnosticOnly.Add(new { boundary, intentionalDifference = "baseline throws exact diagnostic boundary exception before property evaluation; candidate evaluates property successfully", baseline = before, candidate = after });
        }
        return new { propertyScenarioCount = reports.Count, diagnosticOnlyScenarioCount = diagnosticOnly.Count, propertyScenarios = reports, diagnosticOnlyScenarios = diagnosticOnly };
    }

    static object Allocation(GetValue baseline, GetValue candidate, string template)
    {
        const int calls = 10000;
        var c = new DescriptorProofContext { ErrorTemplate = template, Capture = false }; c.Value = c.Identity;
        var host = new DescriptorProofHost(c); var component = new DescriptorProofComponent(c); DescriptorProofDiagnostics.Current = c;
        for (int n = 0; n < 2000; n++) { baseline(host, component); candidate(host, component); }
        long Measure(GetValue method)
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int n = 0; n < calls; n++) if (!ReferenceEquals(method(host, component), c.Identity)) throw new InvalidOperationException("allocation probe changed return identity");
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
        var samples = new List<object>(); var deltas = new List<long>();
        for (int n = 0; n < 5; n++)
        {
            long a, b;
            if ((n & 1) == 0) { a = Measure(baseline); b = Measure(candidate); } else { b = Measure(candidate); a = Measure(baseline); }
            deltas.Add(a - b); samples.Add(new { baselineBytes = a, candidateBytes = b, avoidedBytes = a - b });
        }
        bool deterministic = deltas.Distinct().Count() == 1;
        Require(deltas.All(d => d > 0), "no allocation removal observed in real-handler host probe");
        return new { callsPerSample = calls, samples, deterministicDifference = deterministic, avoidedBytesPerCall = deterministic ? (double?)(deltas[0] / (double)calls) : null, scope = "host .NET allocation only, real handler/string creation with capture disabled; no elapsed timings or Switch gains asserted" };
    }
}
