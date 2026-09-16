using Android.App;
using SMAPIGameLoader.Launcher;
using System;
using System.IO;

namespace SMAPIGameLoader.Tool;

/// <summary>
///     Guards and helpers for reading files the WebView front-end references by absolute path.
///     Everything the bridge serves must stay inside the app-specific external files dir
///     (<c>Android/data/com.modforge.android/files</c>), so a crafted URL cannot read
///     arbitrary device storage.
/// </summary>
internal static class SandboxFileTool
{
    public static string ExternalFilesDir
    {
        get
        {
            var dir = Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
            if (string.IsNullOrEmpty(dir))
                throw new InvalidOperationException("External files dir is unavailable");
            return dir!;
        }
    }

    /// <summary>Directory SAF-picked files are copied into before their path is returned to the front-end.</summary>
    public static string PickedFilesDir => Path.Combine(ExternalFilesDir, "Picked");

    public static bool IsInsideAppSandbox(Activity activity, string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        var root = activity.GetExternalFilesDir(null)?.AbsolutePath;
        if (string.IsNullOrEmpty(root))
            return false;

        try
        {
            var fullRoot = Path.GetFullPath(root!);
            var fullPath = Path.GetFullPath(filePath);
            if (fullPath == fullRoot)
                return true;

            return fullPath.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///     Copies a SAF document into <see cref="PickedFilesDir"/> so downstream commands can read
    ///     it as a plain filesystem path. Returns the copied file's absolute path.
    /// </summary>
    public static string CopyPickedFileToSandbox(global::Android.Net.Uri uri, string? displayName)
    {
        var pickedDir = PickedFilesDir;
        Directory.CreateDirectory(pickedDir);

        var safeName = SanitizeFileName(string.IsNullOrEmpty(displayName) ? "picked.bin" : displayName);
        var destinationPath = Path.Combine(pickedDir, safeName);

        //never overwrite an earlier pick with the same name
        var counter = 1;
        while (File.Exists(destinationPath))
        {
            var baseName = Path.GetFileNameWithoutExtension(safeName);
            var extension = Path.GetExtension(safeName);
            destinationPath = Path.Combine(pickedDir, $"{baseName} ({counter}){extension}");
            counter += 1;
        }

        using var source = Application.Context.ContentResolver?.OpenInputStream(uri)
            ?? throw new InvalidOperationException("Cannot open picked document stream");
        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write);
        source.CopyTo(destination);
        return destinationPath;
    }

    /// <summary>Strips path separators and control characters from a SAF display name.</summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (Array.IndexOf(invalid, character) < 0 && !char.IsControl(character))
                builder.Append(character);
        }

        var result = builder.ToString().Trim();
        return result.Length > 0 ? result : "picked.bin";
    }
}
