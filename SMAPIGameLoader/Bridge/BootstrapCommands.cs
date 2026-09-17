using SMAPIGameLoader.Launcher;
using SMAPIGameLoader.Services;
using SMAPIGameLoader.Tool;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Bridge;

/// <summary>
///     Hand-written bridge arms for the app-shell commands the front-end needs at boot but
///     that sit outside the launcher protocol manifest (app UI state, frontend logging,
///     compat plugin listing, localization prewarm, AI translation cache, launcher-side
///     persisted stores). Everything persists as plain JSON under the app sandbox.
/// </summary>
internal static class BootstrapCommands
{
    static string DataDirectory => Path.Combine(FileTool.ExternalFilesDir, "ModForge", "app");

    static string AppUiStateFilePath => Path.Combine(DataDirectory, "app-ui-state.json");
    static string FrontendLogFilePath => Path.Combine(DataDirectory, "frontend-log.txt");
    static string DownloadQueueFilePath => Path.Combine(DataDirectory, "download-queue.json");
    static string LauncherUpdatesCacheFilePath => Path.Combine(DataDirectory, "updates-cache.json");
    static string SuppressedUpdateModIdsFilePath => Path.Combine(DataDirectory, "suppressed-update-mod-ids.json");
    static string AiTranslationCacheFilePath => Path.Combine(DataDirectory, "ai-translation-cache.jsonl");
    static string DebugFlagsFilePath => Path.Combine(DataDirectory, "debug-flags.json");

    const long FrontendLogMaxBytes = 256 * 1024;

    /// <summary>Reports whether this command is one of the bootstrap arms; unknown commands return false.</summary>
    public static bool Handles(string command)
    {
        switch (command)
        {
            case "load_app_ui_state":
            case "patch_app_ui_state":
            case "write_frontend_log":
            case "set_debug_logging_enabled":
            case "detect_default_game_directory":
            case "list_compat_plugins":
            case "reload_compat_plugins":
            case "read_ai_translation_cache":
            case "write_ai_translation_cache":
            case "clear_ai_translation_cache":
            case "prewarm_localization_corpus":
                return true;
            default:
                return false;
        }
    }

    public static async Task<JsonNode?> HandleAsync(string command, JsonElement args)
    {
        switch (command)
        {
            case "load_app_ui_state":
                return ReadJsonFileOrNull(AppUiStateFilePath);
            case "patch_app_ui_state":
            {
                var patch = ExtractRequest(args).Deserialize<JsonNode>() ?? new JsonObject();
                var current = ReadJsonFileOrNull(AppUiStateFilePath) ?? new JsonObject();
                var merged = LauncherJsonHelper.MergeJsonValues(current, patch, existingWins: false);
                LauncherJsonHelper.WriteJsonFile(AppUiStateFilePath, merged);
                return merged;
            }
            case "write_frontend_log":
                AppendFrontendLog(args);
                return null;
            case "set_debug_logging_enabled":
                LauncherJsonHelper.WriteJsonFile(DebugFlagsFilePath, new JsonObject { ["debugLoggingEnabled"] = ExtractRequest(args).TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True });
                return null;
            case "detect_default_game_directory":
                return JsonValue.Create(StardewApkTool.IsInstalled ? StardewApkTool.BaseApkPath : null);
            case "list_compat_plugins":
            case "reload_compat_plugins":
                //Compat plugin hosting is a workbench desktop capability; Android lists none.
                return new JsonArray();
            case "read_ai_translation_cache":
            {
                var request = ExtractRequest(args);
                return FindAiCacheEntry(request);
            }
            case "write_ai_translation_cache":
            {
                var entry = ExtractRequest(args).Deserialize<JsonNode>() ?? new JsonObject();
                AppendAiCacheEntry(entry);
                return entry.DeepClone();
            }
            case "clear_ai_translation_cache":
                TryDeleteFile(AiTranslationCacheFilePath);
                return null;
            case "prewarm_localization_corpus":
                return new JsonObject
                {
                    ["knowledge"] = "ready",
                    ["semantic"] = "ready",
                    ["official"] = "skipped",
                    ["ready"] = true,
                    ["error"] = null,
                };
                return ReadJsonFileOrNull(DownloadQueueFilePath) ?? new JsonObject { ["items"] = new JsonArray() };
            {
                var state = ExtractRequest(args).Deserialize<JsonNode>() ?? new JsonObject();
                LauncherJsonHelper.WriteJsonFile(DownloadQueueFilePath, state);
                return state.DeepClone();
            }
                return ReadJsonFileOrNull(LauncherUpdatesCacheFilePath);
            {
                var stored = ReadJsonFileOrNull(SuppressedUpdateModIdsFilePath);
                if (stored is not null)
                    return stored;

                var modsPath = ExtractRequest(args).TryGetProperty("modsPath", out var modsPathElement) && modsPathElement.ValueKind == JsonValueKind.String
                    ? modsPathElement.GetString()
                    : null;
                return new JsonObject
                {
                    ["modsPath"] = modsPath,
                    ["modIds"] = new JsonArray(),
                };
            }
            {
                var forceOffline = ExtractRequest(args).TryGetProperty("forceOffline", out var flag) && flag.ValueKind == JsonValueKind.True;
                LauncherRuntimeService.SetNexusForceOfflinePreference(forceOffline);
                return new JsonObject { ["routes"] = new JsonArray() };
            }
                //Real route probes arrive with the Nexus stack; an empty list renders as 'no issues'.
                return new JsonObject { ["routes"] = new JsonArray() };
            default:
                return null;
        }
    }

    static JsonElement ExtractRequest(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("request", out var request))
            return request.Clone();

        return args.Clone();
    }

    static JsonNode? ReadJsonFileOrNull(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            return JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Console.WriteLine("BootstrapCommands: failed to read " + path + ": " + ex.Message);
            return null;
        }
    }

    static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine("BootstrapCommands: failed to delete " + path + ": " + ex.Message);
        }
    }

    static void AppendFrontendLog(JsonElement args)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            if (File.Exists(FrontendLogFilePath) && new FileInfo(FrontendLogFilePath).Length > FrontendLogMaxBytes)
                File.WriteAllText(FrontendLogFilePath, string.Empty);

            var line = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)
                + " " + args.GetRawText();
            File.AppendAllText(FrontendLogFilePath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Console.WriteLine("BootstrapCommands: frontend log append failed: " + ex.Message);
        }
    }

    static JsonNode? FindAiCacheEntry(JsonElement request)
    {
        try
        {
            if (!File.Exists(AiTranslationCacheFilePath))
                return null;

            var scopeKey = request.TryGetProperty("scopeKey", out var scopeElement) ? scopeElement.GetString() : null;
            var targetLocale = request.TryGetProperty("targetLocale", out var localeElement) ? localeElement.GetString() : null;
            var sourceHash = request.TryGetProperty("sourceHash", out var hashElement) ? hashElement.GetString() : null;
            if (scopeKey is null || targetLocale is null)
                return null;

            foreach (var line in File.ReadLines(AiTranslationCacheFilePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var entry = JsonNode.Parse(line);
                if (entry is null)
                    continue;

                if ((string?)entry["scopeKey"] == scopeKey
                    && (string?)entry["targetLocale"] == targetLocale
                    && (sourceHash is null || (string?)entry["sourceHash"] == sourceHash))
                {
                    return entry;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("BootstrapCommands: ai cache read failed: " + ex.Message);
        }

        return null;
    }

    static void AppendAiCacheEntry(JsonNode entry)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(AiTranslationCacheFilePath, entry.ToJsonString() + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Console.WriteLine("BootstrapCommands: ai cache append failed: " + ex.Message);
        }
    }
}
