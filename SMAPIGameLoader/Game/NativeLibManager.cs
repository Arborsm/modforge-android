using LWJGL;
using MonoGame.Framework.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Game;

internal static class NativeLibManager
{
    static nint LoadGameLib(string fileName)
    {
        nint num = FuncLoader.LoadLibrary(fileName);
        if (num == IntPtr.Zero)
        {
            var libPath = Path.Combine(GameAssemblyManager.GameNativeLibDir, fileName);
            if (File.Exists(libPath))
                num = FuncLoader.LoadLibrary(libPath);
        }
        return num;
    }

    public static void Loads()
    {
        try
        {
            //Native bindings shipped inside the game APK are invisible to this
            //process on extractNativeLibs=false installs. Preload the copies
            //VerifyLibs unpacked — once a library is loaded, every later
            //bare-name dlopen (LZ4 probe, MonoGame audio/PNG init) resolves
            //against it instead of dying with DllNotFound/NullReference.
            var lz4 = LoadGameLib("liblwjgl_lz4.so");
            if (lz4 == IntPtr.Zero)
                Console.WriteLine("warn: liblwjgl_lz4 preload returned null; probing anyway");
            int b = LZ4.CompressBound(10);
            LoadGameLib("libopenal32.so");
            LoadGameLib("libstb_png_writer.so");
            Console.WriteLine("done setup native libs");
        }
        catch (Exception ex)
        {
            //Non-fatal sanity probe: on x86_64 hosts (emulator) the game's arm64
            //native libs can never load, and the game still boots — a scary
            //error dialog here only reads as a crash. Log and move on.
            Console.WriteLine("native lib probe failed (non-fatal): " + ex.Message);
        }
    }
}
