using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SMAPIGameLoader.Services;

/// <summary>Shared JSON/file/version helpers ported from the desktop launcher domain.</summary>
internal static class LauncherJsonHelper
{
    /// <summary>Trims + ASCII-lowercases a unique id for case-insensitive matching (manifest.rs normalize_unique_id).</summary>
    public static string NormalizeUniqueId(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    /// <summary>Reads a trimmed non-empty string property; non-string or empty yields null (manifest.rs string_field).</summary>
    public static string? StringField(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        if (!element.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Reads a trimmed non-empty string array property (manifest.rs string_array_field).</summary>
    public static List<string> StringArrayField(JsonElement element, string key)
    {
        var result = new List<string>();
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;

                var text = item.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text))
                    result.Add(text!);
            }
        }

        return result;
    }

    /// <summary>Reads a boolean property with a fallback default.</summary>
    public static bool BoolField(JsonElement element, string key, bool fallback = false)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
            return value.GetBoolean();

        return fallback;
    }

    /// <summary>Parses a JSON file; missing/invalid input yields null (permissive callers must handle null).</summary>
    public static JsonElement? TryParseJsonFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var bytes = File.ReadAllBytes(path);
            try
            {
                using var document = JsonDocument.Parse(bytes);
                return document.RootElement.Clone();
            }
            catch (Exception)
            {
                // Some mod manifests are authored in GBK on Chinese Windows; ReadAllText
                // would silently turn them into replacement glyphs, so retry legacy-encoded.
                var text = DecodeWithLegacyFallback(bytes);
                using var document = JsonDocument.Parse(SanitizeRelaxedJson(text));
                return document.RootElement.Clone();
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Decodes bytes as strict UTF-8, falling back to GB18030 for legacy-encoded files.</summary>
    internal static string DecodeWithLegacyFallback(byte[] bytes)
    {
        try
        {
            return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                return System.Text.Encoding.GetEncoding("GB18030").GetString(bytes);
            }
            catch (Exception)
            {
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
        }
    }

    /// <summary>
    ///     Parses a JSON file, throwing a command exception on invalid JSON. Falls back to
    ///     the desktop json_relaxed sanitization (comments, trailing commas) before failing.
    /// </summary>
    public static JsonElement ParseJsonFile(string path)
    {
        var text = File.ReadAllText(path);
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (Exception strictError)
        {
            try
            {
                using var document = JsonDocument.Parse(SanitizeRelaxedJson(text));
                return document.RootElement.Clone();
            }
            catch (Exception)
            {
                throw new LauncherCommandException("invalid_json", $"Failed to parse JSON file {path}: {strictError.Message}; relaxed parse also failed");
            }
        }
    }

    /// <summary>Strips comments and trailing commas (json_relaxed.rs core rules).</summary>
    public static string SanitizeRelaxedJson(string text)
    {
        if (text.StartsWith('\uFEFF'))
            text = text[1..];

        var builder = new StringBuilder(text.Length);
        var inString = false;
        var escaped = false;
        for (var index = 0; index < text.Length; index += 1)
        {
            var character = text[index];
            if (inString)
            {
                builder.Append(character);
                if (escaped)
                    escaped = false;
                else if (character == '\\')
                    escaped = true;
                else if (character == '"')
                    inString = false;
                continue;
            }

            if (character == '"')
            {
                inString = true;
                builder.Append(character);
                continue;
            }

            if (character == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n')
                    index += 1;
                builder.Append('\n');
                continue;
            }

            if (character == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var blockEnd = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = blockEnd < 0 ? text.Length : blockEnd + 1;
                continue;
            }

            builder.Append(character);
        }

        //trailing commas
        var result = builder.ToString();
        result = new System.Text.RegularExpressions.Regex(",(\\s*[}\\]])").Replace(result, "$1");
        return result;
    }

    /// <summary>Writes pretty JSON with the trailing newline the desktop domain writes.</summary>
    public static void WriteJsonFile(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>
    ///     Recursive JSON object merge (merge_json_values): objects deep-merge over the sorted
    ///     key union; arrays and scalars are replaced wholesale by the preferred side.
    /// </summary>
    public static JsonNode MergeJsonValues(JsonNode existing, JsonNode incoming, bool existingWins)
    {
        if (existing is JsonObject existingObject && incoming is JsonObject incomingObject)
        {
            var merged = new JsonObject();
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var key in existingObject.Select(pair => pair.Key))
                keys.Add(key);
            foreach (var key in incomingObject.Select(pair => pair.Key))
                keys.Add(key);

            foreach (var key in keys)
            {
                var existingValue = existingObject[key];
                var incomingValue = incomingObject[key];
                if (existingValue is not null && incomingValue is not null)
                    merged[key] = MergeJsonValues(existingValue, incomingValue, existingWins);
                else if (existingValue is not null)
                    merged[key] = existingValue.DeepClone();
                else if (incomingValue is not null)
                    merged[key] = incomingValue.DeepClone();
            }

            return merged;
        }

        return (existingWins ? existing : incoming).DeepClone();
    }

    /// <summary>Path conflict de-conflicting: appends " (2)".." (999)" before the extension (unique_path).</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;

        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var parent = Path.GetDirectoryName(path) ?? string.Empty;
        for (var index = 2; index < 1000; index += 1)
        {
            var candidate = Path.Combine(parent, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }

        return path;
    }

    /// <summary>Replaces path-hostile characters with '_' (sanitize_file_name).</summary>
    public static string SanitizeFileName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append("<>:\"/\\|?*".Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }

    public static ulong CurrentTimestampMs()
    {
        return (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>Full byte comparison of two files (files_differ).</summary>
    public static bool FilesDiffer(string leftPath, string rightPath)
    {
        var left = File.ReadAllBytes(leftPath);
        var right = File.ReadAllBytes(rightPath);
        return !left.AsSpan().SequenceEqual(right);
    }

    public static string NormalizeRelativePath(string relativePath)
    {
        return relativePath.Replace('\\', '/');
    }

    /// <summary>
    ///     Version comparison ported from versions.rs: up to 4 numeric dot segments, prerelease
    ///     after the first '-'; stable beats prerelease; '-unofficial' is lowest; numeric tag
    ///     parts compare numerically, otherwise case-insensitive text; unparseable values fall
    ///     back to normalized string inequality.
    /// </summary>
    public static bool VersionIsNewer(string? current, string? latest)
    {
        var currentClean = CleanVersion(current);
        var latestClean = CleanVersion(latest);
        if (currentClean.Length == 0 || latestClean.Length == 0)
            return false;

        var currentParsed = ParseVersion(currentClean);
        var latestParsed = ParseVersion(latestClean);
        if (currentParsed is null || latestParsed is null)
            return currentClean != latestClean;

        return CompareParsedVersions(currentParsed.Value, latestParsed.Value) < 0;
    }

    static string CleanVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[1..];

        var buildIndex = trimmed.IndexOf('+');
        if (buildIndex >= 0)
            trimmed = trimmed[..buildIndex];

        return trimmed.Trim();
    }

    static (ulong[] Parts, string[]? Tags)? ParseVersion(string value)
    {
        var core = value;
        string[]? tags = null;
        var prereleaseIndex = value.IndexOf('-');
        if (prereleaseIndex >= 0)
        {
            core = value[..prereleaseIndex];
            tags = value[(prereleaseIndex + 1)..]
                .Split('.', '-')
                .Where(tag => tag.Length > 0)
                .ToArray();
        }

        var segments = core.Split('.');
        if (segments.Length == 0 || segments.Length > 4)
            return null;

        var parts = new ulong[4];
        for (var index = 0; index < segments.Length; index += 1)
        {
            var segment = segments[index];
            if (segment.Length == 0 || segment.Any(character => character is < '0' or > '9'))
                return null;

            if (!ulong.TryParse(segment, out var parsed))
                return null;

            parts[index] = parsed;
        }

        return (parts, tags);
    }

    static int CompareParsedVersions((ulong[] Parts, string[]? Tags) left, (ulong[] Parts, string[]? Tags) right)
    {
        for (var index = 0; index < 4; index += 1)
        {
            var comparison = left.Parts[index].CompareTo(right.Parts[index]);
            if (comparison != 0)
                return comparison;
        }

        return ComparePrereleaseTags(left.Tags, right.Tags);
    }

    static int ComparePrereleaseTags(string[]? left, string[]? right)
    {
        if (left is null && right is null)
            return 0;
        if (left is null)
            return 1; // stable beats prerelease
        if (right is null)
            return -1;

        var length = Math.Max(left.Length, right.Length);
        for (var index = 0; index < length; index += 1)
        {
            var leftTag = index < left.Length ? left[index] : null;
            var rightTag = index < right.Length ? right[index] : null;
            if (leftTag is null)
                return -1; // shorter prefix loses
            if (rightTag is null)
                return 1;

            var comparison = ComparePrereleaseTag(leftTag, rightTag);
            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    static int ComparePrereleaseTag(string left, string right)
    {
        var bothNumeric = left.All(character => character is >= '0' and <= '9') && left.Length > 0
            && right.All(character => character is >= '0' and <= '9') && right.Length > 0;
        if (bothNumeric)
        {
            var comparison = ulong.Parse(left).CompareTo(ulong.Parse(right));
            return comparison != 0 ? comparison : string.Compare(left, right, StringComparison.Ordinal);
        }

        if (left.Equals("unofficial", StringComparison.OrdinalIgnoreCase))
            return -1;
        if (right.Equals("unofficial", StringComparison.OrdinalIgnoreCase))
            return 1;

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
