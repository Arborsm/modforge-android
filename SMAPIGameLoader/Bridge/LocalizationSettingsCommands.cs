using SMAPIGameLoader.Services;
using SMAPIGameLoader.Tool;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Bridge;

/// <summary>
///     Default-translation-engine persistence (load/save_localization_default_engine).
///     Mirrors the desktop Rust backend (domain/localization/settings.rs): a small
///     versioned JSON document under the app sandbox; the engine ref itself crosses
///     the bridge camelCased ({ kind, profileId }) exactly like the desktop wire shape.
/// </summary>
internal static class LocalizationSettingsCommands
{
    static string DataDirectory => Path.Combine(FileTool.ExternalFilesDir, "ModForge", "app");
    static string SettingsFilePath => Path.Combine(DataDirectory, "localization-settings.json");
    const int SettingsVersion = 1;

    /// <summary>Reports whether this command is one of the localization-settings arms.</summary>
    public static bool Handles(string command)
        => command is "load_localization_default_engine" or "save_localization_default_engine";

    public static Task<JsonNode?> HandleAsync(string command, JsonElement args)
    {
        switch (command)
        {
            case "load_localization_default_engine":
                return Task.FromResult(LoadDefaultEngine());
            case "save_localization_default_engine":
                return Task.FromResult(SaveDefaultEngine(args));
            default:
                throw new LauncherCommandException("unknown_command", $"Localization settings command '{command}' is not implemented on Android.");
        }
    }

    static JsonNode? LoadDefaultEngine()
    {
        if (!File.Exists(SettingsFilePath))
            return null;

        try
        {
            var document = JsonNode.Parse(File.ReadAllText(SettingsFilePath)) as JsonObject;
            var engine = document?["defaultEngine"] as JsonObject;
            return engine is null ? null : NormalizeEngine(engine);
        }
        catch (LauncherCommandException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LauncherCommandException("invalid_json", $"Localization settings {SettingsFilePath} is invalid JSON: {ex.Message}");
        }
    }

    static JsonNode? SaveDefaultEngine(JsonElement args)
    {
        var engine = args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("engine", out var engineElement)
            && engineElement.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(engineElement.GetRawText()) as JsonObject
                : null;
        var normalized = NormalizeEngine(engine);

        var document = new JsonObject
        {
            ["version"] = SettingsVersion,
            ["defaultEngine"] = normalized.DeepClone(),
        };
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(SettingsFilePath, document.ToJsonString() + "\n");
        return normalized;
    }

    /// <summary>Validates the ref exactly like the desktop side and returns a fresh node with only the two fields.</summary>
    static JsonObject NormalizeEngine(JsonObject? engine)
    {
        var kind = engine?["kind"]?.GetValue<string>();
        var profileId = engine?["profileId"]?.GetValue<string>();
        if (kind is not ("generative-ai" or "machine-translation"))
            throw new LauncherCommandException("invalid_args", "Default engine kind must be 'generative-ai' or 'machine-translation'.");
        if (string.IsNullOrWhiteSpace(profileId))
            throw new LauncherCommandException("invalid_args", "Default engine requires a profileId.");

        return new JsonObject
        {
            ["kind"] = kind,
            ["profileId"] = profileId!.Trim(),
        };
    }
}
