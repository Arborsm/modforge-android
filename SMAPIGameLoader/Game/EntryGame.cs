using Android.App;
using Android.Content;
using SMAPIGameLoader.Launcher;
using System;

namespace SMAPIGameLoader;
internal static class EntryGame
{
    public static void LaunchGameActivity(Activity launcherActivity)
    {
        TaskTool.Run(launcherActivity, async () =>
        {
            LaunchGameActivityInternal(launcherActivity);
        });
    }

    static void LaunchGameActivityInternal(Activity launcherActivity)
    {
        //ToastNotifyTool.Notify("Starting Game..");
        //check game it's can launch with version

        try
        {
            if (StardewApkTool.IsGameVersionSupport == false)
            {
                ToastNotifyTool.Notify("Not support game version: " + StardewApkTool.CurrentGameVersion + ", please update game");
                return;
            }

            if (SMAPIInstaller.IsInstalled is false)
            {
                ToastNotifyTool.Notify("Please install SMAPI!!");
                return;
            }

            GameCloner.Setup();

#if DEBUG
            ToastNotifyTool.Notify("Error can't start game on Debug Mode");
            return;
#endif

            StartSMAPIActivity(launcherActivity);
        }
        catch (Exception ex)
        {
            // The toast alone is invisible in logs; mirror failures to logcat so
            // launch aborts are diagnosable without a debugger attached.
            Console.WriteLine("Error:LaunchGameActivity: " + ex);
            ToastNotifyTool.Notify("Error:LaunchGameActivity: " + ex.ToString());
        }
    }
    //prevent Load Game Assembly in scope function LaunchGameActivityInternal()
    static void StartSMAPIActivity(Activity launcherActivity)
    {
        var intent = new Intent(launcherActivity, typeof(SMAPIActivity));
        intent.AddFlags(ActivityFlags.NewTask);
        launcherActivity.StartActivity(intent);
        //The launcher activity stays in its own task: when the game activity
        //exits or fails to boot, the user falls back to the launcher instead
        //of being dropped on the home screen (which reads as an instant crash).
    }
}
