using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class PatchMonoLaunch {
    static void Main(string[] args) {
        if (args.Length < 2) {
            Console.Error.WriteLine("Usage: PatchMonoLaunch <input.dll> <output.dll> [assembly search dir]...");
            Environment.Exit(64);
        }
        string path = args[0];
        string outputPath = args[1];
        Console.WriteLine($"Patching MonoLaunch in {path}...");
        var resolver = new DefaultAssemblyResolver();
        foreach (var dir in args.Skip(2)) {
            resolver.AddSearchDirectory(dir);
        }

        var readerParams = new ReaderParameters { 
            ReadSymbols = false,
            AssemblyResolver = resolver
        };
        var asm = AssemblyDefinition.ReadAssembly(path, readerParams);

        var type = asm.MainModule.Types.FirstOrDefault(t => t.FullName == "Terraria.MonoLaunch");
        if (type != null) {
            var getPlat = type.Methods.FirstOrDefault(m => m.Name == "get_NativePlatformDir");
            if (getPlat != null) {
                Console.WriteLine("Patching get_NativePlatformDir -> ldstr \"Linux\" ret...");
                getPlat.Body.Instructions.Clear();
                getPlat.Body.Variables.Clear();
                var il = getPlat.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldstr, "Linux"));
                il.Append(il.Create(OpCodes.Ret));
            }
        }

        asm.Write(outputPath);
        Console.WriteLine("Done!");
    }
}
