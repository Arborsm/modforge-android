using SMAPIGameLoader.Launcher;
using SMAPIGameLoader.Tool;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Services;

/// <summary>
///     Nexus Mods commands, first half: catalog search (anonymous listing + credentialed
///     fallback) and remote mod detail over the public v2 GraphQL API. HTTP access lives in
///     <see cref="NexusClient"/>; updates/images/downloads live in the sibling partials.
/// </summary>
public sealed partial class NexusService
{
    const string CatalogGraphqlQuery = """
        query CatalogMods($filter: ModsFilter, $sort: [ModsSort!], $offset: Int, $count: Int) {
          mods(filter: $filter, sort: $sort, offset: $offset, count: $count) {
            totalCount
            nodes {
              modId
              name
              summary
              pictureUrl
              createdAt
              downloads
              endorsements
              fileSize
              modCategory {
                name
              }
              updatedAt
              uploader {
                name
              }
            }
          }
        }
        """;

    const string PublicCatalogGraphqlQuery = """
        query ModsListing($count: Int = 0, $facets: ModsFacet, $filter: ModsFilter, $offset: Int, $postFilter: ModsFilter, $sort: [ModsSort!]) {
          mods(
            count: $count
            facets: $facets
            filter: $filter
            offset: $offset
            postFilter: $postFilter
            sort: $sort
            viewUserBlockedContent: false
          ) {
            facetsData
            nodes {
              adultContent
              createdAt
              downloads
              endorsements
              fileSize
              game {
                domainName
                id
                name
              }
              modCategory {
                categoryId
                name
              }
              modId
              name
              status
              summary
              thumbnailUrl
              thumbnailBlurredUrl
              uid
              updatedAt
              uploader {
                avatar
                memberId
                name
              }
              viewerDownloaded
              viewerEndorsed
              viewerTracked
              viewerUpdateAvailable
              viewerIsBlocked
            }
            totalCount
          }
        }
        """;

    const string PublicModDetailGraphqlQuery = """
        query LauncherPublicModDetail($gameId: ID!, $modId: ID!) {
          mod(gameId: $gameId, modId: $modId) {
            modId
            name
            summary
            description
            category
            directDownloadEnabled
            supportsVortex
            downloads
            endorsements
            fileSize
            version
            pictureUrl
            thumbnailUrl
            author
            modCategory {
              name
            }
            tags {
              name
            }
            modRequirements {
              nexusRequirements(offset: 0, count: 8) {
                nodes {
                  modId
                  modName
                  notes
                  url
                  externalRequirement
                }
              }
              dlcRequirements {
                notes
                gameExpansion {
                  name
                }
              }
            }
            updatedAt
            uploader {
              name
            }
          }
        }
        """;

    const string PublicModDetailWithFilesGraphqlQuery = """
        query LauncherPublicModDetail($gameId: ID!, $modId: ID!) {
          mod(gameId: $gameId, modId: $modId) {
            modId
            name
            summary
            description
            category
            directDownloadEnabled
            supportsVortex
            downloads
            endorsements
            fileSize
            version
            pictureUrl
            thumbnailUrl
            author
            modCategory {
              name
            }
            tags {
              name
            }
            modRequirements {
              nexusRequirements(offset: 0, count: 8) {
                nodes {
                  modId
                  modName
                  notes
                  url
                  externalRequirement
                }
              }
              dlcRequirements {
                notes
                gameExpansion {
                  name
                }
              }
            }
            updatedAt
            uploader {
              name
            }
          }
          modFiles(gameId: $gameId, modId: $modId) {
            category
            changelogText
            date
            description
            fileId
            manager
            name
            primary
            scanned
            scannedV2
            size
            sizeInBytes
            totalDownloads
            uCount
            uid
            uniqueDownloads
            requirementsAlert
            uri
            version
          }
        }
        """;

    readonly NexusClient _client;

    public NexusService()
    {
        _client = new NexusClient(LauncherRuntimeService.LoadOrCreateSettings().NexusApiKey);
    }

    static string? TrimOrNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    // --- catalog search ---

    public Task<JsonElement?> SearchLauncherCatalogAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(async () =>
        {
            var req = LibraryService.Deserialize(request, LauncherJsonContext.Default.NexusSearchRequest);
            var page = Math.Max(req.Page ?? 1, 1);
            var pageSize = Math.Clamp(req.PageSize ?? 20, 20, 80);
            var sort = TrimOrNull(req.Sort) ?? "newest";
            var ascending = req.Ascending ?? false;
            var direction = ascending ? "ASC" : "DESC";

            var query = TrimOrNull(req.Query);
            var titleQuery = TrimOrNull(req.TitleQuery);
            var descriptionQuery = TrimOrNull(req.DescriptionQuery);
            var authorQuery = TrimOrNull(req.AuthorQuery);
            var uploaderQuery = TrimOrNull(req.UploaderQuery);
            var category = TrimOrNull(req.Category);
            var language = TrimOrNull(req.Language);
            var tagsInclude = TrimOrNull(req.TagsInclude);
            var tagsExclude = TrimOrNull(req.TagsExclude);
            var includeAdult = req.IncludeAdult ?? false;

            var advancedFilters = category is not null || language is not null || tagsInclude is not null || tagsExclude is not null
                || req.MinFileSize is not null || req.MaxFileSize is not null
                || req.MinDownloads is not null || req.MaxDownloads is not null
                || req.MinEndorsements is not null || req.MaxEndorsements is not null;

            long? timeRangeStartSeconds = null;
            var timeField = "createdAt";
            switch (TrimOrNull(req.TimeRange)?.ToLowerInvariant())
            {
                case "day":
                    timeRangeStartSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1L * 24 * 3600;
                    break;
                case "week":
                    timeRangeStartSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7L * 24 * 3600;
                    break;
                case "month":
                    timeRangeStartSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30L * 24 * 3600;
                    break;
                case "year":
                    timeRangeStartSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 365L * 24 * 3600;
                    break;
            }

            if (string.Equals(TrimOrNull(req.TimeRange), "updated", StringComparison.OrdinalIgnoreCase))
                timeField = "updatedAt";

            var searchFilter = BuildSearchFilter(
                query, titleQuery, descriptionQuery, authorQuery, uploaderQuery,
                includeAdult, timeRangeStartSeconds, timeField,
                req.MinFileSize, req.MaxFileSize, req.MinDownloads, req.MaxDownloads, req.MinEndorsements, req.MaxEndorsements);

            JsonElement data;
            var useCredentialed = query is not null && !advancedFilters && _client.HasApiKey;
            if (useCredentialed)
            {
                var variables = new JsonObject
                {
                    ["filter"] = searchFilter.DeepClone(),
                    ["sort"] = new JsonArray(JsonSerializer.SerializeToNode(BuildSort(sort, direction), LauncherJsonContext.Default.JsonObject)),
                    ["offset"] = (page - 1) * pageSize,
                    ["count"] = pageSize,
                };
                try
                {
                    var response = await _client.PostGraphqlLauncherAsync("CatalogMods", CatalogGraphqlQuery, variables, CancellationToken.None).ConfigureAwait(false);
                    NexusClient.ThrowOnGraphqlErrors(response);
                    data = response;
                }
                catch (Exception)
                {
                    //Credentialed lookups fall back to the anonymous listing on any failure.
                    data = await PostPublicCatalogAsync(searchFilter, category, language, tagsInclude, tagsExclude, page, pageSize, sort, direction).ConfigureAwait(false);
                }
            }
            else
            {
                data = await PostPublicCatalogAsync(searchFilter, category, language, tagsInclude, tagsExclude, page, pageSize, sort, direction).ConfigureAwait(false);
            }

            var result = ParseCatalogResponse(data, page, pageSize);
            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.NexusCatalogPageResult);
        });
    }

    static JsonObject BuildSort(string sort, string direction)
    {
        var field = sort switch
        {
            "updated" => "updatedAt",
            "downloads" => "downloads",
            "endorsements" => "endorsements",
            "name" => "name",
            _ => "createdAt",
        };
        return new JsonObject { [field] = new JsonObject { ["direction"] = direction } };
    }

    static JsonArray BuildRangeFilter(long? min, long? max)
    {
        var entries = new JsonArray();
        if (min is not null)
            entries.Add(new JsonObject { ["op"] = "GTE", ["value"] = min.Value });
        if (max is not null)
            entries.Add(new JsonObject { ["op"] = "LTE", ["value"] = max.Value });
        return entries;
    }

    static async Task<JsonElement> PostPublicCatalogAsync(
        JsonObject searchFilter, string? category, string? language, string? tagsInclude, string? tagsExclude,
        long page, long pageSize, string sort, string direction)
    {
        var postFilter = new JsonObject();
        if (tagsExclude is not null)
        {
            var excluded = tagsExclude.Split(',').Select(tag => tag.Trim()).Where(tag => tag.Length > 0)
                .Select(tag => (JsonNode)new JsonObject { ["value"] = tag, ["op"] = "NOT_EQUALS" }).ToArray();
            if (excluded.Length > 0)
                postFilter["tag"] = new JsonArray(excluded);
        }

        var facets = new JsonObject
        {
            ["categoryName"] = category is null ? new JsonArray() : new JsonArray(JsonValue.Create(category)),
            ["languageName"] = language is null ? new JsonArray() : new JsonArray(JsonValue.Create(language)),
            ["tag"] = tagsInclude is null
                ? new JsonArray()
                : new JsonArray(tagsInclude.Split(',').Select(tag => tag.Trim()).Where(tag => tag.Length > 0).Select(tag => (JsonNode)JsonValue.Create(tag)).ToArray()),
        };

        var variables = new JsonObject
        {
            ["filter"] = searchFilter.DeepClone(),
            ["postFilter"] = postFilter,
            ["facets"] = facets,
            ["offset"] = (page - 1) * pageSize,
            ["count"] = pageSize,
            ["sort"] = BuildSort(sort, direction),
        };

        var client = new NexusClient(LauncherRuntimeService.LoadOrCreateSettings().NexusApiKey);
        var response = await client.PostGraphqlPublicAsync("ModsListing", PublicCatalogGraphqlQuery, variables, "https://www.nexusmods.com/", CancellationToken.None).ConfigureAwait(false);
        NexusClient.ThrowOnGraphqlErrors(response);
        return response;
    }

    NexusCatalogPageResult ParseCatalogResponse(JsonElement payload, long page, long pageSize)
    {
        var result = new NexusCatalogPageResult { Page = page, PageSize = pageSize };
        var data = NexusJson.Obj(payload, "data");
        if (data is { } dataValue && dataValue.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Object)
        {
            result.TotalCount = NexusJson.Num(mods, "totalCount") ?? 0;
            foreach (var node in NexusJson.Arr(mods, "nodes"))
            {
                var modId = NexusJson.Num(node, "modId");
                var name = NexusJson.Str(node, "name");
                if (modId is null || modId <= 0 || string.IsNullOrWhiteSpace(name))
                    continue;

                var uploader = NexusJson.Str(NexusJson.Obj(node, "uploader") ?? default, "name");
                var imageUrl = NexusJson.Str(node, "pictureUrl") ?? NexusJson.Str(node, "thumbnailUrl");
                result.Results.Add(new NexusCatalogResult
                {
                    ModId = modId.Value,
                    Title = name,
                    Summary = TrimOrNull(NexusJson.Str(node, "summary")),
                    Author = uploader,
                    Uploader = uploader,
                    ModUrl = string.Format(NexusClient.ModPageUrlTemplate, modId.Value),
                    ImageUrl = imageUrl,
                    Category = NexusJson.Str(NexusJson.Obj(node, "modCategory") ?? default, "name"),
                    CreatedAt = NexusJson.Str(node, "createdAt"),
                    UpdatedAt = NexusJson.Str(node, "updatedAt"),
                    Downloads = NexusJson.Num(node, "downloads"),
                    Endorsements = NexusJson.Num(node, "endorsements"),
                    FileSize = NexusJson.Num(node, "fileSize"),
                    UpdateAvailable = NexusJson.Bool(node, "viewerUpdateAvailable") ?? false,
                });
            }

            if (mods.TryGetProperty("facetsData", out var facetsData))
            {
                ParseFacetMap(facetsData, "categoryName", result.Facets.Categories);
                ParseFacetMap(facetsData, "languageName", result.Facets.Languages);
                ParseFacetMap(facetsData, "tag", result.Facets.Tags);
            }
        }

        result.HasMore = result.TotalCount > 0 ? page * pageSize < result.TotalCount : result.Results.Count >= pageSize;
        return result;
    }

    static void ParseFacetMap(JsonElement facetsData, string key, List<NexusCatalogFacetEntry> target)
    {
        if (facetsData.ValueKind != JsonValueKind.Object || !facetsData.TryGetProperty(key, out var map) || map.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in map.EnumerateObject())
        {
            var count = NexusJson.NumValue(property.Value) ?? 0;
            var name = property.Name.Trim();
            if (count > 0 && name.Length > 0)
                target.Add(new NexusCatalogFacetEntry { Name = name, Count = count });
        }

        target.Sort((left, right) =>
        {
            var countComparison = right.Count.CompareTo(left.Count);
            return countComparison != 0 ? countComparison : string.CompareOrdinal(left.Name.ToLowerInvariant(), right.Name.ToLowerInvariant());
        });
    }

    // --- remote mod detail ---

    public Task<JsonElement?> LoadLauncherRemoteModDetailAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(async () =>
        {
            var req = LibraryService.Deserialize(request, LauncherJsonContext.Default.NexusDetailRequest);
            if (req.ModId <= 0)
                throw new LauncherCommandException("invalid_args", "modId must be a positive integer.");

            var modId = req.ModId;
            var modUrl = string.Format(NexusClient.ModPageUrlTemplate, modId);
            var query = (req.IncludeFiles ?? true) ? PublicModDetailWithFilesGraphqlQuery : PublicModDetailGraphqlQuery;
            var variables = new JsonObject
            {
                ["gameId"] = NexusClient.DefaultGameId.ToString(),
                ["modId"] = modId.ToString(),
            };

            JsonElement payload;
            try
            {
                payload = await _client.PostGraphqlPublicAsync("LauncherPublicModDetail", query, variables, modUrl, CancellationToken.None).ConfigureAwait(false);
                NexusClient.ThrowOnGraphqlErrors(payload);
            }
            catch (LauncherCommandException ex) when (IsModUnavailableMessage(ex.Message))
            {
                return JsonSerializer.SerializeToElement(new NexusRemoteModDetail
                {
                    ModId = modId,
                    Title = $"Nexus #{modId}",
                    Unavailable = true,
                    UnavailableReason = ex.Message,
                    ModUrl = modUrl,
                }, LauncherJsonContext.Default.NexusRemoteModDetail);
            }

            var data = NexusJson.Obj(payload, "data");
            if (data is null || !data.Value.TryGetProperty("mod", out var mod) || mod.ValueKind != JsonValueKind.Object)
                throw new LauncherCommandException("nexus", "Public Nexus mod detail response did not include data.mod.");

            var detail = ParseModDetail(mod, modId, modUrl);
            if ((req.IncludeFiles ?? true) && data.Value.TryGetProperty("modFiles", out var modFiles) && modFiles.ValueKind == JsonValueKind.Array)
                ParseModFiles(detail, modFiles);

            return JsonSerializer.SerializeToElement(detail, LauncherJsonContext.Default.NexusRemoteModDetail);
        });
    }

    static bool IsModUnavailableMessage(string message)
    {
        var normalized = message.Trim().ToLowerInvariant();
        return normalized is "mod not found" or "mod unavailable" or "mod hidden" or "mod removed" or "mod deleted";
    }

    NexusRemoteModDetail ParseModDetail(JsonElement mod, long modId, string modUrl)
    {
        var descriptionMarkup = NexusJson.Str(mod, "description");
        var summary = TrimOrNull(NexusJson.Str(mod, "summary")) ?? DescriptionToText(descriptionMarkup);
        var uploaderName = NexusJson.Str(NexusJson.Obj(mod, "uploader") ?? default, "name");
        var author = TrimOrNull(NexusJson.Str(mod, "author")) ?? uploaderName;
        var imageUrl = NexusJson.Str(mod, "pictureUrl") ?? NexusJson.Str(mod, "thumbnailUrl");
        var category = TrimOrNull(NexusJson.Str(NexusJson.Obj(mod, "modCategory") ?? default, "name")) ?? TrimOrNull(NexusJson.Str(mod, "category"));

        var tags = new List<string>();
        foreach (var tag in NexusJson.Arr(mod, "tags"))
        {
            var name = NexusJson.Str(tag, "name") ?? NexusJson.Str(tag, "tag");
            if (!string.IsNullOrWhiteSpace(name))
                tags.Add(name);
        }

        var requirements = new List<NexusRemoteModRequirement>();
        var requirementsRoot = NexusJson.Obj(mod, "modRequirements");
        if (requirementsRoot is { } requirementsValue)
        {
            foreach (var nexusRequirement in NexusJson.Arr(requirementsValue, "nexusRequirements"))
            {
                var nodes = NexusJson.Obj(nexusRequirement, "nodes");
                if (nodes is null)
                    continue;

                foreach (var requirement in nodes.Value.EnumerateArray())
                {
                    var requirementModId = NexusJson.Num(requirement, "modId");
                    var requirementName = TrimOrNull(NexusJson.Str(requirement, "modName")) ?? TrimOrNull(NexusJson.Str(requirement, "name"));
                    var notes = TrimOrNull(NexusJson.Str(requirement, "notes"));
                    var url = TrimOrNull(NexusJson.Str(requirement, "url"));
                    if (requirementModId is null && url is not null)
                    {
                        var match = new Regex(@"/mods/(\d+)").Match(url);
                        if (match.Success && long.TryParse(match.Groups[1].Value, out var parsedUrlId))
                            requirementModId = parsedUrlId;
                    }

                    if (requirementName is null && notes is null)
                        continue;

                    requirements.Add(new NexusRemoteModRequirement
                    {
                        Name = requirementName ?? $"Nexus #{requirementModId}",
                        Notes = notes,
                        Url = url,
                        ModId = requirementModId,
                        External = NexusJson.Bool(requirement, "externalRequirement") ?? false,
                    });
                }
            }
        }

        var requiredLoader = requirements
            .Select(requirement => requirement.Notes is not null && !string.Equals(requirement.Notes, requirement.Name, StringComparison.OrdinalIgnoreCase)
                ? $"{requirement.Name}: {requirement.Notes}"
                : requirement.Name)
            .FirstOrDefault();

        var gameVersion = MatchGameVersion(descriptionMarkup) ?? MatchGameVersion(summary);

        return new NexusRemoteModDetail
        {
            ModId = NexusJson.Num(mod, "modId") ?? modId,
            Title = TrimOrNull(NexusJson.Str(mod, "name")) ?? $"Nexus #{modId}",
            Summary = summary,
            Description = descriptionMarkup,
            Author = author,
            Version = TrimOrNull(NexusJson.Str(mod, "version")),
            ModUrl = modUrl,
            ImageUrl = imageUrl,
            GalleryImages = ExtractGalleryImages(descriptionMarkup, imageUrl),
            UpdatedAt = NexusJson.Str(mod, "updatedAt"),
            FileSize = NexusJson.Num(mod, "fileSize"),
            Category = category,
            Downloads = NexusJson.Num(mod, "downloads"),
            Endorsements = NexusJson.Num(mod, "endorsements"),
            Tags = tags,
            DirectDownloadEnabled = NexusJson.Bool(mod, "directDownloadEnabled"),
            SupportsVortex = NexusJson.Bool(mod, "supportsVortex"),
            RequiredLoader = requiredLoader,
            GameVersion = gameVersion,
            Requirements = requirements,
        };
    }

    void ParseModFiles(NexusRemoteModDetail detail, JsonElement filesArray)
    {
        var alertsByFileId = new Dictionary<long, long>();
        foreach (var node in filesArray.EnumerateArray())
        {
            var alertFileId = NexusJson.Num(node, "fileId");
            if (alertFileId is not null)
                alertsByFileId[alertFileId.Value] = NexusJson.Num(node, "requirementsAlert") ?? 0;
        }

        foreach (var node in filesArray.EnumerateArray())
        {
            var fileId = NexusJson.Num(node, "fileId");
            var name = NexusJson.Str(node, "name");
            if (fileId is null || string.IsNullOrWhiteSpace(name))
                continue;

            var changelog = NexusJson.Arr(node, "changelogText")
                .Select(item => item.ValueKind == JsonValueKind.String ? DescriptionToText(item.GetString()) : null)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text!.Trim())
                .ToList();

            detail.Files.Add(new NexusRemoteModFile
            {
                FileId = fileId,
                Name = name,
                Version = TrimOrNull(NexusJson.Str(node, "version")),
                Category = NexusJson.Str(node, "category"),
                UploadedAt = UnixSecondsToRfc3339(NexusJson.Num(node, "date")),
                Description = TrimOrNull(DescriptionToText(NexusJson.Str(node, "description"))),
                UniqueDownloads = NexusJson.Num(node, "uniqueDownloads") ?? NexusJson.Num(node, "uCount"),
                TotalDownloads = NexusJson.Num(node, "totalDownloads"),
                ManagerDownloadEnabled = NexusJson.Bool(node, "manager"),
                Uid = NexusJson.Str(node, "uid"),
                Size = NexusJson.Num(node, "size"),
                SizeBytes = NexusJson.Num(node, "sizeInBytes"),
                Primary = (NexusJson.Num(node, "primary") ?? 0) != 0,
                Scanned = NexusJson.Bool(node, "scanned"),
                ScanStatus = NexusJson.Str(node, "scannedV2"),
                Changelog = changelog,
                ArchiveType = ArchiveTypeFromName(name, NexusJson.Str(node, "uri")),
            });
        }

        var candidates = detail.Files.Where(file => file.FileId is not null).ToList();
        var best = candidates.FirstOrDefault(file => file.Primary);
        if (best is null)
        {
            var mainCandidates = candidates.Where(file => string.Equals(file.Category, "MAIN", StringComparison.OrdinalIgnoreCase)).ToList();
            best = (mainCandidates.Count > 0 ? mainCandidates : candidates)
                .OrderByDescending(file => ParseVersionRank(file.Version))
                .ThenByDescending(file => file.FileId)
                .FirstOrDefault();
        }

        if (best is { } primary)
        {
            var primaryAlert = primary.FileId is not null && alertsByFileId.TryGetValue(primary.FileId.Value, out var alert) ? alert : 0;
            detail.PrimaryFileId = primary.FileId;
            detail.PrimaryFileName = primary.Name;
            detail.PrimaryFileVersion = primary.Version;
            detail.PrimaryFileCategory = primary.Category;
            detail.PrimaryFileSize = primary.Size;
            detail.PrimaryFileSizeBytes = primary.SizeBytes;
            detail.PrimaryFileScanned = primary.Scanned;
            detail.PrimaryFileScanStatus = primary.ScanStatus;
            detail.PrimaryFileChangelog = primary.Changelog;
            detail.ArchiveType = primary.ArchiveType ?? detail.ArchiveType;
            detail.UpdateRisk = primaryAlert != 0
                ? "Requires review: requirements alert"
                : primary.ScanStatus is "VERIFIED" or "INTERNALLY_VERIFIED" or "MANUALLY_VERIFIED"
                    ? "Low: verified primary file"
                    : primary.ScanStatus is not null ? $"Review: scan status {primary.ScanStatus}" : null;
        }

        if (detail.Files.Count > 0)
            detail.SupportsVortex ??= detail.Files.Any(file => file.ManagerDownloadEnabled == true);
        if (detail.GalleryImages.Count == 0 && detail.ImageUrl is not null)
            detail.GalleryImages.Add(detail.ImageUrl);
    }

    static long ParseVersionRank(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return 0;

        var parts = version.Trim().TrimStart('v', 'V').Split('.', '-', '+');
        long rank = 0;
        foreach (var part in parts)
        {
            if (!long.TryParse(part, out var value))
                break;
            rank = rank * 1000 + Math.Min(value, 999);
            if (rank > 999_999_999_999)
                break;
        }

        return rank;
    }

    static string? ArchiveTypeFromName(string? name, string? uri)
    {
        foreach (var candidate in new[] { name, uri })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var withoutQuery = candidate.Split('?')[0];
            var extension = Path.GetExtension(withoutQuery).TrimStart('.').ToLowerInvariant();
            if (extension is "zip" or "7z" or "rar" or "tar" or "gz" or "xz")
                return extension.ToUpperInvariant();
        }

        return null;
    }

    static string? UnixSecondsToRfc3339(long? seconds)
    {
        if (seconds is null or <= 0)
            return null;

        return DateTimeOffset.FromUnixTimeSeconds(seconds.Value).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    }

    static string? MatchGameVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = new Regex(@"(?:stardew(?:\s+valley)?|sdv|game)\s*(?:version|v)?\s*(\d+(?:\.\d+){1,3}\+?)", RegexOptions.IgnoreCase).Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>HTML/BBCode markup to plain text (desktop html_to_text + bbcode strip).</summary>
    static string? DescriptionToText(string? markup)
    {
        if (string.IsNullOrWhiteSpace(markup))
            return null;

        var text = new Regex(@"\[/?(?:[a-z*][^\]]*)\]", RegexOptions.IgnoreCase).Replace(markup, string.Empty);
        text = new Regex(@"<br\s*/?>", RegexOptions.IgnoreCase).Replace(text, "\n");
        text = new Regex(@"<[^>]+>").Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = new Regex(@"[ \t\r\f\v]+").Replace(text, " ");
        text = new Regex(@" ?\n ?").Replace(text, "\n");
        text = new Regex(@"\n{3,}").Replace(text, "\n\n");
        return text.Trim();
    }

    static List<string> ExtractGalleryImages(string? markup, string? fallback)
    {
        var images = new List<string>();
        if (!string.IsNullOrWhiteSpace(markup))
        {
            foreach (Match match in new Regex(@"<img\s[^>]*src\s*=\s*""(https?://[^""]+)""", RegexOptions.IgnoreCase).Matches(markup))
            {
                var url = NormalizeImageUrl(match.Groups[1].Value);
                if (url is not null && !images.Contains(url))
                    images.Add(url);
            }
        }

        return images;
    }

    static string? NormalizeImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var value = url.Trim();
        if (value.StartsWith("//"))
            return "https:" + value;
        if (value.StartsWith('/'))
            return "https://www.nexusmods.com" + value;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return value;

        return null;
    }
}
