using Mono.Cecil.Cil;
using static Common;
static class DrawGatePatch
{
    public static void Apply(Mono.Cecil.ModuleDefinition module)
    {
        var m = Method(module,"System.Void Terraria.GameContent.Drawing.TileDrawing::DrawSingleTile(Microsoft.Xna.Framework.Vector2,Microsoft.Xna.Framework.Vector2,System.Int32,System.Int32)");
        var prep = m.Body.Instructions.Single(i=>i.Offset==0x0713); var load=m.Body.Instructions.Single(i=>i.Offset==0x07b3); var br=m.Body.Instructions.Single(i=>i.Offset==0x07b5); var il=m.Body.GetILProcessor();
        // Move the original visibility gate before rectangle/vector preparation so skipped tiles avoid prepass work.
        il.Remove(load); il.Remove(br); il.InsertBefore(prep,load); il.InsertBefore(prep,br);
        foreach (var off in new[]{0x0676,0x0683}) m.Body.Instructions.Single(i=>i.Offset==off).Operand=load;
    }
}
