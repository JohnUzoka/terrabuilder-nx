using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class Program {
    static void Main(string[] args) {
        if (args.Length < 2) {
            Console.Error.WriteLine("Usage: Patcher <input.dll> <output.dll> [assembly search dir]...");
            Environment.Exit(64);
        }
        string inputPath = args[0];
        string outputPath = args[1];

        Console.WriteLine($"Reading {inputPath}...");
        var resolver = new DefaultAssemblyResolver();
        foreach (var dir in args.Skip(2)) {
            resolver.AddSearchDirectory(dir);
        }

        var readerParams = new ReaderParameters { 
            ReadSymbols = false,
            AssemblyResolver = resolver
        };
        var asm = AssemblyDefinition.ReadAssembly(inputPath, readerParams);

        TypeDefinition FindType(string fullName) {
            foreach (var t in GetAllTypes(asm.MainModule.Types)) {
                if (string.Equals(t.FullName, fullName, StringComparison.OrdinalIgnoreCase)) return t;
            }
            return null;
        }

        // 1. Patch Terraria.ModLoader.Engine.InstallVerifier::Startup
        var installVerifier = FindType("Terraria.ModLoader.Engine.InstallVerifier");
        if (installVerifier != null) {
            var startup = installVerifier.Methods.FirstOrDefault(m => m.Name == "Startup");
            if (startup != null) {
                Console.WriteLine("Patching InstallVerifier::Startup -> set GoG platform...");
                startup.Body.Instructions.Clear();
                startup.Body.Variables.Clear();
                var il = startup.Body.GetILProcessor();
                var distField = installVerifier.Fields.FirstOrDefault(f => f.Name == "DistributionPlatform");
                il.Append(il.Create(OpCodes.Ldc_I4_2));
                il.Append(il.Create(OpCodes.Stsfld, distField));
                il.Append(il.Create(OpCodes.Ret));
            }
        }

        // 2. Patch Terraria.Main::InitTMLContentManager
        var mainType = FindType("Terraria.Main");
        if (mainType != null) {
            var initContent = mainType.Methods.FirstOrDefault(m => m.Name == "InitTMLContentManager");
            if (initContent != null) {
                Console.WriteLine("Patching Main::InitTMLContentManager...");
                var tmlContentManagerType = FindType("Terraria.ModLoader.Engine.TMLContentManager");
                MethodReference ctor = null;
                if (tmlContentManagerType != null) {
                    var m = tmlContentManagerType.Methods.FirstOrDefault(x => x.IsConstructor && x.Parameters.Count == 3);
                    if (m != null) ctor = asm.MainModule.ImportReference(m);
                }

                var baseType = mainType.BaseType.Resolve();
                var contentProp = baseType.Properties.FirstOrDefault(p => p.Name == "Content");
                var getContent = asm.MainModule.ImportReference(contentProp.GetMethod);
                var setContent = asm.MainModule.ImportReference(contentProp.SetMethod);
                var contentMgrType = contentProp.PropertyType.Resolve();
                var getSvcProv = asm.MainModule.ImportReference(contentMgrType.Properties.FirstOrDefault(p => p.Name == "ServiceProvider").GetMethod);

                initContent.Body.Instructions.Clear();
                initContent.Body.Variables.Clear();
                var il = initContent.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldarg_0)); // this
                il.Append(il.Create(OpCodes.Ldarg_0)); // this
                il.Append(il.Create(OpCodes.Call, getContent)); // this.Content
                il.Append(il.Create(OpCodes.Callvirt, getSvcProv)); // .ServiceProvider
                il.Append(il.Create(OpCodes.Ldstr, "Content"));
                il.Append(il.Create(OpCodes.Ldnull));
                il.Append(il.Create(OpCodes.Newobj, ctor));
                il.Append(il.Create(OpCodes.Call, setContent));
                il.Append(il.Create(OpCodes.Ret));
            }
        }

        // 3. Patch Terraria.ModLoader.Engine.LoggingHooks::Init
        var loggingHooks = FindType("Terraria.ModLoader.Engine.LoggingHooks");
        if (loggingHooks != null) {
            var init = loggingHooks.Methods.FirstOrDefault(m => m.Name == "Init");
            if (init != null) {
                Console.WriteLine("Patching LoggingHooks::Init -> no-op ret...");
                init.Body.Instructions.Clear();
                init.Body.Variables.Clear();
                var il = init.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ret));
            }
        }

        // 4. Patch Terraria.Social.Steam.SteamedWraps::Initialize
        var steamedWraps = FindType("Terraria.Social.Steam.SteamedWraps");
        if (steamedWraps != null) {
            var init = steamedWraps.Methods.FirstOrDefault(m => m.Name == "Initialize");
            if (init != null) {
                Console.WriteLine("Patching SteamedWraps::Initialize -> no-op ret...");
                init.Body.Instructions.Clear();
                init.Body.Variables.Clear();
                var il = init.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ret));
            }
        }

        // 5. Patch Terraria.Social.Steam.WorkshopHelper::GetWorkshopFolder
        var workshopHelper = FindType("Terraria.Social.Steam.WorkshopHelper");
        if (workshopHelper != null) {
            var getFolder = workshopHelper.Methods.FirstOrDefault(m => m.Name == "GetWorkshopFolder");
            if (getFolder != null) {
                Console.WriteLine("Patching WorkshopHelper::GetWorkshopFolder -> \"workshop\"...");
                getFolder.Body.Instructions.Clear();
                getFolder.Body.Variables.Clear();
                var il = getFolder.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldstr, "workshop"));
                il.Append(il.Create(OpCodes.Ret));
            }
        }

        // 6. Patch Terraria.ModLoader.Core.AssemblyManager.AssemblyRedirects
        var redirectsType = FindType("Terraria.ModLoader.Core.AssemblyManager/AssemblyRedirects");
        if (redirectsType != null) {
            var cctor = redirectsType.Methods.FirstOrDefault(m => m.Name == ".cctor");
            if (cctor != null) {
                Console.WriteLine("Patching AssemblyRedirects::.cctor to strip MonoMod hook...");
                cctor.Body.Instructions.Clear();
                cctor.Body.Variables.Clear();
                var il = cctor.Body.GetILProcessor();
                var dictField = redirectsType.Fields.FirstOrDefault(f => f.Name == "_redirects");
                var dictCtor = asm.MainModule.ImportReference(dictField.FieldType.Resolve().Methods.FirstOrDefault(m => m.IsConstructor && m.Parameters.Count == 0));
                il.Append(il.Create(OpCodes.Newobj, dictCtor));
                il.Append(il.Create(OpCodes.Stsfld, dictField));
                il.Append(il.Create(OpCodes.Ret));
            }
        }

        Console.WriteLine($"Writing patched assembly to {outputPath}...");
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
