using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using TA = Mono.Cecil.TypeAttributes;

internal sealed record TileHelperReceipt(string Method, int[] OriginalIndices, Dictionary<int, int> Entries, int OriginalLocals, int AddedLocals, int OriginalHandlers, int AddedHandlers, int[] ReturnsChanged);

internal static class TileHelperPatcher
{
    internal const string HelperName = "NXTileProfile49";
    internal static bool IsAdded(TypeDefinition type) => type.FullName == "Terraria." + HelperName || type.DeclaringType != null && IsAdded(type.DeclaringType);
    internal static IAssemblyResolver Resolver(string input) => (IAssemblyResolver)Activator.CreateInstance(Support.GetType("ReferenceAudit+PinnedResolver", true)!, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { Path.GetDirectoryName(input)! }, null)!;

    internal static TileHelperReceipt[] Inject(ModuleDefinition module)
    {
        Require(!Types(module).Any(t => IsAdded(t) || t.Name is "NXFrameProfile43" or "NXTileProfile44" or "NXTileCost45" or "NXLight47" or "NXUi48"), "old or existing injected helper forbidden");
        using var template = AssemblyDefinition.ReadAssembly(typeof(Tile49Template).Assembly.Location);
        var source = template.MainModule.Types.Single(t => t.Name == nameof(Tile49Template));
        Require(!source.Methods.Any(m => m.IsConstructor), "runtime template must initialize lazily, not through a type constructor");
        var helper = new TypeDefinition("Terraria", HelperName, TA.NotPublic | TA.Abstract | TA.Sealed, module.TypeSystem.Object);
        module.Types.Add(helper);
        var map = new Dictionary<string, TypeReference> { [nameof(Tile49Template)] = helper };
        var pairs = new List<(TypeDefinition Source, TypeDefinition Target)> { (source, helper) };
        foreach (string name in new[] { "Tile49Metric", "Tile49Context", "Tile49Pass", "Tile49LayerTotals", "Tile49State", "Tile49Range", "Tile49StateTotals" })
        {
            var from = template.MainModule.Types.Single(t => t.Name == name);
            var to = new TypeDefinition("", name, TA.NestedAssembly | TA.SequentialLayout | TA.Sealed | TA.BeforeFieldInit, new TypeReference("System", "ValueType", module, module.TypeSystem.CoreLibrary));
            helper.NestedTypes.Add(to); map.Add(name, to); pairs.Add((from, to));
        }
        foreach (var pair in new[] { ("Tile49MainShape", "Terraria.Main"), ("Tile49PlayerShape", "Terraria.Player"), ("Tile49EntityShape", "Terraria.Entity") })
            map.Add(pair.Item1, Types(module).Single(t => t.FullName == pair.Item2));
        map.Add("Tile49VectorShape", module.GetTypeReferences().First(t => t.FullName == "Microsoft.Xna.Framework.Vector2").Resolve());
        var mapperType = Support.GetType("Patcher+Mapper", true)!;
        var mapper = Activator.CreateInstance(mapperType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { module, map }, null)!;
        TypeReference Type(TypeReference type) => (TypeReference)mapperType.GetMethod("Type", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mapper, new object[] { type })!;
        object Member(object value)
        {
            if (value is MethodReference method && map.TryGetValue(method.DeclaringType.FullName, out var owner))
                return ((TypeDefinition)owner).Methods.Single(m => m.Name == method.Name && m.ReturnType.FullName == Type(method.ReturnType).FullName && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(method.Parameters.Select(p => Type(p.ParameterType).FullName)));
            if (value is FieldReference field && map.TryGetValue(field.DeclaringType.FullName, out var declaring))
            {
                var mapped = ((TypeDefinition)declaring).Fields.Single(f => f.Name == field.Name);
                Require(mapped.FieldType.FullName == Type(field.FieldType).FullName || field.DeclaringType.Name == "Tile49MainShape" && field.Name == "FrameSkipMode" && mapped.FieldType.Resolve().IsEnum, "shape field type differs " + field.FullName);
                return module.ImportReference(mapped);
            }
            return mapperType.GetMethod("Member", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mapper, new[] { value })!;
        }
        foreach (var (from, to) in pairs)
        {
            Require(!from.HasNestedTypes && !from.HasProperties && !from.HasEvents && !from.HasGenericParameters, "unexpected runtime template metadata " + from.FullName);
            foreach (var field in from.Fields)
            {
                var added = new FieldDefinition(field.Name, field.Attributes, Type(field.FieldType));
                if (field.HasConstant) added.Constant = field.Constant;
                if (field.CustomAttributes.Any(a => a.AttributeType.FullName == "System.ThreadStaticAttribute"))
                {
                    var attribute = new TypeReference("System", "ThreadStaticAttribute", module, module.TypeSystem.CoreLibrary);
                    added.CustomAttributes.Add(new CustomAttribute(new MethodReference(".ctor", module.TypeSystem.Void, attribute) { HasThis = true }));
                }
                to.Fields.Add(added);
            }
            foreach (var method in from.Methods) Invoke("Patcher", "Define", method, to, (Func<TypeReference, TypeReference>)Type);
        }
        foreach (var (from, to) in pairs)
            foreach (var method in from.Methods)
                Copy(method, to.Methods.Single(m => m.Name == method.Name && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(method.Parameters.Select(p => Type(p.ParameterType).FullName))), Type, Member);
        helper.Fields.Add(new FieldDefinition("SourceHash", Mono.Cecil.FieldAttributes.Assembly | Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.Literal | Mono.Cecil.FieldAttributes.HasDefault, module.TypeSystem.String) { Constant = Program.SourceHash() });
        MethodDefinition Hook(string name) => helper.Methods.Single(m => m.Name == name);
        return new[] { PatchFrame(Program.Method(module, Program.MainDrawName), Hook), PatchDraw(Program.Draw(module), (TypeDefinition)map["Tile49Pass"], Hook), PatchSingle(Program.Single(module), helper, Hook), PatchRun(Program.Method(module, Program.RunGameName), Hook) };
    }

    sealed class Splice
    {
        internal readonly MethodDefinition Method;
        internal readonly Instruction[] Original;
        readonly Dictionary<Instruction, Instruction> redirects = new();
        readonly int locals, handlers;
        internal readonly List<Instruction> Returns = new();
        internal Splice(MethodDefinition method) { Method = method; Original = method.Body.Instructions.ToArray(); locals = method.Body.Variables.Count; handlers = method.Body.ExceptionHandlers.Count; }
        internal Instruction At(int offset) => Original.Single(i => i.Offset == offset);
        internal void Before(Instruction target, bool redirect, params Instruction[] additions)
        {
            Require(additions.Length > 0, "empty splice");
            foreach (var instruction in additions) Method.Body.GetILProcessor().InsertBefore(target, instruction);
            if (!redirect) return;
            redirects.Add(additions[0], target);
            foreach (var instruction in Original)
            {
                if (ReferenceEquals(instruction.Operand, target)) instruction.Operand = additions[0];
                else if (instruction.Operand is Instruction[] targets) for (int n = 0; n < targets.Length; n++) if (targets[n] == target) targets[n] = additions[0];
            }
        }
        internal TileHelperReceipt Finish()
        {
            Widen(Method); Method.Body.MaxStackSize += 8;
            return new(Method.FullName, Original.Select(i => Method.Body.Instructions.IndexOf(i)).ToArray(), redirects.ToDictionary(p => Method.Body.Instructions.IndexOf(p.Key), p => Method.Body.Instructions.IndexOf(p.Value)), locals, Method.Body.Variables.Count - locals, handlers, Method.Body.ExceptionHandlers.Count - handlers, Returns.Select(i => Method.Body.Instructions.IndexOf(i)).ToArray());
        }
    }

    static TileHelperReceipt PatchFrame(MethodDefinition method, Func<string, MethodDefinition> hook)
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
        return edit.Finish();
    }

    static TileHelperReceipt PatchDraw(MethodDefinition method, TypeDefinition pass, Func<string, MethodDefinition> hook)
    {
        var edit = new Splice(method);
        Require(edit.Original.Length == 815 && method.Body.ExceptionHandlers.Count == 0 && edit.At(0x9cd).OpCode == OpCodes.Ret, "Draw shape differs");
        Require(edit.At(0x1ef).OpCode == OpCodes.Ldloc_S && edit.At(0x912).OpCode == OpCodes.Ldarg_0 && edit.At(0x203).OpCode == OpCodes.Ldsfld && edit.At(0x25a).OpCode == OpCodes.Bne_Un && edit.At(0x8eb).Operand is MethodReference { Name: "DrawSingleTile" }, "Draw boundaries differ");
        var state = new VariableDefinition(pass); var selected = new VariableDefinition(method.Module.TypeSystem.Boolean);
        method.Body.Variables.Add(state); method.Body.Variables.Add(selected);
        FieldDefinition Field(string name) => pass.Fields.Single(f => f.Name == name);
        Instruction[] Increment(string name) => new[] { Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Ldfld, Field(name)), Instruction.Create(OpCodes.Ldc_I8, 1L), Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Stfld, Field(name)) };
        edit.Before(edit.Original[0], false, Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Call, hook("Enter")));
        void Region(int offset, int region) => edit.Before(edit.At(offset), true, Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Ldc_I4, region), Instruction.Create(OpCodes.Call, hook("Region")));
        Region(0x1ef, 0); Region(0x912, 1); Region(0x9cd, 2);
        edit.Before(edit.At(0x203), true, Increment("Visited")); edit.Before(edit.At(0x25f), true, Increment("Eligible"));
        var call = edit.At(0x8eb);
        var count = Increment("Calls");
        var pre = new List<Instruction> {
            Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Stloc, selected),
            Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Ldfld, Field("Enabled")), Instruction.Create(OpCodes.Brfalse, count[0]),
            Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Ldfld, Field("Calls")),
            Instruction.Create(OpCodes.Ldloca, state), Instruction.Create(OpCodes.Ldfld, Field("Phase")), Instruction.Create(OpCodes.Conv_I8),
            Instruction.Create(OpCodes.Add), Instruction.Create(OpCodes.Ldc_I8, 127L), Instruction.Create(OpCodes.And), Instruction.Create(OpCodes.Ldc_I8, 0L), Instruction.Create(OpCodes.Ceq), Instruction.Create(OpCodes.Stloc, selected)
        };
        pre.AddRange(count); pre.AddRange(new[] { Instruction.Create(OpCodes.Ldloc, selected), Instruction.Create(OpCodes.Brfalse, call), Instruction.Create(OpCodes.Call, hook("BeginSelected")) });
        // Original arguments remain below these observer operands on every path.
        edit.Before(call, false, pre.ToArray());
        var ret = edit.At(0x9cd); var finalRet = Instruction.Create(OpCodes.Ret);
        ret.OpCode = OpCodes.Leave; ret.Operand = finalRet; edit.Returns.Add(ret);
        var finallyStart = Instruction.Create(OpCodes.Ldloca, state);
        foreach (var instruction in new[] { finallyStart, Instruction.Create(OpCodes.Call, hook("Finish")), Instruction.Create(OpCodes.Endfinally), finalRet }) method.Body.Instructions.Add(instruction);
        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = edit.Original[0], TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = finalRet });
        return edit.Finish();
    }

    static TileHelperReceipt PatchSingle(MethodDefinition method, TypeDefinition helper, Func<string, MethodDefinition> hook)
    {
        var edit = new Splice(method);
        Require(edit.Original.Length == 2312 && method.Body.ExceptionHandlers.Count == 0, "Single shape differs");
        var selected = new VariableDefinition(method.Module.TypeSystem.Boolean); method.Body.Variables.Add(selected);
        edit.Before(edit.Original[0], false, Instruction.Create(OpCodes.Ldsfld, helper.Fields.Single(f => f.Name == "Pending")), Instruction.Create(OpCodes.Stloc, selected), Instruction.Create(OpCodes.Ldloc, selected), Instruction.Create(OpCodes.Brfalse, edit.Original[0]), Instruction.Create(OpCodes.Call, hook("ConsumePending")));
        foreach (var (offset, id, name) in new[] { (0x50, 0, "GetColor"), (0xd3, 1, "GetTileDrawData"), (0x16b, 2, "GetTileOutlineInfo"), (0x570, 3, "DrawTiles_GetLightOverride"), (0x6f4, 0, "GetColor"), (0x7de, 4, "GetFinalLight") })
        {
            var call = edit.At(offset); var next = call.Next;
            Require(call.OpCode.Code is Code.Call or Code.Callvirt && call.Operand is MethodReference m && m.Name == name, "Single helper site differs " + offset.ToString("x"));
            edit.Before(call, true, Instruction.Create(OpCodes.Ldloc, selected), Instruction.Create(OpCodes.Brfalse, call), Instruction.Create(OpCodes.Ldc_I4, id), Instruction.Create(OpCodes.Call, hook("BeginOp")));
            edit.Before(next, false, Instruction.Create(OpCodes.Ldloc, selected), Instruction.Create(OpCodes.Brfalse, next), Instruction.Create(OpCodes.Ldc_I4, id), Instruction.Create(OpCodes.Call, hook("EndOp")));
        }
        var returns = edit.Original.Where(i => i.OpCode == OpCodes.Ret).ToArray(); Require(returns.Length == 5, "Single return count differs");
        foreach (var ret in returns) edit.Before(ret, true, Instruction.Create(OpCodes.Ldloc, selected), Instruction.Create(OpCodes.Brfalse, ret), Instruction.Create(OpCodes.Call, hook("EndSample")));
        return edit.Finish();
    }

    static TileHelperReceipt PatchRun(MethodDefinition method, Func<string, MethodDefinition> hook)
    {
        var edit = new Splice(method); var call = edit.At(0xa5); var next = edit.At(0xaa);
        Require(call.Operand is MethodReference { Name: "Run", DeclaringType.FullName: "Microsoft.Xna.Framework.Game" } && call.Next == next && next.OpCode.FlowControl == FlowControl.Branch, "RunGame normal-return site differs");
        edit.Before(next, false, Instruction.Create(OpCodes.Call, hook("Flush")));
        return edit.Finish();
    }
}
