using Android.Content;
using Android.Net;
using SMAPIGameLoader.Bridge;
using SMAPIGameLoader.Launcher;
using SMAPIGameLoader.Tool;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Services;

/// <summary>
///     Nexus mod downloads: REST files listing, ranked candidate selection, CDN download
///     with progress events and cooperative cancellation, manual-browser fallback when the
///     account cannot use the manager download, and optional auto-install. Ported from
///     the desktop downloads.rs; the resumable .part machinery is not ported yet.
/// </summary>
public sealed partial class NexusService
{
    readonly ConcurrentDictionary<string, byte> _downloadCancellation = new();

    public Task<JsonElement?> DownloadLauncherModAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(async () =>
        {
            var req = LibraryService.Deserialize(request, LauncherJsonContext.Default.NexusDownloadRequest);
            if (req.ModId is null or < 1)
                throw new LauncherCommandException("invalid_args", "modId must be greater than 0.");

            var modId = req.ModId.Value;
            var downloadId = req.DownloadId?.Trim() ?? string.Empty;
            if (downloadId.Length > 0 && _downloadCancellation.TryRemove(downloadId, out _))
                throw new LauncherCommandException("cancelled", "Launcher download was cancelled.");

            var settings = LauncherRuntimeService.LoadOrCreateSettings();
            var apiKey = _client.RequireApiKey("Nexus API key is required to download mods.");
            var downloadDirectory = ResolveDownloadDirectory(settings.DownloadPath);
            Directory.CreateDirectory(downloadDirectory);

            //1) Files listing.
            var (filesStatus, filesBody) = await _client.GetRestAsync(
                $"{NexusClient.RestBase}/games/{NexusClient.DefaultGameDomain}/mods/{modId}/files.json",
                CancellationToken.None).ConfigureAwait(false);
            if (filesStatus != 200)
                throw new LauncherCommandException("network", $"Launcher mod files request failed for {modId}: HTTP {filesStatus}");

            using var filesDocument = JsonDocument.Parse(filesBody ?? "{}");
            if (!filesDocument.RootElement.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array)
                throw new LauncherCommandException("network", "Launcher mod files payload did not contain a files array.");

            var candidates = new List<RestCandidate>();
            foreach (var file in filesElement.EnumerateArray())
            {
                var fileId = NexusJson.Num(file, "file_id");
                var fileName = NexusJson.Str(file, "file_name");
                if (fileId is null or <= 0 || string.IsNullOrWhiteSpace(fileName))
                    continue;

                candidates.Add(new RestCandidate(
                    fileId.Value,
                    fileName,
                    NexusJson.Str(file, "version"),
                    NexusJson.Num(file, "category_id"),
                    NexusJson.Str(file, "category_name"),
                    NexusJson.Num(file, "uploaded_timestamp") ?? 0,
                    NexusJson.Bool(file, "is_primary") ?? false));
            }

            if (candidates.Count == 0)
                throw new LauncherCommandException("not_found", "Launcher mod did not contain any downloadable files.");

            var candidate = SelectDownloadCandidate(candidates, req.FileId, req.Version)
                ?? throw new LauncherCommandException("not_found", "Unable to resolve a launcher download file.");

            var title = req.Title?.Trim() ?? $"Nexus Mod {modId}";
            var version = candidate.Version;
            var manualResult = new NexusDownloadResult
            {
                ModId = modId,
                Title = title,
                Version = version,
                FileName = candidate.FileName,
                ArchivePath = string.Empty,
                ManualDownloadPageOpened = true,
            };

            //2) Download link; 403 means the account cannot use the manager download.
            var (linkStatus, linkBody) = await _client.GetRestAsync(
                $"{NexusClient.RestBase}/games/{NexusClient.DefaultGameDomain}/mods/{modId}/files/{candidate.FileId}/download_link.json",
                CancellationToken.None).ConfigureAwait(false);
            if (linkStatus == 403)
            {
                OpenManualDownloadPage(candidate.FileId);
                return JsonSerializer.SerializeToElement(manualResult, LauncherJsonContext.Default.NexusDownloadResult);
            }
            if (linkStatus != 200)
                throw new LauncherCommandException("network", $"Launcher download link request failed for {modId}/{candidate.FileId}: HTTP {linkStatus}");

            using var linkDocument = JsonDocument.Parse(linkBody ?? "[]");
            var linkUri = linkDocument.RootElement.ValueKind == JsonValueKind.Array && linkDocument.RootElement.GetArrayLength() > 0
                ? NexusJson.Str(linkDocument.RootElement[0], "URI")
                : null;
            if (string.IsNullOrWhiteSpace(linkUri))
                throw new LauncherCommandException("network", "Launcher download link response did not include a URI.");

            //3) Streaming download with progress events and cancellation.
            var archiveName = SanitizeDownloadFileName(candidate.FileName);
            var archivePath = UniqueDownloadPath(Path.Combine(downloadDirectory, archiveName));
            EmitDownloadProgress(downloadId, 0, null, 0);
            try
            {
                var totalBytes = await DownloadToArchiveAsync(linkUri!, archivePath, downloadId, $"nexus:{modId}:{candidate.FileId}", CancellationToken.None).ConfigureAwait(false);
                EmitDownloadProgress(downloadId, totalBytes, totalBytes, 0);
            }
            catch (Exception)
            {
                ClearPartialDownload(archivePath);
                throw;
            }

            //4) Optional auto-install into the Mods folder.
            bool installed = false;
            string? installedTargetPath = null;
            if (settings.AutoInstallDownloads)
            {
                var installService = new InstallService();
                var installResult = installService.InstallLauncherArchive(new InstallLauncherArchiveRequest
                {
                    ArchivePath = archivePath,
                    ModsPath = settings.ModsPath,
                });
                installed = true;
                installedTargetPath = installResult.TargetPath;
                if (!settings.KeepDownloadedArchives)
                    TryDeleteFile(archivePath);
            }

            var result = new NexusDownloadResult
            {
                ModId = modId,
                Title = title,
                Version = version,
                FileName = Path.GetFileName(archivePath),
                ArchivePath = archivePath,
                Installed = installed,
                InstalledTargetPath = installedTargetPath,
                ManualDownloadPageOpened = false,
            };
            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.NexusDownloadResult);
        });
    }

    static string DownloadQueueFilePath => Path.Combine(FileTool.ExternalFilesDir, "ModForge", "app", "download-queue.json");

    public Task<JsonElement?> LoadLauncherDownloadQueueAsync()
    {
        return Task.Run<JsonElement?>(() =>
        {
            try
            {
                if (!File.Exists(DownloadQueueFilePath))
                    return JsonSerializer.SerializeToElement(new JsonObject { ["items"] = new JsonArray() });

                using var document = JsonDocument.Parse(File.ReadAllText(DownloadQueueFilePath));
                return document.RootElement.Clone();
            }
            catch (Exception ex)
            {
                Console.WriteLine("NexusService: download queue read failed: " + ex.Message);
                return JsonSerializer.SerializeToElement(new JsonObject { ["items"] = new JsonArray() });
            }
        });
    }

    public Task<JsonElement?> SaveLauncherDownloadQueueAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(() =>
        {
            var state = JsonNode.Parse(request.GetRawText()) as JsonObject ?? new JsonObject();
            LauncherJsonHelper.WriteJsonFile(DownloadQueueFilePath, state);
            return (JsonElement?)JsonSerializer.SerializeToElement(state, LauncherJsonContext.Default.JsonObject);
        });
    }

    public Task<JsonElement?> CancelLauncherDownloadAsync(string downloadId)
    {
        return Task.Run<JsonElement?>(() =>
        {
            var trimmed = downloadId?.Trim() ?? string.Empty;
            if (trimmed.Length > 0)
                _downloadCancellation[trimmed] = 1;

            return (JsonElement?)null;
        });
    }

    sealed record RestCandidate(long FileId, string FileName, string? Version, long? CategoryId, string? CategoryName, long UploadedTimestamp, bool IsPrimary);

    static RestCandidate SelectDownloadCandidate(List<RestCandidate> candidates, long? requestedFileId, string? requestedVersion)
    {
        IEnumerable<RestCandidate> pool = candidates;
        if (requestedFileId is not null)
            pool = pool.Where(candidate => candidate.FileId == requestedFileId.Value);
        else if (!string.IsNullOrWhiteSpace(requestedVersion))
            pool = pool.Where(candidate => string.Equals(candidate.Version?.Trim(), requestedVersion!.Trim(), StringComparison.Ordinal));

        return pool
            .OrderByDescending(candidate => candidate.IsPrimary)
            .ThenByDescending(candidate => CategoryRank(candidate))
            .ThenByDescending(candidate => candidate.UploadedTimestamp)
            .ThenByDescending(candidate => candidate.FileId)
            .FirstOrDefault();
    }

    static int CategoryRank(RestCandidate candidate)
    {
        var name = candidate.CategoryName?.ToUpperInvariant();
        var id = candidate.CategoryId;
        if (name == "MAIN" || id == 1)
            return 5;
        if (name == "UPDATE" || name == "UPDATES" || id == 2)
            return 4;
        if (name == "OPTIONAL" || id == 3)
            return 3;
        if (name == "MISC" || name == "MISCELLANEOUS" || id == 5)
            return 2;
        if (name == "OLD" || name == "OLD_VERSION" || id == 4)
            return 1;
        return 0;
    }

    static string ResolveDownloadDirectory(string? configuredPath)
    {
        var candidate = configuredPath?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(candidate))
            return candidate;

        return SandboxFileTool.PickedFilesDir;
    }

    void OpenManualDownloadPage(long fileId)
    {
        try
        {
            var url = string.Format(NexusClient.DownloadPopupUrlTemplate, fileId);
            var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(url));
            intent.AddFlags(ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            Console.WriteLine("NexusService: manual download page open failed: " + ex.Message);
        }
    }

    static string SanitizeDownloadFileName(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
            builder.Append("<>:\"/\\|?*".Contains(character) ? '_' : character);

        var result = builder.ToString().Trim();
        return result.Length > 0 ? result : "download.zip";
    }

    static string UniqueDownloadPath(string path)
    {
        if (!File.Exists(path))
            return path;

        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var parent = Path.GetDirectoryName(path) ?? string.Empty;
        for (var index = 2; index < 1000; index += 1)
        {
            var candidate = Path.Combine(parent, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return path;
    }

    sealed record PartialDownloadMetadata(string VersionIdentity, string? ETag, string? LastModified);

    static string PartialPathFor(string archivePath) => archivePath + ".part";
    static string PartialMetaPathFor(string archivePath) => archivePath + ".part.json";

    static PartialDownloadMetadata? ReadPartialMetadata(string archivePath)
    {
        try
        {
            if (!File.Exists(PartialMetaPathFor(archivePath)))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(PartialMetaPathFor(archivePath)));
            var versionIdentity = NexusJson.Str(document.RootElement, "versionIdentity");
            if (versionIdentity is null || !File.Exists(PartialPathFor(archivePath)))
                return null;

            return new PartialDownloadMetadata(versionIdentity, NexusJson.Str(document.RootElement, "etag"), NexusJson.Str(document.RootElement, "lastModified"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    static void WritePartialMetadata(string archivePath, PartialDownloadMetadata metadata)
    {
        LauncherJsonHelper.WriteJsonFile(PartialMetaPathFor(archivePath), JsonSerializer.SerializeToNode(metadata, LauncherJsonContext.Default.JsonObject)!);
    }

    static void ClearPartialDownload(string archivePath)
    {
        TryDeleteFile(PartialPathFor(archivePath));
        TryDeleteFile(PartialMetaPathFor(archivePath));
    }

    /// <summary>
    ///     Resumable download (desktop resumable_download.rs semantics): streams into a
    ///     .part file with sidecar metadata keyed by version identity, resumes with
    ///     Range/If-Range when the server validates the partial content, renames on
    ///     success, and deletes the partial on failure or cancellation.
    /// </summary>
    async Task<long> DownloadToArchiveAsync(string url, string archivePath, string downloadId, string versionIdentity, CancellationToken cancellation)
    {
        var partialPath = PartialPathFor(archivePath);
        var existingPartial = ReadPartialMetadata(archivePath);
        var resumable = existingPartial is not null && existingPartial.VersionIdentity == versionIdentity;
        long resumeOffset = resumable ? new FileInfo(partialPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumable && resumeOffset > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeOffset, null);
            if (existingPartial!.ETag is not null)
                request.Headers.IfRange = new System.Net.Http.Headers.RangeConditionHeaderValue(existingPartial.ETag);
            else if (existingPartial.LastModified is not null)
                request.Headers.IfRange = new System.Net.Http.Headers.RangeConditionHeaderValue(existingPartial.LastModified);
        }

        using var response = await NexusClient.GetAsync(url, cancellation).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (resumable && status == 416)
            throw new LauncherCommandException("network", "Download server rejected the requested byte range.");
        if (resumable && resumeOffset > 0 && status != 206)
        {
            if (status != 200)
                throw new LauncherCommandException("network", $"Resumed download failed with HTTP {status}.");

            //Server ignored the range: restart from zero.
            resumeOffset = 0;
        }
        else if (!response.IsSuccessStatusCode)
        {
            throw new LauncherCommandException("network", $"Failed to download launcher mod: HTTP {status}");
        }

        long? totalBytes = response.Content?.Headers.ContentLength is { } contentLength ? contentLength + (status == 206 ? resumeOffset : 0) : null;
        var etag = response.Headers.ETag?.Tag;
        var lastModified = response.Content?.Headers.LastModified?.ToString("R");

        await using (var source = await response.Content!.ReadAsStreamAsync(cancellation).ConfigureAwait(false))
        await using (var destination = new FileStream(partialPath, resumeOffset > 0 && status == 206 ? FileMode.Append : FileMode.Create, FileAccess.Write))
        {
            var buffer = new byte[81920];
            long downloadedBytes = resumeOffset;
            var lastEmit = Environment.TickCount64;
            EmitDownloadProgress(downloadId, downloadedBytes, totalBytes, 0);
            int read;
            while ((read = await source.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
            {
                if (downloadId.Length > 0 && _downloadCancellation.TryRemove(downloadId, out _))
                {
                    ClearPartialDownload(archivePath);
                    throw new LauncherCommandException("cancelled", "Download was cancelled.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                downloadedBytes += read;
                var now = Environment.TickCount64;
                if (now - lastEmit >= 100)
                {
                    lastEmit = now;
                    EmitDownloadProgress(downloadId, downloadedBytes, totalBytes, 0);
                }
            }
        }

        WritePartialMetadata(archivePath, new PartialDownloadMetadata(versionIdentity, etag, lastModified));
        var finalSize = new FileInfo(partialPath).Length;
        if (File.Exists(archivePath))
            File.Delete(archivePath);
        File.Move(partialPath, archivePath);
        TryDeleteFile(PartialMetaPathFor(archivePath));
        return totalBytes ?? finalSize;
    }

    void EmitDownloadProgress(string downloadId, long downloadedBytes, long? totalBytes, long bytesPerSecond)
    {
        try
        {
            ModForgeBridge.Instance?.DispatchEvent(DownloadProgressEvent, new JsonObject
            {
                ["downloadId"] = downloadId,
                ["downloadedBytes"] = downloadedBytes,
                ["totalBytes"] = totalBytes,
                ["bytesPerSecond"] = bytesPerSecond,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("NexusService: download progress dispatch failed: " + ex.Message);
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
            Console.WriteLine("NexusService: temp cleanup failed for " + path + ": " + ex.Message);
        }
    }
}
