using System.Reflection;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;
using FA = Mono.Cecil.FieldAttributes;

internal static class RenderProbe
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static byte[] Build(AssemblyDefinition baseline, AssemblyDefinition patched, List<object> receipts)
    {
        using var probe = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("RenderExactProbe", new Version(53, 0)), "RenderExactProbe", ModuleKind.Dll);
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
            ["Terraria.Graphics.Effects.Filters"] = typeof(CostFilters), ["Terraria.Graphics.Effects.FilterManager"] = typeof(CostFilterManager), ["Terraria.Graphics.Effects.EffectManager`1<Terraria.Graphics.Effects.Filter>"] = typeof(CostFilterManager), ["Terraria.Graphics.Effects.Filter"] = typeof(CostFilter), ["Terraria.Graphics.Effects.GameEffect"] = typeof(CostFilter),
            ["Microsoft.Xna.Framework.GameTime"] = typeof(FixtureTime), ["ReLogic.Content.IAssetRepository"] = typeof(FixtureAssets),
            ["Terraria.Enums.FrameSkipMode"] = typeof(int), ["Terraria.Testing.DetailedFPS/OperationCategory"] = typeof(int),
            ["Terraria.Entity"] = typeof(RenderEntityFixture), ["Terraria.Localization.LanguageManager"] = typeof(RenderLanguageFixture), ["Terraria.Localization.GameCulture"] = typeof(RenderCultureFixture),
            ["Terraria.Social.SocialMode"] = typeof(int), ["Terraria.Program/<>c"] = typeof(RenderClosureFixture)
        };
        var batchHosts = new Dictionary<string, Type> {
            ["Terraria.Graphics.TileBatch"] = typeof(RenderBatch), ["Terraria.Graphics.TileBatch/SpriteData"] = typeof(RenderSpriteData),
            ["Terraria.Graphics.TileBatch/LayerBatch"] = typeof(RenderLayerBatch), ["Terraria.Graphics.TileBatch/DataSlice"] = typeof(RenderDataSlice),
            ["Terraria.Graphics.TileBatch/LayerBatchKey"] = typeof(RenderLayerBatchKey), ["Terraria.Graphics.TileBatch/RecentLayerCacheEntry"] = typeof(RenderRecentLayerCacheEntry),
            ["Microsoft.Xna.Framework.Graphics.Texture2D"] = typeof(RenderTexture), ["Microsoft.Xna.Framework.Graphics.Texture"] = typeof(RenderTextureBase),
            ["Microsoft.Xna.Framework.Graphics.TextureCollection"] = typeof(RenderTextureCollection), ["Microsoft.Xna.Framework.Graphics.GraphicsDevice"] = typeof(RenderGraphicsDevice),
            ["Microsoft.Xna.Framework.Graphics.DynamicVertexBuffer"] = typeof(RenderDynamicVertexBuffer), ["Microsoft.Xna.Framework.Graphics.VertexPositionColorTexture"] = typeof(RenderVertex),
            ["Microsoft.Xna.Framework.Graphics.SetDataOptions"] = typeof(RenderSetDataOptions), ["Microsoft.Xna.Framework.Graphics.PrimitiveType"] = typeof(RenderPrimitiveType)
        };
        var mapped = new Dictionary<string, TypeDefinition>(); var originals = new List<TypeDefinition>();
        void DefineType(TypeDefinition source, string name)
        {
            var target = new TypeDefinition("Probe", name, TA.Public | (source.IsValueType ? TA.SequentialLayout | TA.Sealed : source.IsAbstract ? TA.Abstract | TA.Sealed : 0), source.IsValueType ? module.ImportReference(typeof(ValueType)) : module.TypeSystem.Object);
            module.Types.Add(target); mapped.Add(source.FullName, target); originals.Add(source);
        }
        var sourceRuntime = Types(patched.MainModule).Single(t => t.FullName == "Terraria.NXRenderProfile53"); DefineType(sourceRuntime, "Runtime");
        foreach (var nested in sourceRuntime.NestedTypes) DefineType(nested, nested.Name);
        var sourceScratch = Types(baseline.MainModule).Single(t => t.FullName == "Terraria.DataStructures.TileDrawInfo"); DefineType(sourceScratch, "Scratch");
        string context = "Runtime", prefix = "Patched";
        Type? Host(string name)
        {
            if (name == "Terraria.Main") return context == "Runtime" ? typeof(RenderWorldFixture) : context == "Main" ? typeof(FixtureMain) : context == "Run" ? typeof(RenderGameFixture) : typeof(CostWorld);
            if (name == "Terraria.Player" && context == "Runtime") return typeof(RenderPlayerFixture);
            if ((context == "Batch" || context == "Runtime") && batchHosts.TryGetValue(name, out var batchHost)) return batchHost;
            return hosts.GetValueOrDefault(name);
        }
        TypeReference TypeMap(TypeReference t)
        {
            if (mapped.TryGetValue(t.FullName, out var n)) return n;
            if (Host(t.FullName) is Type host) return module.ImportReference(host);
            if (t is GenericParameter) return t;
            if (t is ArrayType a) { var copy = new ArrayType(TypeMap(a.ElementType), a.Rank); for (int i = 0; i < a.Rank; i++) copy.Dimensions[i] = new ArrayDimension(a.Dimensions[i].LowerBound, a.Dimensions[i].UpperBound); return copy; }
            if (t is ByReferenceType r) return new ByReferenceType(TypeMap(r.ElementType));
            if (t is GenericInstanceType g) { var copy = new GenericInstanceType(TypeMap(g.ElementType)); foreach (var x in g.GenericArguments) copy.GenericArguments.Add(TypeMap(x)); return copy; }
            Require(t.Namespace.StartsWith("System"), "unmapped render53 fixture type " + t.FullName);
            if (t.MetadataType is >= MetadataType.Void and <= MetadataType.String or MetadataType.Object or MetadataType.IntPtr or MetadataType.UIntPtr)
            {
                var primitive = Primitive(module, t.FullName);
                Require(primitive.MetadataType == t.MetadataType && primitive.IsValueType == t.IsValueType, "malformed primitive signature " + t.FullName);
                return primitive;
            }
            return module.ImportReference(t);
        }
        foreach (var source in originals)
        {
            context = source == sourceScratch ? "Tile" : "Runtime";
            var target = mapped[source.FullName];
            foreach (var f in source.Fields)
            {
                var field = new FieldDefinition(f.Name, (f.Attributes & ~FA.FieldAccessMask) | FA.Public, TypeMap(f.FieldType)); if (f.HasConstant) field.Constant = f.Constant;
                foreach (var a in f.CustomAttributes) { Require(a.AttributeType.FullName == "System.ThreadStaticAttribute", "unsupported field attribute " + a.AttributeType.FullName); field.CustomAttributes.Add(new CustomAttribute(module.ImportReference(typeof(ThreadStaticAttribute).GetConstructor(Type.EmptyTypes)!))); }
                target.Fields.Add(field);
            }
            foreach (var m in source.Methods.Where(m => source != sourceScratch || m.IsConstructor && !m.IsStatic)) Invoke("Patcher", "Define", m, target, (Func<TypeReference, TypeReference>)TypeMap);
        }
        var copies = new List<(MethodDefinition source, MethodDefinition target, string context)>();
        var singles = new Dictionary<string, MethodDefinition>();
        foreach (var (game, pre) in new[] { (baseline, "Baseline"), (patched, "Patched") })
        {
            var drawing = Types(game.MainModule).Single(t => t.FullName == "Terraria.GameContent.Drawing.TileDrawing");
            var main = Types(game.MainModule).Single(t => t.FullName == "Terraria.Main");
            var program = Types(game.MainModule).Single(t => t.FullName == "Terraria.Program");
            foreach (var (source, suffix, kind) in new[] {
                (drawing.Methods.Single(m => m.Name == "DrawSingleTile" && m.Parameters.Count == 4), "Single", "Tile"),
                (drawing.Methods.Single(m => m.Name == "Draw" && m.Parameters.Count == 3), "Draw", "Tile"),
                (main.Methods.Single(m => m.Name == "Draw" && m.Parameters.Count == 1), "Main", "Main"),
                (program.Methods.Single(m => m.Name == "RunGame" && m.Parameters.Count == 0), "Run", "Run"),
                (Program.Method(game.MainModule, Program.BatchEndName), "BatchEnd", "Batch"),
                (Program.Method(game.MainModule, Program.RenderBatchName), "RenderBatch", "Batch"),
                (Program.Method(game.MainModule, Program.FlushLayeredName), "FlushLayered", "Batch") })
            {
                context = kind;
                var target = new MethodDefinition(pre + suffix, MA.Public | MA.Static, TypeMap(source.ReturnType)) { ImplAttributes = source.ImplAttributes };
                if (source.HasThis) target.Parameters.Add(new ParameterDefinition(TypeMap(source.DeclaringType)));
                foreach (var p in source.Parameters) target.Parameters.Add(new ParameterDefinition(TypeMap(p.ParameterType)));
                hooks.Methods.Add(target); singles.Add(pre + suffix, target); copies.Add((source, target, kind));
            }
        }
        MethodReference HostMethod(Type type, string name, int count = -1)
        {
            var methods = type.GetMethods(Flags).Where(m => m.Name == name && (count < 0 || m.GetParameters().Length == count)).ToArray();
            Require(methods.Length == 1, "missing/ambiguous host method " + type.Name + "." + name);
            var imported = module.ImportReference(methods[0]);
            foreach (var parameter in imported.GenericParameters) parameter.Name = "!!" + parameter.Position;
            return imported;
        }
        object Member(object o)
        {
            if (o is TypeReference t) return TypeMap(t);
            if (o is FieldReference f)
            {
                if (mapped.TryGetValue(f.DeclaringType.FullName, out var d)) return d.Fields.Single(x => x.Name == f.Name);
                if (f.DeclaringType.FullName == "System.Diagnostics.Stopwatch" && f.Name == "Frequency") return module.ImportReference(typeof(RenderFixture).GetField("Frequency")!);
                if (f.DeclaringType.FullName == "Terraria.Program/<>c") return module.ImportReference(typeof(RenderClosureFixture).GetField(f.Name == "<>9" ? "Instance" : f.Name.EndsWith("_0") ? "Osx" : "Windows")!);
                if (Host(f.DeclaringType.FullName) is Type h)
                {
                    var field = h.GetField(f.Name, Flags); Require(field != null, "missing host field " + f.FullName);
                    var reference = module.ImportReference(field!); reference.FieldType = TypeMap(f.FieldType); return reference;
                }
                return new FieldReference(f.Name, TypeMap(f.FieldType), TypeMap(f.DeclaringType));
            }
            if (o is GenericInstanceMethod generic)
            {
                var element = (MethodReference)Member(generic.ElementMethod);
                if (!element.HasGenericParameters)
                {
                    Require(Host(generic.DeclaringType.FullName) != null, "generic method lost parameters outside explicit fixture boundary " + generic.FullName);
                    return element;
                }
                var closed = new GenericInstanceMethod(element);
                foreach (var argument in generic.GenericArguments) closed.GenericArguments.Add(TypeMap(argument));
                return closed;
            }
            if (o is MethodReference m)
            {
                if (m.DeclaringType.FullName == "Terraria.GameContent.Drawing.TileDrawing" && m.Name == "DrawSingleTile") return singles[prefix + "Single"];
                if (mapped.TryGetValue(m.DeclaringType.FullName, out var d)) return d.Methods.Single(x => x.Name == m.Name && x.Parameters.Count == m.Parameters.Count);
                if (m.DeclaringType.FullName == "System.Diagnostics.Stopwatch" && m.Name == "GetTimestamp") return HostMethod(typeof(RenderFixture), "Timestamp");
                if (m.DeclaringType.FullName == "Terraria.TimeLogger/TimeLogData" && m.Name == "Add") return HostMethod(typeof(TileFixture), "Add");
                if (m.DeclaringType.FullName == "Terraria.Testing.DetailedFPS") return HostMethod(typeof(FixtureMain), "Detailed" + m.Name);
                if (context == "Main" && m.DeclaringType.FullName == "Terraria.Main" && m.Name == "get_IsGraphicsDeviceAvailable") return HostMethod(typeof(FixtureMain), "IsGraphicsDeviceAvailable");
                if (m.DeclaringType.FullName == "Terraria.Program/<>c") return HostMethod(typeof(RenderClosureFixture), m.Name.EndsWith("_0") ? "OsxLoad" : "WindowsLoad");
                if (context == "Run" && m.DeclaringType.FullName is "Terraria.Program" or "Terraria.Lang" or "Terraria.Social.SocialAPI" or "Terraria.Initializers.LaunchInitializer" or "ReLogic.OS.Platform" or "Microsoft.Xna.Framework.Game") return HostMethod(typeof(RenderGameFixture), m.Name);
                if (Host(m.DeclaringType.FullName) is Type h)
                {
                    if (m.Name == ".ctor") return module.ImportReference(h.GetConstructors().Single(c => c.GetParameters().Length == m.Parameters.Count && c.GetParameters().Select(p => module.ImportReference(p.ParameterType).FullName).SequenceEqual(m.Parameters.Select(p => TypeMap(p.ParameterType).FullName))));
                    var reference = HostMethod(h, m.Name, m.Parameters.Count);
                    Require(reference.HasThis == m.HasThis, "fixture receiver mismatch " + m.FullName); return reference;
                }
                var n = new MethodReference(m.Name, TypeMap(m.ReturnType), TypeMap(m.DeclaringType)) { HasThis = m.HasThis, ExplicitThis = m.ExplicitThis, CallingConvention = m.CallingConvention };
                foreach (var gp in m.GenericParameters) n.GenericParameters.Add(new GenericParameter(gp.Name, n));
                foreach (var p in m.Parameters) n.Parameters.Add(new ParameterDefinition(TypeMap(p.ParameterType))); return n;
            }
            return o;
        }
        foreach (var source in originals)
        {
            context = source == sourceScratch ? "Tile" : "Runtime";
            foreach (var method in source.Methods.Where(m => source != sourceScratch || m.IsConstructor && !m.IsStatic))
            {
                var target = mapped[source.FullName].Methods.Single(m => m.Name == method.Name && m.Parameters.Count == method.Parameters.Count);
                Copy(method, target, TypeMap, Member); copies.Add((method, target, context));
            }
        }
        foreach (var (source, target, kind) in copies.Where(p => p.target.DeclaringType == hooks))
        {
            context = kind; prefix = target.Name.StartsWith("Baseline") ? "Baseline" : "Patched";
            Invoke("Patcher", "CopyBody", source, target, (Func<TypeReference, TypeReference>)TypeMap, (Func<object, object>)Member, source.HasThis ? 1 : 0);
        }
        module.Mvid = new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", Types(module).SelectMany(t => t.Methods).Select(Body)))).AsSpan(0, 16));
        using var buffer = new MemoryStream(); probe.Write(buffer, new WriterParameters { Timestamp = 0 }); byte[] bytes = buffer.ToArray();
        using var serialized = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        foreach (var (source, target, kind) in copies)
        {
            var actual = Types(serialized.MainModule).Single(t => t.FullName == target.DeclaringType.FullName).Methods.Single(m => m.Name == target.Name && m.Parameters.Count == target.Parameters.Count);
            var expectedLines = Body(target).Split('\n'); var actualLines = Body(actual).Split('\n');
            int differingLine = Enumerable.Range(0, Math.Min(expectedLines.Length, actualLines.Length)).FirstOrDefault(index => expectedLines[index] != actualLines[index], -1);
            Require(Fingerprint(actual) == Fingerprint(target), "fixture serialization changed method " + target.FullName + (differingLine < 0 ? ": line-count mismatch" : ": expected=" + expectedLines[differingLine] + "; actual=" + actualLines[differingLine]));
            Require(actual.Body.Instructions.Count == source.Body.Instructions.Count && actual.Body.Variables.Count == source.Body.Variables.Count && actual.Body.ExceptionHandlers.Count == source.Body.ExceptionHandlers.Count, "fixture changed control-flow shape " + source.FullName);
            var sourceInstructions = source.Body.Instructions; var fixtureInstructions = actual.Body.Instructions;
            var bindings = sourceInstructions.Select((instruction, index) => (instruction, index)).Where(pair => pair.instruction.Operand is MemberReference).Select(pair => {
                var member = (MemberReference)pair.instruction.Operand;
                TypeReference owner = member is TypeReference type ? type : member.DeclaringType;
                return new { index = pair.index, offset = pair.instruction.Offset, opcode = pair.instruction.OpCode.Name, source = member.FullName,
                    sourceScope = owner.Scope?.ToString(), sourceModule = source.Module.Name, sourceMvid = source.Module.Mvid,
                    destination = ((MemberReference)fixtureInstructions[pair.index].Operand).FullName,
                    destinationScope = (((MemberReference)fixtureInstructions[pair.index].Operand) is TypeReference targetType ? targetType : ((MemberReference)fixtureInstructions[pair.index].Operand).DeclaringType).Scope?.ToString() };
            }).ToArray();
            receipts.Add(new { source = source.FullName, sourceToken = source.MetadataToken.ToInt32(), sourceMvid = source.Module.Mvid, sourceBody = Fingerprint(source), bindings, fixture = actual.FullName, fixtureBody = Fingerprint(actual), instructions = source.Body.Instructions.Count, locals = source.Body.Variables.Count, exceptionHandlers = source.Body.ExceptionHandlers.Count, boundaryContext = kind });
        }
        return bytes;
    }
}
