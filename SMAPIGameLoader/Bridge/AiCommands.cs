using Android.App;
using SMAPIGameLoader.Services;
using SMAPIGameLoader.Tool;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Bridge;

/// <summary>
///     Hand-written bridge arms for the workbench AI settings commands the
///     front-end's AI settings panel and the launcher log-analysis call. The
///     desktop Rust backend owns the canonical implementation; this service
///     mirrors its wire shapes (camelCase envelopes, sanitized snapshots that
///     never expose credentials) with plain-JSON persistence under the app
///     sandbox. Translation batch/stream commands stay desktop-only.
/// </summary>
internal static class AiCommands
{
    static string DataDirectory => Path.Combine(FileTool.ExternalFilesDir, "ModForge", "app");

    static string AiSettingsFilePath => Path.Combine(DataDirectory, "ai-settings.json");
    static string ModelsDevCacheFilePath => Path.Combine(DataDirectory, "models-dev-catalog.json");
    const string ModelsDevCatalogUrl = "https://models.dev/api.json";
    const long ModelsDevCacheTtlMs = 24u * 60u * 60u * 1000u;

    /// <summary>Reports whether this command is one of the AI settings arms; unknown commands return false.</summary>
    public static bool Handles(string command)
    {
        switch (command)
        {
            case "load_ai_settings":
            case "save_ai_settings":
            case "list_ai_models":
            case "test_ai_profile":
            case "fetch_models_dev_catalog":
            case "export_ai_profiles":
            case "preview_ai_profiles_import":
            case "apply_ai_profiles_import":
                return true;
            default:
                return false;
        }
    }

    public static async Task<JsonNode?> HandleAsync(string command, JsonElement args)
    {
        switch (command)
        {
            case "load_ai_settings":
                return LoadSettingsSnapshot();
            case "save_ai_settings":
                return SaveSettings(ExtractRequest(args));
            case "list_ai_models":
                return await ListModelsAsync(ExtractRequest(args)).ConfigureAwait(false);
            case "test_ai_profile":
                return await TestProfileAsync(ExtractRequest(args)).ConfigureAwait(false);
            case "fetch_models_dev_catalog":
                return await FetchModelsDevCatalogAsync().ConfigureAwait(false);
            case "export_ai_profiles":
                return await ExportProfilesAsync(ExtractRequest(args)).ConfigureAwait(false);
            case "preview_ai_profiles_import":
                return PreviewProfilesImport(ExtractRequest(args));
            case "apply_ai_profiles_import":
                return ApplyProfilesImport(ExtractRequest(args));
            default:
                throw new LauncherCommandException("unknown_command", $"AI command '{command}' is not implemented on Android.");
        }
    }

    // --- preset table (mirrors Studio domain/ai/presets.rs) ---

    sealed record Preset(string Id, string Name, string Protocol, string BaseUrl, string? CredentialEnvironment, bool RequiresApiKey, bool SupportsModelListing, string StructuredOutput);

    static IReadOnlyList<Preset> Presets { get; } = new List<Preset>
    {
        new("openai", "OpenAI", "openai-responses", "https://api.openai.com/v1", "OPENAI_API_KEY", true, true, "json-schema"),
        new("anthropic", "Anthropic", "anthropic-messages", "https://api.anthropic.com/v1", "ANTHROPIC_API_KEY", true, true, "tool-use"),
        new("gemini", "Google Gemini", "openai-chat-completions", "https://generativelanguage.googleapis.com/v1beta/openai", "GEMINI_API_KEY", true, true, "json-schema"),
        new("deepseek", "DeepSeek", "openai-chat-completions", "https://api.deepseek.com", "DEEPSEEK_API_KEY", true, true, "json-object"),
        new("openrouter", "OpenRouter", "openai-chat-completions", "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY", true, true, "json-schema"),
        new("xai", "xAI", "openai-chat-completions", "https://api.x.ai/v1", "XAI_API_KEY", true, true, "json-schema"),
        new("mistral", "Mistral AI", "openai-chat-completions", "https://api.mistral.ai/v1", "MISTRAL_API_KEY", true, true, "json-schema"),
        new("groq", "Groq", "openai-chat-completions", "https://api.groq.com/openai/v1", "GROQ_API_KEY", true, true, "json-object"),
        new("moonshot", "Moonshot / Kimi", "openai-chat-completions", "https://api.moonshot.cn/v1", "MOONSHOT_API_KEY", true, true, "json-object"),
        new("qwen-cn", "Qwen (China)", "openai-chat-completions", "https://dashscope.aliyuncs.com/compatible-mode/v1", "DASHSCOPE_API_KEY", true, true, "json-object"),
        new("qwen-intl", "Qwen (International)", "openai-chat-completions", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "DASHSCOPE_API_KEY", true, true, "json-object"),
        new("zhipu", "Zhipu GLM", "openai-chat-completions", "https://open.bigmodel.cn/api/paas/v4", "ZHIPU_API_KEY", true, true, "json-object"),
        new("siliconflow-cn", "SiliconFlow (China)", "openai-chat-completions", "https://api.siliconflow.cn/v1", "SILICONFLOW_API_KEY", true, true, "json-object"),
        new("siliconflow-intl", "SiliconFlow (International)", "openai-chat-completions", "https://api.siliconflow.com/v1", "SILICONFLOW_API_KEY", true, true, "json-object"),
        new("ollama", "Ollama", "openai-chat-completions", "http://127.0.0.1:11434/v1", null, false, true, "none"),
        new("lm-studio", "LM Studio", "openai-chat-completions", "http://127.0.0.1:1234/v1", null, false, true, "none"),
        new("custom", "Custom", "openai-chat-completions", "https://", null, true, false, "none"),
    };

    static JsonObject PresetNode(Preset preset)
    {
        string authentication;
        if (!preset.RequiresApiKey)
            authentication = "none";
        else if (preset.Protocol == "anthropic-messages")
            authentication = "anthropic-api-key";
        else
            authentication = "bearer";

        return new JsonObject
        {
            ["id"] = preset.Id,
            ["name"] = preset.Name,
            ["protocol"] = preset.Protocol,
            ["baseUrl"] = preset.BaseUrl,
            ["credentialEnvironment"] = preset.CredentialEnvironment,
            ["requiresApiKey"] = preset.RequiresApiKey,
            ["authentication"] = authentication,
            ["supportsModelListing"] = preset.SupportsModelListing,
            ["structuredOutput"] = preset.StructuredOutput,
        };
    }

    static Preset? FindPreset(string presetId)
    {
        return Presets.FirstOrDefault(preset => string.Equals(preset.Id, presetId, StringComparison.Ordinal));
    }

    // --- settings persistence (server-side; profiles carry the apiKey, never returned) ---

    static JsonObject LoadSettingsDocument()
    {
        try
        {
            if (File.Exists(AiSettingsFilePath))
            {
                var parsed = JsonNode.Parse(File.ReadAllText(AiSettingsFilePath));
                if (parsed is JsonObject document)
                    return document;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("AiCommands: settings read failed, starting fresh: " + ex.Message);
        }

        return new JsonObject { ["version"] = 1, ["defaultProfileId"] = null, ["profiles"] = new JsonArray() };
    }

    static void SaveSettingsDocument(JsonObject document)
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(AiSettingsFilePath, document.ToJsonString());
    }

    static JsonArray ProfileArray(JsonObject document)
    {
        if (document["profiles"] is not JsonArray profiles)
        {
            profiles = new JsonArray();
            document["profiles"] = profiles;
        }
        return profiles;
    }

    /// <summary>Clones one stored profile into the sanitized wire shape (no apiKey).</summary>
    static JsonObject SanitizeProfile(JsonObject stored)
    {
        var sanitized = (JsonObject)stored.DeepClone();
        sanitized.Remove("apiKey");
        var hasKey = stored["apiKey"] is JsonValue value && value.TryGetValue<string>(out var key) && !string.IsNullOrWhiteSpace(key);
        sanitized["keyConfigured"] = hasKey;
        sanitized["resolvedCredentialSource"] = hasKey ? "keychain" : null;
        return sanitized;
    }

    static JsonObject LoadSettingsSnapshot()
    {
        var document = LoadSettingsDocument();
        var profiles = new JsonArray();
        foreach (var node in ProfileArray(document))
        {
            if (node is JsonObject stored)
                profiles.Add(SanitizeProfile(stored));
        }

        var presets = new JsonArray();
        foreach (var preset in Presets)
            presets.Add(PresetNode(preset));

        return new JsonObject
        {
            ["version"] = document["version"]?.DeepClone() ?? 1,
            ["defaultProfileId"] = document["defaultProfileId"]?.DeepClone(),
            ["profiles"] = profiles,
            ["presets"] = presets,
        };
    }

    static JsonObject SaveSettings(JsonNode? request)
    {
        if (request is not JsonObject payload)
            throw new LauncherCommandException("invalid_args", "save_ai_settings requires a request object.");

        var document = LoadSettingsDocument();
        var storedProfiles = ProfileArray(document);
        var incomingProfiles = payload["profiles"] as JsonArray ?? new JsonArray();
        var nextProfiles = new JsonArray();

        foreach (var node in incomingProfiles)
        {
            if (node is not JsonObject incoming)
                continue;

            var id = incoming["id"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(id))
                throw new LauncherCommandException("invalid_args", "Every AI profile requires an id.");

            var existing = storedProfiles
                .OfType<JsonObject>()
                .FirstOrDefault(profile => string.Equals(profile["id"]?.GetValue<string>(), id, StringComparison.Ordinal));

            var stored = (JsonObject)incoming.DeepClone();
            stored.Remove("apiKey");
            stored.Remove("clearApiKey");

            // Carry the previous credential forward unless the patch replaces or clears it.
            if (incoming["clearApiKey"] is JsonValue clearValue && clearValue.TryGetValue<bool>(out var clear) && clear)
            {
                stored.Remove("apiKey");
            }
            else if (incoming["apiKey"] is JsonValue keyValue && keyValue.TryGetValue<string>(out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
            {
                stored["apiKey"] = apiKey.Trim();
            }
            else if (existing?["apiKey"] is JsonNode existingKey)
            {
                stored["apiKey"] = existingKey.DeepClone();
            }
            else
            {
                stored.Remove("apiKey");
            }

            NormalizeProfileFields(stored);
            nextProfiles.Add(stored);
        }

        document["profiles"] = nextProfiles;
        document["defaultProfileId"] = payload["defaultProfileId"]?.DeepClone();
        SaveSettingsDocument(document);
        return LoadSettingsSnapshot();
    }

    /// <summary>Fills protocol/baseUrl/credentialEnvironment from the preset when the profile omits them.</summary>
    static void NormalizeProfileFields(JsonObject profile)
    {
        var presetId = profile["presetId"]?.GetValue<string>() ?? string.Empty;
        var preset = FindPreset(presetId);
        if (preset is null)
            return;

        profile["protocol"] = profile["protocol"]?.GetValue<string>() is { Length: > 0 } protocol ? protocol : preset.Protocol;
        profile["baseUrl"] = profile["baseUrl"]?.GetValue<string>() is { Length: > 0 } baseUrl ? baseUrl : preset.BaseUrl;
        profile["credentialEnvironment"] ??= preset.CredentialEnvironment;
        profile["name"] = profile["name"]?.GetValue<string>() is { Length: > 0 } name ? name : preset.Name;
    }

    /// <summary>Finds a stored profile by id together with its usable API key; null when missing or keyless.</summary>
    internal static JsonObject? FindProfileWithKey(string profileId)
    {
        foreach (var node in ProfileArray(LoadSettingsDocument()))
        {
            if (node is not JsonObject profile)
                continue;
            if (!string.Equals(profile["id"]?.GetValue<string>(), profileId, StringComparison.Ordinal))
                continue;
            if (profile["apiKey"] is not JsonValue value || !value.TryGetValue<string>(out var key) || string.IsNullOrWhiteSpace(key))
                return null;
            return profile;
        }
        return null;
    }

    // --- provider HTTP helpers ---

    static void AttachAuthHeaders(HttpRequestMessage request, JsonObject profile)
    {
        var protocol = profile["protocol"]?.GetValue<string>() ?? string.Empty;
        var apiKey = profile["apiKey"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new LauncherCommandException("authentication", "The AI profile has no API key configured.");

        if (protocol == "anthropic-messages")
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else
        {
            request.Headers.TryAddWithoutValidation("authorization", $"Bearer {apiKey}");
        }
    }

    static string BaseUrlOf(JsonObject profile)
    {
        var baseUrl = (profile["baseUrl"]?.GetValue<string>() ?? string.Empty).Trim().TrimEnd('/');
        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new LauncherCommandException("invalid_args", "The AI profile baseUrl must be an absolute http(s) URL.");
        return baseUrl;
    }

    static async Task<JsonNode?> ListModelsAsync(JsonNode? request)
    {
        var profileId = request?["profileId"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(profileId))
            throw new LauncherCommandException("invalid_args", "list_ai_models requires a profileId.");

        var profile = FindProfileWithKey(profileId)
            ?? throw new LauncherCommandException("not_found", $"AI profile '{profileId}' was not found or has no API key.");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, BaseUrlOf(profile) + "/models");
        AttachAuthHeaders(httpRequest, profile);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ModForge-Android/1.0");
        using var response = await client.SendAsync(httpRequest).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new LauncherCommandException("network", $"Model listing failed: HTTP {(int)response.StatusCode}.");

        using var document = JsonDocument.Parse(body);
        var models = new JsonArray();
        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in data.EnumerateArray())
            {
                var id = model.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                var displayName = model.TryGetProperty("display_name", out var nameElement) ? nameElement.GetString() : null;
                models.Add(new JsonObject
                {
                    ["id"] = id,
                    ["displayName"] = displayName,
                    ["contextWindowTokens"] = null,
                });
            }
        }

        return models;
    }

    static async Task<JsonNode?> TestProfileAsync(JsonNode? request)
    {
        var profileId = request?["profileId"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(profileId))
            throw new LauncherCommandException("invalid_args", "test_ai_profile requires a profileId.");

        var profile = FindProfileWithKey(profileId)
            ?? throw new LauncherCommandException("not_found", $"AI profile '{profileId}' was not found or has no API key.");

        var protocol = profile["protocol"]?.GetValue<string>() ?? string.Empty;
        var model = profile["model"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(model))
            throw new LauncherCommandException("invalid_args", "The AI profile has no model configured.");

        var baseUrl = BaseUrlOf(profile);
        string url;
        string body;
        if (protocol == "anthropic-messages")
        {
            url = baseUrl + "/messages";
            body = JsonSerializer.Serialize(new { model, max_tokens = 16, messages = new[] { new { role = "user", content = "ping" } } });
        }
        else if (protocol == "openai-responses")
        {
            url = baseUrl + "/responses";
            body = JsonSerializer.Serialize(new { model, input = "ping", max_output_tokens = 16 });
        }
        else
        {
            url = baseUrl + "/chat/completions";
            body = JsonSerializer.Serialize(new { model, max_tokens = 16, messages = new[] { new { role = "user", content = "ping" } } });
        }

        var started = Stopwatch.StartNew();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        AttachAuthHeaders(httpRequest, profile);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ModForge-Android/1.0");
        using var response = await client.SendAsync(httpRequest).ConfigureAwait(false);
        started.Stop();
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new LauncherCommandException("network", $"AI provider probe failed: HTTP {(int)response.StatusCode} — {responseBody.Trim().Trim('"').SliceSafe(200)}");

        return new JsonObject
        {
            ["provider"] = profile["presetId"]?.GetValue<string>() ?? profileId,
            ["protocol"] = protocol,
            ["baseUrl"] = profile["baseUrl"]?.GetValue<string>() ?? baseUrl,
            ["model"] = model,
            ["latencyMs"] = started.ElapsedMilliseconds,
            ["credentialSource"] = "keychain",
            ["reasoning"] = null,
        };
    }

    // --- models.dev catalog (memory/disk TTL cache, mirrors domain/ai/models_dev.rs) ---

    static async Task<JsonNode?> FetchModelsDevCatalogAsync()
    {
        var now = (long)LauncherJsonHelper.CurrentTimestampMs();
        if (TryReadCatalogCache(now) is { } cached)
            return cached;

        string body;
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ModForge-Android/1.0");
            using var response = await client.GetAsync(ModelsDevCatalogUrl).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new LauncherCommandException("network", $"models.dev catalog fetch failed: HTTP {(int)response.StatusCode}.");
        }

        var catalog = ParseModelsDevCatalog(body, now);
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(ModelsDevCacheFilePath, catalog.ToJsonString());
        }
        catch (Exception ex)
        {
            Console.WriteLine("AiCommands: models.dev cache write failed: " + ex.Message);
        }
        return catalog;
    }

    static JsonObject? TryReadCatalogCache(long nowMs)
    {
        try
        {
            if (!File.Exists(ModelsDevCacheFilePath))
                return null;
            var cached = JsonNode.Parse(File.ReadAllText(ModelsDevCacheFilePath));
            if (cached is not JsonObject document)
                return null;
            if (document["fetchedAtMs"]?.GetValue<long>() is not long fetchedAt || nowMs - fetchedAt > ModelsDevCacheTtlMs)
                return null;
            return document;
        }
        catch (Exception)
        {
            return null; //cache is an optimization, never a correctness gate
        }
    }

    static JsonObject ParseModelsDevCatalog(string body, long nowMs)
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new LauncherCommandException("network", "models.dev catalog response is not a JSON object.");

        var providers = new JsonArray();
        foreach (var providerProperty in document.RootElement.EnumerateObject())
        {
            if (providerProperty.Value.ValueKind != JsonValueKind.Object)
                continue;
            var providerObject = providerProperty.Value;
            var name = providerObject.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;

            var models = new JsonArray();
            if (providerObject.TryGetProperty("models", out var modelsElement) && modelsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var modelProperty in modelsElement.EnumerateObject())
                {
                    if (modelProperty.Value.ValueKind != JsonValueKind.Object)
                        continue;
                    var modelObject = modelProperty.Value;
                    long? context = null;
                    long? output = null;
                    if (modelObject.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Object)
                    {
                        if (limit.TryGetProperty("context", out var contextElement) && contextElement.TryGetInt64(out var contextValue) && contextValue > 0)
                            context = contextValue;
                        if (limit.TryGetProperty("output", out var outputElement) && outputElement.TryGetInt64(out var outputValue) && outputValue > 0)
                            output = outputValue;
                    }
                    models.Add(new JsonObject
                    {
                        ["id"] = modelProperty.Name,
                        ["name"] = modelObject.TryGetProperty("name", out var modelName) && modelName.ValueKind == JsonValueKind.String ? modelName.GetString() : null,
                        ["contextWindowTokens"] = context,
                        ["maxOutputTokens"] = output,
                    });
                }
            }

            providers.Add(new JsonObject
            {
                ["id"] = providerProperty.Name,
                ["name"] = name ?? providerProperty.Name,
                ["models"] = models,
            });
        }

        if (providers.Count == 0)
            throw new LauncherCommandException("network", "models.dev catalog contains no providers.");

        return new JsonObject { ["fetchedAtMs"] = nowMs, ["providers"] = providers };
    }

    // --- profile export/import (credentials never enter the document) ---

    static JsonObject BuildExportDocument(IEnumerable<string> profileIds)
    {
        var requested = new HashSet<string>(profileIds, StringComparer.Ordinal);
        var profiles = new JsonArray();
        foreach (var node in ProfileArray(LoadSettingsDocument()))
        {
            if (node is not JsonObject stored)
                continue;
            if (requested.Count > 0 && !requested.Contains(stored["id"]?.GetValue<string>() ?? string.Empty))
                continue;
            profiles.Add(SanitizeProfile(stored));
        }

        return new JsonObject
        {
            ["formatVersion"] = 1,
            ["profiles"] = profiles,
        };
    }

    static async Task<JsonNode?> ExportProfilesAsync(JsonNode? request)
    {
        var destination = request?["destinationPath"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(destination))
            throw new LauncherCommandException("invalid_args", "export_ai_profiles requires a destinationPath.");

        var profileIds = request?["profileIds"] as JsonArray ?? new JsonArray();
        var ids = profileIds
            .Select(node => node?.GetValue<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();

        var document = BuildExportDocument(ids);
        var json = document.ToJsonString();
        if (destination.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = Android.Net.Uri.Parse(destination)!;
            await using var stream = Application.Context.ContentResolver!.OpenOutputStream(uri!)
                ?? throw new LauncherCommandException("io", "Could not open the picked document for writing.");
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(destination, json).ConfigureAwait(false);
        }

        return document["profiles"] is JsonArray exported ? exported.Count : 0;
    }

    static JsonObject ReadImportDocument(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new LauncherCommandException("not_found", $"Profile import file {sourcePath} is not readable.");
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(File.ReadAllText(sourcePath));
        }
        catch (Exception ex)
        {
            throw new LauncherCommandException("invalid_json", $"Profile import file is not valid JSON: {ex.Message}");
        }
        if (parsed is not JsonObject document)
            throw new LauncherCommandException("invalid_json", "Profile import file must be a JSON object.");
        return document;
    }

    static JsonObject PreviewProfilesImport(JsonNode? request)
    {
        var sourcePath = request?["sourcePath"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(sourcePath))
            throw new LauncherCommandException("invalid_args", "preview_ai_profiles_import requires a sourcePath.");

        var document = ReadImportDocument(sourcePath);
        var existingIds = ProfileArray(LoadSettingsDocument())
            .OfType<JsonObject>()
            .Select(profile => profile["id"]?.GetValue<string>() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        var entries = new JsonArray();
        if (document["profiles"] is JsonArray profiles)
        {
            foreach (var node in profiles)
            {
                if (node is not JsonObject profile)
                    continue;
                var id = profile["id"]?.GetValue<string>() ?? string.Empty;
                entries.Add(new JsonObject
                {
                    ["id"] = id,
                    ["name"] = profile["name"]?.GetValue<string>() ?? id,
                    ["provider"] = profile["presetId"]?.GetValue<string>() ?? string.Empty,
                    ["model"] = profile["model"]?.GetValue<string>() ?? string.Empty,
                    ["conflicts"] = existingIds.Contains(id),
                });
            }
        }

        return new JsonObject
        {
            ["formatVersion"] = document["formatVersion"]?.GetValue<int>() ?? 1,
            ["credentialsExcluded"] = true,
            ["entries"] = entries,
        };
    }

    static JsonObject ApplyProfilesImport(JsonNode? request)
    {
        var sourcePath = request?["sourcePath"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(sourcePath))
            throw new LauncherCommandException("invalid_args", "apply_ai_profiles_import requires a sourcePath.");
        var conflictPolicy = request?["conflictPolicy"]?.GetValue<string>() ?? "skip";

        var document = ReadImportDocument(sourcePath);
        var settings = LoadSettingsDocument();
        var storedProfiles = ProfileArray(settings);
        var storedById = storedProfiles
            .OfType<JsonObject>()
            .ToDictionary(profile => profile["id"]?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal);

        var imported = 0;
        var overwritten = 0;
        var copied = 0;
        var skipped = 0;

        if (document["profiles"] is JsonArray profiles)
        {
            foreach (var node in profiles)
            {
                if (node is not JsonObject incoming)
                    continue;
                var id = incoming["id"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrEmpty(id))
                    continue;

                var stored = (JsonObject)incoming.DeepClone();
                stored.Remove("apiKey");
                stored.Remove("clearApiKey");
                NormalizeProfileFields(stored);

                if (storedById.TryGetValue(id, out var existing))
                {
                    if (conflictPolicy == "overwrite")
                    {
                        var apiKey = existing["apiKey"]?.DeepClone();
                        storedProfiles.Remove(existing);
                        stored["apiKey"] = apiKey?.DeepClone();
                        NormalizeProfileFields(stored);
                        storedProfiles.Add(stored);
                        overwritten++;
                    }
                    else if (conflictPolicy == "copy")
                    {
                        var copyId = id + "-copy";
                        var suffix = 1;
                        while (storedById.ContainsKey(copyId))
                            copyId = id + "-copy-" + (++suffix);
                        stored["id"] = copyId;
                        storedById[copyId] = stored;
                        storedProfiles.Add(stored);
                        copied++;
                    }
                    else
                    {
                        skipped++;
                    }
                    continue;
                }

                storedById[id] = stored;
                storedProfiles.Add(stored);
                imported++;
            }
        }

        SaveSettingsDocument(settings);
        return new JsonObject
        {
            ["settings"] = LoadSettingsSnapshot(),
            ["imported"] = imported,
            ["overwritten"] = overwritten,
            ["copied"] = copied,
            ["skipped"] = skipped,
        };
    }

    static JsonNode? ExtractRequest(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("request", out var request))
            return JsonNode.Parse(request.GetRawText());
        return null;
    }
}

static class StringSliceExtensions
{
    /// <summary>Substring bounded to the available length (C# lacks Rust-style slicing on short strings).</summary>
    public static string SliceSafe(this string value, int maxChars)
    {
        return value.Length <= maxChars ? value : value.Substring(0, maxChars);
    }
}
