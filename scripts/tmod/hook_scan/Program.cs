using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mono.Cecil;

namespace HookScan;

public class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: HookScan <TerrariaHooks.dll> <tModLoader.dll> <mod.dll>... -o hook-inventory.json");
            return 1;
        }

        string? outputPath = null;
        var positionalArgs = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "-o" || a == "--output")
            {
                if (i + 1 < args.Length)
                {
                    outputPath = args[++i];
                }
            }
            else if (a.StartsWith("-o="))
            {
                outputPath = a.Substring(3);
            }
            else if (a.StartsWith("--output="))
            {
                outputPath = a.Substring(9);
            }
            else
            {
                positionalArgs.Add(a);
            }
        }

        if (positionalArgs.Count < 3)
        {
            Console.WriteLine("Error: Must provide TerrariaHooks.dll, tModLoader.dll, and at least one mod DLL.");
            return 1;
        }

        string hooksPath = positionalArgs[0];
        string tmlPath = positionalArgs[1];
        var modPaths = positionalArgs.Skip(2).ToList();

        if (!File.Exists(hooksPath))
        {
            Console.Error.WriteLine($"Error: TerrariaHooks not found at {hooksPath}");
            return 1;
        }

        if (!File.Exists(tmlPath))
        {
            Console.Error.WriteLine($"Error: tModLoader not found at {tmlPath}");
            return 1;
        }

        foreach (var m in modPaths)
        {
            if (!File.Exists(m))
            {
                Console.Error.WriteLine($"Error: Mod assembly not found at {m}");
                return 1;
            }
        }

        Console.WriteLine($"[HookScan] Loading game assemblies:");
        Console.WriteLine($"  TerrariaHooks: {hooksPath}");
        Console.WriteLine($"  tModLoader:    {tmlPath}");
        Console.WriteLine($"[HookScan] Target mod assemblies ({modPaths.Count}):");
        foreach (var m in modPaths)
            Console.WriteLine($"  - {m}");

        var hooksAsm = AssemblyDefinition.ReadAssembly(hooksPath);
        var tmlAsm = AssemblyDefinition.ReadAssembly(tmlPath);

        var resolver = new TargetResolver(hooksAsm, tmlAsm);
        var scanner = new ModScanner(resolver, hooksPath, tmlPath, modPaths);

        Console.WriteLine("[HookScan] Scanning assemblies for hook registrations...");
        var inventory = scanner.Scan();

        Console.WriteLine("[HookScan] Scan complete.");
        Console.WriteLine($"  On_ hooks:        {inventory.OnHooks.Count} distinct targets");
        Console.WriteLine($"  IL hooks:         {inventory.ILHooks.Count} hooks");
        Console.WriteLine($"  Runtime detours:  {inventory.RuntimeDetours.Count} detours");
        Console.WriteLine($"  Unsupported:      {inventory.Unsupported.Count} items");

        // Report overlaps
        var overlaps = inventory.OnHooks.Where(h => h.Registrations.Select(r => r.Mod).Distinct().Count() > 1).ToList();
        if (overlaps.Count > 0)
        {
            Console.WriteLine($"[HookScan] Overlapping hook targets ({overlaps.Count}):");
            foreach (var ov in overlaps)
            {
                var mods = string.Join(", ", ov.Registrations.Select(r => r.Mod).Distinct());
                Console.WriteLine($"  * {ov.Target.FullName} (hooked by {mods})");
            }
        }

        string json = JsonSerializer.Serialize(inventory, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        if (!string.IsNullOrEmpty(outputPath))
        {
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(outputPath, json);
            Console.WriteLine($"[HookScan] Wrote inventory JSON to: {outputPath}");
        }
        else
        {
            Console.WriteLine(json);
        }

        return 0;
    }
}
