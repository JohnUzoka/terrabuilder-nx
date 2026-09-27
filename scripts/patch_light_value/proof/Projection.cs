using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;

namespace ColorProof;

public delegate Color ColorBody(int x, int y);
public delegate float BrightnessBody();

internal static class Projection
{
    internal const string OriginalSha = "90b135121829d6650ce0e4f8ddb1395108615aa7d46ae487d279bc3a18698b22";
    internal const string CandidateSha = "4c02e40e6b0502d311764d1c4cb0eaebe0fa17d194c61976250378a90be7e55f";
    internal const string FnaSha = "15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f";
    internal const int Token = 0x06000d45;
    internal static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static void Json(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    internal static string Scope(TypeReference type) => type.Scope is ModuleDefinition module ? module.Assembly.Name.FullName : type.Scope.ToString()!;
    internal static string Qualified(MemberReference member)
    {
        if (member is TypeReference t) return "[" + Scope(t) + "]" + t.FullName;
        if (member is FieldReference f) return Qualified(f.DeclaringType) + "::" + f.Name + "|" + Qualified(f.FieldType);
        var m = (MethodReference)member;
        return Qualified(m.DeclaringType) + "::" + m.Name + "|" + Qualified(m.ReturnType) + "(" + string.Join(",", m.Parameters.Select(p => Qualified(p.ParameterType))) + ")|this=" + m.HasThis + "|explicit=" + m.ExplicitThis + "|cc=" + m.CallingConvention + "|arity=" + m.GenericParameters.Count;
    }
    internal static string Key(Instruction i, MethodDefinition owner, Func<object, object>? map = null)
    {
        string operand = i.Operand switch
        {
            null => "", Instruction b => "instruction:" + owner.Body.Instructions.IndexOf(b),
            Instruction[] bs => "instructions:" + string.Join(",", bs.Select(b => owner.Body.Instructions.IndexOf(b))),
            ParameterDefinition p => "parameter:" + p.Index, VariableDefinition v => "variable:" + v.Index,
            MemberReference m => Qualified((MemberReference)(map == null ? m : map(m))),
            float f => "float-bits:" + BitConverter.SingleToInt32Bits(f).ToString("x8"),
            double d => "double-bits:" + BitConverter.DoubleToInt64Bits(d).ToString("x16"),
            bool b => b ? "bool:true" : "bool:false", char c => "char:" + ((int)c).ToString("x4"),
            string s => "string:" + s, _ => i.Operand.GetType().FullName + ":" + Convert.ToString(i.Operand, CultureInfo.InvariantCulture)
        };
        return i.OpCode.Name + " " + operand;
    }
    internal static string BodyHash(MethodDefinition m) => Sha(Encoding.UTF8.GetBytes(string.Join("\n", new[] { m.Body.InitLocals.ToString(), m.ImplAttributes.ToString(), string.Join(",", m.Body.Variables.Select(v => Qualified(v.VariableType))) }.Concat(m.Body.Instructions.Select(i => Key(i, m))))));
    internal sealed record Loaded(Dictionary<string, ColorBody> Bodies, BrightnessBody OriginalGetter, BrightnessBody CandidateGetter, object Evidence);

    internal static Loaded Build(string originalPath, string candidatePath, string candidateSha, string fnaPath, string output)
    {
        Require(Sha(File.ReadAllBytes(originalPath)) == OriginalSha, "original pin mismatch");
        Require(candidateSha == CandidateSha && Sha(File.ReadAllBytes(candidatePath)) == CandidateSha, "candidate pin mismatch");
        Require(Sha(File.ReadAllBytes(fnaPath)) == FnaSha, "FNA pin mismatch");
        Require(Sha(File.ReadAllBytes(typeof(Color).Assembly.Location)) == FnaSha, "loaded FNA pin mismatch");
        using var original = AssemblyDefinition.ReadAssembly(originalPath);
        using var candidate = AssemblyDefinition.ReadAssembly(candidatePath);
        using var fna = AssemblyDefinition.ReadAssembly(fnaPath);
        Require(original.Name.FullName == candidate.Name.FullName, "candidate assembly qualifier changed");
        Require(original.MainModule.Mvid == new Guid("df61a8d1-0622-9555-82c2-a2118b6c9bc4"), "original MVID mismatch");
        Require(candidate.MainModule.Mvid == new Guid("b6af8788-d1e2-ad83-4d3d-2896548ad830"), "candidate MVID mismatch");
        using var probe = AssemblyDefinition.CreateAssembly(new("Color57ActualBodies", new Version(57, 0)), "Color57ActualBodies", ModuleKind.Dll);
        var module = probe.MainModule;
        var hooks = new TypeDefinition("Probe", "Bodies", TA.Public | TA.Abstract | TA.Sealed, module.TypeSystem.Object);
        module.Types.Add(hooks);
        var hosts = new Dictionary<string, Type> { ["Microsoft.Xna.Framework.Color"] = typeof(Color), ["Microsoft.Xna.Framework.Vector3"] = typeof(Vector3), ["Terraria.Main"] = typeof(Boundary), ["Terraria.Lighting"] = typeof(Boundary), ["Terraria.Graphics.Light.ILightingEngine"] = typeof(IEngine) };
        void VerifyScope(TypeReference t)
        {
            var expected = t.Namespace == "System" ? "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" : t.FullName.StartsWith("Microsoft.Xna.", StringComparison.Ordinal) ? fna.Name.FullName : original.Name.FullName;
            Require(Scope(t) == expected, "unapproved source qualifier " + Qualified(t));
        }
        TypeReference MapType(TypeReference t)
        {
            VerifyScope(t);
            if (hosts.TryGetValue(t.FullName, out var host)) return module.ImportReference(host);
            return t.MetadataType switch
            {
                MetadataType.Void => module.TypeSystem.Void, MetadataType.Boolean => module.TypeSystem.Boolean,
                MetadataType.Char => module.TypeSystem.Char, MetadataType.Int32 => module.TypeSystem.Int32,
                MetadataType.UInt32 => module.TypeSystem.UInt32, MetadataType.Single => module.TypeSystem.Single,
                _ => throw new InvalidDataException("unmapped type " + Qualified(t))
            };
        }
        bool Compatible(TypeReference reflected, TypeReference mapped)
        {
            if (Qualified(reflected) == Qualified(mapped)) return true;
            return reflected.FullName == mapped.FullName && reflected.MetadataType == mapped.MetadataType &&
                mapped.MetadataType is MetadataType.Void or MetadataType.Boolean or MetadataType.Char or MetadataType.Int32 or MetadataType.UInt32 or MetadataType.Single &&
                Scope(reflected) == typeof(object).Assembly.FullName && Scope(mapped) == module.TypeSystem.CoreLibrary.ToString();
        }
        var getters = new Dictionary<bool, MethodDefinition>();
        object Map(object value, bool candidateSide, bool stress)
        {
            if (value is TypeReference t) return MapType(t);
            if (value is FieldReference f)
            {
                VerifyScope(f.DeclaringType);
                string name = (f.DeclaringType.FullName, f.Name) switch
                {
                    ("Terraria.Main", "gameMenu") => nameof(Boundary.Menu),
                    ("Terraria.Lighting", "_activeEngine") => nameof(Boundary.ActiveEngine),
                    ("Terraria.Lighting", "<GlobalBrightness>k__BackingField") => nameof(Boundary.Brightness),
                    ("Microsoft.Xna.Framework.Vector3", "X" or "Y" or "Z") => f.Name,
                    _ => throw new InvalidDataException("unmapped field " + Qualified(f))
                };
                var field = hosts[f.DeclaringType.FullName].GetField(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)!;
                var mapped = module.ImportReference(field);
                Require(Compatible(mapped.FieldType, MapType(f.FieldType)), "mapped field type differs");
                mapped.FieldType = MapType(f.FieldType);
                return mapped;
            }
            if (value is MethodReference m)
            {
                VerifyScope(m.DeclaringType);
                MethodReference mapped;
                if (m.DeclaringType.FullName == "Terraria.Lighting" && m.Name == "get_GlobalBrightness") mapped = stress ? module.ImportReference(typeof(Boundary).GetMethod(nameof(Boundary.ReadBrightness))!) : getters[candidateSide];
                else
                {
                    Require(hosts.TryGetValue(m.DeclaringType.FullName, out var host), "unmapped method owner");
                    var matches = host!.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).Where(x => x.Name == m.Name && x.IsStatic != m.HasThis && x.GetParameters().Length == m.Parameters.Count && x.GetParameters().Zip(m.Parameters).All(p => Compatible(module.ImportReference(p.First.ParameterType), MapType(p.Second.ParameterType)))).ToArray();
                    Require(matches.Length == 1, "unmapped or ambiguous method " + Qualified(m));
                    mapped = module.ImportReference(matches[0]);
                }
                Require(mapped.HasThis == m.HasThis && mapped.ExplicitThis == m.ExplicitThis && mapped.CallingConvention == m.CallingConvention && Compatible(mapped.ReturnType, MapType(m.ReturnType)) && mapped.Parameters.Count == m.Parameters.Count, "mapped method signature differs " + Qualified(m));
                mapped.ReturnType = MapType(m.ReturnType);
                for (int n = 0; n < m.Parameters.Count; n++)
                {
                    Require(Compatible(mapped.Parameters[n].ParameterType, MapType(m.Parameters[n].ParameterType)), "mapped parameter differs");
                    mapped.Parameters[n].ParameterType = MapType(m.Parameters[n].ParameterType);
                }
                return mapped;
            }
            return value;
        }
        var pairs = new List<(MethodDefinition Source, MethodDefinition Target, bool Candidate, bool Stress)>();
        MethodDefinition Copy(MethodDefinition source, string name, bool candidateSide, bool stress)
        {
            Require(source.HasBody && !source.HasThis && source.Body.ExceptionHandlers.Count == 0, "unsupported source target shape");
            var target = new MethodDefinition(name, MA.Public | MA.Static, MapType(source.ReturnType)) { ImplAttributes = source.ImplAttributes };
            hooks.Methods.Add(target);
            foreach (var p in source.Parameters) target.Parameters.Add(new(p.Name, p.Attributes, MapType(p.ParameterType)));
            target.Body.InitLocals = source.Body.InitLocals;
            target.Body.MaxStackSize = source.Body.MaxStackSize;
            foreach (var v in source.Body.Variables) target.Body.Variables.Add(new(MapType(v.VariableType)));
            foreach (var i in source.Body.Instructions) target.Body.Instructions.Add(Instruction.Create(OpCodes.Nop));
            for (int n = 0; n < source.Body.Instructions.Count; n++)
            {
                var i = source.Body.Instructions[n]; var dest = target.Body.Instructions[n]; dest.OpCode = i.OpCode;
                dest.Operand = i.Operand switch
                {
                    null => null, Instruction b => target.Body.Instructions[source.Body.Instructions.IndexOf(b)],
                    Instruction[] bs => bs.Select(b => target.Body.Instructions[source.Body.Instructions.IndexOf(b)]).ToArray(),
                    VariableDefinition v => target.Body.Variables[v.Index], ParameterDefinition p => target.Parameters[p.Index],
                    _ => Map(i.Operand, candidateSide, stress)
                };
            }
            pairs.Add((source, target, candidateSide, stress)); return target;
        }
        foreach (bool c in new[] { false, true })
        {
            var getter = (MethodDefinition)(c ? candidate : original).MainModule.LookupToken(0x06000d2e);
            Require(getter.FullName == "System.Single Terraria.Lighting::get_GlobalBrightness()" && getter.Body.Instructions.Count == 2 && getter.Body.Instructions[0].OpCode.Code == Code.Ldsfld && getter.Body.Instructions[1].OpCode.Code == Code.Ret, "brightness getter is not expected field accessor");
            getters[c] = Copy(getter, c ? "CandidateGetter" : "OriginalGetter", c, false);
        }
        foreach (var (c, name, stress) in new[] { (false, "Original", false), (true, "Candidate", false), (false, "Replica", false), (false, "OriginalStress", true), (true, "CandidateStress", true) })
        {
            var source = (MethodDefinition)(c ? candidate : original).MainModule.LookupToken(Token);
            Require(source.FullName == "Microsoft.Xna.Framework.Color Terraria.Lighting::GetColor(System.Int32,System.Int32)", "unexpected target identity");
            if (!c) Require(source.Body.Instructions.Count == 65 && source.Body.Variables.Count == 5, "unexpected exact52 body shape");
            else Require(source.Body.Instructions.Count == 67 && source.Body.Variables.Count == 6, "unexpected exact57 body shape");
            Copy(source, name, c, stress);
        }
        module.Mvid = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", pairs.Select(p => BodyHash(p.Target))))).AsSpan(0, 16));
        using var stream = new MemoryStream(); probe.Write(stream, new WriterParameters { Timestamp = 0 }); var bytes = stream.ToArray();
        using var serialized = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        var receipts = new List<object>();
        foreach (var pair in pairs)
        {
            var actual = serialized.MainModule.Types.Single(t => t.FullName == "Probe.Bodies").Methods.Single(m => m.Name == pair.Target.Name);
            var source = pair.Source;
            Require(BodyHash(actual) == BodyHash(pair.Target), "serialized body fingerprint changed");
            Require(actual.Body.Instructions.Count == source.Body.Instructions.Count && actual.Body.Variables.Count == source.Body.Variables.Count && actual.Body.InitLocals == source.Body.InitLocals && actual.ImplAttributes == source.ImplAttributes && actual.Body.ExceptionHandlers.Count == 0, "serialized target shape differs");
            Require(actual.Parameters.Count == source.Parameters.Count && Qualified(actual.ReturnType) == Qualified(MapType(source.ReturnType)), "serialized signature differs");
            foreach (var p in source.Parameters) Require(Qualified(actual.Parameters[p.Index].ParameterType) == Qualified(MapType(p.ParameterType)), "serialized parameter type differs");
            foreach (var v in source.Body.Variables) Require(Qualified(actual.Body.Variables[v.Index].VariableType) == Qualified(MapType(v.VariableType)), "serialized local type differs");
            var rows = source.Body.Instructions.Select((i, n) => new { index = n, offset = i.Offset, source = Key(i, source), expected = Key(i, source, x => Map(x, pair.Candidate, pair.Stress)), actual = Key(actual.Body.Instructions[n], actual) }).ToArray();
            foreach (var row in rows) Require(row.expected == row.actual, "instruction mismatch " + actual.Name + " @" + row.index);
            receipts.Add(new { name = actual.Name, sourceToken = source.MetadataToken.ToInt32().ToString("x8"), source = Qualified(source), sourceMvid = source.Module.Mvid, sourceSha256 = pair.Candidate ? candidateSha : OriginalSha, sourceBodySha256 = BodyHash(source), projectedBodySha256 = BodyHash(actual), initLocals = source.Body.InitLocals, locals = source.Body.Variables.Select(v => new { source = Qualified(v.VariableType), mapped = Qualified(actual.Body.Variables[v.Index].VariableType) }).ToArray(), instructions = rows });
        }
        File.WriteAllBytes(Path.Combine(output, "Color57ActualBodies.dll"), bytes);
        var loaded = Assembly.Load(bytes).GetType("Probe.Bodies", true)!;
        var delegates = new Dictionary<string, ColorBody>();
        foreach (var name in new[] { "Original", "Candidate", "Replica", "OriginalStress", "CandidateStress" }) delegates[name] = loaded.GetMethod(name)!.CreateDelegate<ColorBody>();
        var fnaBodies = fna.MainModule.Types.Where(t => t.FullName is "Microsoft.Xna.Framework.Color" or "Microsoft.Xna.Framework.Vector3").Select(t => new { type = Qualified(t), fields = t.Fields.Where(f => !f.IsStatic).Select(f => new { field = Qualified(f), attributes = f.Attributes.ToString(), offset = f.Offset }).ToArray(), layout = t.Attributes.ToString(), accessors = t.Methods.Where(m => t.Name == "Color" && (m.Name is "get_White" or "set_PackedValue" or "get_PackedValue" or ".cctor")).Select(m => new { source = Qualified(m), token = m.MetadataToken.ToInt32().ToString("x8"), bodySha256 = BodyHash(m), instructions = m.Body.Instructions.Select(i => Key(i, m)).ToArray() }).ToArray() }).ToArray();
        return new(delegates, loaded.GetMethod("OriginalGetter")!.CreateDelegate<BrightnessBody>(), loaded.GetMethod("CandidateGetter")!.CreateDelegate<BrightnessBody>(), new { passed = true, originalPath, candidatePath, originalSha256 = OriginalSha, candidateSha256 = candidateSha, fnaSha256 = FnaSha, loadedFna = typeof(Color).Assembly.Location, probeSha256 = Sha(bytes), exactOpcodeComparison = true, normalization = "None: every opcode, constant type/bit pattern, branch target index, local, parameter and qualified member is compared after serialization. Only explicit source-qualified boundary member/type remapping occurs.", receipts, fnaBodies });
    }

    internal static (string Name, ColorBody Body, object Evidence)[] Controls(string output)
    {
        var result = new List<(string, ColorBody, object)>();
        foreach (string kind in new[] { "wrong-channel", "wrong-clamp", "wrong-coordinate", "duplicate-engine-call", "early-brightness" })
        {
            using var image = AssemblyDefinition.ReadAssembly(Path.Combine(output, "Color57ActualBodies.dll"));
            image.Name.Name = "ColorControl_" + kind; image.MainModule.Name = image.Name.Name; image.MainModule.Mvid = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(kind)).AsSpan(0, 16));
            var method = image.MainModule.Types.Single(t => t.FullName == "Probe.Bodies").Methods.Single(m => m.Name == "OriginalStress");
            var il = method.Body.Instructions; var proc = method.Body.GetILProcessor(); string before = BodyHash(method);
            if (kind == "wrong-channel")
            {
                var i = il.Single(i => i.Operand is FieldReference f && f.Name == "X");
                var field = image.MainModule.ImportReference(typeof(Vector3).GetField("Y")!);
                field.FieldType = image.MainModule.TypeSystem.Single; i.Operand = field;
            }
            else if (kind == "wrong-clamp")
            {
                var i = il.Where(i => i.OpCode.Code == Code.Ldc_I4 && (int)i.Operand == 255).Skip(1).First(); i.Operand = 254;
            }
            else if (kind == "wrong-coordinate")
            {
                il.First(i => i.OpCode.Code == Code.Ldarg_0).OpCode = OpCodes.Ldarg_1;
            }
            else if (kind == "duplicate-engine-call")
            {
                var engine = il.Single(i => i.Operand is FieldReference f && f.Name == nameof(Boundary.ActiveEngine));
                var call = il.Single(i => i.Operand is MethodReference m && m.Name == "GetColor");
                foreach (var i in new[] { Instruction.Create(OpCodes.Ldsfld, (FieldReference)engine.Operand), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Callvirt, (MethodReference)call.Operand), Instruction.Create(OpCodes.Pop) }) proc.InsertBefore(engine, i);
            }
            else
            {
                var getter = il.Single(i => i.Operand is MethodReference m && m.Name == nameof(Boundary.ReadBrightness));
                var engine = il.Single(i => i.Operand is FieldReference f && f.Name == nameof(Boundary.ActiveEngine));
                var local = new VariableDefinition(image.MainModule.TypeSystem.Single); method.Body.Variables.Add(local);
                proc.InsertBefore(engine, Instruction.Create(OpCodes.Call, (MethodReference)getter.Operand)); proc.InsertBefore(engine, Instruction.Create(OpCodes.Stloc, local));
                getter.OpCode = OpCodes.Ldloc; getter.Operand = local;
            }
            foreach (var i in il) if (i.OpCode.Code == Code.Brfalse_S) i.OpCode = OpCodes.Brfalse;
            using var ms = new MemoryStream(); image.Write(ms, new WriterParameters { Timestamp = 0 }); var bytes = ms.ToArray();
            File.WriteAllBytes(Path.Combine(output, "Control-" + kind + ".dll"), bytes);
            using var check = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes)); var checkedBody = check.MainModule.Types.Single(t => t.FullName == "Probe.Bodies").Methods.Single(m => m.Name == "OriginalStress");
            Require(BodyHash(method) == BodyHash(checkedBody) && before != BodyHash(checkedBody), "negative control serialization failed");
            var body = Assembly.Load(bytes).GetType("Probe.Bodies")!.GetMethod("OriginalStress")!.CreateDelegate<ColorBody>();
            result.Add((kind, body, new { kind, intentionalMutation = true, before, after = BodyHash(checkedBody), imageSha256 = Sha(bytes) }));
        }
        return result.ToArray();
    }
}
