using Android.App;
using Android.Content.PM;
using Android.OS;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SMAPIGameLoader;

internal static class StardewApkTool
{
    public const string GamePlayStorePackageName = "com.chucklefish.stardewvalley";
    public const string GameGalaxyStorePackageName = "com.chucklefish.stardewvalleysamsung";
    static bool IsGameFromPlayStore = false;
    static bool IsGameFromGalaxyStore = false;
    static PackageInfo _currentPackageInfo;

    //init at first SDK
    static StardewApkTool()
    {
        Console.WriteLine("Initialize Stardew Apk Tool");
        var playStore = ApkTool.GetPackageInfo(GamePlayStorePackageName);
        var samsung = ApkTool.GetPackageInfo(GameGalaxyStorePackageName);

        //select samsung first, better for debug, test app
        if (samsung != null)
        {
            _currentPackageInfo = samsung;
            IsGameFromGalaxyStore = true;
            Console.WriteLine("Game Install From Galaxy Store");
        }
        else if (playStore != null)
        {
            _currentPackageInfo = playStore;
            IsGameFromPlayStore = true;
            Console.WriteLine("Game Install From Play Store");
        }
    }

    public static PackageInfo CurrentPackageInfo => _currentPackageInfo;

    public static bool IsInstalled
    {
        get
        {
            if (CurrentPackageInfo == null)
                return false;

            //play store
            if (IsGameFromPlayStore)
            {
                var version = CurrentPackageInfo.VersionName;
                var splitApks = CurrentPackageInfo.ApplicationInfo?.SplitSourceDirs;
                //Merged/standalone installs (e.g. sideloaded single APKs) keep every
                //assembly and asset in the base APK, so there are no split sources.
                return splitApks is null || splitApks.Count == 2;
            }

            //samsung
            return true;
        }
    }

    public static Android.Content.Context GetContext => Application.Context;
    public static string? BaseApkPath => CurrentPackageInfo?.ApplicationInfo?.PublicSourceDir;
    public static string? Arm64ApkPath
    {
        get
        {
            try
            {
                if (CurrentPackageInfo == null)
                    return null;

                //Merged installs carry the assemblies in the base APK.
                var splitApks = CurrentPackageInfo.ApplicationInfo?.SplitSourceDirs;
                if (splitApks is null || splitApks.Count == 0)
                    return BaseApkPath;

                if (IsGameFromPlayStore)
                    return splitApks.FirstOrDefault(path => path.Contains("split_config.arm64"));

                // Samsung: assemblies are in the base APK
                return BaseApkPath;
            }
            catch (Exception ex)
            {
                ErrorDialogTool.Show(ex, "Error try to get Arm64ApkPath");
                return null;
            }
        }
    }

    public static string? ContentApkPath
    {
        get
        {
            try
            {
                if (CurrentPackageInfo == null)
                    return null;

                //Merged installs carry the content in the base APK.
                var splitApks = CurrentPackageInfo.ApplicationInfo?.SplitSourceDirs;
                if (splitApks is null || splitApks.Count == 0)
                    return BaseApkPath;

                //play store
                if (IsGameFromPlayStore)
                    return splitApks.First(path => path.Contains("split_content"));

                //samsung
                return BaseApkPath;
            }
            catch (Exception ex)
            {
                ErrorDialogTool.Show(ex, "Error try to get ContentApkPath");
                return null;
            }
        }
    }

    public static Version GameVersionSupport
    {
        get
        {
            if (CurrentPackageInfo == null)
                return null;

            switch (CurrentPackageInfo.PackageName)
            {
                case GamePlayStorePackageName:
                    return new(1, 6, 15, 0);
                case GameGalaxyStorePackageName:
                    return new(1, 6, 15, 3);
                default:
                    return null;
            }
        }
    }
    public static Version CurrentGameVersion
    {
        get
        {
            try
            {
                return new Version(CurrentPackageInfo?.VersionName);
            }
            catch (Exception ex)
            {
                return new Version(0, 0, 0, 0);
            }
        }
    }
    public static bool IsGameVersionSupport => CurrentGameVersion >= GameVersionSupport;
}
