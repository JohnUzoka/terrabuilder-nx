using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class PatchAutoLoad {
    static void Main(string[] args) {
        if (args.Length < 2) {
            Console.Error.WriteLine("Usage: PatchAutoLoad <input.dll> <output.dll> [assembly search dir]...");
            Environment.Exit(64);
        }
        string path = args[0];
        string outputPath = args[1];
        Console.WriteLine($"Patching LoaderManager::AutoLoad in {path}...");
        var resolver = new DefaultAssemblyResolver();
        foreach (var dir in args.Skip(2)) {
            resolver.AddSearchDirectory(dir);
        }

        var readerParams = new ReaderParameters { 
            ReadSymbols = false,
            AssemblyResolver = resolver
        };
        var asm = AssemblyDefinition.ReadAssembly(path, readerParams);
        var type = asm.MainModule.Types.FirstOrDefault(t => t.FullName == "Terraria.ModLoader.LoaderManager");
        var autoLoad = type.Methods.FirstOrDefault(m => m.Name == "AutoLoad");
        if (autoLoad != null) {
            // Find GetTypes call and change to GetExportedTypes or wrap in try/catch or custom loader
            // Let's see instructions of AutoLoad
            foreach (var instr in autoLoad.Body.Instructions) {
                if (instr.Operand is MethodReference mr && mr.Name == "GetTypes") {
                    Console.WriteLine("Replacing GetTypes with GetExportedTypes...");
                    var getExported = asm.MainModule.ImportReference(typeof(System.Reflection.Assembly).GetMethod("GetExportedTypes"));
                    instr.Operand = getExported;
                }
            }
        }
        asm.Write(outputPath);
        Console.WriteLine("Done!");
    }
}
