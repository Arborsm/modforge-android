using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SMAPIGameLoader.Services;

// Nexus Mods wire contracts (camelCase), ported from the desktop nexusmods/launcher domains.
// Fields marked null-on-write mirror the Rust skip_serializing_if attributes.

public sealed class NexusCatalogFacetEntry
{
    public string Name { get; set; } = string.Empty;
    public long Count { get; set; }
}

public sealed class NexusCatalogFacets
{
    public List<NexusCatalogFacetEntry> Categories { get; set; } = new();
    public List<NexusCatalogFacetEntry> Languages { get; set; } = new();
    public List<NexusCatalogFacetEntry> Tags { get; set; } = new();
}

public sealed class NexusCatalogResult
{
    public long ModId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? Author { get; set; }
    public string? Uploader { get; set; }
    public string ModUrl { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public string? Category { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
    public long? Downloads { get; set; }
    public long? Endorsements { get; set; }
    public long? FileSize { get; set; }
    public bool UpdateAvailable { get; set; }
}

public sealed class NexusCatalogPageResult
{
    public long Page { get; set; } = 1;
    public long PageSize { get; set; } = 20;
    public long TotalCount { get; set; }
    public bool HasMore { get; set; }
    public NexusCatalogFacets Facets { get; set; } = new();
    public List<NexusCatalogResult> Results { get; set; } = new();
}

public sealed class NexusRemoteModRequirement
{
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string? Url { get; set; }
    public long? ModId { get; set; }
    public bool External { get; set; }
}

public sealed class NexusRemoteModFile
{
    public long? FileId { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Category { get; set; }
    public string? UploadedAt { get; set; }
    public string? Description { get; set; }
    public long? UniqueDownloads { get; set; }
    public long? TotalDownloads { get; set; }
    public bool? ManagerDownloadEnabled { get; set; }
    public string? Uid { get; set; }
    public long? Size { get; set; }
    public long? SizeBytes { get; set; }
    public bool Primary { get; set; }
    public bool? Scanned { get; set; }
    public string? ScanStatus { get; set; }
    public List<string> Changelog { get; set; } = new();
    public string? ArchiveType { get; set; }
}

public sealed class NexusRemoteModDetail
{
    public long ModId { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool Unavailable { get; set; }
    public string? UnavailableReason { get; set; }
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string ModUrl { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public List<string> GalleryImages { get; set; } = new();
    public string? UpdatedAt { get; set; }
    public long? FileSize { get; set; }
    public string? Category { get; set; }
    public long? Downloads { get; set; }
    public long? Endorsements { get; set; }
    public List<string> Tags { get; set; } = new();
    public bool? DirectDownloadEnabled { get; set; }
    public bool? SupportsVortex { get; set; }
    public long? PrimaryFileId { get; set; }
    public string? PrimaryFileName { get; set; }
    public string? PrimaryFileVersion { get; set; }
    public string? PrimaryFileCategory { get; set; }
    public long? PrimaryFileSize { get; set; }
    public long? PrimaryFileSizeBytes { get; set; }
    public bool? PrimaryFileScanned { get; set; }
    public string? PrimaryFileScanStatus { get; set; }
    public List<string> PrimaryFileChangelog { get; set; } = new();
    public string? RequiredLoader { get; set; }
    public string? GameVersion { get; set; }
    public string? ArchiveType { get; set; }
    public string? UpdateRisk { get; set; }
    public List<NexusRemoteModRequirement> Requirements { get; set; } = new();
    public List<NexusRemoteModFile> Files { get; set; } = new();
}

public sealed class NexusUpdateChangelogResult
{
    public long ModId { get; set; }
    public string? Version { get; set; }
    public string? Changelog { get; set; }
}

public sealed class NexusUpdateSummary
{
    public long ModId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? CurrentVersion { get; set; }
    public string LatestVersion { get; set; } = string.Empty;
    public string AbsolutePath { get; set; } = string.Empty;
    public string ModUrl { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public string? UpdatedAt { get; set; }
    public long? FileSize { get; set; }
}

public sealed class NexusUpdatesResult
{
    public string ModsPath { get; set; } = string.Empty;
    public ulong CheckedAtMs { get; set; }
    public bool IsComplete { get; set; } = true;
    public List<NexusUpdateSummary> Updates { get; set; } = new();
}

public sealed class NexusDownloadResult
{
    public long ModId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ArchivePath { get; set; } = string.Empty;
    public bool Installed { get; set; }
    public string? InstalledTargetPath { get; set; }
    public bool ManualDownloadPageOpened { get; set; }
}

public sealed class NexusSuppressedUpdateModIdsResult
{
    public string ModsPath { get; set; } = string.Empty;
    public List<long> ModIds { get; set; } = new();
}

public sealed class NexusResolveImageResult
{
    public string SourceUrl { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string MimeType { get; set; } = "image/jpeg";
}

public sealed class NexusRouteSnapshot
{
    public string RouteId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string Status { get; set; } = "loading";
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public bool Available { get; set; } = true;
    public long? LatencyMs { get; set; }
    public string Message { get; set; } = "loading";
}

public sealed class NexusDiagnosticsResult
{
    public List<NexusRouteSnapshot> Routes { get; set; } = new();
}

public sealed class NexusValidateApiKeyResult
{
    public string UserName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string? ProfileUrl { get; set; }
    public bool IsPremium { get; set; }
    public bool? IsLifetimePremium { get; set; }
    public long? DailyRemaining { get; set; }
    public long? HourlyRemaining { get; set; }
    public long? DailyResetAt { get; set; }
    public long? HourlyResetAt { get; set; }
}

// --- REST v1 payloads (snake_case upstream) ---

public sealed class NexusRestFile
{
    [JsonPropertyName("file_id")] public long? FileId { get; set; }
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("category_id")] public long? CategoryId { get; set; }
    [JsonPropertyName("category_name")] public string? CategoryName { get; set; }
    [JsonPropertyName("is_primary")] public bool? IsPrimary { get; set; }
    [JsonPropertyName("uploaded_timestamp")] public long? UploadedTimestamp { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

public sealed class NexusRestFilesPayload
{
    [JsonPropertyName("files")] public List<NexusRestFile> Files { get; set; } = new();
}

public sealed class NexusRestDownloadLink
{
    [JsonPropertyName("URI")] public string? Uri { get; set; }
}

public sealed class NexusRestValidatePayload
{
    [JsonPropertyName("user_id")] public long? UserId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("is_premium")] public bool? IsPremium { get; set; }
    [JsonPropertyName("is_lifetime_premium")] public bool? IsLifetimePremium { get; set; }
    [JsonPropertyName("profile_url")] public string? ProfileUrl { get; set; }
}

// --- JSON element access helpers for GraphQL payloads ---

internal static class NexusJson
{
    public static string? Str(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    public static long? Num(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        return NumValue(value);
    }

    public static long? NumValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetInt64(out var parsed) ? parsed : (long)value.GetDouble();
            case JsonValueKind.String:
                return long.TryParse(value.GetString(), out var fromString) ? fromString : null;
            default:
                return null;
        }
    }

    public static bool? Bool(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                return NumValue(value) != 0;
            default:
                return null;
        }
    }

    public static JsonElement? Obj(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind == JsonValueKind.Object || value.ValueKind == JsonValueKind.Array ? value.Clone() : null;
    }

    public static List<JsonElement> Arr(JsonElement element, string property)
    {
        var result = new List<JsonElement>();
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array)
            result.AddRange(value.EnumerateArray());

        return result;
    }
}
