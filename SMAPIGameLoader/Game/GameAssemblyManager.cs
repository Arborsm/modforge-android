using Android.App;
using MonoGame.Framework.Utilities;
using SMAPIGameLoader.Game;
using SMAPIGameLoader.Launcher;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Xamarin.Android.AssemblyStore;

namespace SMAPIGameLoader;

internal class GameAssemblyManager
{
    public const string AssembliesDirName = "Stardew Assemblies";
    public static string AssembliesDirPath => Path.Combine(FileTool.ExternalFilesDir, AssembliesDirName);
    public const string StardewDllName = "StardewValley.dll";
    public const string MonoGameDLLFileName = "MonoGame.Framework.dll";
    public static string StardewValleyFilePath => Path.Combine(AssembliesDirPath, StardewDllName);
    public static void VerifyAssemblies()
    {
        Console.WriteLine("Verify Assemblies");
        var assembliesOutputDirPath = AssembliesDirPath;
        Directory.CreateDirectory(assembliesOutputDirPath);

        // Loader store first: any assembly present in both stores (MonoGame.Framework,
        // Newtonsoft.Json, ...) must end up as the game's copy — the game is the side
        // whose API shape the runtime code is built against, so it always wins last.
        {
            Console.WriteLine("try clone SMAPI Game Loader Assemblies");
            //clone dll & no trimming from this app 
            var appInfo = Application.Context.ApplicationInfo;
            var store = new AssemblyStoreExplorer(appInfo.PublicSourceDir, keepStoreInMemory: true);
            foreach (var asm in store.Assemblies)
            {
                asm.ExtractImage(assembliesOutputDirPath);
            }
            Console.WriteLine("done clone SMAPI Game Loader Assemblies");
        }

        {
            Console.WriteLine("try clone stardew assemblies");
            //clone dlls Stardew Valley - try base APK first, then arm64 split APK for .NET 9+
            var store = new AssemblyStoreExplorer(StardewApkTool.BaseApkPath, keepStoreInMemory: true);
            if (store.Assemblies.Count == 0 && StardewApkTool.Arm64ApkPath != null)
            {
                Console.WriteLine("No assemblies in base APK, trying arm64 split APK");
                store = new AssemblyStoreExplorer(StardewApkTool.Arm64ApkPath, keepStoreInMemory: true);
            }
            foreach (var asm in store.Assemblies)
            {
                // The game store's bundled BCL is trimmed to the game's own usage:
                // its System.* facades forward only a few hundred types (the full
                // runtime facade forwards ~900), so overwriting the loader store's
                // untrimmed BCL with them breaks every member SMAPI's mod loader
                // resolves through those facades (Cecil follows the reference
                // scope "System.Runtime" into a facade that forwards nothing and
                // reports core APIs like IList.Contains as missing). BCL always
                // comes from the loader store; the game store only wins the
                // game-domain assemblies the runtime API is built against
                // (StardewValley, MonoGame, bundled third-party).
                if (IsFrameworkAssemblyName(asm.Name.Length > 0 ? asm.Name : asm.DllName))
                    continue;
                asm.ExtractImage(assembliesOutputDirPath);
            }
            Console.WriteLine("done clone stardew assemblies");
        }
    }

    /// <summary>True for BCL/framework assemblies (System.*, Microsoft.*, mscorlib, netstandard)
    /// whose full untrimmed copies must come from the loader store, never the game's trimmed ones.</summary>
    static bool IsFrameworkAssemblyName(string name)
    {
        var shortName = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
        return shortName.StartsWith("System.", StringComparison.Ordinal)
            || shortName.StartsWith("Microsoft.", StringComparison.Ordinal)
            || shortName is "mscorlib" or "netstandard" or "WindowsBase";
    }
    public static Assembly LoadAssembly(string dllFileName)
    {
        return Assembly.LoadFrom(Path.Combine(AssembliesDirPath, dllFileName));
    }

    static string LibDirPath => Path.Combine(FileTool.ExternalFilesDir, "lib");

    /// <summary>App-private dir the game's own native bindings are unpacked into
    /// (internal storage: external/sdcard paths are noexec for dlopen).</summary>
    internal static string GameNativeLibDir =>
        Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.Personal))!, "lib");

    static readonly string[] GameNativeLibs = { "liblwjgl_lz4.so", "libopenal32.so", "libstb_png_writer.so" };

    internal static void VerifyLibs()
    {
        Console.WriteLine("try setup libs");
        //Play Store split installs ship extractNativeLibs=false, so the game's own
        //native bindings stay inside the APK and are invisible to this process.
        //Unpack them into internal storage so MonoGame's LZ4/OpenAL/stb bindings
        //can dlopen them — otherwise every boot logs LZ4 NullReferenceException
        //and audio never initializes (OpenALSoundController DllNotFoundException).
        Directory.CreateDirectory(GameNativeLibDir);
        var apkPath = StardewApkTool.Arm64ApkPath ?? StardewApkTool.BaseApkPath;
        using var archive = new ZipArchive(File.OpenRead(apkPath));
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("lib/arm64-v8a/", StringComparison.Ordinal))
                continue;
            if (Array.IndexOf(GameNativeLibs, entry.Name) < 0)
                continue;
            entry.ExtractToFile(Path.Combine(GameNativeLibDir, entry.Name), overwrite: true);
        }
        Console.WriteLine("done setup libs");
    }
}
