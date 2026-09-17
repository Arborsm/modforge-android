using SMAPIGameLoader.Launcher;
using SMAPIGameLoader.Tool;
using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Services;

/// <summary>Shared constants and filter builders for the Nexus command partials.</summary>
public sealed partial class NexusService
{
    const int UpdateBatchSize = 24;
    const string UpdateProgressEvent = "launcher://update-check-progress";
    const string DownloadProgressEvent = "launcher://download-progress";
    const string ImageFetchDisconnectedEvent = "launcher://image-fetch-disconnected";

    static string ImagesCacheDirectory => Path.Combine(FileTool.ExternalFilesDir, "ModForge", "launcher", "images");
    static string UpdatesCacheFilePath => Path.Combine(FileTool.ExternalFilesDir, "ModForge", "launcher", "updates-cache.json");

    const string PublicGraphqlProbeQuery = """
        query GameModsListing($count: Int = 0, $filter: ModsFilter, $offset: Int, $sort: [ModsSort!]) {
          mods(
            count: $count
            filter: $filter
            offset: $offset
            sort: $sort
            viewUserBlockedContent: false
          ) {
            totalCount
          }
        }
        """;

    const string PrivateGraphqlProbeQuery = """
        query CatalogMods($filter: ModsFilter, $sort: [ModsSort!], $offset: Int, $count: Int) {
          mods(filter: $filter, sort: $sort, offset: $offset, count: $count) {
            totalCount
          }
        }
        """;

    const string UpdateBatchGraphqlQuery = """
        query LauncherUpdateBatch($ids: [CompositeDomainWithIdInput!]!) {
          legacyModsByDomain(ids: $ids) {
            nodes {
              modId
              name
              version
              pictureUrl
            }
          }
        }
        """;

    static JsonObject BuildSearchFilter(
        string? query, string? titleQuery, string? descriptionQuery, string? authorQuery, string? uploaderQuery,
        bool includeAdult, long? timeRangeStartSeconds, string timeField,
        long? minFileSize, long? maxFileSize, long? minDownloads, long? maxDownloads, long? minEndorsements, long? maxEndorsements)
    {
        var filter = new JsonObject();
        if (!includeAdult)
            filter["adultContent"] = new JsonArray(new JsonObject { ["op"] = "EQUALS", ["value"] = false });
        filter["filter"] = new JsonArray();
        filter["gameDomainName"] = new JsonArray(new JsonObject { ["op"] = "EQUALS", ["value"] = NexusClient.DefaultGameDomain });

        var nameValue = titleQuery ?? query;
        filter["name"] = nameValue is null
            ? new JsonArray()
            : new JsonArray(new JsonObject { ["value"] = nameValue, ["op"] = "WILDCARD" });

        if (descriptionQuery is not null)
            filter["description"] = new JsonArray(new JsonObject { ["value"] = descriptionQuery, ["op"] = "MATCHES" });
        if (authorQuery is not null)
            filter["author"] = new JsonArray(new JsonObject { ["value"] = authorQuery, ["op"] = "WILDCARD" });
        if (uploaderQuery is not null)
            filter["uploader"] = new JsonArray(new JsonObject { ["value"] = uploaderQuery, ["op"] = "WILDCARD" });
        if (timeRangeStartSeconds is not null)
            filter[timeField] = new JsonArray(new JsonObject { ["value"] = timeRangeStartSeconds.Value.ToString(), ["op"] = "GTE" });
        if (minFileSize is not null || maxFileSize is not null)
            filter["fileSize"] = BuildRangeFilter(minFileSize, maxFileSize);
        if (minDownloads is not null || maxDownloads is not null)
            filter["downloads"] = BuildRangeFilter(minDownloads, maxDownloads);
        if (minEndorsements is not null || maxEndorsements is not null)
            filter["endorsements"] = BuildRangeFilter(minEndorsements, maxEndorsements);

        return filter;
    }
}
