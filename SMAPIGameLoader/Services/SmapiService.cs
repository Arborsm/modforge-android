using SMAPIGameLoader.Bridge;
using SMAPIGameLoader.Launcher;
using SMAPIGameLoader.Tool;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Services;

/// <summary>
///     SMAPI update flow for the Android fork: compares the installed SMAPI assembly version
///     against the latest <c>NRTnarathip/SMAPI-Android-1.6</c> release, downloads the release
///     zip with SHA-256 verification, and installs it by stripping the archive's root folder
///     into the game assemblies directory (upstream SMAPIInstaller semantics). Progress is
///     emitted on the <c>launcher://smapi-update-progress</c> event the front-end listens to.
///     The desktop PC-installer execution flow does not exist on Android.
/// </summary>
public sealed class SmapiService
{
    const string LatestReleaseUrl = "https://api.github.com/repos/NRTnarathip/SMAPI-Android-1.6/releases/latest";
    const string ProgressEventName = "launcher://smapi-update-progress";
    const long MaxInstallerBytes = 10 * 1024 * 1024; //Android SMAPI zips are ~1.5MB; PC installers are ~40MB

    static string CacheFilePath => Path.Combine(FileTool.ExternalFilesDir, "ModForge", "launcher", "smapi-update-cache.json");

    // --- commands ---

    public Task<JsonElement?> CheckSmapiUpdateAsync()
    {
        return Task.Run<JsonElement?>(async () =>
        {
            //A missing SMAPI is a normal "update available" state on Android: the card
            //offers the fresh-install download instead of failing the whole check.
            var installed = SMAPIInstaller.IsInstalled;
            var installedVersion = installed ? SMAPIInstaller.GetCurrentVersion()?.ToString() ?? "0.0.0" : "0.0.0";
            var gameVersion = StardewApkTool.CurrentGameVersion?.ToString() ?? string.Empty;

            var release = await LoadLatestReleaseAsync().ConfigureAwait(false);
            var updateAvailable = !installed || LauncherJsonHelper.VersionIsNewer(installedVersion, release.Version);

            var result = new SmapiUpdateCheckResult
            {
                InstalledVersion = installedVersion,
                GameVersion = gameVersion,
                LatestStableVersion = release.Version,
                TargetVersion = release.Version,
                UpdateAvailable = updateAvailable,
                VersionSource = "github",
                RequiredByMods = ScanRequiredByMods(installedVersion),
            };

            if (updateAvailable)
            {
                result.Download = new SmapiUpdateDownloadInfo
                {
                    Source = "github",
                    Url = release.AssetUrl,
                    Sha256 = release.AssetSha256,
                    SizeBytes = release.AssetSizeBytes,
                    AssetName = release.AssetName,
                };
            }

            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.SmapiUpdateCheckResult);
        });
    }

    public Task<JsonElement?> InstallSmapiUpdateAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(async () =>
        {
            var parsed = LibraryService.Deserialize(request, LauncherJsonContext.Default.InstallSmapiUpdateRequest);
            var targetVersion = parsed.TargetVersion.Trim();
            if (targetVersion.Length == 0)
                throw new LauncherCommandException("invalid_args", "targetVersion is required.");

            var localFilePath = parsed.LocalFilePath?.Trim();
            string installerZipPath;
            if (!string.IsNullOrEmpty(localFilePath))
            {
                if (!File.Exists(localFilePath))
                    throw new LauncherCommandException("not_found", $"localFilePath {localFilePath} is not a readable file.");

                installerZipPath = localFilePath!;
                if (!string.IsNullOrEmpty(parsed.ExpectedSha256))
                    VerifySha256(installerZipPath, parsed.ExpectedSha256!);
            }
            else
            {
                var downloadUrl = parsed.DownloadUrl?.Trim();
                if (string.IsNullOrEmpty(downloadUrl))
                    throw new LauncherCommandException("invalid_args", "downloadUrl is required when no localFilePath is provided.");
                if (!downloadUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !downloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    throw new LauncherCommandException("invalid_args", "downloadUrl must use http or https.");
                var expectedSha256 = NormalizeExpectedSha256(parsed.ExpectedSha256)
                    ?? throw new LauncherCommandException("invalid_args", "expectedSha256 is required when downloading the SMAPI installer.");

                installerZipPath = Path.Combine(Path.GetTempPath(), $"modforge-smapi-update-{Guid.NewGuid():N}.zip");
                try
                {
                    await DownloadInstallerAsync(downloadUrl, installerZipPath).ConfigureAwait(false);
                    EmitProgress("verifying", 100, "Verified SMAPI installer checksum");
                    VerifySha256(installerZipPath, expectedSha256);
                }
                catch (Exception)
                {
                    TryDelete(installerZipPath);
                    throw;
                }
            }

            var size = new FileInfo(installerZipPath).Length;
            if (size > MaxInstallerBytes)
            {
                if (string.IsNullOrEmpty(localFilePath))
                    TryDelete(installerZipPath);
                throw new LauncherCommandException("invalid_archive", $"The SMAPI installer archive is {size} bytes; Android SMAPI zips are much smaller. This is probably a PC installer.");
            }

            EmitProgress("extracting", null, $"Extracting SMAPI {targetVersion} installer");
            InstallFromZipFile(installerZipPath);
            if (string.IsNullOrEmpty(localFilePath))
                TryDelete(installerZipPath);

            var installedVersion = SMAPIInstaller.GetCurrentVersion()?.ToString() ?? targetVersion;
            var result = new InstallSmapiUpdateResult { Success = true, InstalledVersion = installedVersion };
            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.InstallSmapiUpdateResult);
        });
    }

    public Task<JsonElement?> FindSmapiInstallerDownloadsAsync()
    {
        return Task.Run<JsonElement?>(() =>
        {
            var candidates = new List<SmapiInstallerDownloadCandidate>();
            var scanDirectories = new List<string>();
            var settings = LauncherRuntimeService.LoadOrCreateSettings();
            if (!string.IsNullOrWhiteSpace(settings.DownloadPath))
                scanDirectories.Add(settings.DownloadPath!);
            scanDirectories.Add(SandboxFileTool.PickedFilesDir);
            scanDirectories.Add(FileTool.ExternalFilesDir);

            var seenDirectories = new HashSet<string>(StringComparer.Ordinal);
            var smapiVersionRegex = new Regex(@"SMAPI-(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (var directory in scanDirectories)
            {
                if (!seenDirectories.Add(Path.TrimEndingDirectorySeparator(directory)) || !Directory.Exists(directory))
                    continue;

                foreach (var filePath in Directory.GetFiles(directory, "*.zip"))
                {
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.Length > MaxInstallerBytes)
                        continue;

                    var fileName = Path.GetFileName(filePath);
                    var match = smapiVersionRegex.Match(fileName);
                    if (!match.Success)
                        continue;

                    var version = match.Groups[1].Value;
                    candidates.Add(new SmapiInstallerDownloadCandidate
                    {
                        Path = filePath,
                        FileName = fileName,
                        Version = version,
                        SizeBytes = fileInfo.Length,
                        DoubleZipped = false,
                        Naming = "github",
                        Compatible = LauncherJsonHelper.VersionIsNewer(LauncherRequirements.MinimumSmapiVersion.ToString(), version) ? false : true,
                        SatisfiesTarget = (bool?)null,
                    });
                }
            }

            //newest version first, ties by file name
            candidates.Sort((left, right) =>
            {
                var newer = LauncherJsonHelper.VersionIsNewer(left.Version, right.Version);
                var older = LauncherJsonHelper.VersionIsNewer(right.Version, left.Version);
                if (newer != older)
                    return newer ? -1 : 1;
                return string.CompareOrdinal(left.FileName, right.FileName);
            });

            var result = new FindSmapiInstallerDownloadsResult { Candidates = candidates };
            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.FindSmapiInstallerDownloadsResult);
        });
    }

    // --- release lookup (with a 30 minute disk cache, like the desktop flow) ---

    sealed record SmapiReleaseInfo(string Version, string AssetName, string AssetUrl, long AssetSizeBytes, string? AssetSha256);

    static async Task<SmapiReleaseInfo> LoadLatestReleaseAsync()
    {
        if (TryReadCache() is { } cached)
            return cached;

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ModForge-Android/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var response = await client.GetAsync(LatestReleaseUrl).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new LauncherCommandException("network", $"SMAPI release lookup failed: HTTP {(int)response.StatusCode}.");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var root = document.RootElement;
        var tagName = root.TryGetProperty("tag_name", out var tagNameElement) && tagNameElement.ValueKind == JsonValueKind.String
            ? tagNameElement.GetString()
            : null;
        if (string.IsNullOrEmpty(tagName))
            throw new LauncherCommandException("network", "SMAPI GitHub release response is missing tag_name.");

        var version = CleanReleaseVersion(tagName!);

        string? assetName = null;
        string? assetUrl = null;
        long assetSize = 0;
        string? assetSha256 = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
                    continue;
                if (!name!.StartsWith("SMAPI-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;

                assetName = name;
                assetUrl = url;
                assetSize = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : 0;
                assetSha256 = NormalizeExpectedSha256(asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null);
                break;
            }
        }

        if (assetName is null || assetUrl is null)
            throw new LauncherCommandException("network", "The latest SMAPI Android release does not contain a SMAPI-*.zip asset.");

        var release = new SmapiReleaseInfo(version, assetName, assetUrl, assetSize, assetSha256);
        WriteCache(release);
        return release;
    }

    /// <summary>Strips the v/SMAPI prefix from a release tag ("SMAPI-4.1.10.2" → "4.1.10.2").</summary>
    internal static string CleanReleaseVersion(string tag)
    {
        var value = tag.Trim();
        if (value.StartsWith("SMAPI-", StringComparison.OrdinalIgnoreCase))
            value = value["SMAPI-".Length..];
        else if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            value = value[1..];

        return value.Trim();
    }

    static SmapiReleaseInfo? TryReadCache()
    {
        try
        {
            if (!File.Exists(CacheFilePath))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(CacheFilePath));
            var root = document.RootElement;
            if (!root.TryGetProperty("expiresAtMs", out var expiresElement) || !expiresElement.TryGetUInt64(out var expiresAtMs))
                return null;
            if (expiresAtMs <= LauncherJsonHelper.CurrentTimestampMs())
                return null;
            if (!root.TryGetProperty("release", out var releaseElement))
                return null;

            return new SmapiReleaseInfo(
                releaseElement.GetProperty("version").GetString() ?? string.Empty,
                releaseElement.GetProperty("assetName").GetString() ?? string.Empty,
                releaseElement.GetProperty("assetUrl").GetString() ?? string.Empty,
                releaseElement.GetProperty("assetSizeBytes").GetInt64(),
                releaseElement.TryGetProperty("assetSha256", out var shaElement) && shaElement.ValueKind == JsonValueKind.String ? shaElement.GetString() : null);
        }
        catch (Exception)
        {
            return null; //cache is an optimization, never a correctness gate
        }
    }

    static void WriteCache(SmapiReleaseInfo release)
    {
        try
        {
            var now = LauncherJsonHelper.CurrentTimestampMs();
            var cache = new JsonObject
            {
                ["checkedAtMs"] = now,
                ["expiresAtMs"] = now + 30u * 60u * 1000u,
                ["release"] = new JsonObject
                {
                    ["version"] = release.Version,
                    ["assetName"] = release.AssetName,
                    ["assetUrl"] = release.AssetUrl,
                    ["assetSizeBytes"] = release.AssetSizeBytes,
                    ["assetSha256"] = release.AssetSha256,
                },
            };
            LauncherJsonHelper.WriteJsonFile(CacheFilePath, cache);
        }
        catch (Exception ex)
        {
            Console.WriteLine("SmapiService: cache write failed: " + ex.Message);
        }
    }

    List<SmapiUpdateRequiredByMod> ScanRequiredByMods(string installedVersion)
    {
        var required = new List<SmapiUpdateRequiredByMod>();
        try
        {
            var settings = LauncherRuntimeService.LoadOrCreateSettings();
            var modsPath = settings.ModsPath?.Trim();
            if (string.IsNullOrEmpty(modsPath) || !Directory.Exists(modsPath))
                return required;

            var scan = new LibraryService().ScanLibraryAtPath(modsPath!);
            foreach (var mod in scan.Mods)
            {
                if (mod.MinimumApiVersion is null)
                    continue;
                if (!LauncherJsonHelper.VersionIsNewer(installedVersion, mod.MinimumApiVersion))
                    continue;

                required.Add(new SmapiUpdateRequiredByMod
                {
                    ModId = mod.UniqueId ?? mod.Id,
                    ModName = mod.Name,
                    MinimumApiVersion = mod.MinimumApiVersion,
                });
            }

            required.Sort((left, right) => string.Compare(left.ModName, right.ModName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Console.WriteLine("SmapiService: requiredByMods scan skipped: " + ex.Message);
        }

        return required;
    }

    // --- download + install ---

    async Task DownloadInstallerAsync(string downloadUrl, string destinationPath)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ModForge-Android/1.0");
        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new LauncherCommandException("network", $"Failed to download SMAPI installer: HTTP {(int)response.StatusCode}.");

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        var buffer = new byte[81920];
        long downloadedBytes = 0;
        await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write);
        int read;
        while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            downloadedBytes += read;
            if (totalBytes > 0)
            {
                var percent = (int)Math.Round(downloadedBytes * 100.0 / totalBytes);
                EmitProgress("downloading", percent, $"Downloading SMAPI installer ({downloadedBytes}/{totalBytes} bytes)");
            }
        }

        if (totalBytes <= 0)
            EmitProgress("downloading", null, $"Downloading SMAPI installer ({downloadedBytes} bytes)");
    }

    static void VerifySha256(string filePath, string expectedSha256)
    {
        var expected = NormalizeExpectedSha256(expectedSha256)
            ?? throw new LauncherCommandException("invalid_args", "expectedSha256 must be a 64-character hex SHA-256 digest (optionally prefixed with \"sha256:\").");

        using var stream = File.OpenRead(filePath);
        var digestBytes = SHA256.HashData(stream);
        var actual = Convert.ToHexString(digestBytes).ToLowerInvariant();
        if (actual != expected)
            throw new LauncherCommandException("checksum", $"The SMAPI installer checksum does not match (expected {expected}, got {actual}).");
    }

    static string? NormalizeExpectedSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["sha256:".Length..];

        normalized = normalized.Trim();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            return null;

        return normalized.ToLowerInvariant();
    }

    /// <summary>
    ///     Installs the SMAPI Android zip by stripping the archive's first directory component
    ///     into the game assemblies directory (upstream SMAPIInstaller.InstallSMAPIFromZipFile).
    /// </summary>
    static void InstallFromZipFile(string smapiZipFilePath)
    {
        var assembliesDirectory = GameAssemblyManager.AssembliesDirPath;
        var hasSmapiAssembly = false;
        using (var zip = ZipFile.OpenRead(smapiZipFilePath))
        {
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var fullName = entry.FullName.Replace('\\', '/');
                var rootSeparator = fullName.IndexOf('/');
                var relativePath = rootSeparator >= 0 ? fullName[(rootSeparator + 1)..] : fullName;
                if (relativePath.Length == 0)
                    continue;
                if (string.Equals(relativePath, SMAPIInstaller.StardewModdingAPIFileName, StringComparison.OrdinalIgnoreCase))
                    hasSmapiAssembly = true;

                var destinationPath = Path.Combine(assembliesDirectory, relativePath);
                ZipFileTool.Extract(entry, destinationPath);
            }
        }

        if (!hasSmapiAssembly)
            throw new LauncherCommandException("invalid_archive", "The SMAPI installer archive does not contain StardewModdingAPI.dll; this is not a valid SMAPI Android package.");

        FileTool.ClearCache();
    }

    static void EmitProgress(string phase, int? percent, string message)
    {
        try
        {
            var bridge = ModForgeBridge.Instance;
            if (bridge is null)
                return;

            var payload = new JsonObject
            {
                ["phase"] = phase,
                ["percent"] = percent,
                ["message"] = message,
            };
            bridge.DispatchEvent(ProgressEventName, payload);
        }
        catch (Exception ex)
        {
            Console.WriteLine("SmapiService: progress dispatch failed: " + ex.Message);
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine("SmapiService: temp cleanup failed for " + path + ": " + ex.Message);
        }
    }
}
