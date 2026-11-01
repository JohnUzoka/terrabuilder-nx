using Mono.Cecil;
using Mono.Cecil.Cil;
using static NxIl;

// First-load GC loop. ModLoader.Load starts with Unload() -> Mods_Unload() -> AssemblyManager.Unload(),
// which unloads every mod AssemblyLoadContext and then runs GC.Collect() + GC.WaitForPendingFinalizers()
// ten times so the unloaded contexts can be collected (ModLoader.WarnModsStillLoaded then reports any
// that survive). On the first load of a session no mod was ever loaded: loadedModContexts and
// oldLoadContexts are empty, nothing is unloaded, and the ten forced full collections only cost time.
// In run 42 they took 2.2 s of the load thread (profile 52.87-55.06 s, before tModLoader's mod-load
// stopwatch starts); the managed heap went from 376.9 to 372.5 MB around them (NX_GC).
// AssemblyManager.Unload now returns at once when both collections are empty. Reloading mods (any
// context loaded or unloaded before) runs the original loop unchanged.
static class UnloadGcPatch
{
    public static void Apply(ModuleDefinition module)
    {
        var manager = module.GetType("Terraria.ModLoader.Core.AssemblyManager")
            ?? throw new InvalidOperationException("Terraria.ModLoader.Core.AssemblyManager not found");
        var unload = manager.Methods.Single(m => m.Name == "Unload" && m.HasBody && m.Parameters.Count == 0);
        const string Loaded = "System.Collections.Generic.Dictionary`2<System.String,Terraria.ModLoader.Core.AssemblyManager/ModLoadContext> Terraria.ModLoader.Core.AssemblyManager::loadedModContexts";
        const string Old = "System.Collections.Generic.List`1<System.WeakReference`1<System.Runtime.Loader.AssemblyLoadContext>> Terraria.ModLoader.Core.AssemblyManager::oldLoadContexts";
        const string Enumerator = "System.Collections.Generic.Dictionary`2/ValueCollection/Enumerator<System.String,Terraria.ModLoader.Core.AssemblyManager/ModLoadContext>";
        var expected = new[] {
            "ldsfld " + Loaded,
            "callvirt System.Collections.Generic.Dictionary`2/ValueCollection<!0,!1> System.Collections.Generic.Dictionary`2<System.String,Terraria.ModLoader.Core.AssemblyManager/ModLoadContext>::get_Values()",
            "callvirt System.Collections.Generic.Dictionary`2/ValueCollection/Enumerator<!0,!1> System.Collections.Generic.Dictionary`2/ValueCollection<System.String,Terraria.ModLoader.Core.AssemblyManager/ModLoadContext>::GetEnumerator()",
            "stloc.0", "br",
            "ldloca.s V_0", "call !1 " + Enumerator + "::get_Current()", "stloc.1",
            "ldsfld " + Old, "ldloc.1",
            "newobj System.Void System.WeakReference`1<System.Runtime.Loader.AssemblyLoadContext>::.ctor(!0)",
            "callvirt System.Void System.Collections.Generic.List`1<System.WeakReference`1<System.Runtime.Loader.AssemblyLoadContext>>::Add(!0)",
            "ldloc.1", "callvirt System.Void System.Runtime.Loader.AssemblyLoadContext::Unload()",
            "ldloca.s V_0", "call System.Boolean " + Enumerator + "::MoveNext()", "brtrue", "leave",
            "ldloca.s V_0", "constrained. " + Enumerator, "callvirt System.Void System.IDisposable::Dispose()", "endfinally",
            "ldsfld " + Loaded,
            "callvirt System.Void System.Collections.Generic.Dictionary`2<System.String,Terraria.ModLoader.Core.AssemblyManager/ModLoadContext>::Clear()",
            "ldc.i4.0", "stloc.2", "br",
            "call System.Void System.GC::Collect()", "call System.Void System.GC::WaitForPendingFinalizers()",
            "ldloc.2", "ldc.i4.1", "add", "stloc.2",
            "ldloc.2", "ldc.i4.s 10", "blt",
            "ret",
        };
        var text = Text(unload);
        if (!unload.IsStatic || !IsVoid(unload.ReturnType) || !text.SequenceEqual(expected)
            || unload.Body.ExceptionHandlers.Count != 1 || unload.Body.ExceptionHandlers[0].HandlerType != ExceptionHandlerType.Finally)
            throw new InvalidOperationException("unexpected AssemblyManager.Unload shape:\n  " + string.Join("\n  ", text));

        var body = unload.Body.Instructions;
        var loaded = (FieldReference)body[0].Operand;
        var old = (FieldReference)body[8].Operand;
        var dictionaryCount = new MethodReference("get_Count", module.TypeSystem.Int32, loaded.FieldType) { HasThis = true };
        var listCount = new MethodReference("get_Count", module.TypeSystem.Int32, old.FieldType) { HasThis = true };
        if (dictionaryCount.Resolve() == null || listCount.Resolve() == null)
            throw new InvalidOperationException("AssemblyManager.Unload: Dictionary/List get_Count does not resolve");

        WidenShortBranches(unload.Body);
        var il = unload.Body.GetILProcessor();
        var start = body[0];
        il.InsertBefore(start, il.Create(OpCodes.Ldsfld, loaded));
        il.InsertBefore(start, il.Create(OpCodes.Callvirt, dictionaryCount));
        il.InsertBefore(start, il.Create(OpCodes.Brtrue, start));
        il.InsertBefore(start, il.Create(OpCodes.Ldsfld, old));
        il.InsertBefore(start, il.Create(OpCodes.Callvirt, listCount));
        il.InsertBefore(start, il.Create(OpCodes.Brtrue, start));
        il.InsertBefore(start, il.Create(OpCodes.Ret));
        VerifyPatched(unload, Text(unload).Skip(7).SequenceEqual(expected) && Text(unload)[6] == "ret", "guard not inserted at the start");
        Console.WriteLine("FIX AssemblyManager.Unload: returns before its 10x GC.Collect + WaitForPendingFinalizers loop when no mod context was ever loaded (first load)");
    }

    static List<string> Text(MethodDefinition method) => method.Body.Instructions.Select(i => i.Operand is Instruction
        ? i.OpCode.Name.Replace(".s", "") : i.ToString().Substring(i.ToString().IndexOf(':') + 2)).ToList();
}
