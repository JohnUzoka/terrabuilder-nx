using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using static Common;
using MA = Mono.Cecil.MethodAttributes;
using TA = Mono.Cecil.TypeAttributes;

internal static class ReusePatcher
{
    internal const string HelperName = "NXTileReuse46", ResetName = "NXReset46";
    internal sealed record Plan(string Method, int[] OriginalIndices, Dictionary<int, int> Entries, int OriginalLocals, int OriginalHandlers, int[] ReturnsChanged, bool ReplacedAllocation);
    internal static bool IsAdded(TypeDefinition type) => type.Name == HelperName || type.DeclaringType?.Name == HelperName;
    internal static bool IsReset(MethodDefinition method) => method.Name == ResetName && method.DeclaringType.FullName == "Terraria.DataStructures.TileDrawInfo";
    internal static void Run(string input, string fna, string output, AssemblyDefinition original, bool accept)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Run acceptance in Linux monobuild");
        var resolverType = Support.GetType("ReferenceAudit+PinnedResolver", true)!;
        using var resolver = (IAssemblyResolver)Activator.CreateInstance(resolverType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { Path.GetDirectoryName(input)! }, null)!;
        using var game = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(input)), new ReaderParameters { AssemblyResolver = resolver });
        var plans = Inject(game.MainModule);
        game.MainModule.Mvid = Guid.Empty;
        using (var identity = new MemoryStream()) { game.Write(identity, new WriterParameters { Timestamp = 0 }); game.MainModule.Mvid = new Guid(System.Security.Cryptography.SHA256.HashData(identity.ToArray()).AsSpan(0, 16)); }
        byte[] bytes; using (var stream = new MemoryStream()) { game.Write(stream, new WriterParameters { Timestamp = 0 }); bytes = stream.ToArray(); }
        using var changed = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
        Json(Path.Combine(output, "injection-plan.json"), plans);
        if (!accept)
        {
            File.WriteAllBytes(Path.Combine(output, "Terraria.unaccepted.exe"), bytes);
            Json(Path.Combine(output, "emission.json"), new { accepted = false, sha256 = Sha(bytes), mvid = changed.MainModule.Mvid });
            Console.WriteLine("EMITTED UNACCEPTED " + output); return;
        }
        var preservation = ReuseAudit.Preservation(original, changed, plans);
        var resources = PeResources.Fingerprints(File.ReadAllBytes(input));
        Require(resources.SequenceEqual(PeResources.Fingerprints(bytes)), "native resources changed");
        var signatures = ReuseAudit.Signatures(bytes);
        var sdk = ReuseAudit.Sdk(bytes, input);
        Json(Path.Combine(output, "raw-signatures.json"), signatures); Json(Path.Combine(output, "sdk-references.json"), sdk);
        string probe = Environment.GetEnvironmentVariable("REUSE_FRAME_PROBE") ?? throw new InvalidDataException("REUSE_FRAME_PROBE fixture infrastructure required");
        File.Copy(probe, Path.Combine(output, "FrameProfileProbe.dll"));
        var proofType = typeof(Program).Assembly.GetType("ReuseProof", true)!;
        var proof = proofType.GetMethod("Verify", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { original, changed, output })!;
        Require(Sha(File.ReadAllBytes(input)) == Program.InputHash, "input changed during acceptance");
        string destination = Path.Combine(output, "Terraria.exe");
        using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) stream.Write(bytes);
        File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Json(Path.Combine(output, "acceptance.json"), new { accepted = true, inputSha256 = Program.InputHash, outputSha256 = Sha(bytes), outputMvid = changed.MainModule.Mvid, changedMethods = plans.Select(p => p.Method), addedReset = ResetName, preservation, resources, signatures, sdk, proof, scope = "Clean42-derived scratch storage experiment; no43-45profilers, original game signatures and effects preserved subject to pinned lifetime audit and executable fixtures; hardware performance untested" });
        Console.WriteLine("ACCEPTED " + destination + " sha256=" + Sha(bytes) + " mvid=" + changed.MainModule.Mvid);
    }

    internal static Plan[] Inject(ModuleDefinition module)
    {
        Require(!Types(module).Any(IsAdded) && !Types(module).SelectMany(t => t.Methods).Any(IsReset), "already patched");
        var drawing = module.GetType("Terraria.GameContent.Drawing.TileDrawing");
        var scratch = module.GetType("Terraria.DataStructures.TileDrawInfo");
        Require(scratch.BaseType.FullName == "System.Object" && scratch.Interfaces.Count == 0 && scratch.Methods.Count == 1 && scratch.Methods[0].IsConstructor && scratch.Fields.Count == 19, "scratch lifecycle/layout differs from audited baseline");
        AddReset(module, scratch);
        var helper = new TypeDefinition("", HelperName, TA.NestedAssembly | TA.Abstract | TA.Sealed, module.TypeSystem.Object); drawing.NestedTypes.Add(helper);
        var scope = new TypeDefinition("", "Scope", TA.NestedAssembly | TA.Sealed | TA.BeforeFieldInit, module.TypeSystem.Object); helper.NestedTypes.Add(scope);
        using var template = AssemblyDefinition.ReadAssembly(typeof(ReuseTemplate).Assembly.Location);
        var source = template.MainModule.Types.Single(t => t.Name == nameof(ReuseTemplate));
        var sourceScope = template.MainModule.Types.Single(t => t.Name == nameof(ReuseScopeShape));
        var map = new Dictionary<string, TypeReference> { [nameof(ReuseTemplate)] = helper, [nameof(ReuseScopeShape)] = scope, [nameof(ReuseOwnerShape)] = drawing, [nameof(ReuseScratchShape)] = scratch };
        var mapperType = Support.GetType("Patcher+Mapper", true)!;
        var mapper = Activator.CreateInstance(mapperType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { module, map }, null)!;
        TypeReference Type(TypeReference type) => (TypeReference)mapperType.GetMethod("Type", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mapper, new object[] { type })!;
        object Member(object member) => mapperType.GetMethod("Member", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mapper, new[] { member })!;
        foreach (var (from, to) in new[] { (source, helper), (sourceScope, scope) })
        {
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
        foreach (var (from, to) in new[] { (source, helper), (sourceScope, scope) }) foreach (var method in from.Methods)
            Copy(method, to.Methods.Single(m => m.Name == method.Name && m.Parameters.Count == method.Parameters.Count), Type, Member);
        MethodDefinition Hook(string name) => helper.Methods.Single(m => m.Name == name);
        Require(Types(module).SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions.Select(i => (method: m, instruction: i))).Where(p => p.instruction.Operand is MethodReference r && r.FullName == Program.SingleName).All(p => p.method.FullName == Program.DrawName), "unaudited direct Single caller");
        return new[] { PatchDraw(Program.Draw(module), scope, Hook), PatchSingle(Program.Single(module), scope, Hook) };
    }
    static void AddReset(ModuleDefinition module, TypeDefinition scratch)
    {
        var slices = scratch.Fields.Single(f => f.Name == "colorSlices");
        Require(slices.FieldType.FullName == "Microsoft.Xna.Framework.Vector3[]", "owned slice type changed");
        var arrayType = new TypeReference("System", "Array", module, module.TypeSystem.CoreLibrary);
        var reset = new MethodDefinition(ResetName, MA.Assembly | MA.HideBySig, module.TypeSystem.Void);
        reset.Parameters.Add(new ParameterDefinition("ownedSlices", Mono.Cecil.ParameterAttributes.None, arrayType)); scratch.Methods.Add(reset);
        var il = reset.Body.GetILProcessor();
        foreach (var field in scratch.Fields.Where(f => f != slices))
        {
            Require(!field.IsStatic, "unexpected static scratch state");
            il.Append(Instruction.Create(OpCodes.Ldarg_0)); il.Append(Instruction.Create(OpCodes.Ldflda, field)); il.Append(Instruction.Create(OpCodes.Initobj, field.FieldType));
        }
        il.Append(Instruction.Create(OpCodes.Ldarg_0)); il.Append(Instruction.Create(OpCodes.Ldarg_1)); il.Append(Instruction.Create(OpCodes.Castclass, slices.FieldType)); il.Append(Instruction.Create(OpCodes.Stfld, slices));
        var clear = new MethodReference("Clear", module.TypeSystem.Void, arrayType) { HasThis = false };
        clear.Parameters.Add(new ParameterDefinition(arrayType)); clear.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32)); clear.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        il.Append(Instruction.Create(OpCodes.Ldarg_1)); il.Append(Instruction.Create(OpCodes.Ldc_I4_0)); il.Append(Instruction.Create(OpCodes.Ldc_I4, 9)); il.Append(Instruction.Create(OpCodes.Call, clear)); il.Append(Instruction.Create(OpCodes.Ret)); reset.Body.MaxStackSize = 3;
    }
    sealed class Splice
    {
        internal readonly MethodDefinition Method;
        internal readonly Instruction[] Original;
        readonly int locals, handlers;
        readonly Dictionary<Instruction, Instruction> redirects = new();
        readonly List<Instruction> returns = new();
        internal Splice(MethodDefinition method) { Method = method; Original = method.Body.Instructions.ToArray(); locals = method.Body.Variables.Count; handlers = method.Body.ExceptionHandlers.Count; Require(handlers == 0, "audited method handlers differ"); }
        internal void Before(Instruction target, bool redirect, params Instruction[] added)
        {
            foreach (var instruction in added) Method.Body.GetILProcessor().InsertBefore(target, instruction);
            if (!redirect) return;
            redirects.Add(added[0], target);
            foreach (var instruction in Original)
            {
                if (ReferenceEquals(instruction.Operand, target)) instruction.Operand = added[0];
                else if (instruction.Operand is Instruction[] targets) for (int i = 0; i < targets.Length; i++) if (targets[i] == target) targets[i] = added[0];
            }
        }
        internal void Finally(Instruction first, VariableDefinition local, MethodDefinition hook)
        {
            var finalReturn = Instruction.Create(OpCodes.Ret);
            foreach (var ret in Original.Where(i => i.OpCode == OpCodes.Ret)) { returns.Add(ret); ret.OpCode = OpCodes.Leave; ret.Operand = finalReturn; }
            var start = Instruction.Create(OpCodes.Ldloc, local);
            Method.Body.Instructions.Add(start); Method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, hook)); Method.Body.Instructions.Add(Instruction.Create(OpCodes.Endfinally)); Method.Body.Instructions.Add(finalReturn);
            Method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = first, TryEnd = start, HandlerStart = start, HandlerEnd = finalReturn });
        }
        internal Plan Finish(bool allocation)
        {
            Widen(Method); Method.Body.MaxStackSize += 4;
            return new Plan(Method.FullName, Original.Select(i => Method.Body.Instructions.IndexOf(i)).ToArray(), redirects.ToDictionary(p => Method.Body.Instructions.IndexOf(p.Key), p => Method.Body.Instructions.IndexOf(p.Value)), locals, handlers, returns.Select(i => Method.Body.Instructions.IndexOf(i)).ToArray(), allocation);
        }
    }
    static Plan PatchDraw(MethodDefinition method, TypeDefinition scope, Func<string, MethodDefinition> hook)
    {
        var edit = new Splice(method); Require(edit.Original.Count(i => i.OpCode == OpCodes.Ret) == 1, "Draw return shape changed");
        var previous = new VariableDefinition(scope); method.Body.Variables.Add(previous);
        edit.Before(edit.Original[0], false, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, hook("Enter")), Instruction.Create(OpCodes.Stloc, previous));
        edit.Finally(edit.Original[0], previous, hook("Exit"));
        return edit.Finish(false);
    }
    static Plan PatchSingle(MethodDefinition method, TypeDefinition scope, Func<string, MethodDefinition> hook)
    {
        var edit = new Splice(method); Require(edit.Original.Count(i => i.OpCode == OpCodes.Ret) == 5 && edit.Original[0].OpCode == OpCodes.Newobj && edit.Original[0].Operand is MethodReference ctor && ctor.DeclaringType.FullName == "Terraria.DataStructures.TileDrawInfo", "Single allocation/return shape changed");
        var lease = new VariableDefinition(scope); method.Body.Variables.Add(lease);
        var first = Instruction.Create(OpCodes.Ldarg_0);
        edit.Before(edit.Original[0], true, first, Instruction.Create(OpCodes.Ldloca, lease));
        edit.Original[0].OpCode = OpCodes.Call; edit.Original[0].Operand = hook("Acquire");
        edit.Finally(first, lease, hook("Release"));
        return edit.Finish(true);
    }
}
