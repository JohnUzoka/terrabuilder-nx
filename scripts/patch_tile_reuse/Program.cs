using System.Reflection;
using Mono.Cecil;
using static Common;

internal static class Program
{
    internal const string InputHash = "d4c767a545d5f1a4cacbb6b0d3b16be68b6d1b9af1962d0cd439c3b7946b7298";
    internal const string DrawName = "System.Void Terraria.GameContent.Drawing.TileDrawing::Draw(System.Boolean,System.Boolean,System.Int32)";
    internal const string SingleName = "System.Void Terraria.GameContent.Drawing.TileDrawing::DrawSingleTile(Microsoft.Xna.Framework.Vector2,Microsoft.Xna.Framework.Vector2,System.Int32,System.Int32)";
    internal static void Require(bool value, string message) => Common.Require(value, message);
    internal static string Sha(byte[] bytes) => Common.Sha(bytes);
    internal static MethodDefinition Draw(ModuleDefinition module) => Types(module).SelectMany(t => t.Methods).Single(m => m.FullName == DrawName);
    internal static MethodDefinition Single(ModuleDefinition module) => Types(module).SelectMany(t => t.Methods).Single(m => m.FullName == SingleName);
    static int Main(string[] args)
    {
        try
        {
            Require(args.Length == 4, "Usage: PatchTileReuse <inspect|emit|accept> <clean42 Terraria.exe> <FNA.dll> <fresh-output>");
            string input = Path.GetFullPath(args[1]), fna = Path.GetFullPath(args[2]), output = Path.GetFullPath(args[3]);
            Require(!Path.Exists(output) && new DirectoryInfo(output).LinkTarget == null, "existing or aliased output forbidden");
            Require(Sha(File.ReadAllBytes(input)) == InputHash, "unsupported or already-patched input");
            Require(Sha(File.ReadAllBytes(fna)) == "15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f", "unsupported FNA");
            using var original = AssemblyDefinition.ReadAssembly(input);
            Require(original.MainModule.Mvid == Guid.Parse("2a9040da-3f4b-844f-a14b-3056cdda95ba"), "input identity changed");
            Directory.CreateDirectory(output);
            if (args[0] == "inspect")
            {
                Json(Path.Combine(output, "inspection.json"), new { inputSha256 = InputHash, draw = Body(Draw(original.MainModule)), single = Body(Single(original.MainModule)), fields = original.MainModule.GetType("Terraria.DataStructures.TileDrawInfo").Fields.Select(f => new { f.Name, type = f.FieldType.FullName, attrs = f.Attributes.ToString() }) });
                return 0;
            }
            Require(args[0] is "emit" or "accept", "unknown command");
            var patcher = typeof(Program).Assembly.GetType("ReusePatcher", true)!;
            patcher.GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { input, fna, output, original, args[0] == "accept" });
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
