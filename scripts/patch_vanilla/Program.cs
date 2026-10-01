using System.Text.Json;
using Mono.Cecil;
using static Common;

sealed record AsmInfo(string Path, string Sha256, string Mvid);

static class Program
{
    const string CleanTerrariaPrefix="6bba49a535bc9dc2"; const string CleanFnaPrefix="46d201f59ce6c382"; const string CleanReLogicPrefix="2e7750fe79ba48bc";
    static int Main(string[] args)
    {
        try { Require(args.Length==4, "usage: PatchVanilla <clean-game-dir> <output-dir> <NxCrypto.dll> <NxInputDiag.dll>"); Run(Path.GetFullPath(args[0]),Path.GetFullPath(args[1]),Path.GetFullPath(args[2]),Path.GetFullPath(args[3])); return 0; }
        catch(Exception e){ Console.Error.WriteLine("PatchVanilla: "+e); return 1; }
    }
    static void Run(string game, string output, string nxCryptoPath, string nxInputPath)
    {
        Directory.CreateDirectory(output);
        var terrariaPath=Path.Combine(game,"Terraria.exe"); var fnaPath=Path.Combine(game,"FNA.dll");
        Require(File.Exists(terrariaPath)&&File.Exists(fnaPath), "clean GOG directory must contain Terraria.exe and FNA.dll");
        var terrariaBytes=File.ReadAllBytes(terrariaPath); var fnaBytes=File.ReadAllBytes(fnaPath);
        Require(Sha(terrariaBytes).StartsWith(CleanTerrariaPrefix), "Terraria.exe is not clean GOG 1.4.5.8"); Require(Sha(fnaBytes).StartsWith(CleanFnaPrefix), "FNA.dll is not clean GOG 1.4.5.8");
        using var resolver = new DefaultAssemblyResolver(); resolver.AddSearchDirectory(game); resolver.AddSearchDirectory(output); resolver.AddSearchDirectory(Path.GetDirectoryName(nxCryptoPath)!); resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        var rp = new ReaderParameters{ReadSymbols=false, AssemblyResolver=resolver};
        using var gameAsm = AssemblyDefinition.ReadAssembly(new MemoryStream(terrariaBytes), rp);
        var inputTerrariaMvid = gameAsm.MainModule.Mvid.ToString();
        foreach (var er in gameAsm.MainModule.Resources.OfType<EmbeddedResource>()) { var data=er.GetResourceData(); if (data.Length>2 && data[0]==0x4d && data[1]==0x5a) { try { using var ea=AssemblyDefinition.ReadAssembly(new MemoryStream(data)); File.WriteAllBytes(Path.Combine(output, ea.Name.Name+".dll"), data); } catch { } } }
        resolver.AddSearchDirectory(output);
        var relogicResource = gameAsm.MainModule.Resources.OfType<EmbeddedResource>().Single(r=>r.Name=="Terraria.Libraries.ReLogic.ReLogic.dll");
        var relogicBytes = relogicResource.GetResourceData(); Require(Sha(relogicBytes).StartsWith(CleanReLogicPrefix), "embedded ReLogic.dll is not clean 1.4.5.8");
        File.WriteAllBytes(Path.Combine(output,"ReLogic.clean.dll"), relogicBytes);
        using var relogicAsm = AssemblyDefinition.ReadAssembly(new MemoryStream(relogicBytes), rp);
        using var fnaAsm = AssemblyDefinition.ReadAssembly(new MemoryStream(fnaBytes), rp);
        using var nxCrypto = AssemblyDefinition.ReadAssembly(nxCryptoPath, rp); using var nxInput = AssemblyDefinition.ReadAssembly(nxInputPath, rp);
        TerrariaPatches.Apply(gameAsm, nxCrypto); HintPatch.Apply(gameAsm.MainModule, relogicAsm.MainModule, fnaAsm.MainModule); FnaPatches.Apply(fnaAsm, nxInput);
        gameAsm.MainModule.Mvid = Guid.Parse("241a054a-e6db-ec4f-0c1d-e9bfe13a59cd"); relogicAsm.MainModule.Mvid = Guid.Parse("77a15d83-545c-7620-2bb7-033335c7a666");
        var outTerraria=Path.Combine(output,"Terraria.exe"); var outRe=Path.Combine(output,"ReLogic.dll"); var outFna=Path.Combine(output,"FNA.dll");
        gameAsm.Write(outTerraria, new WriterParameters{Timestamp=0}); relogicAsm.Write(outRe, new WriterParameters{Timestamp=0}); fnaAsm.Write(outFna, new WriterParameters{Timestamp=0});
        File.Copy(nxCryptoPath,Path.Combine(output,"NxCrypto.dll"),true); File.Copy(nxInputPath,Path.Combine(output,"NxInputDiag.dll"),true); File.Delete(Path.Combine(output,"ReLogic.clean.dll")); foreach (var extra in Directory.EnumerateFiles(output,"*.dll").Where(p=>!new[]{"ReLogic.dll","FNA.dll","NxCrypto.dll","NxInputDiag.dll"}.Contains(Path.GetFileName(p)))) File.Delete(extra);
        var receipt = new { input = new { terraria=new AsmInfo(terrariaPath,Sha(terrariaBytes),inputTerrariaMvid), fna=new AsmInfo(fnaPath,Sha(fnaBytes),fnaAsm.MainModule.Mvid.ToString()), relogicEmbeddedSha256=Sha(relogicBytes) }, output = Directory.EnumerateFiles(output,"*.dll").Concat(new[]{outTerraria}).Distinct().OrderBy(x=>Path.GetFileName(x)).Select(Info).ToArray(), notes="Generated from clean GOG 1.4.5.8 by content-addressed Cecil patches; Terraria/ReLogic MVIDs set to known shipping values, FNA MVID preserved." };
        File.WriteAllText(Path.Combine(output,"patch_vanilla_receipt.json"), JsonSerializer.Serialize(receipt,new JsonSerializerOptions{WriteIndented=true})+"\n");
        Console.WriteLine("patched vanilla assemblies -> "+output);
    }
    static AsmInfo Info(string p){ using var a=AssemblyDefinition.ReadAssembly(p); return new AsmInfo(Path.GetFileName(p),Sha(File.ReadAllBytes(p)),a.MainModule.Mvid.ToString()); }
}
