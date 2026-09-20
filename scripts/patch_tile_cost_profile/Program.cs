using Mono.Cecil;
using static Common;

internal static class Program
{
    internal const string InputHash = "e909fee83e8664b431cc21fbd980909ca99c8765c2c90ea2e19dedc73c940611";
    internal const string DrawName = "System.Void Terraria.GameContent.Drawing.TileDrawing::Draw(System.Boolean,System.Boolean,System.Int32)";
    internal const string SingleName = "System.Void Terraria.GameContent.Drawing.TileDrawing::DrawSingleTile(Microsoft.Xna.Framework.Vector2,Microsoft.Xna.Framework.Vector2,System.Int32,System.Int32)";
    internal static void Require(bool ok, string message) => Common.Require(ok, message);
    internal static string Sha(byte[] bytes) => Common.Sha(bytes);
    internal static MethodDefinition Draw(ModuleDefinition module) => Types(module).SelectMany(t => t.Methods).Single(m => m.FullName == DrawName);
    internal static MethodDefinition Single(ModuleDefinition module) => Types(module).SelectMany(t => t.Methods).Single(m => m.FullName == SingleName);
    static int Main(string[] args)
    {
        try
        {
            Require(args.Length == 4, "Usage: PatchTileCostProfile <inspect|emit|accept> <Terraria.exe> <FNA.dll> <fresh-output>");
            string input = Path.GetFullPath(args[1]), fna = Path.GetFullPath(args[2]), output = Path.GetFullPath(args[3]);
            Require(!Path.Exists(output) && new DirectoryInfo(output).LinkTarget == null, "existing/alias output forbidden");
            Require(Sha(File.ReadAllBytes(input)) == InputHash, "unsupported or already-patched input");
            Require(Sha(File.ReadAllBytes(fna)) == "15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f", "FNA differs");
            using var original = AssemblyDefinition.ReadAssembly(input);
            Require(original.MainModule.Mvid == Guid.Parse("e89b915c-54de-900b-8bfc-895e1a970aa3"), "input MVID differs");
            Directory.CreateDirectory(output);
            if (args[0] == "inspect")
            {
                Json(Path.Combine(output, "inspection.json"), new { draw = Body(Draw(original.MainModule)), single = Body(Single(original.MainModule)), types = Types(original.MainModule).Where(t => t.FullName.StartsWith("Terraria.TimeLogger")).Select(t => new { t.FullName, fields = t.Fields.Select(f => new { f.Name, type = f.FieldType.FullName, attrs = f.Attributes.ToString() }), methods = t.Methods.Select(m => new { m.FullName, attrs = m.Attributes.ToString(), hash = Fingerprint(m), il = m.Name is "Add" or "AddCounter" or "NewEntry" or "NewCounterEntry" ? Body(m) : null }) }) });
                return 0;
            }
            Require(args[0] is "emit" or "accept", "unsupported command");
            CostPatcher.Run(input, fna, output, original, args[0] == "accept");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
