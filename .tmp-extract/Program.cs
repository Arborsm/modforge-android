using System.IO.Compression;
using Xamarin.Android.AssemblyStore;

var apkPath = @"D:\BaiduNetdiskDownload\星露谷物语安卓手机版1.6.15.apk";
var outputDir = @"E:\Arbor\SMAPI-Android-1.6\src\DependenciesDll";

var wanted = new[]
{
    "StardewValley",
    "StardewValley.GameData",
    "MonoGame.Framework",
};

Directory.CreateDirectory(outputDir);
using var archive = ZipFile.OpenRead(apkPath);
var explorer = new AssemblyStoreExplorer(archive, "assemblies", null, keepStoreInMemory: true);
Console.WriteLine($"assemblies: {explorer.Assemblies.Count}, named: {explorer.AssembliesByName.Count}");

foreach (var name in wanted)
{
    if (!explorer.AssembliesByName.TryGetValue(name, out var assembly))
    {
        Console.WriteLine($"MISSING: {name}");
        continue;
    }

    assembly.ExtractImage(outputDir);
    Console.WriteLine($"{name} -> {Path.Combine(outputDir, assembly.DllName)} ({new FileInfo(Path.Combine(outputDir, assembly.DllName)).Length} bytes)");
}
