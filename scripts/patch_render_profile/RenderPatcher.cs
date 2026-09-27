using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;

internal static class RenderPatcher
{
    internal const string HelperName = "Terraria.NXRenderProfile53";
    internal sealed record CallReplacement(int OriginalIndex, string Opcode, string Target, string Wrapper);
    internal sealed record Receipt(string Method, string Kind, int ScopeId, int[] OriginalIndices, int OriginalLocals,
        int OriginalHandlers, bool OriginalInitLocals, int OriginalMaxStack, int[] ReturnIndices,
        CallReplacement[] Calls, string BeforeHash, string AfterHash);
    internal static bool IsAdded(TypeDefinition type) => type.FullName == HelperName || type.DeclaringType != null && IsAdded(type.DeclaringType);
    internal static IAssemblyResolver Resolver(string input) => new PinnedResolver(Path.GetDirectoryName(Path.GetFullPath(input))!);

    internal static Receipt[] Inject(ModuleDefinition module)
    {
        Require(module.Mvid == Guid.Parse("df61a8d1-0622-9555-82c2-a2118b6c9bc4"), "render53 requires exact accepted52 module");
        Require(!Types(module).Any(t => IsAdded(t) || t.Name is "NXFrameProfile43" or "NXTileProfile44" or "NXTileCost45" or "NXTileProfile49" or "NXUIProfile51"), "existing experimental profiler forbidden");
        var helper = MapRuntime(module);
        MethodDefinition Hook(string name) => helper.Methods.Single(m => m.Name == name);
        var frame = PatchFrame(Program.Method(module, Program.MainDrawName), Hook);
        var draw = PatchDraw(Program.Draw(module), helper.NestedTypes.Single(t => t.Name == nameof(Render53Pass)), Hook);
        var single = PatchSingle(Program.Single(module), helper, Hook);
        var run = PatchRun(Program.Method(module, Program.RunGameName), Hook);
        var end = EnvelopeBatchEnd(Program.Method(module, Program.BatchEndName), Hook);
        var render = Program.Method(module, Program.RenderBatchName);
        var layered = Program.Method(module, Program.FlushLayeredName);
        Require(render.MetadataToken.ToInt32() == 0x06002b62 && render.Body.Instructions.Count == 114 && render.Body.ExceptionHandlers.Count == 0, "RenderBatch shape differs");
        Require(layered.MetadataToken.ToInt32() == 0x06002b64 && layered.Body.Instructions.Count == 186 && layered.Body.ExceptionHandlers.Count == 0, "FlushLayered shape differs");
        Instruction Site(MethodDefinition method, int offset, string name) {
            var instruction = method.Body.Instructions.Single(i => i.Offset == offset);
            Require(instruction.OpCode == OpCodes.Callvirt && instruction.Operand is MethodReference m && m.Name == name && (instruction.Previous == null || instruction.Previous.OpCode.OpCodeType != OpCodeType.Prefix), "batch callsite shape differs");
            return instruction;
        }
        var shortUpload = Site(layered, 0x00f6, "SetData");
        var longUpload = Site(render, 0x008d, "SetData");
        var renderSubmit = Site(render, 0x00be, "DrawIndexedPrimitives");
        var layeredSubmit = Site(layered, 0x0125, "DrawIndexedPrimitives");
        Require(RenderAudit.MemberIdentity((MethodReference)renderSubmit.Operand) == RenderAudit.MemberIdentity((MethodReference)layeredSubmit.Operand), "indexed submit signatures differ");
        var upload0 = BatchWrapper(helper, "BatchUpload000", (MethodReference)shortUpload.Operand, 1, Hook);
        var upload1 = BatchWrapper(helper, "BatchUpload001", (MethodReference)longUpload.Operand, 1, Hook);
        var submit = BatchWrapper(helper, "BatchSubmit002", (MethodReference)renderSubmit.Operand, 2, Hook);
        return new[] { frame, draw, single, run, end,
            PatchCalls(render, new[] { (longUpload, upload1), (renderSubmit, submit) }),
            PatchCalls(layered, new[] { (shortUpload, upload0), (layeredSubmit, submit) }) };
    }

    sealed class Splice
    {
        internal readonly MethodDefinition Method;
        internal readonly Instruction[] Original;
        readonly int locals, handlers, maxStack;
        readonly bool initLocals;
        readonly string before;
        internal readonly List<int> Returns = new();
        internal Splice(MethodDefinition method) {
            Method = method; Original = method.Body.Instructions.ToArray(); locals = method.Body.Variables.Count;
            handlers = method.Body.ExceptionHandlers.Count; maxStack = method.Body.MaxStackSize; initLocals = method.Body.InitLocals; before = Fingerprint(method);
        }
        internal Instruction At(int offset) => Original.Single(i => i.Offset == offset);
        internal void Before(Instruction target, bool redirect, params Instruction[] additions) {
            Require(additions.Length > 0, "empty splice");
            foreach (var instruction in additions) Method.Body.GetILProcessor().InsertBefore(target, instruction);
            if (!redirect) return;
            foreach (var instruction in Original) {
                if (ReferenceEquals(instruction.Operand, target)) instruction.Operand = additions[0];
                else if (instruction.Operand is Instruction[] branches) for (int n = 0; n < branches.Length; n++) if (branches[n] == target) branches[n] = additions[0];
            }
        }
        internal Receipt Finish(string kind, int scope = -1, CallReplacement[]? calls = null, int extraStack = 8) {
            Widen(Method); Method.Body.MaxStackSize = Math.Max(Method.Body.MaxStackSize, maxStack + extraStack);
            return new(Method.FullName, kind, scope, Original.Select(i => Method.Body.Instructions.IndexOf(i)).ToArray(), locals, handlers, initLocals, maxStack, Returns.ToArray(), calls ?? Array.Empty<CallReplacement>(), before, Fingerprint(Method));
        }
    }

    static Receipt PatchFrame(MethodDefinition method, Func<string, MethodDefinition> hook)
    {
        var edit = new Splice(method);
        Require(edit.Original.Length == 27 && method.Body.ExceptionHandlers.Count == 0 && edit.At(0x51).OpCode == OpCodes.Ret, "Main.Draw shape differs");
        var depth = new VariableDefinition(method.Module.TypeSystem.Int32); var completed = new VariableDefinition(method.Module.TypeSystem.Boolean);
        method.Body.Variables.Add(depth); method.Body.Variables.Add(completed);
        var first = edit.At(0x16); var ret = edit.At(0x51);
        edit.Before(first, false, Instruction.Create(OpCodes.Call, hook("FrameBegin")), Instruction.Create(OpCodes.Stloc, depth), Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Stloc, completed));
        var finallyStart = Instruction.Create(OpCodes.Ldloc, depth);
        edit.Before(ret, false, Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Stloc, completed), Instruction.Create(OpCodes.Leave, ret), finallyStart, Instruction.Create(OpCodes.Ldloc, completed), Instruction.Create(OpCodes.Call, hook("FrameEnd")), Instruction.Create(OpCodes.Endfinally));
        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = first, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = ret });
        return edit.Finish("frame");
    }

    static Receipt PatchDraw(MethodDefinition method, TypeDefinition pass, Func<string, MethodDefinition> hook)
    {
        var edit = new Splice(method);
        Require(edit.Original.Length == 815 && method.Body.ExceptionHandlers.Count == 0 && edit.At(0x9cd).OpCode == OpCodes.Ret, "TileDrawing.Draw shape differs");
        Require(edit.At(0x1ef).OpCode == OpCodes.Ldloc_S && edit.At(0x912).OpCode == OpCodes.Ldarg_0 && edit.At(0x203).OpCode == OpCodes.Ldsfld && edit.At(0x25a).OpCode == OpCodes.Bne_Un && edit.At(0x8eb).Operand is MethodReference { Name: "DrawSingleTile" }, "tile region boundaries differ");
        var state = new VariableDefinition(pass); var selected = new VariableDefinition(method.Module.TypeSystem.Boolean);
        method.Body.Variables.Add(state); method.Body.Variables.Add(selected);
        FieldDefinition Field(string name) => pass.Fields.Single(f => f.Name == name);
        Instruction[] Increment(string name) => new[] { Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Ldfld, Field(name)), Instruction.Create(OpCodes.Ldc_I8, 1L), Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Stfld, Field(name)) };
        edit.Before(edit.Original[0], false, Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Call, hook("Enter")));
        void Region(int offset, int region) => edit.Before(edit.At(offset), true, Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Ldc_I4, region), Instruction.Create(OpCodes.Call, hook("Region")));
        Region(0x1ef, 0); Region(0x912, 1); Region(0x9cd, 2);
        edit.Before(edit.At(0x203), true, Increment("Visited")); edit.Before(edit.At(0x25f), true, Increment("Eligible"));
        var call = edit.At(0x8eb); var count = Increment("Calls");
        var pre = new List<Instruction> {
            Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Stloc, selected),
            Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Ldfld, Field("Enabled")), Instruction.Create(OpCodes.Brfalse, count[0]),
            Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Ldfld, Field("Calls")),
            Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Ldfld, Field("Phase")), Instruction.Create(OpCodes.Conv_I8),
            Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Ldc_I8, 127L), Instruction.Create(OpCodes.And), Instruction.Create(OpCodes.Ldc_I8, 0L), Instruction.Create(OpCodes.Ceq), Instruction.Create(OpCodes.Stloc, selected)
        };
        pre.AddRange(count); pre.AddRange(new[] { Instruction.Create(OpCodes.Ldloc, selected), Instruction.Create(OpCodes.Brfalse, call), Instruction.Create(OpCodes.Call, hook("BeginSelected")) });
        edit.Before(call, false, pre.ToArray());
        var ret = edit.At(0x9cd); var finalRet = Instruction.Create(OpCodes.Ret);
        edit.Returns.Add(Array.IndexOf(edit.Original, ret)); ret.OpCode = OpCodes.Leave; ret.Operand = finalRet;
        var finallyStart = Instruction.Create(OpCodes.Ldloca, state);
        foreach (var instruction in new[] { finallyStart, Instruction.Create(OpCodes.Call, hook("Finish")), Instruction.Create(OpCodes.Endfinally), finalRet }) method.Body.Instructions.Add(instruction);
        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = edit.Original[0], TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = finalRet });
        return edit.Finish("tile_draw");
    }

    static Receipt PatchSingle(MethodDefinition method, TypeDefinition helper, Func<string, MethodDefinition> hook)
    {
        var edit = new Splice(method);
        Require(edit.Original.Length == 2312 && method.Body.ExceptionHandlers.Count == 0, "DrawSingleTile shape differs");
        var selected = new VariableDefinition(method.Module.TypeSystem.Boolean); method.Body.Variables.Add(selected);
        edit.Before(edit.Original[0], false, Instruction.Create(OpCodes.Ldsfld, helper.Fields.Single(f => f.Name == "Pending")), Instruction.Create(OpCodes.Stloc, selected), Instruction.Create(OpCodes.Ldloc, selected), Instruction.Create(OpCodes.Brfalse, edit.Original[0]), Instruction.Create(OpCodes.Call, hook("ConsumePending")));
        foreach (var (offset, id, name) in new[] { (0x50, 0, "GetColor"), (0xd3, 1, "GetTileDrawData"), (0x16b, 2, "GetTileOutlineInfo"), (0x570, 3, "DrawTiles_GetLightOverride"), (0x6f4, 0, "GetColor"), (0x7de, 4, "GetFinalLight") }) {
            var call = edit.At(offset); var next = call.Next;
            Require(call.OpCode.Code is Code.Call or Code.Callvirt && call.Operand is MethodReference m && m.Name == name, "Single helper site differs " + offset.ToString("x"));
            edit.Before(call, true, Instruction.Create(OpCodes.Ldloc, selected), Instruction.Create(OpCodes.Brfalse, call), Instruction.Create(OpCodes.Ldc_I4, id), Instruction.Create(OpCodes.Call, hook("BeginOp")));
            edit.Before(next, false, Instruction.Create(OpCodes.Ldloc, selected), Instruction.Create(OpCodes.Brfalse, next), Instruction.Create(OpCodes.Ldc_I4, id), Instruction.Create(OpCodes.Call, hook("EndOp")));
        }
        var returns = edit.Original.Where(i => i.OpCode == OpCodes.Ret).ToArray(); Require(returns.Length == 5, "Single return count differs");
        foreach (var ret in returns) edit.Before(ret, true, Instruction.Create(OpCodes.Ldloc, selected), Instruction.Create(OpCodes.Brfalse, ret), Instruction.Create(OpCodes.Call, hook("EndSample")));
        return edit.Finish("tile_single");
    }

    static Receipt PatchRun(MethodDefinition method, Func<string, MethodDefinition> hook)
    {
        var edit = new Splice(method); var call = edit.At(0xa5); var next = edit.At(0xaa);
        Require(call.Operand is MethodReference { Name: "Run", DeclaringType.FullName: "Microsoft.Xna.Framework.Game" } && call.Next == next && next.OpCode.FlowControl == FlowControl.Branch, "RunGame normal-return site differs");
        edit.Before(next, false, Instruction.Create(OpCodes.Call, hook("Flush")));
        return edit.Finish("run");
    }

    static Receipt EnvelopeBatchEnd(MethodDefinition method, Func<string, MethodDefinition> hook)
    {
        var edit = new Splice(method);
        Require(method.MetadataToken.ToInt32() == 0x06002b60 && edit.Original.Length == 22 && method.Body.ExceptionHandlers.Count == 0 && method.ReturnType.MetadataType == MetadataType.Int32, "TileBatch.End shape differs");
        var cookie = new VariableDefinition(method.Module.TypeSystem.Int32); var completed = new VariableDefinition(method.Module.TypeSystem.Boolean); var result = new VariableDefinition(method.ReturnType);
        method.Body.Variables.Add(cookie); method.Body.Variables.Add(completed); method.Body.Variables.Add(result);
        edit.Before(edit.Original[0], false, Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Stloc, completed), Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Call, hook("BatchBegin")), Instruction.Create(OpCodes.Stloc, cookie));
        var epilogue = Instruction.Create(OpCodes.Ldloc, result);
        foreach (var ret in edit.Original.Where(i => i.OpCode == OpCodes.Ret)) {
            edit.Returns.Add(Array.IndexOf(edit.Original, ret)); ret.OpCode = OpCodes.Stloc; ret.Operand = result;
            var at = ret;
            foreach (var instruction in new[] { Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Stloc, completed), Instruction.Create(OpCodes.Leave, epilogue) }) { method.Body.GetILProcessor().InsertAfter(at, instruction); at = instruction; }
        }
        var finallyStart = Instruction.Create(OpCodes.Ldloc, cookie);
        foreach (var instruction in new[] { finallyStart, Instruction.Create(OpCodes.Ldloc, completed), Instruction.Create(OpCodes.Call, hook("BatchEnd")), Instruction.Create(OpCodes.Endfinally), epilogue, Instruction.Create(OpCodes.Ret) }) method.Body.Instructions.Add(instruction);
        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = edit.Original[0], TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = epilogue });
        return edit.Finish("batch_end", 0);
    }

    static Receipt PatchCalls(MethodDefinition method, (Instruction Call, MethodDefinition Wrapper)[] sites)
    {
        var edit = new Splice(method); var rows = new List<CallReplacement>();
        foreach (var (call, wrapper) in sites) {
            rows.Add(new(Array.IndexOf(edit.Original, call), call.OpCode.Name, RenderAudit.MemberIdentity((MethodReference)call.Operand), wrapper.FullName));
            call.OpCode = OpCodes.Call; call.Operand = wrapper;
        }
        return edit.Finish("calls", calls: rows.ToArray(), extraStack: 0);
    }

    internal static TypeReference ClosedType(TypeReference type, MethodReference method)
    {
        if (type is GenericParameter parameter) {
            Require(parameter.Type == GenericParameterType.Method && method is GenericInstanceMethod, "unbound generic wrapper parameter");
            var generic = (GenericInstanceMethod)method;
            Require(parameter.Position < generic.GenericArguments.Count, "generic wrapper parameter outside arguments");
            return generic.GenericArguments[parameter.Position];
        }
        if (type is ArrayType array) { Require(array.IsVector, "multidimensional wrapper array not supported"); return new ArrayType(ClosedType(array.ElementType, method)); }
        if (type is ByReferenceType reference) return new ByReferenceType(ClosedType(reference.ElementType, method));
        Require(type is not TypeSpecification, "unapproved wrapper type specification");
        return type;
    }

    static MethodDefinition BatchWrapper(TypeDefinition helper, string name, MethodReference target, int scope, Func<string, MethodDefinition> hook)
    {
        Require(target.HasThis && !target.DeclaringType.IsValueType && target.ReturnType.MetadataType == MetadataType.Void, "unsupported batch wrapper receiver/return");
        if (scope == 1) Require(target is GenericInstanceMethod g && g.GenericArguments.Count == 1 && g.GenericArguments[0].FullName == "Microsoft.Xna.Framework.Graphics.VertexPositionColorTexture" && g.GenericArguments[0].Scope.Name == "FNA" && target.Name == "SetData" && target.DeclaringType.FullName == "Microsoft.Xna.Framework.Graphics.DynamicVertexBuffer", "unsupported vertex upload");
        else Require(scope == 2 && target is not GenericInstanceMethod && target.Name == "DrawIndexedPrimitives" && target.DeclaringType.FullName == "Microsoft.Xna.Framework.Graphics.GraphicsDevice", "unsupported indexed submission");
        var wrapper = new MethodDefinition(name, MA.Assembly | MA.Static | MA.HideBySig, ClosedType(target.ReturnType, target));
        wrapper.Parameters.Add(new ParameterDefinition("receiver", Mono.Cecil.ParameterAttributes.None, target.DeclaringType));
        foreach (var parameter in target.Parameters) wrapper.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, ClosedType(parameter.ParameterType, target)));
        helper.Methods.Add(wrapper);
        var cookie = new VariableDefinition(helper.Module.TypeSystem.Int32); var completed = new VariableDefinition(helper.Module.TypeSystem.Boolean);
        wrapper.Body.Variables.Add(cookie); wrapper.Body.Variables.Add(completed);
        var body = wrapper.Body.Instructions;
        body.Add(Instruction.Create(OpCodes.Ldc_I4_0)); body.Add(Instruction.Create(OpCodes.Stloc, completed));
        body.Add(Instruction.Create(OpCodes.Ldc_I4, scope)); body.Add(Instruction.Create(OpCodes.Call, hook("BatchBegin"))); body.Add(Instruction.Create(OpCodes.Stloc, cookie));
        var first = Instruction.Create(OpCodes.Ldarg, wrapper.Parameters[0]); body.Add(first);
        foreach (var parameter in wrapper.Parameters.Skip(1)) body.Add(Instruction.Create(OpCodes.Ldarg, parameter));
        body.Add(Instruction.Create(OpCodes.Callvirt, target)); body.Add(Instruction.Create(OpCodes.Ldc_I4_1)); body.Add(Instruction.Create(OpCodes.Stloc, completed));
        var ret = Instruction.Create(OpCodes.Ret); body.Add(Instruction.Create(OpCodes.Leave, ret));
        var finallyStart = Instruction.Create(OpCodes.Ldloc, cookie); body.Add(finallyStart); body.Add(Instruction.Create(OpCodes.Ldloc, completed)); body.Add(Instruction.Create(OpCodes.Call, hook("BatchEnd"))); body.Add(Instruction.Create(OpCodes.Endfinally)); body.Add(ret);
        wrapper.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = first, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = ret });
        wrapper.Body.MaxStackSize = Math.Max(wrapper.Parameters.Count, 2);
        return wrapper;
    }

    static TypeDefinition MapRuntime(ModuleDefinition module)
    {
        using var source = AssemblyDefinition.ReadAssembly(typeof(Render53Template).Assembly.Location);
        var helper = new TypeDefinition("Terraria", "NXRenderProfile53", TA.NotPublic | TA.Abstract | TA.Sealed, module.TypeSystem.Object);
        module.Types.Add(helper);
        var sourceHelper = source.MainModule.Types.Single(t => t.Name == nameof(Render53Template));
        Require(!sourceHelper.Methods.Any(m => m.IsConstructor), "runtime must not allocate through a cctor");
        var data = source.MainModule.Types.Where(t => t.Name.StartsWith("Render53", StringComparison.Ordinal) && t.IsValueType && !t.Name.EndsWith("Shape", StringComparison.Ordinal)).ToArray();
        Require(data.Length == 10, "unexpected runtime data types");
        var pairs = new List<(TypeDefinition Source, TypeDefinition Destination)> { (sourceHelper, helper) };
        var mapped = new Dictionary<string, TypeReference> { [sourceHelper.FullName] = helper };
        foreach (var type in data) {
            // The original TileDrawing body directly addresses the pass fields; retain
            // the established internal accessibility rather than an inaccessible private type.
            var dest = new TypeDefinition("", type.Name, TA.NestedAssembly | TA.SequentialLayout | TA.Sealed | TA.BeforeFieldInit, new TypeReference("System", "ValueType", module, module.TypeSystem.CoreLibrary));
            helper.NestedTypes.Add(dest); mapped.Add(type.FullName, dest); pairs.Add((type, dest));
        }
        foreach (var pair in new[] { ("Render53MainShape", "Terraria.Main"), ("Render53EntityShape", "Terraria.Entity"), ("Render53PlayerShape", "Terraria.Player") }) mapped.Add(pair.Item1, Types(module).Single(t => t.FullName == pair.Item2));
        var vector = module.GetTypeReferences().First(t => t.FullName == "Microsoft.Xna.Framework.Vector2");
        Require(vector.Scope.Name == "FNA", "Vector2 target must be pinned FNA"); mapped.Add("Render53VectorShape", vector.Resolve());
        var mapper = new RuntimeMapper(module, mapped);
        foreach (var (from, to) in pairs) {
            Require(!from.HasNestedTypes && !from.HasProperties && !from.HasEvents && !from.HasGenericParameters, "unexpected runtime template metadata");
            foreach (var field in from.Fields) {
                var dest = new FieldDefinition(field.Name, field.Attributes, mapper.Type(field.FieldType));
                if (field.HasConstant) dest.Constant = field.Constant;
                foreach (var attribute in field.CustomAttributes.Where(a => a.AttributeType.FullName == "System.ThreadStaticAttribute")) dest.CustomAttributes.Add(new CustomAttribute((MethodReference)mapper.Member(attribute.Constructor)));
                to.Fields.Add(dest);
            }
            foreach (var method in from.Methods) {
                Require(!method.HasGenericParameters, "generic runtime helper not supported");
                var dest = new MethodDefinition(method.Name, method.Attributes, mapper.Type(method.ReturnType)) { ImplAttributes = method.ImplAttributes, CallingConvention = method.CallingConvention };
                foreach (var parameter in method.Parameters) dest.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, mapper.Type(parameter.ParameterType)));
                to.Methods.Add(dest);
            }
        }
        foreach (var (from, to) in pairs) foreach (var method in from.Methods) CopyBody(method, to.Methods.Single(m => mapper.SignatureMatches(method, m)), mapper.Type, mapper.Member);
        helper.Fields.Add(new FieldDefinition("SourceHash", Mono.Cecil.FieldAttributes.Assembly | Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.Literal | Mono.Cecil.FieldAttributes.HasDefault, module.TypeSystem.String) { Constant = Program.SourceHash() });
        return helper;
    }

    internal static void CopyBody(MethodDefinition source, MethodDefinition target, Func<TypeReference, TypeReference> type, Func<object, object> member)
    {
        if (!source.HasBody) return;
        target.Body = new Mono.Cecil.Cil.MethodBody(target) { InitLocals = source.Body.InitLocals, MaxStackSize = source.Body.MaxStackSize };
        foreach (var local in source.Body.Variables) target.Body.Variables.Add(new VariableDefinition(type(local.VariableType)));
        var map = source.Body.Instructions.ToDictionary(i => i, _ => Instruction.Create(OpCodes.Nop));
        foreach (var instruction in source.Body.Instructions) {
            var dest = map[instruction]; dest.OpCode = instruction.OpCode;
            dest.Operand = instruction.Operand switch {
                null => null, Instruction branch => map[branch], Instruction[] branches => branches.Select(b => map[b]).ToArray(),
                VariableDefinition local => target.Body.Variables[local.Index], ParameterDefinition parameter => target.Parameters[parameter.Index], var operand => member(operand)
            };
            target.Body.Instructions.Add(dest);
        }
        foreach (var handler in source.Body.ExceptionHandlers) target.Body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType) {
            TryStart = map[handler.TryStart], TryEnd = handler.TryEnd == null ? null : map[handler.TryEnd], HandlerStart = map[handler.HandlerStart], HandlerEnd = handler.HandlerEnd == null ? null : map[handler.HandlerEnd],
            FilterStart = handler.FilterStart == null ? null : map[handler.FilterStart], CatchType = handler.CatchType == null ? null : type(handler.CatchType)
        });
    }

    sealed class RuntimeMapper
    {
        readonly ModuleDefinition module;
        readonly Dictionary<string, TypeReference> mapped;
        internal RuntimeMapper(ModuleDefinition module, Dictionary<string, TypeReference> mapped) { this.module = module; this.mapped = mapped; }
        internal TypeReference Type(TypeReference source) {
            if (mapped.TryGetValue(source.FullName, out var target)) return module.ImportReference(target);
            if (source is ByReferenceType reference) return new ByReferenceType(Type(reference.ElementType));
            if (source is ArrayType array) { Require(array.IsVector, "runtime multidimensional array not approved"); return new ArrayType(Type(array.ElementType)); }
            if (source is GenericInstanceType generic) { var result = new GenericInstanceType(Type(generic.ElementType)); foreach (var arg in generic.GenericArguments) result.GenericArguments.Add(Type(arg)); return result; }
            if (source is GenericParameter) return source;
            if (source.MetadataType is >= MetadataType.Void and <= MetadataType.String or MetadataType.IntPtr or MetadataType.UIntPtr or MetadataType.Object or MetadataType.TypedByReference) return Primitive(module, source.FullName);
            Require(source.Namespace.StartsWith("System", StringComparison.Ordinal) && source.DeclaringType == null, "unmapped runtime type " + source.FullName);
            var scope = module.AssemblyReferences.Single(r => r.Name == (source.FullName == "System.Diagnostics.Stopwatch" ? "System" : "mscorlib"));
            return new TypeReference(source.Namespace, source.Name, module, scope, source.IsValueType);
        }
        internal bool SignatureMatches(MethodReference source, MethodReference target) => source.Name == target.Name && source.HasThis == target.HasThis && Type(source.ReturnType).FullName == target.ReturnType.FullName && source.Parameters.Select(p => Type(p.ParameterType).FullName).SequenceEqual(target.Parameters.Select(p => p.ParameterType.FullName));
        internal object Member(object value) {
            if (value is TypeReference type) return Type(type);
            if (value is FieldReference field) {
                if (mapped.TryGetValue(field.DeclaringType.FullName, out var owner)) {
                    var dest = owner.Resolve().Fields.Single(f => f.Name == field.Name);
                    bool frameSkip = field.DeclaringType.FullName == "Render53MainShape" && field.Name == "FrameSkipMode" && field.FieldType.MetadataType == MetadataType.Int32;
                    if (frameSkip) Require(dest.FieldType.Resolve() is { IsEnum: true } enumType && enumType.Fields.Single(f => f.Name == "value__").FieldType.MetadataType == MetadataType.Int32, "FrameSkipMode must retain its Int32 enum");
                    Require(frameSkip || Type(field.FieldType).FullName == dest.FieldType.FullName, "mapped view field type mismatch: " + field.FullName);
                    return module.ImportReference(dest);
                }
                return new FieldReference(field.Name, Type(field.FieldType), Type(field.DeclaringType));
            }
            if (value is MethodReference method) {
                if (mapped.TryGetValue(method.DeclaringType.FullName, out var owner)) return module.ImportReference(owner.Resolve().Methods.Single(m => SignatureMatches(method, m)));
                Require(!method.HasGenericParameters && method is not GenericInstanceMethod, "generic runtime BCL call not approved: " + method.FullName);
                var dest = new MethodReference(method.Name, Type(method.ReturnType), Type(method.DeclaringType)) { HasThis = method.HasThis, ExplicitThis = method.ExplicitThis, CallingConvention = method.CallingConvention };
                foreach (var parameter in method.Parameters) dest.Parameters.Add(new ParameterDefinition(Type(parameter.ParameterType)));
                return dest;
            }
            Require(value is not CallSite, "runtime calli not approved"); return value;
        }
    }

    internal sealed class PinnedResolver : IAssemblyResolver
    {
        const string Core = "/mono-nx/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug/System.Private.CoreLib.dll";
        const string Runtime = "/mono-nx/dotnet_runtime/artifacts/bin/runtime/net9.0-libnx-Debug-arm64";
        readonly string game; readonly Dictionary<string, AssemblyDefinition> loaded = new(StringComparer.Ordinal);
        internal PinnedResolver(string game) { this.game = game; }
        public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());
        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters) {
            if (loaded.TryGetValue(name.FullName, out var assembly)) return assembly;
            bool bcl = name.Name == "mscorlib" || name.Name == "System" || name.Name.StartsWith("System.", StringComparison.Ordinal) || name.Name == "Microsoft.CSharp";
            string path = name.Name == "System.Private.CoreLib" ? Core : Path.Combine(bcl ? Runtime : game, name.Name + ".dll");
            Require(File.Exists(path), "no pinned target resolver image: " + name.FullName);
            parameters.AssemblyResolver = this; assembly = AssemblyDefinition.ReadAssembly(path, parameters); loaded.Add(name.FullName, assembly); return assembly;
        }
        public void Dispose() { foreach (var assembly in loaded.Values) assembly.Dispose(); }
    }
}
