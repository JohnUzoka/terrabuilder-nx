using System.Reflection;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;
using FA = Mono.Cecil.FieldAttributes;

// Patch-time only: the complete serialized bodies execute against the reviewed
// cost45 boundaries. The three changed-region primitive bodies come from FNA.
internal static class DrawGateProbe
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static byte[] Build(AssemblyDefinition baseline, AssemblyDefinition candidate, AssemblyDefinition fna, List<object> receipts, string fault = "")
    {
        using var probe = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("DrawGateExactProbe" + fault, new Version(50, 0)), "DrawGateExactProbe" + fault, ModuleKind.Dll);
        var module = probe.MainModule;
        var hooks = new TypeDefinition("Probe", "Hooks", TA.Public | TA.Abstract | TA.Sealed, module.TypeSystem.Object); module.Types.Add(hooks);
        var hosts = new Dictionary<string, Type> {
            ["Terraria.GameContent.Drawing.TileDrawing"] = typeof(CostDrawing), ["Terraria.GameContent.Drawing.TileDrawingBase"] = typeof(CostDrawing),
            ["Terraria.Main"] = typeof(CostWorld), ["Terraria.Tile"] = typeof(CostCell), ["Terraria.Player"] = typeof(CostPlayer), ["Terraria.HitTile"] = typeof(object),
            ["Terraria.TimeLogger/TimeLogData"] = typeof(FixtureMetric), ["Terraria.TimeLogger/DataSeries"] = typeof(FixtureSeries), ["Terraria.TimeLogger"] = typeof(CostWorld),
            ["Microsoft.Xna.Framework.Vector2"] = typeof(CostVec), ["Microsoft.Xna.Framework.Vector3"] = typeof(CostVec3), ["Microsoft.Xna.Framework.Vector4"] = typeof(CostVec4), ["Microsoft.Xna.Framework.Point"] = typeof(CostPoint), ["Microsoft.Xna.Framework.Rectangle"] = typeof(CostRect), ["Microsoft.Xna.Framework.Color"] = typeof(CostColor), ["Microsoft.Xna.Framework.Graphics.SpriteEffects"] = typeof(int),
            ["Terraria.GameContent.TilePaintSystemV2/TileVariationkey"] = typeof(TileKey), ["Terraria.GameContent.Drawing.DrawBlackHelper"] = typeof(CostBlack),
            ["Terraria.Graphics.Camera"] = typeof(CostCamera), ["Terraria.SceneMetrics"] = typeof(CostScene), ["Terraria.Graphics.TileBatch"] = typeof(CostBatch), ["Terraria.Graphics.VertexColors"] = typeof(CostVertices),
            ["Terraria.Testing.DebugOptions"] = typeof(TileDebug), ["Terraria.FocusHelper"] = typeof(CostFocus), ["Terraria.GameContent.TextureAssets"] = typeof(CostTextures),
            ["ReLogic.Content.Asset`1<Microsoft.Xna.Framework.Graphics.Texture2D>"] = typeof(CostAsset), ["Microsoft.Xna.Framework.Graphics.Texture2D"] = typeof(CostTexture),
            ["Microsoft.Xna.Framework.Graphics.SpriteBatch"] = typeof(object), ["Terraria.DataStructures.TileObjectPreviewData"] = typeof(TilePreview), ["Terraria.TileObject"] = typeof(CostObject),
            ["Terraria.Graphics.Capture.CaptureManager"] = typeof(TileCapture), ["Terraria.GameContent.Drawing.TileDrawing/TileCounterType"] = typeof(int),
            ["Terraria.ID.TileID/Sets"] = typeof(CostSets), ["Terraria.Lighting"] = typeof(CostLighting), ["Terraria.Dust"] = typeof(CostDust), ["Terraria.Utilities.UnifiedRandom"] = typeof(CostRandom), ["Terraria.Utilities.FastRandom"] = typeof(CostFastRandom),
            ["Terraria.Utils"] = typeof(CostUtils), ["Terraria.WorldGen"] = typeof(CostUtils), ["Terraria.GameContent.Liquid.LiquidRenderer"] = typeof(CostUtils), ["Terraria.GameContent.PortalHelper"] = typeof(CostUtils),
            ["Terraria.Graphics.Effects.Filters"] = typeof(CostFilters), ["Terraria.Graphics.Effects.FilterManager"] = typeof(CostFilterManager), ["Terraria.Graphics.Effects.EffectManager`1<Terraria.Graphics.Effects.Filter>"] = typeof(CostFilterManager), ["Terraria.Graphics.Effects.Filter"] = typeof(CostFilter), ["Terraria.Graphics.Effects.GameEffect"] = typeof(CostFilter)
        };
        var sourceScratch = Types(baseline.MainModule).Single(t => t.FullName == "Terraria.DataStructures.TileDrawInfo");
        var scratch = new TypeDefinition("Probe", "Scratch", TA.Public, module.TypeSystem.Object); module.Types.Add(scratch);
        TypeReference TypeMap(TypeReference t)
        {
            if (t.FullName == sourceScratch.FullName) return scratch;
            if (hosts.TryGetValue(t.FullName, out var host)) return module.ImportReference(host);
            if (t is GenericParameter) return t;
            if (t is ArrayType a) { var copy = new ArrayType(TypeMap(a.ElementType), a.Rank); for (int i = 0; i < a.Rank; i++) copy.Dimensions[i] = new ArrayDimension(a.Dimensions[i].LowerBound, a.Dimensions[i].UpperBound); return copy; }
            if (t is ByReferenceType r) return new ByReferenceType(TypeMap(r.ElementType));
            if (t is GenericInstanceType g) { var copy = new GenericInstanceType(TypeMap(g.ElementType)); foreach (var x in g.GenericArguments) copy.GenericArguments.Add(TypeMap(x)); return copy; }
            Require(t.Namespace.StartsWith("System"), "unmapped gate50 fixture type " + t.FullName);
            if (t.MetadataType is >= MetadataType.Void and <= MetadataType.String or MetadataType.Object or MetadataType.IntPtr or MetadataType.UIntPtr) return Primitive(module, t.FullName);
            return module.ImportReference(t);
        }
        foreach (var f in sourceScratch.Fields) scratch.Fields.Add(new FieldDefinition(f.Name, (f.Attributes & ~FA.FieldAccessMask) | FA.Public, TypeMap(f.FieldType)));
        var sourceCtor = sourceScratch.Methods.Single(m => m.IsConstructor && !m.IsStatic);
        var ctor = new MethodDefinition(".ctor", MA.Public | MA.HideBySig | MA.SpecialName | MA.RTSpecialName, module.TypeSystem.Void); scratch.Methods.Add(ctor);
        var copies = new List<(MethodDefinition source, MethodDefinition target, string kind)> { (sourceCtor, ctor, "OriginalScratchConstructor") };
        var singles = new Dictionary<string, MethodDefinition>();
        foreach (var (game, prefix) in new[] { (baseline, "Baseline"), (candidate, "Patched") })
            foreach (var (source, suffix) in new[] { (Program.Single(game.MainModule), "Single"), (Program.Draw(game.MainModule), "Draw") })
            {
                var target = new MethodDefinition(prefix + suffix, MA.Public | MA.Static, module.TypeSystem.Void) { ImplAttributes = source.ImplAttributes };
                target.Parameters.Add(new ParameterDefinition(TypeMap(source.DeclaringType)));
                foreach (var p in source.Parameters) target.Parameters.Add(new ParameterDefinition(TypeMap(p.ParameterType)));
                hooks.Methods.Add(target); singles.Add(target.Name, target); copies.Add((source, target, "Complete" + suffix));
            }
        var primitiveCopies = new Dictionary<string, MethodDefinition>(); var factories = new Dictionary<string, MethodDefinition>();
        foreach (var (token, name) in new[] { (0x06000f43, "RectangleCtor"), (0x06000f65, "VectorCtor"), (0x06000fa6, "VectorAdd"), (0x06000f60, "VectorZero"), (0x06000fad, ".cctor") })
        {
            var source = (MethodDefinition)fna.MainModule.LookupToken(token);
            var target = new MethodDefinition(name, MA.Public | MA.Static | (name == ".cctor" ? MA.SpecialName | MA.RTSpecialName : 0), TypeMap(source.ReturnType));
            if (source.HasThis) target.Parameters.Add(new ParameterDefinition(new ByReferenceType(TypeMap(source.DeclaringType))));
            foreach (var p in source.Parameters) target.Parameters.Add(new ParameterDefinition(TypeMap(p.ParameterType)));
            hooks.Methods.Add(target); primitiveCopies.Add(source.FullName, target); copies.Add((source, target, "ExactFnaPrimitive"));
            if (source.IsConstructor && !source.IsStatic)
            {
                var factory = new MethodDefinition(name + "Value", MA.Public | MA.Static, TypeMap(source.DeclaringType));
                foreach (var p in source.Parameters) factory.Parameters.Add(new ParameterDefinition(TypeMap(p.ParameterType)));
                hooks.Methods.Add(factory); factories.Add(source.FullName, factory);
                factory.Body.InitLocals = true; var local = new VariableDefinition(factory.ReturnType); factory.Body.Variables.Add(local);
                var il = factory.Body.GetILProcessor(); il.Emit(OpCodes.Ldloca, local); foreach (var p in factory.Parameters) il.Emit(OpCodes.Ldarg, p);
                il.Emit(OpCodes.Call, target); il.Emit(OpCodes.Ldloc, local); il.Emit(OpCodes.Ret);
            }
        }
        var vector = Types(fna.MainModule).Single(t => t.FullName == "Microsoft.Xna.Framework.Vector2");
        foreach (var field in vector.Fields.Where(f => f.IsStatic)) hooks.Fields.Add(new FieldDefinition("Vector_" + field.Name, FA.Private | FA.Static, TypeMap(field.FieldType)));
        MethodReference HostMethod(Type type, string name, int count = -1)
        {
            var methods = type.GetMethods(Flags).Where(m => m.Name == name && (count < 0 || m.GetParameters().Length == count)).ToArray();
            Require(methods.Length == 1, "missing/ambiguous gate50 host method " + type.Name + "." + name); return module.ImportReference(methods[0]);
        }
        string prefixNow = "Baseline";
        object Member(object o)
        {
            if (o is TypeReference t) return TypeMap(t);
            if (o is FieldReference f)
            {
                if (f.DeclaringType.FullName == sourceScratch.FullName) return scratch.Fields.Single(x => x.Name == f.Name);
                if (f.DeclaringType.FullName == vector.FullName && vector.Fields.Any(x => x.Name == f.Name && x.IsStatic)) return hooks.Fields.Single(x => x.Name == "Vector_" + f.Name);
                if (hosts.TryGetValue(f.DeclaringType.FullName, out var h))
                {
                    var field = h.GetField(f.Name, Flags); Require(field != null, "missing gate50 host field " + f.FullName);
                    var reference = module.ImportReference(field!); reference.FieldType = TypeMap(f.FieldType); return reference;
                }
                return new FieldReference(f.Name, TypeMap(f.FieldType), TypeMap(f.DeclaringType));
            }
            if (o is MethodReference m)
            {
                if (m.DeclaringType.FullName == sourceScratch.FullName && m.Name == ".ctor") return ctor;
                if (primitiveCopies.TryGetValue(m.FullName, out var exact)) return exact;
                if (m.DeclaringType.FullName == "Terraria.GameContent.Drawing.TileDrawing" && m.Name == "DrawSingleTile") return singles[prefixNow + "Single"];
                if (m.DeclaringType.FullName == "Terraria.GameContent.Drawing.TileDrawing" && m.Name is "GetTileDrawData" or "DrawTiles_GetLightOverride" or "IsVisible" or "CacheSpecialDraws_Part1" or "CacheSpecialDraws_Part2" or "DrawBasicTile") return HostMethod(typeof(DrawGateBoundary), m.Name);
                if (m.DeclaringType.FullName == "Terraria.Lighting" && m.Name == "GetColor") return HostMethod(typeof(DrawGateBoundary), "GetColor");
                if (m.DeclaringType.FullName == "Terraria.Graphics.TileBatch" && m.Name == "Draw") return HostMethod(typeof(DrawGateBoundary), "GraphicsDraw");
                if (m.DeclaringType.FullName == "Terraria.TimeLogger/TimeLogData" && m.Name == "Add") return HostMethod(typeof(TileFixture), "Add");
                if (m.DeclaringType.FullName == "System.Diagnostics.Stopwatch" && m.Name == "GetTimestamp") return HostMethod(typeof(CostFixture), "Clock");
                if (hosts.TryGetValue(m.DeclaringType.FullName, out var h))
                {
                    if (m.Name == ".ctor") return module.ImportReference(h.GetConstructors().Single(c => c.GetParameters().Length == m.Parameters.Count && c.GetParameters().Select(p => module.ImportReference(p.ParameterType).FullName).SequenceEqual(m.Parameters.Select(p => TypeMap(p.ParameterType).FullName))));
                    var reference = HostMethod(h, m.Name, m.Parameters.Count); Require(reference.HasThis == m.HasThis, "fixture receiver mismatch " + m.FullName); return reference;
                }
                var n = new MethodReference(m.Name, TypeMap(m.ReturnType), TypeMap(m.DeclaringType)) { HasThis = m.HasThis, ExplicitThis = m.ExplicitThis, CallingConvention = m.CallingConvention };
                foreach (var p in m.Parameters) n.Parameters.Add(new ParameterDefinition(TypeMap(p.ParameterType))); return n;
            }
            return o;
        }
        foreach (var (source, target, kind) in copies)
        {
            prefixNow = target.Name.StartsWith("Patched") ? "Patched" : "Baseline";
            Invoke("Patcher", "CopyBody", source, target, (Func<TypeReference, TypeReference>)TypeMap, (Func<object, object>)Member, source.HasThis && target.IsStatic ? 1 : 0);
            var sourceInstructions = source.Body.Instructions.ToArray();
            for (int i = 0; i < sourceInstructions.Length; i++)
                if (sourceInstructions[i].OpCode == OpCodes.Newobj && sourceInstructions[i].Operand is MethodReference m && factories.TryGetValue(m.FullName, out var factory))
                { target.Body.Instructions[i].OpCode = OpCodes.Call; target.Body.Instructions[i].Operand = factory; }
            Require(source.Body.Instructions.Count == target.Body.Instructions.Count && source.Body.Variables.Count == target.Body.Variables.Count && source.Body.ExceptionHandlers.Count == target.Body.ExceptionHandlers.Count, "incomplete source copy " + source.FullName);
            receipts.Add(new { source = source.FullName, sourceMvid = source.Module.Mvid, sourceBody = Fingerprint(source), fixture = target.FullName, mappedBodyBeforeHostMarkers = Fingerprint(target), instructions = source.Body.Instructions.Count, locals = source.Body.Variables.Count, exceptionHandlers = source.Body.ExceptionHandlers.Count, kind });
            if (kind == "CompleteSingle")
            {
                var at = sourceInstructions.Select((s, i) => (s.Offset, instruction: target.Body.Instructions[i])).ToDictionary(x => x.Offset, x => x.instruction);
                var prep = target.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldloca_S && i.Operand is VariableDefinition v && v.Index == 6 && i.Next.OpCode == OpCodes.Ldloc_0);
                if (prefixNow == "Patched" && fault != "")
                {
                    if (fault is "MissIncoming0676" or "MissIncoming0683") at[fault.EndsWith("0676") ? 0x676 : 0x683].Operand = prep;
                    else if (fault == "UseVisibilityInsteadOfV4")
                    {
                        var gate = prep.Previous; Require(gate.OpCode == OpCodes.Brfalse, "negative gate not relocated"); gate.Previous.Operand = target.Body.Variables[5];
                    }
                    else if (fault == "SkipPriorCache")
                    {
                        var call = at[0x65f]; Require(call.Operand is MethodReference { Name: "CacheSpecialDraws_Part1" }, "negative cache call missing");
                        call.OpCode = OpCodes.Pop; call.Operand = null; for (int n = 1; n < 7; n++) target.Body.GetILProcessor().InsertBefore(call, Instruction.Create(OpCodes.Pop));
                    }
                    else throw new InvalidDataException("unknown negative candidate " + fault);
                }
                // Host-only counters: retarget every incoming edge to the marker,
                // rather than accidentally hiding an incoming-edge patch mistake.
                void Mark(Instruction point, int id)
                {
                    var load = Instruction.Create(OpCodes.Ldc_I4, id); var call = Instruction.Create(OpCodes.Call, HostMethod(typeof(DrawGateBoundary), "Mark"));
                    foreach (var instruction in target.Body.Instructions)
                    {
                        if (ReferenceEquals(instruction.Operand, point)) instruction.Operand = load;
                        else if (instruction.Operand is Instruction[] branches) for (int b = 0; b < branches.Length; b++) if (ReferenceEquals(branches[b], point)) branches[b] = load;
                    }
                    target.Body.GetILProcessor().InsertBefore(point, load); target.Body.GetILProcessor().InsertBefore(point, call);
                }
                foreach (var instruction in sourceInstructions.Where(i => i.OpCode == OpCodes.Ret)) Mark(at[instruction.Offset], instruction.Offset);
                Mark(prep, -1); Mark(target.Body.Instructions[0], -2); Widen(target);
            }
        }
        module.Mvid = new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", Types(module).SelectMany(t => t.Methods).Select(Body)))).AsSpan(0, 16));
        using var buffer = new MemoryStream(); probe.Write(buffer, new WriterParameters { Timestamp = 0 }); byte[] bytes = buffer.ToArray();
        using var serialized = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        foreach (var target in Types(module).SelectMany(t => t.Methods))
        {
            var actual = Types(serialized.MainModule).Single(t => t.FullName == target.DeclaringType.FullName).Methods.Single(m => m.Name == target.Name);
            Require(Fingerprint(actual) == Fingerprint(target), "gate50 fixture serialization changed " + target.FullName);
        }
        return bytes;
    }
}
