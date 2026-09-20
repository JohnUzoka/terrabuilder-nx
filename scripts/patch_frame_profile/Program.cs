using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    internal const string InputHash = "d4c767a545d5f1a4cacbb6b0d3b16be68b6d1b9af1962d0cd439c3b7946b7298";
    internal static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static IEnumerable<TypeDefinition> Types(ModuleDefinition module) => module.Types.SelectMany(Flatten);
    static IEnumerable<TypeDefinition> Flatten(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Flatten));
    internal static string Body(MethodDefinition m)
    {
        if (!m.HasBody) return "no-body";
        string Operand(object? o) => o switch {
            null => "", Instruction i => "@" + m.Body.Instructions.IndexOf(i),
            Instruction[] a => string.Join(",", a.Select(i => "@" + m.Body.Instructions.IndexOf(i))),
            VariableDefinition v => "local:" + v.Index, ParameterDefinition p => "arg:" + p.Index,
            MemberReference r => r.FullName, float f => BitConverter.SingleToInt32Bits(f).ToString("x8"),
            double d => BitConverter.DoubleToInt64Bits(d).ToString("x16"),
            _ => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture)!
        };
        return string.Join("\n", new[] { "init=" + m.Body.InitLocals, "locals=" + string.Join(",", m.Body.Variables.Select(v => v.VariableType.FullName)) }
            .Concat(m.Body.Instructions.Select(i => i.OpCode.Code + " " + Operand(i.Operand)))
            .Concat(m.Body.ExceptionHandlers.Select(e => $"EH {e.HandlerType} {Operand(e.TryStart)} {Operand(e.TryEnd)} {Operand(e.HandlerStart)} {Operand(e.HandlerEnd)} {Operand(e.FilterStart)} {e.CatchType?.FullName}")));
    }
    internal static string Fingerprint(MethodDefinition m) => Sha(Encoding.UTF8.GetBytes(Body(m)));
    internal static void Json(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    static int Main(string[] args)
    {
        try {
            Require(args.Length == 4, "Usage: PatchFrameProfile <inspect|accept|signatures> <Terraria.exe> <FNA.dll> <fresh-output-directory>");
            string input = Path.GetFullPath(args[1]), fna = Path.GetFullPath(args[2]), output = Path.GetFullPath(args[3]);
            Require(!Directory.Exists(output) && !File.Exists(output), "output already exists; no overwrite or aliases allowed");
            if (args[0] == "signatures")
            {
                var result = RawSignatures.Verify(File.ReadAllBytes(input));
                Directory.CreateDirectory(output); Json(Path.Combine(output, "raw-signatures.json"), result); return 0;
            }
            Require(Sha(File.ReadAllBytes(input)) == InputHash, "input SHA differs from reviewed build42 (or already patched)");
            using var game = AssemblyDefinition.ReadAssembly(input);
            using var framework = AssemblyDefinition.ReadAssembly(fna);
            Require(game.MainModule.Mvid == Guid.Parse("2a9040da-3f4b-844f-a14b-3056cdda95ba"), "input MVID differs");
            Directory.CreateDirectory(output);
            if (args[0] == "inspect") {
                var names = new[] { "NewCounterEntry", "NewEntry", "StartNextFrame", "Reset", "Draw", "Update", "OnExiting", "QuitGame", "DedServ", "Run", "Main" };
                Json(Path.Combine(output, "inspect.json"), new {
                    refs = game.MainModule.AssemblyReferences.Select(r => r.FullName), fnaSha = Sha(File.ReadAllBytes(fna)),
                    types = Types(game.MainModule).Where(t => t.FullName.StartsWith("Terraria.TimeLogger") || t.FullName == "Terraria.Main").Select(t => new { t.FullName, attributes = t.Attributes.ToString(), fields = t.Fields.Where(f => t.FullName != "Terraria.Main" || new[] { "gameMenu", "gamePaused", "playerInventory", "mapFullscreen", "autoPause", "FrameSkipMode", "renderCount", "renderNow" }.Contains(f.Name)).Select(f => new { f.FullName, attributes = f.Attributes.ToString() }), methods = t.Methods.Where(m => names.Contains(m.Name)).Select(m => new { m.FullName, attributes = m.Attributes.ToString(), hash = Fingerprint(m), il = Body(m) }) }),
                    callers = Types(game.MainModule).SelectMany(t => t.Methods).Where(m => m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference r && ((r.DeclaringType.FullName == "Terraria.TimeLogger" && r.Name == "StartNextFrame") || r.Name == "Run" && r.DeclaringType.FullName == "Microsoft.Xna.Framework.Game"))).Select(m => new { m.FullName, hash = Fingerprint(m), il = Body(m) })
                });
                return 0;
            }
            Require(args[0] == "accept", "unknown operation");
            Patcher.Accept(input, fna, output, game, framework);
            return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
