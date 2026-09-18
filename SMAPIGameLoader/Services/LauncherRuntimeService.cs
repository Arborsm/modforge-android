using Android.App;
using Android.Content;
using Android.Net;
using LauncherActivity = SMAPIGameLoader.Launcher.LauncherActivity;
using SMAPIGameLoader.Launcher;
using SMAPIGameLoader.Tool;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Services;

/// <summary>
///     Launcher runtime commands: settings persistence, game launch, runtime info and external
///     URL opening. The Android launch path reuses the upstream game-host chain
///     (EntryGame → SMAPIActivity) and validates the version gate before starting.
/// </summary>
public sealed class LauncherRuntimeService
{
    static string DataDirectory => Path.Combine(FileTool.ExternalFilesDir, "ModForge", "launcher");
    static string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");

    // --- commands ---

    public Task<JsonElement?> LoadLauncherSettingsAsync()
    {
        return Task.Run<JsonElement?>(() =>
        {
            var settings = LoadOrCreateSettings();
            return JsonSerializer.SerializeToElement(settings, LauncherJsonContext.Default.LauncherSettings);
        });
    }

    /// <summary>Returns the tail of the captured console log for the in-app log viewer.</summary>
    public Task<JsonElement?> ReadLauncherLogAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(() =>
        {
            var maxLines = 400;
            if (request.ValueKind == JsonValueKind.Object
                && request.TryGetProperty("maxLines", out var maxLinesElement)
                && maxLinesElement.TryGetInt32(out var parsedMaxLines))
            {
                maxLines = Math.Clamp(parsedMaxLines, 1, 20_000);
            }

            var (lines, totalLines, truncated) = LogCapture.ReadTail(maxLines);
            var payload = new System.Text.Json.Nodes.JsonObject
            {
                ["lines"] = new System.Text.Json.Nodes.JsonArray(
                    System.Linq.Enumerable.Select(lines, line => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(line)).ToArray()),
                ["totalLines"] = totalLines,
                ["truncated"] = truncated,
            };
            return JsonSerializer.SerializeToElement(payload);
        });
    }

    public Task<JsonElement?> SaveLauncherSettingsAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(() =>
        {
            var parsed = LibraryService.Deserialize(request, LauncherJsonContext.Default.SaveLauncherSettingsRequest);
            var settings = SaveSettings(parsed);
            return JsonSerializer.SerializeToElement(settings, LauncherJsonContext.Default.LauncherSettings);
        });
    }

    public Task<JsonElement?> LaunchLauncherGameAsync()
    {
        return Task.Run<JsonElement?>(() =>
        {
            var activity = LauncherActivity.Instance
                ?? throw new LauncherCommandException("unavailable", "The launcher activity is not running.");

            if (StardewApkTool.IsInstalled == false)
                throw new LauncherCommandException("game_missing", "Stardew Valley is not installed. Install the game from Play Store or Galaxy Store first.");

            LauncherRequirements.PrefetchFromRemote();

            if (StardewApkTool.IsGameVersionSupport == false || !IsGameVersionAboveGate())
            {
                throw new LauncherCommandException(
                    "game_version",
                    $"Game version {StardewApkTool.CurrentGameVersion} is below the supported minimum {LauncherRequirements.MinimumGameVersion}. Please update the game.");
            }

            if (SMAPIInstaller.IsInstalled == false)
                throw new LauncherCommandException("smapi_missing", "SMAPI is not installed yet. Install SMAPI before launching the game.");

            EntryGame.LaunchGameActivity(activity);

            var result = new LauncherGameLaunchResult
            {
                //Android has no executable path; the game package's base APK identifies what was launched.
                ExecutablePath = StardewApkTool.BaseApkPath ?? string.Empty,
                Target = "smapi",
            };
            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.LauncherGameLaunchResult);
        });
    }

    public Task<JsonElement?> LoadLauncherRuntimeInfoAsync()
    {
        return Task.Run<JsonElement?>(() =>
        {
            string? gameVersion = null;
            string? smapiVersion = null;
            try
            {
                gameVersion = StardewApkTool.CurrentGameVersion?.ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine("RuntimeService: game version detection failed: " + ex.Message);
            }

            try
            {
                smapiVersion = SMAPIInstaller.GetCurrentVersion()?.ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine("RuntimeService: SMAPI version detection failed: " + ex.Message);
            }

            var result = new LauncherRuntimeInfo { GameVersion = gameVersion, SmapiVersion = smapiVersion };
            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.LauncherRuntimeInfo);
        });
    }

    public Task<JsonElement?> OpenLauncherUrlAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(() =>
        {
            var parsed = LibraryService.Deserialize(request, LauncherJsonContext.Default.OpenLauncherUrlRequest);
            var url = parsed.Url.Trim();
            if (System.Uri.TryCreate(url, System.UriKind.Absolute, out var parsedUri)
                && (parsedUri.Scheme == "http" || parsedUri.Scheme == "https"))
            {
                var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(url));
                intent.AddFlags(ActivityFlags.NewTask);
                Application.Context.StartActivity(intent);
                return (JsonElement?)null;
            }

            throw new LauncherCommandException("invalid_args", "open_launcher_url requires an absolute http(s) URL.");
        });
    }

    // --- settings persistence (settings.rs semantics on the Android sandbox) ---

    /// <summary>Compares the installed game version against the remote-aware launch gate.</summary>
    static bool IsGameVersionAboveGate()
    {
        try
        {
            var currentVersion = StardewApkTool.CurrentGameVersion?.ToString();
            return Version.TryParse(currentVersion, out var parsed) && parsed >= LauncherRequirements.MinimumGameVersion;
        }
        catch (Exception)
        {
            //Unreadable game versions fall back to the upstream hard gate already checked above.
            return true;
        }
    }

    internal static LauncherSettings LoadOrCreateSettings()
    {
        if (!File.Exists(SettingsFilePath))
        {
            var defaults = NormalizeSettings(new LauncherSettings());
            WriteSettings(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            var settings = JsonSerializer.Deserialize(json, LauncherJsonContext.Default.LauncherSettings) ?? new LauncherSettings();
            return NormalizeSettings(settings);
        }
        catch (Exception ex)
        {
            throw new LauncherCommandException("invalid_json", $"Launcher settings {SettingsFilePath} is invalid JSON: {ex.Message}");
        }
    }

    internal static LauncherSettings SaveSettings(SaveLauncherSettingsRequest request)
    {
        var settings = LoadOrCreateSettings();

        var gamePath = request.GamePath?.Trim();
        if (!string.IsNullOrEmpty(gamePath))
            settings.GamePath = gamePath;

        var modsPath = request.ModsPath?.Trim();
        if (!string.IsNullOrEmpty(modsPath))
            settings.ModsPath = modsPath;

        var downloadPath = request.DownloadPath?.Trim();
        if (!string.IsNullOrEmpty(downloadPath))
            settings.DownloadPath = downloadPath;

        //Tri-state API key: absent keeps, JSON null clears, a string sets.
        if (request.NexusApiKey is { } apiKeyElement)
        {
            if (apiKeyElement.ValueKind == JsonValueKind.Null)
            {
                settings.NexusApiKey = null;
            }
            else if (apiKeyElement.ValueKind == JsonValueKind.String)
            {
                var apiKey = apiKeyElement.GetString()?.Trim();
                settings.NexusApiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey;
            }
        }

        settings.AutoInstallDownloads = request.AutoInstallDownloads ?? settings.AutoInstallDownloads;
        settings.KeepDownloadedArchives = request.KeepDownloadedArchives ?? settings.KeepDownloadedArchives;
        settings.AutoCheckModUpdates = request.AutoCheckModUpdates ?? settings.AutoCheckModUpdates;
        settings.GmcmParsingEnabled = request.GmcmParsingEnabled ?? settings.GmcmParsingEnabled;
        settings.ShowConsoleWindow = request.ShowConsoleWindow ?? settings.ShowConsoleWindow;

        settings = NormalizeSettings(settings);
        WriteSettings(settings);
        return settings;
    }

    /// <summary>Persists an authorized Nexus API key (SSO completion path).</summary>
    public static void SetNexusApiKey(string apiKey)
    {
        var settings = LoadOrCreateSettings();
        settings.NexusApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        WriteSettings(settings);
    }

    /// <summary>Persists the debug force-offline override in the settings file (app_ui launcher slice).</summary>
    public static void SetNexusForceOfflinePreference(bool forceOffline)
    {
        var settings = LoadOrCreateSettings();
        settings.ForceOffline = forceOffline;
        WriteSettings(settings);
    }

    static LauncherSettings NormalizeSettings(LauncherSettings settings)
    {
        settings.GamePath = NormalizeOptionalPath(settings.GamePath);
        settings.ModsPath = NormalizeOptionalPath(settings.ModsPath);
        settings.NexusApiKey = string.IsNullOrWhiteSpace(settings.NexusApiKey) ? null : settings.NexusApiKey!.Trim();

        //Android defaults: the sandbox Mods dir and the SAF import folder.
        if (settings.ModsPath is null)
            settings.ModsPath = ModTool.ModsDir;
        if (settings.DownloadPath is null)
            settings.DownloadPath = SandboxFileTool.PickedFilesDir;

        settings.DownloadPath = NormalizeOptionalPath(settings.DownloadPath);
        return settings;
    }

    static string? NormalizeOptionalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().Trim('"');
    }

    static void WriteSettings(LauncherSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(settings, LauncherJsonContext.Default.LauncherSettings) + "\n");
        }
        catch (Exception ex)
        {
            throw new LauncherCommandException("io", $"Failed to write launcher settings {SettingsFilePath}: {ex.Message}");
        }
    }
}
