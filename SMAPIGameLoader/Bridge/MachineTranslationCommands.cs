using SMAPIGameLoader.Services;
using SMAPIGameLoader.Tool;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Bridge;

/// <summary>
///     Hand-written bridge arms for the machine-translation settings commands
///     the front-end's AI settings panel (machine-translation tab) calls. The
///     desktop Rust backend owns the canonical implementation
///     (domain/localization/machine_translation); this service mirrors its wire
///     shapes (camelCase envelopes, sanitized snapshots that never expose
///     credentials) and its provider adapters (DeepL, Google, Microsoft, Baidu,
///     Tencent, LibreTranslate) with plain-JSON persistence under the app
///     sandbox. Translation batch/stream commands stay desktop-only — the
///     Android product surface is profile management plus provider probes.
/// </summary>
internal static class MachineTranslationCommands
{
    static string DataDirectory => Path.Combine(FileTool.ExternalFilesDir, "ModForge", "app");

    static string SettingsFilePath => Path.Combine(DataDirectory, "mt-settings.json");
    const int MaxProfiles = 32;
    const int MaxFieldBytes = 2048;
    const int MaxSecretBytes = 16 * 1024;
    const string UserAgent = "ModForge-Android/1.0";

    /// <summary>Reports whether this command is one of the machine-translation arms; unknown commands return false.</summary>
    public static bool Handles(string command)
    {
        switch (command)
        {
            case "load_machine_translation_settings":
            case "save_machine_translation_settings":
            case "list_machine_translation_languages":
            case "test_machine_translation_profile":
                return true;
            default:
                return false;
        }
    }

    public static async Task<JsonNode?> HandleAsync(string command, JsonElement args)
    {
        switch (command)
        {
            case "load_machine_translation_settings":
                return LoadSettingsSnapshot();
            case "save_machine_translation_settings":
                return SaveSettings(ExtractRequest(args));
            case "list_machine_translation_languages":
                return await ListLanguagesAsync(RequireProfileId(ExtractRequest(args))).ConfigureAwait(false);
            case "test_machine_translation_profile":
                return await TestProfileAsync(RequireProfileId(ExtractRequest(args))).ConfigureAwait(false);
            default:
                throw new LauncherCommandException("unknown_command", $"Machine translation command '{command}' is not implemented on Android.");
        }
    }

    // --- preset table (mirrors Studio domain/localization/machine_translation/presets.rs) ---

    sealed record MtCapability(bool LanguagesDynamic, long MaxItemCharacters, long MaxBatchCharacters, bool SupportsHtml, bool SupportsGlossary, string UsageCapability, string Authentication);

    sealed record MtPreset(string Id, string Name, string Protocol, string BaseUrl, string[] CredentialFields, MtCapability Capability);

    static IReadOnlyList<MtPreset> Presets { get; } = new List<MtPreset>
    {
        new("deepl-free", "DeepL API Free", "deepl", "https://api-free.deepl.com",
            new[] { "api-key" },
            new MtCapability(true, 128_000, 128_000, true, true, "billed-characters", "header")),
        new("deepl-pro", "DeepL API Pro", "deepl", "https://api.deepl.com",
            new[] { "api-key" },
            new MtCapability(true, 128_000, 128_000, true, true, "billed-characters", "header")),
        new("google-basic-v2", "Google Cloud Translation Basic v2", "google-basic-v2", "https://translation.googleapis.com",
            new[] { "api-key" },
            new MtCapability(true, 30_000, 100_000, true, false, "local-measured", "query")),
        new("microsoft-v3", "Microsoft Translator v3", "microsoft-v3", "https://api.cognitive.microsofttranslator.com",
            new[] { "api-key" },
            new MtCapability(true, 50_000, 50_000, true, false, "metered-characters", "header")),
        new("baidu-general", "Baidu General Translation", "baidu-general", "https://fanyi-api.baidu.com",
            new[] { "app-id", "secret" },
            new MtCapability(false, 6_000, 6_000, false, false, "local-measured", "signed-form")),
        new("tencent-tmt", "Tencent Cloud TMT", "tencent-tmt", "https://tmt.tencentcloudapi.com",
            new[] { "secret-id", "secret-key" },
            new MtCapability(false, 2_000, 10_000, false, false, "local-measured", "tc3-hmac")),
        new("libretranslate", "LibreTranslate", "libretranslate", "https://libretranslate.com",
            new[] { "api-key" },
            new MtCapability(true, 10_000, 50_000, true, false, "local-measured", "body")),
    };

    static JsonObject PresetNode(MtPreset preset)
    {
        return new JsonObject
        {
            ["id"] = preset.Id,
            ["name"] = preset.Name,
            ["protocol"] = preset.Protocol,
            ["baseUrl"] = preset.BaseUrl,
            ["credentialFields"] = new JsonArray(preset.CredentialFields.Select(field => (JsonNode)field).ToArray()),
            ["capability"] = new JsonObject
            {
                ["languagesDynamic"] = preset.Capability.LanguagesDynamic,
                ["maxItemCharacters"] = preset.Capability.MaxItemCharacters,
                ["maxBatchCharacters"] = preset.Capability.MaxBatchCharacters,
                ["supportsHtml"] = preset.Capability.SupportsHtml,
                ["supportsGlossary"] = preset.Capability.SupportsGlossary,
                ["usageCapability"] = preset.Capability.UsageCapability,
                ["authentication"] = preset.Capability.Authentication,
            },
        };
    }

    static MtPreset? FindPreset(string presetId)
    {
        return Presets.FirstOrDefault(preset => string.Equals(preset.Id, presetId, StringComparison.Ordinal));
    }

    // --- settings persistence (server-side; profiles carry their credentials, never returned) ---

    static JsonObject LoadSettingsDocument()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var parsed = JsonNode.Parse(File.ReadAllText(SettingsFilePath));
                if (parsed is JsonObject document)
                {
                    if (document["version"]?.GetValue<int>() != 1)
                        throw new LauncherCommandException("invalid_settings", "Machine translation settings version is not supported.");
                    return document;
                }
            }
        }
        catch (LauncherCommandException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine("MachineTranslationCommands: settings read failed, starting fresh: " + ex.Message);
        }

        return new JsonObject { ["version"] = 1, ["defaultProfileId"] = null, ["profiles"] = new JsonArray() };
    }

    /// <summary>Atomic replace with backup, mirroring the desktop settings writer.</summary>
    static void SaveSettingsDocument(JsonObject document)
    {
        var temporary = SettingsFilePath + ".tmp";
        var backup = SettingsFilePath + ".bak";
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(temporary, document.ToJsonString());
        if (!File.Exists(SettingsFilePath))
        {
            File.Move(temporary, SettingsFilePath);
            return;
        }
        if (File.Exists(backup))
            File.Delete(backup);
        File.Move(SettingsFilePath, backup);
        try
        {
            File.Move(temporary, SettingsFilePath);
        }
        catch
        {
            File.Move(backup, SettingsFilePath);
            throw;
        }
        File.Delete(backup);
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

    static string? StoredCredential(JsonObject stored, string field)
    {
        if (stored["credentials"] is JsonObject credentials
            && credentials[field] is JsonValue value
            && value.TryGetValue<string>(out var secret)
            && !string.IsNullOrWhiteSpace(secret))
        {
            return secret;
        }
        return null;
    }

    /// <summary>Property access that tolerates non-object nodes in provider responses.</summary>
    static JsonNode? Child(JsonNode? node, string property)
    {
        return node is JsonObject obj ? obj[property] : null;
    }

    /// <summary>Clones one stored profile into the sanitized wire shape (credentials become source markers only).</summary>
    static JsonObject SanitizeProfile(JsonObject stored)
    {
        var preset = FindPreset(stored["presetId"]?.GetValue<string>() ?? string.Empty);
        var sources = new JsonObject();
        foreach (var field in preset?.CredentialFields ?? Array.Empty<string>())
        {
            if (StoredCredential(stored, field) is not null)
                sources[field] = "keychain";
            else if (stored["credentialEnvironments"] is JsonObject environments
                && environments[field] is JsonValue nameValue
                && nameValue.TryGetValue<string>(out var name)
                && EnvironmentValue(name) is not null)
                sources[field] = "environment";
        }

        return new JsonObject
        {
            ["id"] = stored["id"]?.DeepClone(),
            ["name"] = stored["name"]?.DeepClone(),
            ["presetId"] = stored["presetId"]?.DeepClone(),
            ["protocol"] = stored["protocol"]?.DeepClone(),
            ["baseUrl"] = stored["baseUrl"]?.DeepClone(),
            ["region"] = stored["region"]?.DeepClone(),
            ["enabled"] = stored["enabled"]?.DeepClone() ?? true,
            ["defaultSourceLocale"] = stored["defaultSourceLocale"]?.DeepClone(),
            ["defaultTargetLocale"] = stored["defaultTargetLocale"]?.DeepClone(),
            ["credentialEnvironments"] = stored["credentialEnvironments"]?.DeepClone() ?? new JsonObject(),
            ["credentialSources"] = sources,
        };
    }

    static string? EnvironmentValue(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var value = Environment.GetEnvironmentVariable(name.Trim())?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
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
            ["version"] = 1,
            ["defaultProfileId"] = document["defaultProfileId"]?.DeepClone(),
            ["profiles"] = profiles,
            ["presets"] = presets,
        };
    }

    static JsonObject SaveSettings(JsonNode? request)
    {
        if (request is not JsonObject payload)
            throw new LauncherCommandException("invalid_args", "save_machine_translation_settings requires a request object.");

        var incomingProfiles = payload["profiles"] as JsonArray ?? new JsonArray();
        if (incomingProfiles.Count > MaxProfiles)
            throw new LauncherCommandException("invalid_args", $"Machine translation settings support at most {MaxProfiles} profiles.");

        var nextProfiles = new JsonArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in incomingProfiles)
        {
            if (node is not JsonObject incoming)
                continue;
            nextProfiles.Add(NormalizeProfile(incoming));
            var id = nextProfiles[^1]!["id"]!.GetValue<string>();
            if (!ids.Add(id))
                throw new LauncherCommandException("invalid_args", "Machine translation profile ids must be unique.");
        }

        var defaultProfileId = payload["defaultProfileId"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(defaultProfileId))
        {
            if (!ids.Contains(defaultProfileId))
                throw new LauncherCommandException("invalid_args", "The default machine translation profile does not exist.");
            var defaultProfile = nextProfiles.OfType<JsonObject>().First(profile => profile["id"]!.GetValue<string>() == defaultProfileId);
            if (defaultProfile["enabled"]?.GetValue<bool>() == false)
                throw new LauncherCommandException("invalid_args", "The default machine translation profile must be enabled.");
        }

        // Carry existing credentials forward unless the patch replaces or clears them;
        // delete credentials only for profiles that disappear from the new document.
        var previous = LoadSettingsDocument();
        var previousById = ProfileArray(previous)
            .OfType<JsonObject>()
            .ToDictionary(profile => profile["id"]?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal);
        foreach (var node in nextProfiles)
        {
            if (node is not JsonObject next)
                continue;
            var id = next["id"]!.GetValue<string>();
            var stored = new JsonObject();
            if (previousById.TryGetValue(id, out var existing))
                stored = (JsonObject)((existing["credentials"] as JsonObject)?.DeepClone() ?? new JsonObject());

            if (incomingProfiles.OfType<JsonObject>().First(profile => profile["id"]?.GetValue<string>() == id) is { } incoming)
            {
                if (incoming["clearCredentials"] is JsonArray clear)
                {
                    foreach (var field in clear.Select(node => node?.GetValue<string>()))
                    {
                        if (field is not null)
                            stored.Remove(field);
                    }
                }
                if (incoming["credentials"] is JsonObject credentials)
                {
                    foreach (var pair in credentials)
                    {
                        if (pair.Value is JsonValue value && value.TryGetValue<string>(out var secret) && !string.IsNullOrWhiteSpace(secret))
                        {
                            if (secret.Trim().Length > MaxSecretBytes)
                                throw new LauncherCommandException("invalid_args", $"Machine translation credentials must not exceed {MaxSecretBytes} bytes.");
                            stored[pair.Key] = secret.Trim();
                        }
                    }
                }
            }
            next["credentials"] = stored;
        }

        var document = new JsonObject
        {
            ["version"] = 1,
            ["defaultProfileId"] = string.IsNullOrWhiteSpace(defaultProfileId) ? null : defaultProfileId,
            ["profiles"] = nextProfiles,
        };
        SaveSettingsDocument(document);
        return LoadSettingsSnapshot();
    }

    /// <summary>Validates one incoming profile and returns its stored (credential-carrying) form.</summary>
    static JsonObject NormalizeProfile(JsonObject incoming)
    {
        string Required(string field)
        {
            var value = incoming[field]?.GetValue<string>()?.Trim() ?? string.Empty;
            if (value.Length == 0)
                throw new LauncherCommandException("invalid_args", $"Machine translation profile {field} cannot be empty.");
            if (value.Length > MaxFieldBytes)
                throw new LauncherCommandException("invalid_args", $"Machine translation profile {field} exceeds the {MaxFieldBytes} byte limit.");
            return value;
        }

        var id = Required("id");
        var name = Required("name");
        var presetId = incoming["presetId"]?.GetValue<string>() ?? string.Empty;
        var preset = FindPreset(presetId)
            ?? throw new LauncherCommandException("invalid_args", "Unknown machine translation preset.");
        var protocol = incoming["protocol"]?.GetValue<string>() ?? string.Empty;
        if (protocol != preset.Protocol)
            throw new LauncherCommandException("invalid_args", "Machine translation protocol does not match its preset.");
        var baseUrl = ValidateBaseUrl(Required("baseUrl"));

        var allowed = new HashSet<string>(preset.CredentialFields, StringComparer.Ordinal);
        JsonObject? ObjectOrNull(string field) => incoming[field] as JsonObject;
        foreach (var field in ObjectOrNull("credentialEnvironments")?.Select(pair => pair.Key) ?? Enumerable.Empty<string>())
            if (!allowed.Contains(field))
                throw new LauncherCommandException("invalid_args", "Machine translation profile contains an unsupported credential field.");
        foreach (var field in ObjectOrNull("credentials")?.Select(pair => pair.Key) ?? Enumerable.Empty<string>())
            if (!allowed.Contains(field))
                throw new LauncherCommandException("invalid_args", "Machine translation profile contains an unsupported credential field.");
        if (incoming["clearCredentials"] is JsonArray clear && clear.Any(node => node?.GetValue<string>() is { } field && !allowed.Contains(field)))
            throw new LauncherCommandException("invalid_args", "Machine translation profile contains an unsupported credential field.");

        string? OptionalTrimmed(string field) =>
            incoming[field]?.GetValue<string>()?.Trim() is { Length: > 0 } value ? value : null;

        var environments = new JsonObject();
        if (incoming["credentialEnvironments"] is JsonObject incomingEnvironments)
        {
            foreach (var pair in incomingEnvironments)
            {
                var value = pair.Value?.GetValue<string>()?.Trim() ?? string.Empty;
                if (value.Length == 0)
                    continue;
                if (value.Length > 128)
                    throw new LauncherCommandException("invalid_args", "Credential environment names must not exceed 128 bytes.");
                environments[pair.Key] = value;
            }
        }

        return new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["presetId"] = preset.Id,
            ["protocol"] = protocol,
            ["baseUrl"] = baseUrl,
            ["region"] = OptionalTrimmed("region"),
            ["enabled"] = incoming["enabled"]?.GetValue<bool>() ?? true,
            ["defaultSourceLocale"] = OptionalTrimmed("defaultSourceLocale"),
            ["defaultTargetLocale"] = OptionalTrimmed("defaultTargetLocale"),
            ["credentialEnvironments"] = environments,
        };
    }

    static string ValidateBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            throw new LauncherCommandException("invalid_args", "Machine translation profile base URL is invalid.");
        var loopback = url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (url.HostNameType == UriHostNameType.IPv4 && IPAddressIsLoopback(url.Host))
            || url.Host == "[::1]" || url.Host == "::1";
        if (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && loopback))
            throw new LauncherCommandException("invalid_args", "Machine translation endpoints must use HTTPS; HTTP is allowed only for loopback hosts.");
        if (!string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            throw new LauncherCommandException("invalid_args", "Machine translation base URL must not contain credentials, query parameters, or fragments.");
        return value.TrimEnd('/');
    }

    static bool IPAddressIsLoopback(string host)
    {
        return System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
    }

    // --- profile resolution + credentials (mirrors settings.rs resolve_*) ---

    static JsonObject ResolveProfile(string profileId)
    {
        foreach (var node in ProfileArray(LoadSettingsDocument()))
        {
            if (node is not JsonObject profile)
                continue;
            if (string.Equals(profile["id"]?.GetValue<string>(), profileId, StringComparison.Ordinal))
                return profile;
        }
        throw new LauncherCommandException("not_found", $"Machine translation profile '{profileId}' does not exist.");
    }

    static Dictionary<string, string> ResolveCredentials(JsonObject profile)
    {
        var preset = FindPreset(profile["presetId"]?.GetValue<string>() ?? string.Empty)
            ?? throw new LauncherCommandException("invalid_args", "Machine translation preset does not exist.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in preset.CredentialFields)
        {
            var value = StoredCredential(profile, field)
                ?? (profile["credentialEnvironments"] is JsonObject environments
                    && environments[field] is JsonValue nameValue
                    && nameValue.TryGetValue<string>(out var envName)
                    ? EnvironmentValue(envName)
                    : null);
            if (!string.IsNullOrWhiteSpace(value))
                result[field] = value!;
        }
        return result;
    }

    static string RequireProfileId(JsonNode? request)
    {
        var profileId = request?["profileId"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(profileId))
            throw new LauncherCommandException("invalid_args", "A profileId is required.");
        return profileId;
    }

    // --- provider wire adapters (mirrors adapters.rs translate_wire/list_languages) ---

    static string CredentialOf(IReadOnlyDictionary<string, string> credentials, string field)
    {
        if (credentials.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        throw new LauncherCommandException("authentication", $"Machine translation credential `{field}` is not configured.");
    }

    static string Endpoint(JsonObject profile, string suffix)
    {
        var baseUrl = (profile["baseUrl"]?.GetValue<string>() ?? string.Empty).TrimEnd('/');
        return baseUrl + "/" + suffix.TrimStart('/');
    }

    static string LocaleOf(string protocol, string value)
    {
        var normalized = value.Replace('_', '-');
        switch (protocol)
        {
            case "deepl":
                return normalized.Split('-')[0].ToUpperInvariant();
            case "baidu-general":
                return (normalized.ToLowerInvariant() switch
                {
                    "en" or "en-us" => "en",
                    "zh-cn" or "zh-hans" => "zh",
                    "zh-tw" or "zh-hant" => "cht",
                    "ja" or "ja-jp" => "jp",
                    "ko" or "ko-kr" => "kor",
                    var other => other,
                });
            case "tencent-tmt":
                return (normalized.ToLowerInvariant() switch
                {
                    "zh-cn" or "zh-hans" => "zh",
                    "zh-tw" or "zh-hant" => "zh-TW",
                    var other => other,
                });
            default:
                return normalized;
        }
    }

    static string Md5Hex(string value)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    static string Sha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    static byte[] HmacSha256(byte[] key, string value)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    static Dictionary<string, string> TencentHeaders(JsonObject profile, IReadOnlyDictionary<string, string> credentials, string payload)
    {
        var secretId = CredentialOf(credentials, "secret-id");
        var secretKey = CredentialOf(credentials, "secret-key");
        var uri = new Uri(profile["baseUrl"]?.GetValue<string>() ?? string.Empty);
        var host = uri.Host;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        const string action = "BatchTranslate";
        const string service = "tmt";
        var canonicalHeaders = $"content-type:application/json\nhost:{host}\nx-tc-action:{action.ToLowerInvariant()}\n";
        const string signedHeaders = "content-type;host;x-tc-action";
        var canonicalRequest = $"POST\n/\n\n{canonicalHeaders}\n{signedHeaders}\n{Sha256Hex(payload)}";
        var scope = $"{date}/{service}/tc3_request";
        var stringToSign = $"TC3-HMAC-SHA256\n{timestamp}\n{scope}\n{Sha256Hex(canonicalRequest)}";
        var secretDate = HmacSha256(Encoding.UTF8.GetBytes($"TC3{secretKey}"), date);
        var secretService = HmacSha256(secretDate, service);
        var secretSigning = HmacSha256(secretService, "tc3_request");
        var signature = Convert.ToHexString(HmacSha256(secretSigning, stringToSign)).ToLowerInvariant();
        var region = profile["region"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(region))
            throw new LauncherCommandException("invalid_args", "Tencent TMT profile requires a region.");

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = $"TC3-HMAC-SHA256 Credential={secretId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}",
            ["Host"] = host,
            ["X-TC-Action"] = action,
            ["X-TC-Version"] = "2018-03-21",
            ["X-TC-Timestamp"] = timestamp.ToString(CultureInfo.InvariantCulture),
            ["X-TC-Region"] = region!,
        };
    }

    sealed class WireResult
    {
        public required string Text { get; init; }
        public string? DetectedLanguage { get; init; }
    }

    static async Task<JsonNode> SendAsync(Func<HttpRequestMessage> build, string operation)
    {
        const int maxAttempts = 3; //one initial try plus two retries on 429/5xx, mirroring adapters.rs MAX_RETRIES
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        HttpResponseMessage response;
        for (var attempt = 0; ; attempt++)
        {
            response = await client.SendAsync(build()).ConfigureAwait(false);
            if ((response.StatusCode != System.Net.HttpStatusCode.TooManyRequests
                    && (int)response.StatusCode < 500)
                || attempt >= maxAttempts - 1)
                break;
            var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1 << attempt);
            await Task.Delay(retryAfter > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : retryAfter).ConfigureAwait(false);
        }
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string? detail = null;
            try
            {
                var parsed = JsonNode.Parse(body);
                detail = Child(Child(parsed, "error"), "message")?.GetValue<string>() ?? Child(parsed, "message")?.GetValue<string>();
            }
            catch (Exception)
            {
                //non-JSON error bodies fall through to the status reason
            }
            throw new LauncherCommandException("network",
                $"Machine translation provider request failed ({(int)response.StatusCode}): {(detail ?? response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}").Trim().SliceSafe(500)}");
        }
        try
        {
            return JsonNode.Parse(body) ?? throw new LauncherCommandException("network", $"{operation} response is not valid JSON.");
        }
        catch (JsonException ex)
        {
            throw new LauncherCommandException("network", $"{operation} response is not valid JSON: {ex.Message}");
        }
    }

    static async Task<WireResult> TranslateOneAsync(JsonObject profile, IReadOnlyDictionary<string, string> credentials, string source, string target, string text)
    {
        var protocol = profile["protocol"]?.GetValue<string>() ?? string.Empty;
        source = LocaleOf(protocol, source);
        target = LocaleOf(protocol, target);
        JsonNode value;
        switch (protocol)
        {
            case "deepl":
            {
                var form = new List<KeyValuePair<string, string>>
                {
                    new("source_lang", source),
                    new("target_lang", target),
                    new("text", text),
                };
                value = await SendAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(profile, "v2/translate"))
                    {
                        Content = new FormUrlEncodedContent(form),
                    };
                    request.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {CredentialOf(credentials, "api-key")}");
                    return request;
                }, "DeepL translation").ConfigureAwait(false);
                var translations = Child(value, "translations") as JsonArray
                    ?? throw new LauncherCommandException("network", "DeepL response has no translations.");
                return new WireResult
                {
                    Text = Child(translations[0], "text")?.GetValue<string>()
                        ?? throw new LauncherCommandException("network", "DeepL translation has no text."),
                    DetectedLanguage = Child(translations[0], "detected_source_language")?.GetValue<string>(),
                };
            }
            case "google-basic-v2":
            {
                var url = Endpoint(profile, "language/translate/v2") + "?key=" + Uri.EscapeDataString(CredentialOf(credentials, "api-key"));
                var body = JsonSerializer.Serialize(new { q = new[] { text }, source, target, format = "text" });
                value = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }, "Google translation").ConfigureAwait(false);
                var translations = Child(Child(value, "data"), "translations") as JsonArray
                    ?? throw new LauncherCommandException("network", "Google response has no translations.");
                return new WireResult
                {
                    Text = DecodeHtmlEntities(Child(translations[0], "translatedText")?.GetValue<string>()
                        ?? throw new LauncherCommandException("network", "Google translation has no text.")),
                    DetectedLanguage = Child(translations[0], "detectedSourceLanguage")?.GetValue<string>(),
                };
            }
            case "microsoft-v3":
            {
                var url = Endpoint(profile, "translate")
                    + "?api-version=3.0&from=" + Uri.EscapeDataString(source)
                    + "&to=" + Uri.EscapeDataString(target);
                var body = JsonSerializer.Serialize(new[] { new { Text = text } });
                value = await SendAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };
                    request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", CredentialOf(credentials, "api-key"));
                    var region = profile["region"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(region))
                        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", region);
                    return request;
                }, "Microsoft translation").ConfigureAwait(false);
                var items = value as JsonArray ?? throw new LauncherCommandException("network", "Microsoft response is not an array.");
                var firstTranslations = Child(items[0], "translations") as JsonArray;
                return new WireResult
                {
                    Text = (firstTranslations is { Count: > 0 } ? Child(firstTranslations[0], "text") : null)?.GetValue<string>()
                        ?? throw new LauncherCommandException("network", "Microsoft translation has no text."),
                    DetectedLanguage = Child(Child(items[0], "detectedLanguage"), "language")?.GetValue<string>(),
                };
            }
            case "baidu-general":
            {
                var salt = Guid.NewGuid().ToString("N");
                var appId = CredentialOf(credentials, "app-id");
                var sign = Md5Hex($"{appId}{text}{salt}{CredentialOf(credentials, "secret")}");
                var form = new List<KeyValuePair<string, string>>
                {
                    new("q", text),
                    new("from", source),
                    new("to", target),
                    new("appid", appId),
                    new("salt", salt),
                    new("sign", sign),
                };
                value = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Endpoint(profile, "api/trans/vip/translate"))
                {
                    Content = new FormUrlEncodedContent(form),
                }, "Baidu translation").ConfigureAwait(false);
                var results = Child(value, "trans_result") as JsonArray
                    ?? throw new LauncherCommandException("network", "Baidu response has no translations.");
                return new WireResult
                {
                    Text = Child(results[0], "dst")?.GetValue<string>()
                        ?? throw new LauncherCommandException("network", "Baidu translation has no text."),
                    DetectedLanguage = Child(value, "from")?.GetValue<string>(),
                };
            }
            case "tencent-tmt":
            {
                var payload = JsonSerializer.Serialize(new
                {
                    Source = source,
                    Target = target,
                    ProjectId = 0,
                    SourceTextList = new[] { text },
                });
                value = await SendAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, profile["baseUrl"]?.GetValue<string>())
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                    };
                    foreach (var header in TencentHeaders(profile, credentials, payload))
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    return request;
                }, "Tencent translation").ConfigureAwait(false);
                var targetTexts = Child(Child(value, "Response"), "TargetTextList") as JsonArray
                    ?? throw new LauncherCommandException("network", "Tencent response has no translations.");
                return new WireResult
                {
                    Text = targetTexts[0]?.GetValue<string>()
                        ?? throw new LauncherCommandException("network", "Tencent translation is not text."),
                    DetectedLanguage = null,
                };
            }
            case "libre-translate":
            {
                var hasKey = credentials.TryGetValue("api-key", out var key) && !string.IsNullOrWhiteSpace(key);
                var body = JsonSerializer.Serialize(new
                {
                    q = new[] { text },
                    source,
                    target,
                    format = "text",
                    api_key = hasKey ? key : null,
                });
                value = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Endpoint(profile, "translate"))
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }, "LibreTranslate translation").ConfigureAwait(false);
                return new WireResult
                {
                    Text = Child(value, "translatedText")?.GetValue<string>()
                        ?? throw new LauncherCommandException("network", "LibreTranslate response has no translatedText."),
                    DetectedLanguage = Child(Child(value, "detectedLanguage"), "language")?.GetValue<string>(),
                };
            }
            default:
                throw new LauncherCommandException("invalid_args", $"Machine translation protocol '{protocol}' is not supported.");
        }
    }

    static readonly Dictionary<string, string> HtmlEntities = new(StringComparer.Ordinal)
    {
        ["&amp;"] = "&",
        ["&lt;"] = "<",
        ["&gt;"] = ">",
        ["&quot;"] = "\"",
        ["&#39;"] = "'",
        ["&apos;"] = "'",
    };

    static string DecodeHtmlEntities(string value)
    {
        var result = value;
        foreach (var pair in HtmlEntities)
            result = result.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        return result;
    }

    static JsonArray StaticLanguages(string protocol)
    {
        var values = protocol switch
        {
            "baidu-general" => new (string Code, string Name)[]
            {
                ("auto", "Auto"), ("zh", "Chinese Simplified"), ("cht", "Chinese Traditional"),
                ("en", "English"), ("jp", "Japanese"), ("kor", "Korean"),
                ("fra", "French"), ("spa", "Spanish"), ("de", "German"),
            },
            "tencent-tmt" => new (string Code, string Name)[]
            {
                ("auto", "Auto"), ("zh", "Chinese Simplified"), ("zh-TW", "Chinese Traditional"),
                ("en", "English"), ("ja", "Japanese"), ("ko", "Korean"),
                ("fr", "French"), ("es", "Spanish"), ("de", "German"),
            },
            _ => Array.Empty<(string, string)>(),
        };
        var languages = new JsonArray();
        foreach (var (code, name) in values)
        {
            languages.Add(new JsonObject
            {
                ["code"] = code,
                ["name"] = name,
                ["supportsSource"] = true,
                ["supportsTarget"] = code != "auto",
            });
        }
        return languages;
    }

    static async Task<JsonArray> ListLanguagesAsync(string profileId)
    {
        var profile = ResolveProfile(profileId);
        var protocol = profile["protocol"]?.GetValue<string>() ?? string.Empty;
        if (protocol is "baidu-general" or "tencent-tmt")
            return StaticLanguages(protocol);

        var credentials = ResolveCredentials(profile);
        JsonNode value;
        switch (protocol)
        {
            case "deepl":
                value = await SendAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(profile, "v2/languages?type=target"));
                    request.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {CredentialOf(credentials, "api-key")}");
                    return request;
                }, "DeepL languages").ConfigureAwait(false);
                return ParseLanguageArray(value, item => (Child(item, "language")?.GetValue<string>(), Child(item, "name")?.GetValue<string>()));
            case "google-basic-v2":
                value = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
                    Endpoint(profile, "language/translate/v2/languages")
                    + "?key=" + Uri.EscapeDataString(CredentialOf(credentials, "api-key"))
                    + "&target=en"), "Google languages").ConfigureAwait(false);
                return ParseLanguageArray(Child(Child(value, "data"), "languages"), item => (Child(item, "language")?.GetValue<string>(), Child(item, "name")?.GetValue<string>()));
            case "microsoft-v3":
                value = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
                    Endpoint(profile, "languages") + "?api-version=3.0"), "Microsoft languages").ConfigureAwait(false);
                var microsoft = new JsonArray();
                if (Child(value, "translation") is JsonObject translation)
                {
                    foreach (var pair in translation)
                    {
                        microsoft.Add(new JsonObject
                        {
                            ["code"] = pair.Key,
                            ["name"] = Child(pair.Value, "name")?.GetValue<string>() ?? pair.Key,
                            ["supportsSource"] = true,
                            ["supportsTarget"] = true,
                        });
                    }
                }
                return microsoft;
            case "libre-translate":
                value = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Endpoint(profile, "languages")), "LibreTranslate languages").ConfigureAwait(false);
                return ParseLanguageArray(value, item => (Child(item, "code")?.GetValue<string>(), Child(item, "name")?.GetValue<string>()));
            default:
                throw new LauncherCommandException("invalid_args", $"Machine translation protocol '{protocol}' has no language listing.");
        }
    }

    static JsonArray ParseLanguageArray(JsonNode? node, Func<JsonNode?, (string? Code, string? Name)> pick)
    {
        if (node is not JsonArray array)
            throw new LauncherCommandException("network", "Machine translation languages response is invalid.");
        var languages = new JsonArray();
        foreach (var item in array)
        {
            var (code, name) = pick(item);
            if (string.IsNullOrWhiteSpace(code))
                continue;
            languages.Add(new JsonObject
            {
                ["code"] = code,
                ["name"] = name ?? code,
                ["supportsSource"] = true,
                ["supportsTarget"] = true,
            });
        }
        return languages;
    }

    static async Task<JsonObject> TestProfileAsync(string profileId)
    {
        var profile = ResolveProfile(profileId);
        var credentials = ResolveCredentials(profile);
        var protocol = profile["protocol"]?.GetValue<string>() ?? string.Empty;
        var (source, target) = protocol is "baidu-general" or "tencent-tmt"
            ? ("en-US", "zh-CN")
            : ("en-US", "de-DE");

        var started = Stopwatch.StartNew();
        var result = await TranslateOneAsync(profile, credentials, source, target, "Hello").ConfigureAwait(false);
        started.Stop();
        return new JsonObject
        {
            ["latencyMs"] = started.ElapsedMilliseconds,
            ["detectedLanguage"] = result.DetectedLanguage,
        };
    }

    static JsonNode? ExtractRequest(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("request", out var request))
            return JsonNode.Parse(request.GetRawText());
        return null;
    }
}
