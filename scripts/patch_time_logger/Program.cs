using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    internal const string SeriesName = "Terraria.TimeLogger/DataSeries";
    internal static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidDataException(message);
    }
    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static IEnumerable<TypeDefinition> Types(ModuleDefinition module) => module.Types.SelectMany(Flatten);
    static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));
    internal static string Body(MethodDefinition method)
    {
        if (!method.HasBody) return "no-body";
        string Operand(object? value) => value switch
        {
            null => "",
            Instruction i => "@" + method.Body.Instructions.IndexOf(i),
            Instruction[] list => string.Join(",", list.Select(i => "@" + method.Body.Instructions.IndexOf(i))),
            VariableDefinition v => "local:" + v.Index,
            ParameterDefinition p => "arg:" + p.Index,
            MemberReference m => m.FullName,
            float f => BitConverter.SingleToInt32Bits(f).ToString("x8"),
            double d => BitConverter.DoubleToInt64Bits(d).ToString("x16"),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!
        };
        return string.Join("\n", new[] { "init=" + method.Body.InitLocals, "locals=" + string.Join(",", method.Body.Variables.Select(v => v.VariableType.FullName)) }
            .Concat(method.Body.Instructions.Select(i => i.OpCode.Code + " " + Operand(i.Operand)))
            .Concat(method.Body.ExceptionHandlers.Select(e => $"EH {e.HandlerType} {Operand(e.TryStart)} {Operand(e.TryEnd)} {Operand(e.HandlerStart)} {Operand(e.HandlerEnd)} {Operand(e.FilterStart)} {e.CatchType?.FullName}")));
    }
    internal static string Fingerprint(MethodDefinition m) => Sha(Encoding.UTF8.GetBytes(Body(m)));
    internal static void Json(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    static int Main(string[] args)
    {
        try
        {
            Require(args.Length == 4, "Usage: PatchTimeLogger <inspect|accept> <Terraria.exe> <FNA.dll> <output-directory>");
            string input = Path.GetFullPath(args[1]), fna = Path.GetFullPath(args[2]), output = Path.GetFullPath(args[3]);
            Require(!input.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.Ordinal), "output must not contain original input");
            Directory.CreateDirectory(output);
            using var game = AssemblyDefinition.ReadAssembly(input);
            using var framework = AssemblyDefinition.ReadAssembly(fna);
            var series = Types(game.MainModule).Single(t => t.FullName == SeriesName);
            var lerp = Types(framework.MainModule).Single(t => t.FullName == "Microsoft.Xna.Framework.MathHelper").Methods.Single(m => m.Name == "Lerp");
            var references = Types(game.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody)
                .SelectMany(m => m.Body.Instructions.Where(i => i.Operand is MemberReference r && (r.DeclaringType?.FullName == SeriesName || r.FullName.Contains("TimeLogger::FrameCount")))
                    .Select(i => new { caller = m.FullName, instruction = i.ToString() })).ToArray();
            Json(Path.Combine(output, "inspection.json"), new
            {
                input, sha256 = Sha(File.ReadAllBytes(input)), mvid = game.MainModule.Mvid, fnaSha256 = Sha(File.ReadAllBytes(fna)),
                fields = series.Fields.Select(f => new { f.Name, type = f.FieldType.FullName, attributes = f.Attributes.ToString() }),
                methods = series.Methods.Select(m => new { m.FullName, attributes = m.Attributes.ToString(), hash = Fingerprint(m), il = Body(m) }),
                references, lerp = new { lerp.FullName, hash = Fingerprint(lerp), il = Body(lerp) }
            });
            Console.WriteLine($"Inspection: {series.Fields.Count} fields, {series.Methods.Count} methods, {references.Length} references; {game.MainModule.Mvid}");
            if (args[0] == "inspect") return 0;
            Require(args[0] == "accept", "unknown command");
            Patcher.Accept(input, fna, output, game, framework);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
