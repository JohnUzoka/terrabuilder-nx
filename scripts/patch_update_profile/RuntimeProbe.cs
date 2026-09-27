using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;
using FA = Mono.Cecil.FieldAttributes;

internal static class RuntimeProbe
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    internal static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("Update runtime probe: " + message); }
    static IEnumerable<TypeDefinition> Types(TypeDefinition type) { yield return type; foreach (var nested in type.NestedTypes.SelectMany(Types)) yield return nested; }
    internal static byte[] Build(string candidatePath, List<object> receipts)
    {
        using var candidate = AssemblyDefinition.ReadAssembly(candidatePath);
        using var compiledSource = AssemblyDefinition.ReadAssembly(typeof(Update56Template).Assembly.Location);
        var template = compiledSource.MainModule.Types.Single(t => t.FullName == nameof(Update56Template));
        using var probe = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Update56RuntimeProbe", new Version(56, 0)), "Update56RuntimeProbe", ModuleKind.Dll);
        var module = probe.MainModule;
        var root = candidate.MainModule.Types.Single(t => t.FullName == "Terraria.NXUpdateProfile56");
        var types = new Dictionary<string, TypeDefinition>();
        var methods = new Dictionary<string, MethodDefinition>();
        var copied = new List<(MethodDefinition Source, MethodDefinition Target)>();
        var boundaries = new List<object>();
        var hosts = new Dictionary<string, Type> {
            ["Terraria.Main"] = typeof(RuntimeMain), ["Terraria.Entity"] = typeof(RuntimeEntity),
            ["Terraria.Player"] = typeof(RuntimePlayer), ["Terraria.Item"] = typeof(RuntimeItem),
            ["Microsoft.Xna.Framework.Vector2"] = typeof(RuntimeVector), ["Terraria.Enums.FrameSkipMode"] = typeof(int)
        };
        foreach (var source in Types(root))
        {
            var target = new TypeDefinition("Probe", source == root ? "UpdateRuntime" : "UpdateRuntime_" + source.Name,
                TA.Public | (source.IsValueType ? TA.SequentialLayout | TA.Sealed : TA.Abstract | TA.Sealed),
                source.IsValueType ? module.ImportReference(typeof(ValueType)) : module.TypeSystem.Object);
            module.Types.Add(target); types.Add(source.FullName, target);
        }
        TypeReference MapType(TypeReference source)
        {
            if (types.TryGetValue(source.FullName, out var target)) return target;
            if (hosts.TryGetValue(source.FullName, out var host))
            {
                bool framework = source.FullName.StartsWith("Microsoft.Xna.", StringComparison.Ordinal);
                Require(framework ? source.Scope is AssemblyNameReference frameworkScope && frameworkScope.Name == "FNA" :
                    source.Scope == candidate.MainModule || source.Scope is AssemblyNameReference gameScope && gameScope.FullName == candidate.Name.FullName,
                    "unexpected game/framework source scope " + source.FullName + " @ " + source.Scope);
                Require(source.IsValueType == host.IsValueType, "CLASS/VALUETYPE boundary mismatch " + source.FullName);
                return module.ImportReference(host);
            }
            if (source is ArrayType array)
            {
                var value = new ArrayType(MapType(array.ElementType), array.Rank);
                for (int i = 0; i < array.Rank; i++) value.Dimensions[i] = new ArrayDimension(array.Dimensions[i].LowerBound, array.Dimensions[i].UpperBound);
                return value;
            }
            if (source is ByReferenceType byref) return new ByReferenceType(MapType(byref.ElementType));
            if (source is PointerType pointer) return new PointerType(MapType(pointer.ElementType));
            if (source is GenericInstanceType generic)
            {
                var value = new GenericInstanceType(MapType(generic.ElementType));
                foreach (var argument in generic.GenericArguments) value.GenericArguments.Add(MapType(argument));
                return value;
            }
            if (source is GenericParameter) return source;
            Require(source.Namespace == "System" || source.Namespace.StartsWith("System.", StringComparison.Ordinal), "unmapped candidate type " + source.FullName);
            return source.FullName switch {
                "System.Void" => module.TypeSystem.Void, "System.Boolean" => module.TypeSystem.Boolean,
                "System.Byte" => module.TypeSystem.Byte, "System.SByte" => module.TypeSystem.SByte,
                "System.Char" => module.TypeSystem.Char, "System.Int16" => module.TypeSystem.Int16,
                "System.UInt16" => module.TypeSystem.UInt16, "System.Int32" => module.TypeSystem.Int32,
                "System.UInt32" => module.TypeSystem.UInt32, "System.Int64" => module.TypeSystem.Int64,
                "System.UInt64" => module.TypeSystem.UInt64, "System.Single" => module.TypeSystem.Single,
                "System.Double" => module.TypeSystem.Double, "System.String" => module.TypeSystem.String,
                "System.Object" => module.TypeSystem.Object, "System.IntPtr" => module.TypeSystem.IntPtr,
                "System.UIntPtr" => module.TypeSystem.UIntPtr, _ => module.ImportReference(source)
            };
        }
        var runtimeNames = typeof(Update56Template).GetMethods(Flags).Where(m => m.DeclaringType == typeof(Update56Template)).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var source in Types(root))
        {
            var target = types[source.FullName];
            foreach (var field in source.Fields)
            {
                var value = new FieldDefinition(field.Name, (field.Attributes & ~FA.FieldAccessMask) | FA.Public, MapType(field.FieldType));
                if (field.HasConstant) value.Constant = field.Constant;
                foreach (var attribute in field.CustomAttributes)
                {
                    Require(attribute.AttributeType.FullName == "System.ThreadStaticAttribute", "unsupported runtime field attribute");
                    value.CustomAttributes.Add(new CustomAttribute(module.ImportReference(typeof(ThreadStaticAttribute).GetConstructor(Type.EmptyTypes)!)));
                }
                target.Fields.Add(value);
            }
            foreach (var method in source.Methods)
            {
                Require(method.HasBody && !method.HasGenericParameters, "unsupported candidate runtime method " + method.FullName);
                var value = new MethodDefinition(method.Name, (method.Attributes & ~MA.MemberAccessMask) | MA.Public, MapType(method.ReturnType)) { ImplAttributes = method.ImplAttributes };
                foreach (var parameter in method.Parameters) value.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, MapType(parameter.ParameterType)));
                target.Methods.Add(value); methods.Add(method.FullName, value); copied.Add((method, value));
            }
        }
        Require(runtimeNames.SetEquals(copied.Where(p => p.Source.DeclaringType == root).Select(p => p.Source.Name)), "candidate runtime method set differs from compiled source contract");
        object MapMember(object value)
        {
            if (value is TypeReference type) return MapType(type);
            if (value is FieldReference field)
            {
                if (types.TryGetValue(field.DeclaringType.FullName, out var owner)) return owner.Fields.Single(f => f.Name == field.Name && f.FieldType.FullName == MapType(field.FieldType).FullName);
                if (hosts.TryGetValue(field.DeclaringType.FullName, out var host))
                {
                    MapType(field.DeclaringType);
                    var match = host.GetField(field.Name, Flags) ?? throw new InvalidOperationException("Missing runtime field boundary " + field.FullName);
                    var mapped = module.ImportReference(match);
                    Require(mapped.FieldType.FullName == MapType(field.FieldType).FullName, "field boundary type differs " + field.FullName);
                    boundaries.Add(new { source = field.FullName, target = mapped.FullName }); return mapped;
                }
                return new FieldReference(field.Name, MapType(field.FieldType), MapType(field.DeclaringType));
            }
            if (value is GenericInstanceMethod generic)
            {
                var mapped = new GenericInstanceMethod((MethodReference)MapMember(generic.ElementMethod));
                foreach (var argument in generic.GenericArguments) mapped.GenericArguments.Add(MapType(argument));
                return mapped;
            }
            if (value is MethodReference method)
            {
                if (methods.TryGetValue(method.FullName, out var exact)) return exact;
                MethodReference? boundary = null;
                if (method.DeclaringType.FullName == "System.Diagnostics.Stopwatch" && method.Name == "GetTimestamp" && method.Parameters.Count == 0)
                    boundary = module.ImportReference(typeof(RuntimeFixture).GetMethod(nameof(RuntimeFixture.Clock))!);
                else if (method.DeclaringType.FullName == "System.Console" && method.Name == "Write" && method.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.Char[]", "System.Int32", "System.Int32" }))
                    boundary = module.ImportReference(typeof(RuntimeFixture).GetMethod(nameof(RuntimeFixture.Write))!);
                else if (hosts.TryGetValue(method.DeclaringType.FullName, out var host))
                {
                    MapType(method.DeclaringType);
                    var matches = host.GetMethods(Flags).Where(m => m.Name == method.Name && m.GetParameters().Select(p => module.ImportReference(p.ParameterType).FullName).SequenceEqual(method.Parameters.Select(p => MapType(p.ParameterType).FullName))).ToArray();
                    Require(matches.Length == 1, "ambiguous runtime method boundary " + method.FullName);
                    boundary = module.ImportReference(matches[0]);
                }
                if (boundary != null)
                {
                    Require(boundary.HasThis == method.HasThis && boundary.ReturnType.FullName == MapType(method.ReturnType).FullName, "method boundary signature differs " + method.FullName);
                    boundaries.Add(new { source = method.FullName, target = boundary.FullName }); return boundary;
                }
                var result = new MethodReference(method.Name, MapType(method.ReturnType), MapType(method.DeclaringType)) { HasThis = method.HasThis, ExplicitThis = method.ExplicitThis, CallingConvention = method.CallingConvention };
                foreach (var parameter in method.Parameters) result.Parameters.Add(new ParameterDefinition(MapType(parameter.ParameterType)));
                foreach (var parameter in method.GenericParameters) result.GenericParameters.Add(new GenericParameter(parameter.Name, result));
                return result;
            }
            return value;
        }
        foreach (var (source, target) in copied)
        {
            CopyBody(source, target, MapType, MapMember);
            for (int i = 0; i < source.Body.Instructions.Count; i++)
            {
                if (source.Body.Instructions[i].Operand is FieldReference { Name: "Frequency", DeclaringType.FullName: "System.Diagnostics.Stopwatch" } frequency)
                {
                    Require(source.Body.Instructions[i].OpCode == OpCodes.Ldsfld && frequency.FieldType.FullName == "System.Int64", "unexpected frequency binding");
                    target.Body.Instructions[i].OpCode = OpCodes.Call; target.Body.Instructions[i].Operand = module.ImportReference(typeof(RuntimeFixture).GetMethod(nameof(RuntimeFixture.Frequency))!);
                    boundaries.Add(new { source = frequency.FullName, target = "RuntimeFixture.Frequency" });
                }
            }
            Require(target.Body.Instructions.Count == source.Body.Instructions.Count && target.Body.Variables.Count == source.Body.Variables.Count && target.Body.ExceptionHandlers.Count == source.Body.ExceptionHandlers.Count, "runtime body shape changed");
            receipts.Add(new { source = source.FullName, sourceMvid = source.Module.Mvid, sourceBodySha256 = Fingerprint(source),
                compiledTemplateMvid = compiledSource.MainModule.Mvid,
                compiledTemplateBodySha256 = source.DeclaringType == root ? Fingerprint(template.Methods.Single(m => m.Name == source.Name)) : null,
                mapped = target.FullName, mappedBodySha256 = Fingerprint(target), instructions = source.Body.Instructions.Count, locals = source.Body.Variables.Count, handlers = source.Body.ExceptionHandlers.Count });
        }
        module.Mvid = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", copied.Select(p => Fingerprint(p.Target))))).AsSpan(0, 16));
        using var stream = new MemoryStream(); probe.Write(stream, new WriterParameters { Timestamp = 0 }); var bytes = stream.ToArray();
        using var serialized = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        foreach (var (_, target) in copied)
        {
            var actual = serialized.MainModule.Types.SelectMany(t => t.Methods).Single(m => m.FullName == target.FullName);
            Require(Fingerprint(actual) == Fingerprint(target), "serialized mapped body differs " + target.FullName);
        }
        receipts.Add(new { kind = "boundary-map", boundaries });
        return bytes;
    }
    static void CopyBody(MethodDefinition source, MethodDefinition target, Func<TypeReference, TypeReference> mapType, Func<object, object> mapMember)
    {
        target.Body = new Mono.Cecil.Cil.MethodBody(target) { InitLocals = source.Body.InitLocals, MaxStackSize = source.Body.MaxStackSize };
        foreach (var variable in source.Body.Variables) target.Body.Variables.Add(new VariableDefinition(mapType(variable.VariableType)));
        var instructions = new Dictionary<Instruction, Instruction>();
        foreach (var instruction in source.Body.Instructions) { var copy = Instruction.Create(OpCodes.Nop); copy.OpCode = instruction.OpCode; target.Body.Instructions.Add(copy); instructions.Add(instruction, copy); }
        foreach (var instruction in source.Body.Instructions)
            instructions[instruction].Operand = instruction.Operand switch {
                null => null, Instruction branch => instructions[branch], Instruction[] branches => branches.Select(b => instructions[b]).ToArray(),
                VariableDefinition variable => target.Body.Variables[variable.Index], ParameterDefinition parameter => target.Parameters[parameter.Index],
                var other => mapMember(other)
            };
        Instruction? At(Instruction? instruction) => instruction == null ? null : instructions[instruction];
        foreach (var handler in source.Body.ExceptionHandlers) target.Body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType) {
            TryStart = At(handler.TryStart), TryEnd = At(handler.TryEnd), HandlerStart = At(handler.HandlerStart), HandlerEnd = At(handler.HandlerEnd),
            FilterStart = At(handler.FilterStart), CatchType = handler.CatchType == null ? null : mapType(handler.CatchType)
        });
    }
    static string Fingerprint(MethodDefinition method)
    {
        var body = method.Body; var positions = body.Instructions.Select((i, n) => (i, n)).ToDictionary(p => p.i, p => p.n);
        int At(Instruction? instruction) => instruction == null ? -1 : positions[instruction];
        string Operand(object? value) => value switch {
            null => "", Instruction instruction => "@" + At(instruction), Instruction[] instructions => string.Join(",", instructions.Select(At)),
            VariableDefinition variable => "local:" + variable.Index, ParameterDefinition parameter => "arg:" + parameter.Index,
            MemberReference member => member.FullName, string text => "string:" + text, _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
        };
        string text = body.InitLocals + "\n" + string.Join("\n", body.Variables.Select(v => v.VariableType.FullName)) + "\n" +
            string.Join("\n", body.Instructions.Select(i => i.OpCode.Code + " " + Operand(i.Operand))) + "\n" +
            string.Join("\n", body.ExceptionHandlers.Select(h => $"{h.HandlerType}:{At(h.TryStart)}:{At(h.TryEnd)}:{At(h.HandlerStart)}:{At(h.HandlerEnd)}:{At(h.FilterStart)}:{h.CatchType?.FullName}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
