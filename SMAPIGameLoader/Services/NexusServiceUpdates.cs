using SMAPIGameLoader.Bridge;
using SMAPIGameLoader.Launcher;
using SMAPIGameLoader.Tool;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Services;

/// <summary>
///     Launcher update checks: the library scan provides candidates, smapi.io resolves
///     them without an API key, the Nexus GraphQL batch and public detail fill the rest,
///     and per-session progress events stream to the front-end. Ported from the desktop
///     updates.rs with the Windows-only SMAPI version detection replaced by the Android
///     SMAPI assembly read.
/// </summary>
public sealed partial class NexusService
{
    public Task<JsonElement?> CheckLauncherUpdatesAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(async () =>
        {
            var req = LibraryService.Deserialize(request, LauncherJsonContext.Default.NexusCheckUpdatesRequest);
            var modsPath = req.ModsPath?.Trim() ?? string.Empty;
            if (modsPath.Length == 0)
                throw new LauncherCommandException("invalid_args", "modsPath is required.");
            var sessionId = req.SessionId?.Trim() ?? string.Empty;
            if (sessionId.Length == 0)
                throw new LauncherCommandException("invalid_args", "sessionId is required.");
            var forceRefresh = req.ForceRefresh ?? false;

            var cache = LoadUpdatesCache();
            var cacheKey = LogicalPathKey(modsPath);

            if (!forceRefresh && cache.TryGetProperty("entries", out var entriesElement) && entriesElement.TryGetProperty(cacheKey, out var entry))
            {
                var expiresAtMs = NexusJson.Num(entry, "expiresAtMs") ?? 0;
                if (expiresAtMs > (long)LauncherJsonHelper.CurrentTimestampMs())
                {
                    var cached = EntryToResult(entry, modsPath);
                    if (cached is not null && (NexusJson.Bool(entry, "isComplete") ?? true))
                        return JsonSerializer.SerializeToElement(cached, LauncherJsonContext.Default.NexusUpdatesResult);
                }
            }

            var settings = LauncherRuntimeService.LoadOrCreateSettings();
            var installedSmapiVersion = LibraryService.ResolveInstalledSmapiVersion() ?? "4.0.0";
            var scan = new LibraryService().ScanLibraryAtPath(modsPath);

            var suppressedIds = forceRefresh
                ? new HashSet<long>()
                : LoadSuppressedIdSet(cache);

            var candidates = scan.Mods
                .Where(mod => mod.NexusModId is > 0 && mod.Version is not null)
                .GroupBy(mod => mod.NexusModId!.Value)
                .Select(group => group.First())
                .Where(mod => !suppressedIds.Contains(mod.NexusModId!.Value))
                .Select(mod => new UpdateCandidate(mod.NexusModId!.Value, mod.UniqueId ?? string.Empty, mod.Name, mod.Version!, mod.AbsolutePath, mod.UpdateKeys ?? new List<string>()))
                .ToList();

            EmitUpdateProgress(modsPath, sessionId, 0, candidates.Count, null, new List<NexusUpdateSummary>());

            var updates = new List<NexusUpdateSummary>();
            var resolvedIds = new HashSet<long>();
            var checkedCount = 0;
            foreach (var batch in candidates.Chunk(UpdateBatchSize))
            {
                var remoteByModId = await ResolveBatchRemoteDetailsAsync(batch).ConfigureAwait(false);
                foreach (var candidate in batch)
                {
                    checkedCount += 1;
                    if (!remoteByModId.TryGetValue(candidate.ModId, out var remote))
                    {
                        RecordAutoFailure(cacheKey, candidate.ModId, forceRefresh);
                        continue;
                    }

                    ClearAutoFailure(cacheKey, candidate.ModId);
                    resolvedIds.Add(candidate.ModId);
                    if (!LauncherJsonHelper.VersionIsNewer(candidate.CurrentVersion, remote.Version))
                        continue;

                    updates.Add(new NexusUpdateSummary
                    {
                        ModId = candidate.ModId,
                        Name = remote.Name ?? candidate.Name,
                        Author = remote.Author,
                        CurrentVersion = candidate.CurrentVersion,
                        LatestVersion = remote.Version ?? string.Empty,
                        AbsolutePath = candidate.AbsolutePath,
                        ModUrl = remote.ModUrl ?? $"https://www.nexusmods.com/stardewvalley/mods/{candidate.ModId}",
                        ImageUrl = remote.ImageUrl,
                        UpdatedAt = remote.UpdatedAt,
                        FileSize = remote.FileSize,
                    });
                }

                updates.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
                EmitUpdateProgress(modsPath, sessionId, checkedCount, candidates.Count, batch.FirstOrDefault()?.Name, updates);
            }

            var result = new NexusUpdatesResult
            {
                ModsPath = modsPath,
                CheckedAtMs = (ulong)LauncherJsonHelper.CurrentTimestampMs(),
                IsComplete = true,
                Updates = updates,
            };
            SaveUpdatesCacheEntry(cacheKey, modsPath, result);
            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.NexusUpdatesResult);
        });
    }

    sealed record UpdateCandidate(long ModId, string UniqueId, string Name, string CurrentVersion, string AbsolutePath, List<string> UpdateKeys);

    sealed record RemoteDetail(long ModId, string? Name, string? Version, string? Author, string? ImageUrl, string? UpdatedAt, long? FileSize, string? ModUrl);

    void EmitUpdateProgress(string modsPath, string sessionId, long checkedCount, long total, string? currentModName, List<NexusUpdateSummary> updates)
    {
        try
        {
            var payload = new JsonObject
            {
                ["modsPath"] = modsPath,
                ["sessionId"] = sessionId,
                ["checked"] = checkedCount,
                ["total"] = total,
                ["currentModName"] = currentModName,
            };
            var updatesArray = new JsonArray();
            foreach (var update in updates)
                updatesArray.Add(JsonSerializer.SerializeToNode(update, LauncherJsonContext.Default.NexusUpdateSummary) ?? new JsonObject());
            payload["updates"] = updatesArray;

            ModForgeBridge.Instance?.DispatchEvent(UpdateProgressEvent, payload);
        }
        catch (Exception ex)
        {
            Console.WriteLine("NexusService: update progress dispatch failed: " + ex.Message);
        }
    }

    async Task<Dictionary<long, RemoteDetail>> ResolveBatchRemoteDetailsAsync(UpdateCandidate[] batch)
    {
        var resolved = new Dictionary<long, RemoteDetail>();

        //1) SMAPI lookup resolves most mods without an API key.
        var smapiCandidates = batch.Where(candidate => candidate.UniqueId.Length > 0).ToArray();
        if (smapiCandidates.Length > 0)
        {
            var payload = new JsonObject
            {
                ["Mods"] = new JsonArray(smapiCandidates.Select(candidate => (JsonNode)new JsonObject
                {
                    ["ID"] = candidate.UniqueId,
                    ["Version"] = candidate.CurrentVersion,
                    ["UpdateKeys"] = new JsonArray(candidate.UpdateKeys.Select(key => (JsonNode)JsonValue.Create(key)).ToArray()),
                }).ToArray()),
                ["ApiVersion"] = "4.5.2",
                ["GameVersion"] = "1.6.14",
                ["Platform"] = "Windows",
                ["IncludeExtendedMetadata"] = true,
            };

            var (status, body) = await NexusClient.PostJsonAsync(NexusClient.SmapiLookupEndpoint, payload.ToJsonString(), CancellationToken.None).ConfigureAwait(false);
            if (status == 200 && body is not null)
            {
                try
                {
                    using var document = JsonDocument.Parse(body);
                    var root = document.RootElement.Clone();
                    var results = root.ValueKind == JsonValueKind.Array
                        ? root.EnumerateArray().ToList()
                        : root.TryGetProperty("Mods", out var mods) && mods.ValueKind == JsonValueKind.Array ? mods.EnumerateArray().ToList() : new List<JsonElement>();

                    for (var index = 0; index < Math.Min(results.Count, smapiCandidates.Length); index += 1)
                    {
                        var candidate = smapiCandidates[index];
                        var result = results[index];
                        var latestVersion = FirstString(result, "Metadata.Main.Version", "Metadata.Version", "Version");
                        if (string.IsNullOrWhiteSpace(latestVersion))
                            continue;

                        var name = FirstString(result, "Metadata.Main.Name", "Metadata.Name", "Name") ?? candidate.Name;
                        var author = FirstString(result, "Metadata.Main.Author", "Metadata.Author", "Author");
                        var pageUrl = FirstString(result, "Metadata.Main.URL", "Metadata.Main.Url", "Metadata.Main.ModPageUrl", "Metadata.Main.ModUrl", "URL", "Url");
                        var modUrl = pageUrl is not null && pageUrl.Contains("nexusmods.com/") ? pageUrl : $"https://www.nexusmods.com/stardewvalley/mods/{candidate.ModId}";
                        var imageUrl = FirstString(result, "Metadata.Main.ImageUrl", "Metadata.Main.ImageURL", "Metadata.ImageUrl", "ImageUrl");

                        resolved[candidate.ModId] = new RemoteDetail(
                            candidate.ModId,
                            name,
                            latestVersion,
                            author,
                            NormalizeImageUrl(imageUrl),
                            null,
                            null,
                            modUrl);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("NexusService: SMAPI lookup parse failed: " + ex.Message);
                }
            }
            else
            {
                Console.WriteLine($"NexusService: SMAPI mod lookup request failed: HTTP {status}");
            }
        }

        //2) Nexus GraphQL batch for the remainder (only with an API key).
        var missing = batch.Where(candidate => !resolved.ContainsKey(candidate.ModId)).ToArray();
        if (missing.Length > 0 && _client.HasApiKey)
        {
            try
            {
                var ids = missing.Select(candidate => (object)new { gameDomain = NexusClient.DefaultGameDomain, modId = candidate.ModId }).ToArray();
                var payload = await _client.PostGraphqlLauncherAsync("LauncherUpdateBatch", UpdateBatchGraphqlQuery, new { ids }, CancellationToken.None).ConfigureAwait(false);
                NexusClient.ThrowOnGraphqlErrors(payload);
                var legacyRoot = NexusJson.Obj(payload, "data");
                var legacy = legacyRoot is { } legacyValue ? NexusJson.Obj(legacyValue, "legacyModsByDomain") : null;
                var nodes = legacy is { } legacyElement ? legacyElement.EnumerateArray().ToList() : new List<JsonElement>();
                var nodeElements = new List<JsonElement>();
                foreach (var legacyEntry in nodes)
                    nodeElements.AddRange(NexusJson.Arr(legacyEntry, "nodes"));
                foreach (var node in nodeElements)
                {
                    var modId = NexusJson.Num(node, "modId");
                    if (modId is null or <= 0)
                        continue;

                    resolved[modId.Value] = new RemoteDetail(
                        modId.Value,
                        NexusJson.Str(node, "name"),
                        NexusJson.Str(node, "version"),
                        null,
                        NexusJson.Str(node, "pictureUrl"),
                        null,
                        null,
                        $"https://www.nexusmods.com/stardewvalley/mods/{modId}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("NexusService: GraphQL batch failed: " + ex.Message);
            }
        }

        return resolved;
    }

    static string? FirstString(JsonElement element, params string[] paths)
    {
        foreach (var path in paths)
        {
            var current = element;
            var found = true;
            foreach (var segment in path.Split('.'))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
                {
                    found = false;
                    break;
                }

                current = next;
            }

            if (found && current.ValueKind == JsonValueKind.String && current.GetString() is { Length: > 0 } value)
                return value;
        }

        return null;
    }

    // --- updates cache persistence (entries keyed by logical mods path) ---

    const int AutoFailureSuppressionThreshold = 3;

    static HashSet<long> LoadSuppressedIdSet(JsonElement cache)
    {
        var suppressed = new HashSet<long>();
        if (cache.ValueKind == JsonValueKind.Object && cache.TryGetProperty("autoFailures", out var failures) && failures.ValueKind == JsonValueKind.Object)
        {
            foreach (var perPath in failures.EnumerateObject())
            {
                if (perPath.Value.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var failureEntry in perPath.Value.EnumerateObject())
                {
                    var modId = NexusJson.Num(failureEntry.Value, "modId") ?? 0;
                    var failureCount = NexusJson.Num(failureEntry.Value, "failureCount") ?? 0;
                    if (modId > 0 && failureCount >= AutoFailureSuppressionThreshold)
                        suppressed.Add(modId);
                }
            }
        }

        return suppressed;
    }

    void RecordAutoFailure(string cacheKey, long modId, bool forceRefresh)
    {
        if (forceRefresh)
            return;

        try
        {
            var cache = JsonNode.Parse(LoadUpdatesCache().GetRawText()) as JsonObject ?? new JsonObject();
            var failures = cache["autoFailures"] as JsonObject ?? new JsonObject();
            var perPath = failures[cacheKey] as JsonObject ?? new JsonObject();
            var entry = perPath[modId.ToString()] as JsonObject ?? new JsonObject();
            var failureCount = (NexusJson.Num(JsonSerializer.SerializeToElement(entry), "failureCount") ?? 0) + 1;
            entry["modId"] = modId;
            entry["failureCount"] = failureCount;
            entry["lastFailedAtMs"] = LauncherJsonHelper.CurrentTimestampMs();
            entry["lastError"] = "All remote update detail fallbacks failed.";
            perPath[modId.ToString()] = entry;
            failures[cacheKey] = perPath;
            cache["autoFailures"] = failures;
            LauncherJsonHelper.WriteJsonFile(UpdatesCacheFilePath, cache);
        }
        catch (Exception ex)
        {
            Console.WriteLine("NexusService: auto failure record failed: " + ex.Message);
        }
    }

    void ClearAutoFailure(string cacheKey, long modId)
    {
        try
        {
            var cache = JsonNode.Parse(LoadUpdatesCache().GetRawText()) as JsonObject ?? new JsonObject();
            if (cache["autoFailures"] is not JsonObject failures || failures[cacheKey] is not JsonObject perPath)
                return;

            perPath.Remove(modId.ToString());
            if (perPath.Count == 0)
                failures.Remove(cacheKey);
            else
                failures[cacheKey] = perPath;
            cache["autoFailures"] = failures;
            LauncherJsonHelper.WriteJsonFile(UpdatesCacheFilePath, cache);
        }
        catch (Exception ex)
        {
            Console.WriteLine("NexusService: auto failure clear failed: " + ex.Message);
        }
    }

    static string LogicalPathKey(string modsPath)
    {
        var key = modsPath.Trim().Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        return key.Length == 0 ? "/" : key;
    }

    JsonElement LoadUpdatesCache()
    {
        try
        {
            if (!File.Exists(UpdatesCacheFilePath))
                return JsonSerializer.SerializeToElement(new JsonObject { ["entries"] = new JsonObject() });

            using var document = JsonDocument.Parse(File.ReadAllText(UpdatesCacheFilePath));
            return document.RootElement.Clone();
        }
        catch (Exception)
        {
            return JsonSerializer.SerializeToElement(new JsonObject { ["entries"] = new JsonObject() });
        }
    }

    static NexusUpdatesResult? EntryToResult(JsonElement entry, string modsPath)
    {
        try
        {
            var updates = new List<NexusUpdateSummary>();
            if (entry.TryGetProperty("updates", out var updatesElement) && updatesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var update in updatesElement.EnumerateArray())
                {
                    var deserialized = update.Deserialize(LauncherJsonContext.Default.NexusUpdateSummary);
                    if (deserialized is not null)
                        updates.Add(deserialized);
                }
            }

            return new NexusUpdatesResult
            {
                ModsPath = NexusJson.Str(entry, "modsPath") ?? modsPath,
                CheckedAtMs = (ulong)(NexusJson.Num(entry, "checkedAtMs") ?? 0),
                IsComplete = NexusJson.Bool(entry, "isComplete") ?? true,
                Updates = updates,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    void SaveUpdatesCacheEntry(string cacheKey, string modsPath, NexusUpdatesResult result)
    {
        try
        {
            var cache = JsonNode.Parse(LoadUpdatesCache().GetRawText()) as JsonObject ?? new JsonObject();
            var entries = cache["entries"] as JsonObject ?? new JsonObject();

            var now = (long)LauncherJsonHelper.CurrentTimestampMs();
            foreach (var key in entries.Select(property => property.Key).ToList())
            {
                var expiresAtMs = long.TryParse(entries[key]?.ToString(), out var parsed) ? parsed : 0;
                if (expiresAtMs <= now)
                    entries.Remove(key);
            }

            entries[cacheKey] = JsonSerializer.SerializeToNode(result, LauncherJsonContext.Default.NexusUpdatesResult)!.AsObject();
            cache["entries"] = entries;
            LauncherJsonHelper.WriteJsonFile(UpdatesCacheFilePath, cache);
        }
        catch (Exception ex)
        {
            Console.WriteLine("NexusService: updates cache write failed: " + ex.Message);
        }
    }

    public Task<JsonElement?> LoadCachedLauncherUpdatesAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(() =>
        {
            var req = LibraryService.Deserialize(request, LauncherJsonContext.Default.NexusCachedUpdatesRequest);
            var modsPath = req.ModsPath?.Trim() ?? string.Empty;
            if (modsPath.Length == 0)
                throw new LauncherCommandException("invalid_args", "modsPath is required.");

            var cache = LoadUpdatesCache();
            if (!cache.TryGetProperty("entries", out var entries) || !entries.TryGetProperty(LogicalPathKey(modsPath), out var entry))
                return null;

            var expiresAtMs = NexusJson.Num(entry, "expiresAtMs") ?? 0;
            if (expiresAtMs <= (long)LauncherJsonHelper.CurrentTimestampMs())
                return null;

            var cached = EntryToResult(entry, modsPath);
            return cached is null ? null : JsonSerializer.SerializeToElement(cached, LauncherJsonContext.Default.NexusUpdatesResult);
        });
    }

    public Task<JsonElement?> LoadSuppressedLauncherUpdateModIdsAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(() =>
        {
            var req = LibraryService.Deserialize(request, LauncherJsonContext.Default.NexusCachedUpdatesRequest);
            var modsPath = req.ModsPath?.Trim() ?? string.Empty;
            if (modsPath.Length == 0)
                throw new LauncherCommandException("invalid_args", "modsPath is required.");

            var cache = LoadUpdatesCache();
            var result = new NexusSuppressedUpdateModIdsResult
            {
                ModsPath = modsPath,
                ModIds = LoadSuppressedIdSet(cache).OrderBy(id => id).ToList(),
            };
            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.NexusSuppressedUpdateModIdsResult);
        });
    }
}
