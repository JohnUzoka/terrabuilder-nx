using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;

if (args.Length != 6)
{
    Console.Error.WriteLine("Usage: PrepareAot <staged-game-directory> <dependency-directory> <manifest.json> <corelib.dll> <sdk-runtime-directory> <runtime-metadata-directory>");
    return 2;
}

try
{
    Prepare(args.Select(Path.GetFullPath).ToArray());
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("PrepareAot: " + error.Message);
    return 1;
}

static void Prepare(string[] paths)
{
    var (game, dependencies, manifest, corelib) = (paths[0], paths[1], paths[2], paths[3]);
    var (runtime, runtimeMetadata) = (paths[4], paths[5]);
    Require(runtimeMetadata != dependencies, "runtime metadata must be separate from compile-time dependencies");
    foreach (var output in new[] { dependencies, manifest, runtimeMetadata })
        foreach (var input in new[] { game, runtime, Path.GetDirectoryName(corelib)! })
            Require(!Within(output, input), "outputs must be outside staged RomFS and SDK inputs");
    Require(Directory.Exists(game), "missing staged game directory: " + game);
    var known = new Dictionary<string, AssemblyInfo>(StringComparer.OrdinalIgnoreCase);
    var staged = new List<AssemblyInfo>();
    foreach (var path in Directory.EnumerateFiles(game).OrderBy(p => p, StringComparer.Ordinal))
    {
        if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            continue;
        var info = Inspect(File.ReadAllBytes(path), path, null);
        Register(known, info);
        staged.Add(info);
    }
    foreach (var filename in new[] { "Terraria.exe", "FNA.dll", "NxCrypto.dll", "NxInputDiag.dll" })
        Require(staged.Any(a => a.Path == Path.Combine(game, filename)), "missing staged assembly: " + filename);

    var core = Inspect(File.ReadAllBytes(corelib), corelib, null);
    Require(core.Name == "System.Private.CoreLib", "corelib is not System.Private.CoreLib");
    Register(known, core);
    var runtimeAssemblies = new List<AssemblyInfo>();
    var runtimeNames = new Dictionary<string, AssemblyInfo>(StringComparer.OrdinalIgnoreCase);
    foreach (var path in Directory.EnumerateFiles(runtime, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
    {
        var info = Inspect(File.ReadAllBytes(path), path, null);
        Register(runtimeNames, info);
        runtimeAssemblies.Add(info);
    }
    var terrariaPath = Path.Combine(game, "Terraria.exe");
    using var terraria = AssemblyDefinition.ReadAssembly(terrariaPath, new ReaderParameters { ReadSymbols = false });
    var embedded = new List<(AssemblyInfo Info, byte[] Bytes)>();
    // Runtime facades may intentionally share names with old embedded framework DLLs.
    // Only collisions inside one resolution directory would overwrite an assembly.
    var embeddedNames = new Dictionary<string, AssemblyInfo>(StringComparer.OrdinalIgnoreCase);
    var skipped = new List<string>();
    foreach (var resource in terraria.MainModule.Resources.OfType<EmbeddedResource>().OrderBy(r => r.Name, StringComparer.Ordinal))
    {
        var bytes = resource.GetResourceData();
        if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z')
        {
            if (resource.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("embedded DLL is not a raw PE image: " + resource.Name);
            continue;
        }
        AssemblyInfo info;
        try
        {
            info = Inspect(bytes, "", resource.Name);
        }
        catch (BadImageFormatException)
        {
            skipped.Add(resource.Name);
            continue;
        }
        Require(info.Name.Length > 0 && info.Name != "." && info.Name != ".." &&
            info.Name.IndexOfAny(new[] { '/', '\\', ':' }) < 0, "unsafe assembly name: " + info.Name);
        info = info with { Path = Path.Combine(dependencies, info.Name + ".dll") };
        Register(embeddedNames, info);
        embedded.Add((info, bytes));
    }
    Directory.CreateDirectory(dependencies);
    foreach (var (info, bytes) in embedded)
    {
        // Write the resource bytes, never AssemblyDefinition.Write: MVID and signatures stay intact.
        if (!File.Exists(info.Path))
            File.WriteAllBytes(info.Path, bytes);
        var savedBytes = File.ReadAllBytes(info.Path);
        Require(savedBytes.AsSpan().SequenceEqual(bytes), "resource bytes changed: " + info.Resource);
        var saved = Inspect(savedBytes, info.Path, info.Resource);
        Require(saved.Mvid == info.Mvid && saved.Sha256 == info.Sha256, "resource identity changed: " + info.Resource);
    }
    var expectedPaths = embedded.Select(e => e.Info.Path).ToHashSet(StringComparer.Ordinal);
    Require(Directory.EnumerateFileSystemEntries(dependencies).All(expectedPaths.Contains),
        "unexpected file in dependency directory; use a clean output directory: " + dependencies);
    // AOT loads its image table before Terraria installs its embedded resolver.
    // Preserve framework overlays by copying only otherwise unavailable assemblies.
    var runtimeMetadataAssemblies = new List<AssemblyInfo>();
    Directory.CreateDirectory(runtimeMetadata);
    foreach (var (info, bytes) in embedded)
    {
        if (known.ContainsKey(info.Name) || runtimeNames.ContainsKey(info.Name))
            continue;
        var target = info with { Path = Path.Combine(runtimeMetadata, info.Name + ".dll") };
        if (!File.Exists(target.Path))
            File.WriteAllBytes(target.Path, bytes);
        Require(File.ReadAllBytes(target.Path).AsSpan().SequenceEqual(bytes),
            "runtime metadata bytes conflict: " + target.Path);
        runtimeMetadataAssemblies.Add(target);
    }
    var expectedRuntimePaths = runtimeMetadataAssemblies.Select(a => a.Path).ToHashSet(StringComparer.Ordinal);
    Require(Directory.EnumerateFileSystemEntries(runtimeMetadata).All(expectedRuntimePaths.Contains),
        "unexpected file in runtime metadata directory: " + runtimeMetadata);
    foreach (var info in runtimeAssemblies)
        Require(Hash(File.ReadAllBytes(info.Path)) == info.Sha256, "SDK input changed during extraction: " + info.Path);
    foreach (var info in staged.Append(core))
        Require(Hash(File.ReadAllBytes(info.Path)) == info.Sha256, "input changed during extraction: " + info.Path);
    Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
    var json = JsonSerializer.Serialize(new
    {
        schema_version = 2,
        game_directory = game,
        dependency_directory = dependencies,
        staged_assemblies = staged,
        runtime_directory = runtime,
        runtime_assemblies = runtimeAssemblies,
        runtime_metadata_directory = runtimeMetadata,
        runtime_metadata_assemblies = runtimeMetadataAssemblies,
        runtime_metadata_excluded = embedded.Select(e => e.Info).Where(e =>
            known.ContainsKey(e.Name) || runtimeNames.ContainsKey(e.Name)).Select(e => new
            {
                embedded = e,
                provider = known.TryGetValue(e.Name, out var existing) ? existing : runtimeNames[e.Name]
            }),
        embedded_assemblies = embedded.Select(e => e.Info),
        staged_embedded_overlays = embedded.Select(e => e.Info).Where(e =>
            known.TryGetValue(e.Name, out var s) && s.Sha256 != e.Sha256).Select(e => e.Name),
        skipped_native_resources = skipped,
        corelib = core,
        resource_bytes_verified = true
    }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
    File.WriteAllText(manifest, json + "\n");
    Console.WriteLine($"Prepared {embedded.Count} byte-identical embedded managed resources; {staged.Count} staged assemblies unchanged");
    Console.WriteLine($"Prepared {runtimeMetadataAssemblies.Count} unshadowed runtime metadata assemblies in {runtimeMetadata}");
    Console.WriteLine("Manifest: " + manifest);
}

static AssemblyInfo Inspect(byte[] bytes, string path, string? resource)
{
    using var stream = new MemoryStream(bytes, writable: false);
    using var assembly = AssemblyDefinition.ReadAssembly(stream, new ReaderParameters { ReadSymbols = false });
    return new(assembly.Name.Name, assembly.Name.FullName, assembly.MainModule.Mvid.ToString(), Hash(bytes), bytes.Length, path, resource);
}

static void Register(Dictionary<string, AssemblyInfo> known, AssemblyInfo info)
{
    if (known.TryGetValue(info.Name, out var previous))
        Require(previous.Sha256 == info.Sha256, $"conflicting assembly {info.Name}: {previous.Resource ?? previous.Path} vs {info.Resource ?? info.Path}");
    else
        known.Add(info.Name, info);
}

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static bool Within(string path, string directory) => path == directory || path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal);
static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidDataException(message);
}

record AssemblyInfo(string Name, string FullName, string Mvid, string Sha256, long Size, string Path, string? Resource);
