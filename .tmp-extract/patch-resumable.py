import io

p = r'SMAPIGameLoader\Services\NexusDownloads.cs'
s = io.open(p, encoding='utf-8').read()
marker = '    async Task<long> DownloadToArchiveAsync(string url, string archivePath, string downloadId, CancellationToken cancellation)'
start = s.index(marker)
end = s.index('    void EmitDownloadProgress', start)

replacement = r'''    sealed record PartialDownloadMetadata(string VersionIdentity, string? ETag, string? LastModified);

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

'''

s = s[:start] + replacement + s[end:]

old_call = 'var totalBytes = await DownloadToArchiveAsync(linkUri!, archivePath, downloadId, CancellationToken.None).ConfigureAwait(false);'
new_call = 'var totalBytes = await DownloadToArchiveAsync(linkUri!, archivePath, downloadId, $"nexus:{modId}:{candidate.FileId}", CancellationToken.None).ConfigureAwait(false);'
assert s.count(old_call) == 1
s = s.replace(old_call, new_call)

old_catch = '''            catch (Exception)
            {
                TryDeleteFile(archivePath);
                throw;
            }'''
new_catch = '''            catch (Exception)
            {
                ClearPartialDownload(archivePath);
                throw;
            }'''
assert s.count(old_catch) == 1
s = s.replace(old_catch, new_catch)

io.open(p, 'w', encoding='utf-8', newline='\n').write(s)
print('resumable ok')
