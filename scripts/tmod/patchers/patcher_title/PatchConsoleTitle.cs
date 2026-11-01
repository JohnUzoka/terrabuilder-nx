using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class PatchConsoleTitle {
    static void Main(string[] args) {
        if (args.Length < 2) {
            Console.Error.WriteLine("Usage: PatchConsoleTitle <input.dll> <output.dll> [assembly search dir]...");
            Environment.Exit(64);
        }
        string path = args[0];
        string outputPath = args[1];
        Console.WriteLine($"Patching Console calls in {path}...");
        var resolver = new DefaultAssemblyResolver();
        foreach (var dir in args.Skip(2)) {
            resolver.AddSearchDirectory(dir);
        }

        var readerParams = new ReaderParameters { 
            ReadSymbols = false,
            AssemblyResolver = resolver
        };
        var asm = AssemblyDefinition.ReadAssembly(path, readerParams);

        // Replace calls to Console.set_Title or Console.set_ForegroundColor with pop
        foreach (var t in GetAllTypes(asm.MainModule.Types)) {
            foreach (var m in t.Methods) {
                if (!m.HasBody) continue;
                var il = m.Body.GetILProcessor();
                for (int i = 0; i < m.Body.Instructions.Count; i++) {
                    var instr = m.Body.Instructions[i];
                    if (instr.Operand is MethodReference mr && mr.DeclaringType.FullName == "System.Console") {
                        if (mr.Name == "set_Title" || mr.Name == "set_ForegroundColor" || mr.Name == "ResetColor") {
                            // pop the argument and replace with nop
                            instr.OpCode = OpCodes.Pop;
                            instr.Operand = null;
                        }
                    }
                }
            }
        }

        asm.Write(outputPath);
        Console.WriteLine("Done!");
    }

    static System.Collections.Generic.IEnumerable<TypeDefinition> GetAllTypes(System.Collections.Generic.IEnumerable<TypeDefinition> types) {
        foreach (var t in types) {
            yield return t;
            foreach (var n in GetAllTypes(t.NestedTypes)) {
                yield return n;
            }
        }
    }
}
