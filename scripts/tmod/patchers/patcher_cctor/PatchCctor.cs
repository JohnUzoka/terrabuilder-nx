using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class PatchCctor {
    static void Main(string[] args) {
        if (args.Length < 2) {
            Console.Error.WriteLine("Usage: PatchCctor <input.dll> <output.dll> [assembly search dir]...");
            Environment.Exit(64);
        }
        string path = args[0];
        string outputPath = args[1];
        Console.WriteLine($"Patching InstallVerifier..cctor in {path}...");
        var resolver = new DefaultAssemblyResolver();
        foreach (var dir in args.Skip(2)) {
            resolver.AddSearchDirectory(dir);
        }

        var readerParams = new ReaderParameters { 
            ReadSymbols = false,
            AssemblyResolver = resolver
        };
        var asm = AssemblyDefinition.ReadAssembly(path, readerParams);
        var type = asm.MainModule.Types.FirstOrDefault(t => t.FullName == "Terraria.ModLoader.Engine.InstallVerifier");
        var cctor = type.Methods.FirstOrDefault(m => m.Name == ".cctor");
        if (cctor != null) {
            cctor.Body.Instructions.Clear();
            cctor.Body.Variables.Clear();
            var il = cctor.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldstr, "Terraria.exe"));
            il.Append(il.Create(OpCodes.Stsfld, type.Fields.First(f => f.Name == "VanillaExe")));
            il.Append(il.Create(OpCodes.Ldstr, "Terraria_v1.4.5.8.exe"));
            il.Append(il.Create(OpCodes.Stsfld, type.Fields.First(f => f.Name == "CheckExe")));
            il.Append(il.Create(OpCodes.Ret));
        }
        asm.Write(outputPath);
        Console.WriteLine("Done!");
    }
}
