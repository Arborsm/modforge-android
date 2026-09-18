using System;
using System.IO;
using System.Text;

namespace SMAPIGameLoader.Launcher;

/// <summary>
///     Tees <see cref="Console" /> output (game, SMAPI, .NET runtime, front-end logs) into
///     logcat AND a size-capped log file, so the in-app log viewer can read what the desktop
///     launcher shows in its native SMAPI console. Install once from the activity OnCreate.
/// </summary>
internal static class LogCapture
{
    const long MaxLogBytes = 1_000_000;
    const string LogTag = "MODFORGE";

    static readonly object FileGate = new();
    static bool _installed;

    public static string LogFilePath { get; } = Path.Combine(FileTool.ExternalFilesDir, "launcher-log.txt");

    public static void Install()
    {
        if (_installed)
            return;

        _installed = true;
        try
        {
            TrimLogFile();
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn(LogTag, $"LogCapture trim failed: {ex}");
        }

        Console.SetOut(new TeeWriter(Console.Out));
        Console.SetError(new TeeWriter(Console.Error));
        Android.Util.Log.Info(LogTag, $"LogCapture installed, log file: {LogFilePath}");
    }

    static void TrimLogFile()
    {
        var info = new FileInfo(LogFilePath);
        if (info.Exists && info.Length > MaxLogBytes)
            File.Delete(LogFilePath);
    }

    /// <summary>Reads the newest <paramref name="maxLines" /> lines plus total count for the viewer.</summary>
    public static (string[] Lines, long TotalLines, bool Truncated) ReadTail(int maxLines)
    {
        try
        {
            lock (FileGate)
            {
                if (File.Exists(LogFilePath) is false)
                    return (Array.Empty<string>(), 0, false);

                var lines = File.ReadAllLines(LogFilePath);
                var start = Math.Max(0, lines.Length - maxLines);
                var page = new string[lines.Length - start];
                Array.Copy(lines, start, page, 0, page.Length);
                return (page, lines.Length, start > 0);
            }
        }
        catch (Exception ex)
        {
            // Log through logcat only: re-entering Console here would recurse into the tee.
            Android.Util.Log.Error(LogTag, $"LogCapture.ReadTail failed: {ex}");
            return (Array.Empty<string>(), 0, false);
        }
    }

    sealed class TeeWriter : TextWriter
    {
        readonly TextWriter _previous;

        public TeeWriter(TextWriter previous)
        {
            _previous = previous;
        }

        public override Encoding Encoding => _previous.Encoding;

        public override void Write(char value)
        {
            _previous.Write(value);
        }

        public override void Write(string? value)
        {
            _previous.Write(value);
            AppendFile(value ?? string.Empty);
        }

        public override void WriteLine(string? value)
        {
            _previous.WriteLine(value);
            AppendFile((value ?? string.Empty) + Environment.NewLine);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _previous.Dispose();
            base.Dispose(disposing);
        }

        void AppendFile(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            try
            {
                lock (FileGate)
                {
                    File.AppendAllText(LogFilePath, text);
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn(LogTag, $"LogCapture append failed: {ex}");
            }
        }
    }
}
