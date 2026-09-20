using Mono.Cecil;
using static Common;

internal static class Program
{
    internal const string InputHash = "87b833138e342ff8ac8378535d181bcf2e185d9505e76d612cd9918869bfb061";
    // Interface required by the linked, reviewed native-resource utility.
    internal static void Require(bool ok, string message) => Common.Require(ok, message);
    internal static string Sha(byte[] bytes) => Common.Sha(bytes);
    internal const string DrawName = "System.Void Terraria.GameContent.Drawing.TileDrawing::Draw(System.Boolean,System.Boolean,System.Int32)";
    internal static MethodDefinition Draw(ModuleDefinition m) => Types(m).SelectMany(t => t.Methods).Single(x => x.FullName == DrawName);
    static int Main(string[] args)
    {
        try {
            Require(args.Length == 4, "Usage: PatchTileProfile <inspect|accept> <Terraria.exe> <FNA.dll> <fresh-output>");
            string input = Path.GetFullPath(args[1]), fna = Path.GetFullPath(args[2]), output = Path.GetFullPath(args[3]);
            Require(!Path.Exists(output) && new DirectoryInfo(output).LinkTarget == null, "existing/alias output forbidden");
            Require(Sha(File.ReadAllBytes(input)) == InputHash, "unsupported or already-patched input");
            Require(Sha(File.ReadAllBytes(fna)) == "15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f", "FNA differs");
            using var original = AssemblyDefinition.ReadAssembly(input);
            Require(original.MainModule.Mvid == Guid.Parse("689c1a33-8aff-12ce-a940-803508ad5db3"), "input MVID differs");
            Directory.CreateDirectory(output);
            if (args[0] == "inspect") {
                Json(Path.Combine(output, "inspection.json"), new { draw = new { hash = Fingerprint(Draw(original.MainModule)), il = Body(Draw(original.MainModule)) }, types = Types(original.MainModule).Where(t => t.FullName.StartsWith("Terraria.TimeLogger")).Select(t => new { t.FullName, methods = t.Methods.Select(m => new { m.FullName, attrs = m.Attributes.ToString(), hash = Fingerprint(m), il = m.Name is "AddTime" or "Add" or "AddCounter" or "NewEntry" or "NewCounterEntry" ? Body(m) : null }) }) });
                return 0;
            }
            Require(args[0] == "accept", "unsupported command");
            TilePatcher.Accept(input, fna, output, original);
            return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
