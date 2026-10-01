using static Common;
static class AotInliningPatch
{
    public static void Apply(Mono.Cecil.ModuleDefinition module)
    {
        foreach (var pair in new[]{("Terraria.Main","SetDisplayMode"),("Terraria.Graphics.WindowStateController","TryMovingToScreen")})
            Type(module,pair.Item1).Methods.Single(m=>m.Name==pair.Item2).ImplAttributes |= Mono.Cecil.MethodImplAttributes.NoOptimization;
    }
}
