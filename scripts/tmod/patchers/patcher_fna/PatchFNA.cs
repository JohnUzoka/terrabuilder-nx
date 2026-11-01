using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class PatchFNA {
    static void Main(string[] args) {
        if (args.Length < 2) {
            Console.Error.WriteLine("Usage: PatchFNA <input.dll> <output.dll>");
            Environment.Exit(64);
        }
        string inputPath = args[0];
        string outputPath = args[1];

        Console.WriteLine($"Reading {inputPath}...");
        var asm = AssemblyDefinition.ReadAssembly(inputPath);
        var type = asm.MainModule.Types.FirstOrDefault(t => t.FullName == "Microsoft.Xna.Framework.SDL2_FNAPlatform");
        if (type != null) {
            var getBaseDir = type.Methods.FirstOrDefault(m => m.Name == "GetBaseDirectory");
            if (getBaseDir != null) {
                Console.WriteLine("Patching SDL2_FNAPlatform::GetBaseDirectory -> ldstr \"romfs:/\" ret...");
                getBaseDir.Body.Instructions.Clear();
                getBaseDir.Body.Variables.Clear();
                var il = getBaseDir.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldstr, "romfs:/"));
                il.Append(il.Create(OpCodes.Ret));
            }
        }
        var type3 = asm.MainModule.Types.FirstOrDefault(t => t.FullName == "Microsoft.Xna.Framework.SDL3_FNAPlatform");
        if (type3 != null) {
            var getBaseDir3 = type3.Methods.FirstOrDefault(m => m.Name == "GetBaseDirectory");
            if (getBaseDir3 != null) {
                Console.WriteLine("Patching SDL3_FNAPlatform::GetBaseDirectory -> ldstr \"romfs:/\" ret...");
                getBaseDir3.Body.Instructions.Clear();
                getBaseDir3.Body.Variables.Clear();
                var il = getBaseDir3.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldstr, "romfs:/"));
                il.Append(il.Create(OpCodes.Ret));
            }
        }
        Console.WriteLine($"Writing patched FNA to {outputPath}...");
        asm.Write(outputPath);
        Console.WriteLine("Done!");
    }
}
