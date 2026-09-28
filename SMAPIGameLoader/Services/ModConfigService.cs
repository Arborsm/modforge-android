using SMAPIGameLoader.Launcher;
using SMAPIGameLoader.Tool;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Services;

/// <summary>
///     Mod config form building over pure JSON sources: the Content Patcher ConfigSchema
///     (manifest.json / content.json), GMCM-compatible assets/options.json and config.json
///     leftovers, plus i18n label translations. This is the Android port of the desktop
///     mod_config schema.rs pure JSON path; the .NET GMCM probe does not exist here, so
///     probeStatus is always "not-run" (parsing disabled) or "unavailable" (parsing enabled
///     but no probe), matching the desktop fallback warnings.
/// </summary>
public sealed class ModConfigService
{
    const string ManifestFileName = "manifest.json";
    const string ConfigFileName = "config.json";
    const string ContentFileName = "content.json";

    // --- commands ---

    public Task<JsonElement?> LoadLauncherModConfigAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(() =>
        {
            var parsed = LibraryService.Deserialize(request, LauncherJsonContext.Default.LoadLauncherModConfigRequest);
            var result = LoadModConfig(parsed.ModPath, parsed.Locale);
            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.LauncherModConfigResult);
        });
    }

    public Task<JsonElement?> SaveLauncherModConfigAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(() =>
        {
            var parsed = LibraryService.Deserialize(request, LauncherJsonContext.Default.SaveLauncherModConfigRequest);
            SaveModConfig(parsed);
            var result = LoadModConfig(parsed.ModPath, parsed.Locale);
            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.LauncherModConfigResult);
        });
    }

    // --- load ---

    LauncherModConfigResult LoadModConfig(string modPath, string? locale)
    {
        var root = CanonicalModRoot(modPath);
        var configPath = GuardedConfigPath(root);
        var configExists = File.Exists(configPath);
        var currentConfig = ReadConfigObject(configPath);

        var warnings = new List<string>();
        var translations = LoadTranslations(root, locale);
        var unresolvedKeys = new SortedSet<string>(StringComparer.Ordinal);

        var fields = new List<LauncherModConfigField>();
        var knownKeys = new HashSet<string>(StringComparer.Ordinal);

        // (a) Content Patcher ConfigSchema from manifest.json, else content.json
        var cpSchema = ExtractCpSchema(root);
        if (cpSchema is not null)
            AddFieldsFromCpSchema(fields, knownKeys, cpSchema.Value, currentConfig, translations, unresolvedKeys);

        // (b) GMCM-compatible assets/options.json
        AddFieldsFromOptionsSchema(fields, knownKeys, root, currentConfig, translations, unresolvedKeys, warnings);

        // (c) leftover config.json keys
        AddFieldsFromConfigJson(fields, knownKeys, currentConfig);

        ApplyLocalization(fields, translations, unresolvedKeys);
        ApplyUiHintInference(fields);
        InferMissingDefaults(fields);
        if (unresolvedKeys.Count > 0)
            warnings.Add("Unresolved config translation keys: " + string.Join(", ", unresolvedKeys.Take(12)));

        bool gmcmParsingEnabled = LauncherRuntimeService.LoadOrCreateSettings().GmcmParsingEnabled;
        string probeStatus;
        if (!gmcmParsingEnabled)
        {
            probeStatus = "not-run";
        }
        else
        {
            //The .NET probe cannot run on Android; pure JSON parsing serves as the fallback.
            probeStatus = "unavailable";
            warnings.Add("GMCM probe is not bundled yet; falling back to Content Patcher/config.json parsing.");
            if (fields.Any(field => field.Source == "config-json"))
                warnings.Add("GMCM probe did not expose structured options; config.json keys were parsed as editable fallback fields.");
        }

        fields.Sort((left, right) =>
        {
            var section = string.CompareOrdinal(left.Section ?? string.Empty, right.Section ?? string.Empty);
            if (section != 0)
                return section;
            var label = string.CompareOrdinal(left.Label, right.Label);
            return label != 0 ? label : string.CompareOrdinal(left.Key, right.Key);
        });

        var schemaSources = new List<string>();
        foreach (var field in fields)
        {
            if (!schemaSources.Contains(field.Source))
                schemaSources.Add(field.Source);
        }

        return new LauncherModConfigResult
        {
            ModPath = root,
            ConfigPath = configPath,
            ConfigExists = configExists,
            Fields = fields,
            SchemaSources = schemaSources,
            Warnings = warnings,
            ProbeStatus = probeStatus,
        };
    }

    // --- save ---

    void SaveModConfig(SaveLauncherModConfigRequest request)
    {
        var root = CanonicalModRoot(request.ModPath);
        var configPath = GuardedConfigPath(root);
        var config = ReadConfigObject(configPath);

        foreach (var pair in request.Values.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var key = pair.Key.Trim();
            if (key.Length == 0)
                continue;

            var caseMatches = config
                .Select(item => item.Key)
                .Where(existing => string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var targetKey = caseMatches.Contains(key) ? key : caseMatches.FirstOrDefault() ?? key;
            foreach (var match in caseMatches.Where(match => match != targetKey))
                config.Remove(match);

            config[targetKey] = pair.Value.Deserialize<JsonNode>() ?? (JsonNode)JsonValue.Create((string?)null)!;
        }

        LauncherJsonHelper.WriteJsonFile(configPath, config);
    }

    // --- roots and guards ---

    static string CanonicalModRoot(string modPath)
    {
        var trimmed = modPath.Trim().Trim('"');
        var full = Path.GetFullPath(trimmed);
        if (!Directory.Exists(full))
            throw new LauncherCommandException("not_found", $"Launcher mod path {full} is not a directory.");
        if (!File.Exists(Path.Combine(full, ManifestFileName)))
            throw new LauncherCommandException("not_found", $"Launcher mod path {full} does not contain manifest.json.");

        return full;
    }

    static string GuardedConfigPath(string root)
    {
        var configPath = Path.GetFullPath(Path.Combine(root, ConfigFileName));
        if (!configPath.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new LauncherCommandException("invalid_path", "Launcher mod config path resolves outside the mod directory.");

        return configPath;
    }

    static JsonObject ReadConfigObject(string configPath)
    {
        if (!File.Exists(configPath))
            return new JsonObject();

        var parsed = LauncherJsonHelper.ParseJsonFile(configPath);
        if (parsed.ValueKind != JsonValueKind.Object)
            throw new LauncherCommandException("invalid_json", $"{configPath} must be a JSON object.");

        return JsonNode.Parse(parsed.GetRawText()) as JsonObject ?? new JsonObject();
    }

    // --- (a) Content Patcher schema ---

    static JsonElement? ExtractCpSchema(string root)
    {
        var manifest = LauncherJsonHelper.TryParseJsonFile(Path.Combine(root, ManifestFileName));
        if (manifest is not null
            && manifest.Value.ValueKind == JsonValueKind.Object
            && manifest.Value.TryGetProperty("ConfigSchema", out var manifestSchema)
            && manifestSchema.ValueKind == JsonValueKind.Object)
        {
            return manifestSchema;
        }

        var content = LauncherJsonHelper.TryParseJsonFile(Path.Combine(root, ContentFileName));
        if (content is not null
            && content.Value.ValueKind == JsonValueKind.Object
            && content.Value.TryGetProperty("ConfigSchema", out var contentSchema)
            && contentSchema.ValueKind == JsonValueKind.Object)
        {
            return contentSchema;
        }

        return null;
    }

    void AddFieldsFromCpSchema(
        List<LauncherModConfigField> fields,
        HashSet<string> knownKeys,
        JsonElement schema,
        JsonObject currentConfig,
        Dictionary<string, string> translations,
        SortedSet<string> unresolvedKeys)
    {
        foreach (var property in schema.EnumerateObject())
        {
            var key = property.Name.Trim();
            if (key.Length == 0 || !knownKeys.Add(LauncherJsonHelper.NormalizeUniqueId(key)))
                continue;

            var definition = property.Value;
            var allowValues = ParseAllowValues(definition.TryGetProperty("AllowValues", out var allowElement) ? allowElement : (JsonElement?)null);
            var allowMultiple = LauncherJsonHelper.BoolField(definition, "AllowMultiple");
            var defaultValue = definition.TryGetProperty("Default", out var defaultElement) && defaultElement.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null
                ? (JsonElement?)defaultElement.Clone()
                : null;

            var value = TryGetCaseInsensitive(currentConfig, key) is { } existingValue
                ? existingValue.Clone()
                : defaultValue?.Clone() ?? JsonSerializer.SerializeToElement((string?)null);

            var fieldType = allowMultiple
                ? "string-array"
                : allowValues.Count > 0 && value.ValueKind == JsonValueKind.Array
                    ? "string-array"
                    : InferFieldType(value);

            fields.Add(new LauncherModConfigField
            {
                Key = key,
                Label = LauncherJsonHelper.StringField(definition, "Name") ?? key,
                Description = LauncherJsonHelper.StringField(definition, "Description"),
                Section = LauncherJsonHelper.StringField(definition, "Section"),
                FieldType = fieldType,
                UiHint = null,
                Value = value,
                DefaultValue = defaultValue,
                AllowValues = allowValues,
                AllowBlank = LauncherJsonHelper.BoolField(definition, "AllowBlank"),
                AllowMultiple = allowMultiple,
                Editable = true,
                Source = "content-patcher",
            });
        }
    }

    // --- (b) options schema ---

    void AddFieldsFromOptionsSchema(
        List<LauncherModConfigField> fields,
        HashSet<string> knownKeys,
        string root,
        JsonObject currentConfig,
        Dictionary<string, string> translations,
        SortedSet<string> unresolvedKeys,
        List<string> warnings)
    {
        var optionsPath = Path.Combine(root, "assets", "options.json");
        if (!File.Exists(optionsPath))
            return;

        JsonElement parsed;
        try
        {
            parsed = LauncherJsonHelper.ParseJsonFile(optionsPath);
        }
        catch (LauncherCommandException)
        {
            warnings.Add($"{optionsPath} is not an options schema array.");
            return;
        }

        if (parsed.ValueKind != JsonValueKind.Array)
        {
            warnings.Add($"{optionsPath} is not an options schema array.");
            return;
        }

        string? currentSection = null;
        string? pendingSubtitle = null;
        foreach (var item in parsed.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var optionType = LauncherJsonHelper.StringField(item, "Type") ?? string.Empty;
            var name = LauncherJsonHelper.StringField(item, "Name");
            var loweredType = optionType.ToLowerInvariant();

            if (loweredType == "page")
            {
                var pageId = LauncherJsonHelper.StringField(item, "PageID") ?? name;
                if (pageId is not null && !pageId.All(character => character is >= '0' and <= '9'))
                    currentSection = Translate(translations, $"GMCM.PageTitle.{pageId}") ?? HumanizeConfigKey(pageId);
                pendingSubtitle = null;
                continue;
            }

            if (loweredType == "pagelink")
                continue;

            if (loweredType == "subtitle")
            {
                if (name is not null)
                    pendingSubtitle = Translate(translations, $"GMCM.Title.{name}.Name") ?? HumanizeConfigKey(name);
                continue;
            }

            if (name is null)
                continue;
            if (!knownKeys.Add(LauncherJsonHelper.NormalizeUniqueId(name)))
                continue;

            var (fieldType, uiHint) = FieldTypeFromOptionSchema(item, loweredType);
            if (fieldType is null)
                continue;

            var defaultValue = ParseSchemaDefault(item.TryGetProperty("Default", out var defaultElement) ? defaultElement : (JsonElement?)null, fieldType);
            var value = TryGetCaseInsensitive(currentConfig, name) is { } existingValue
                ? existingValue.Clone()
                : defaultValue?.Clone() ?? JsonSerializer.SerializeToElement((string?)null);

            var allowValues = ParseAllowValues(item.TryGetProperty("AllowedValues", out var allowElement) ? allowElement : (JsonElement?)null);
            if (allowValues.Count == 0 && loweredType is "bool" or "boolean" && fieldType == "string")
            {
                allowValues.Add(JsonSerializer.SerializeToElement("True"));
                allowValues.Add(JsonSerializer.SerializeToElement("False"));
            }

            fields.Add(new LauncherModConfigField
            {
                Key = name,
                Label = pendingSubtitle ?? Translate(translations, $"GMCM.Options.{name}.Name") ?? HumanizeConfigKey(name),
                Description = ResolveDescription(item, name, translations, unresolvedKeys),
                Section = currentSection,
                FieldType = fieldType,
                UiHint = uiHint,
                Value = value,
                DefaultValue = defaultValue,
                AllowValues = allowValues,
                AllowBlank = false,
                AllowMultiple = false,
                Editable = true,
                Source = "dll-static",
            });
            pendingSubtitle = null;
        }
    }

    static (string? FieldType, string? UiHint) FieldTypeFromOptionSchema(JsonElement item, string loweredType)
    {
        switch (loweredType)
        {
            case "bool" or "boolean":
                return LauncherJsonHelper.StringField(item, "Default") is not null ? ("string", null) : ("boolean", null);
            case "int" or "integer":
                return ("integer", null);
            case "float" or "double" or "number":
                return ("number", null);
            case "string[]" or "strings" or "string-array" or "items" or "item-list" or "itemlist" or "item-id-list":
                return ("string-array", "item-list");
            case "keybind-list" or "keybindlist":
                return ("string-array", "keybind-list");
            case "string" or "text":
                return ("string", null);
            case "color" or "colour":
                return ("string", "color");
            case "keybind" or "key":
                return ("string", "keybind");
            case "item":
            case "item-id":
                return ("string", "item");
            default:
                return (null, null); //unknown schema types are dropped
        }
    }

    static string? ResolveDescription(JsonElement item, string key, Dictionary<string, string> translations, SortedSet<string> unresolvedKeys)
    {
        var toolTipKey = LauncherJsonHelper.StringField(item, "ToolTipKey");
        if (toolTipKey is not null)
        {
            var translated = Translate(translations, toolTipKey);
            if (translated is not null)
                return translated;
        }

        var inferred = Translate(translations, $"GMCM.Options.{key}.ToolTip");
        if (inferred is not null)
            return inferred;

        if (toolTipKey is not null)
            return toolTipKey;

        return null;
    }

    // --- (c) config.json leftovers ---

    static void AddFieldsFromConfigJson(List<LauncherModConfigField> fields, HashSet<string> knownKeys, JsonObject currentConfig)
    {
        foreach (var property in currentConfig)
        {
            var key = property.Key.Trim();
            if (key.Length == 0 || !knownKeys.Add(LauncherJsonHelper.NormalizeUniqueId(key)))
                continue;

            fields.Add(new LauncherModConfigField
            {
                Key = key,
                Label = key,
                Description = null,
                Section = null,
                FieldType = InferFieldType(property.Value is null ? JsonSerializer.SerializeToElement<JsonElement?>(null) : JsonSerializer.SerializeToElement(property.Value)),
                UiHint = null,
                Value = property.Value is null ? JsonSerializer.SerializeToElement<JsonElement?>(null) : JsonSerializer.SerializeToElement(property.Value),
                DefaultValue = null,
                AllowValues = new List<JsonElement>(),
                AllowBlank = false,
                AllowMultiple = false,
                Editable = true,
                Source = "config-json",
            });
        }
    }

    // --- shared field helpers ---

    static JsonElement? TryGetCaseInsensitive(JsonObject source, string key)
    {
        foreach (var property in source)
        {
            if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
                return property.Value is null ? JsonSerializer.SerializeToElement<JsonElement?>(null) : JsonSerializer.SerializeToElement(property.Value);
        }

        return null;
    }

    static List<JsonElement> ParseAllowValues(JsonElement? allowElement)
    {
        var values = new List<JsonElement>();
        if (allowElement is null)
            return values;

        var element = allowElement.Value;
        if (element.ValueKind == JsonValueKind.Array)
        {
            values.AddRange(element.EnumerateArray().Select(item => item.Clone()));
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            values.AddRange(element
                .GetString()!
                .Split(',')
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Select(value => JsonSerializer.SerializeToElement(value)));
        }
        else if (element.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            values.Add(element.Clone());
        }

        return values;
    }

    static JsonElement? ParseSchemaDefault(JsonElement? defaultElement, string fieldType)
    {
        if (defaultElement is null)
            return null;

        var element = defaultElement.Value;
        if (fieldType == "boolean" && element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.SerializeToElement(true);
            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.SerializeToElement(false);
            return element.Clone();
        }

        if (fieldType == "integer" && element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out var intValue))
            return JsonSerializer.SerializeToElement(intValue);
        if (fieldType == "number" && element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), out var doubleValue))
            return JsonSerializer.SerializeToElement(doubleValue);

        return element.Clone();
    }

    static string InferFieldType(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
            case JsonValueKind.False:
                return "boolean";
            case JsonValueKind.Number:
            {
                var raw = value.GetRawText();
                return raw.Contains('.') || raw.Contains('e') || raw.Contains('E') ? "number" : "integer";
            }
            case JsonValueKind.String:
                return "string";
            case JsonValueKind.Array:
                return value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String) ? "string-array" : "object";
            case JsonValueKind.Object:
                return "object";
            default:
                return "unknown";
        }
    }

    static void InferMissingDefaults(List<LauncherModConfigField> fields)
    {
        foreach (var field in fields)
        {
            if (field.DefaultValue is null && field.Value.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                field.DefaultValue = field.Value.Clone();
        }
    }

    // --- localization + heuristics ---

    static void ApplyLocalization(
        List<LauncherModConfigField> fields,
        Dictionary<string, string> translations,
        SortedSet<string> unresolvedKeys)
    {
        foreach (var field in fields)
        {
            var label = Translate(translations, field.Label);
            if (label is not null)
            {
                field.Label = label;
            }
            else if (LooksLikeTranslationKey(field.Label))
            {
                unresolvedKeys.Add(field.Label);
                field.Label = HumanizeConfigKey(field.Key);
            }

            if (field.Description is { } description)
            {
                var translatedDescription = Translate(translations, description);
                if (translatedDescription is not null)
                {
                    field.Description = translatedDescription;
                }
                else if (LooksLikeTranslationKey(description))
                {
                    unresolvedKeys.Add(description);
                    field.Description = null;
                }
            }

            if (field.Section is { } section)
            {
                var translatedSection = Translate(translations, section) ?? Translate(translations, $"GMCM.PageTitle.{section}");
                if (translatedSection is not null)
                {
                    field.Section = translatedSection;
                }
                else if (section.All(character => character is >= '0' and <= '9'))
                {
                    field.Section = null;
                }
                else if (LooksLikeTranslationKey(section))
                {
                    unresolvedKeys.Add(section);
                    field.Section = null;
                }
            }
        }
    }

    static void ApplyUiHintInference(List<LauncherModConfigField> fields)
    {
        foreach (var field in fields)
        {
            if (field.UiHint is not null)
                continue;

            var key = field.Key.ToLowerInvariant();
            if (field.FieldType is "string" or "object" && LooksLikeColorField(field, key))
                field.UiHint = "color";
            else if (field.FieldType == "string" && (key.Contains("keybind") || key.Contains("hotkey") || key.EndsWith("keys") || key.EndsWith("key")))
                field.UiHint = "keybind";
            else if (key == "item" || key == "items" || key.EndsWith("itemid") || key.EndsWith("itemids") || key.EndsWith("itemname") || key.EndsWith("itemnames"))
                field.UiHint = key.EndsWith("s") ? "item-list" : "item";
        }
    }

    static bool LooksLikeColorField(LauncherModConfigField field, string lowerKey)
    {
        if (field.Value.ValueKind == JsonValueKind.String && field.Value.GetString()?.StartsWith('#') == true)
            return true;
        if (field.Value.ValueKind == JsonValueKind.Object)
        {
            var raw = field.Value.GetRawText();
            if (raw.Contains("\"r\"") && raw.Contains("\"g\"") && raw.Contains("\"b\""))
                return true;
        }

        return lowerKey.EndsWith("color") || lowerKey.EndsWith("colour");
    }

    static bool LooksLikeTranslationKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
            return false;
        if (!value.Contains('.'))
            return false;

        return value.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    static string HumanizeConfigKey(string key)
    {
        var builder = new System.Text.StringBuilder(key.Length + 4);
        var previousLower = false;
        foreach (var character in key)
        {
            if (character is '_' or '-')
            {
                builder.Append(' ');
                previousLower = false;
                continue;
            }

            if (char.IsUpper(character) && previousLower && builder.Length > 0 && builder[^1] != ' ')
                builder.Append(' ');

            builder.Append(character);
            previousLower = char.IsLower(character) || char.IsDigit(character);
        }

        return builder.ToString().Trim();
    }

    /// <summary>Merges i18n candidate files (default, en, language, locale) in override order.</summary>
    static Dictionary<string, string> LoadTranslations(string root, string? locale)
    {
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        var fullLocale = (locale ?? string.Empty).Trim().Replace('_', '-');
        var language = fullLocale.Split('-').FirstOrDefault() ?? string.Empty;
        if (language.Length == 0)
            language = "en";

        var candidates = new[] { "default", "en", language, fullLocale };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (candidate.Length == 0 || !seen.Add(candidate))
                continue;

            var candidatePath = Path.Combine(root, "i18n", candidate + ".json");
            var parsed = LauncherJsonHelper.TryParseJsonFile(candidatePath);
            if (parsed is null || parsed.Value.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var property in parsed.Value.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    continue;

                var value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    translations[property.Name.Trim()] = value!.Trim();
            }
        }

        return translations;
    }

    static string? Translate(Dictionary<string, string> translations, string key)
    {
        var trimmed = key.Trim();
        return translations.TryGetValue(trimmed, out var value) ? value : null;
    }
}
