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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Services;

/// <summary>
///     Nexus Mods commands, second half: changelog, API key validation, route diagnostics,
///     the launcher image cache and the SMAPI/Nexus update check. Download flows live in
///     <see cref="NexusDownloads"/>.
/// </summary>
public sealed partial class NexusService
{
    public Task<JsonElement?> LoadLauncherUpdateChangelogAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(() =>
        {
            var req = LibraryService.Deserialize(request, LauncherJsonContext.Default.NexusChangelogRequest);
            if (req.ModId <= 0)
                throw new LauncherCommandException("invalid_args", "modId must be a positive integer.");

            //Changelog text reaches the UI through the mod detail files; this legacy command stays null.
            return JsonSerializer.SerializeToElement(new NexusUpdateChangelogResult { ModId = req.ModId }, LauncherJsonContext.Default.NexusUpdateChangelogResult);
        });
    }

    public Task<JsonElement?> ValidateNexusApiKeyAsync()
    {
        return Task.Run<JsonElement?>(async () =>
        {
            var apiKey = _client.RequireApiKey("Not authenticated: no API Key configured");
            var (status, body) = await _client.GetRestAsync(NexusClient.RestBase + "/users/validate.json", CancellationToken.None).ConfigureAwait(false);
            if (status == 401)
                throw new LauncherCommandException("nexus", "Invalid API Key: the Nexus Mods API rejected the provided key (HTTP 401).");
            if (status == 403)
                throw new LauncherCommandException("nexus", $"Forbidden: {(body is null ? "Access denied by Nexus Mods." : body)}");
            if (status != 200)
                throw new LauncherCommandException("nexus", $"API error: HTTP {status} — HTTP {status}");

            using var document = JsonDocument.Parse(body ?? "{}");
            var payload = document.RootElement.Clone();
            var userName = NexusJson.Str(payload, "name") ?? string.Empty;
            var userId = NexusJson.Num(payload, "user_id");
            string? avatarUrl = null;
            if (userId is > 0)
            {
                try
                {
                    var variables = JsonSerializer.Serialize(new { id = userId.Value });
                    using var variablesDocument = JsonDocument.Parse(variables);
                    var avatarPayload = await _client.PostGraphqlLauncherAsync(
                        "LauncherUserAvatar",
                        "query LauncherUserAvatar($id: Int!) { user(id: $id) { memberId name avatar } }",
                        variablesDocument.RootElement.Clone(),
                        CancellationToken.None).ConfigureAwait(false);
                    var avatarData = NexusJson.Obj(avatarPayload, "data");
                    var avatar = avatarData is { } avatarElement ? NexusJson.Str(avatarElement, "avatar") : null;
                    avatarUrl = avatar is null ? null : NormalizeImageUrl(avatar);
                }
                catch (Exception)
                {
                    //Avatar lookup is cosmetic; a failure leaves it null.
                }
            }

            var result = new NexusValidateApiKeyResult
            {
                UserName = userName,
                AvatarUrl = avatarUrl,
                ProfileUrl = userId is > 0 ? $"https://www.nexusmods.com/users/{userId}" : null,
                IsPremium = NexusJson.Bool(payload, "is_premium") ?? false,
                IsLifetimePremium = NexusJson.Bool(payload, "is_lifetime_premium"),
            };
            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.NexusValidateApiKeyResult);
        });
    }

    sealed record ProbeTarget(string RouteId, string Label, string Endpoint);

    static ProbeTarget[] ProbeTargets(bool hasApiKey) => hasApiKey
        ? new[]
        {
            new ProbeTarget("publicGraphql", "Nexus Public GraphQL", NexusClient.GraphQlEndpoint),
            new ProbeTarget("nexusImages", "Nexus Image CDN", NexusClient.ImageCdnBase),
            new ProbeTarget("smapi", "SMAPI", NexusClient.SmapiLookupEndpoint),
            new ProbeTarget("privateGraphql", "Nexus Private GraphQL", NexusClient.GraphQlEndpoint),
            new ProbeTarget("nexusApi", "Nexus REST API", NexusClient.RestBase + "/games/stardewvalley/mods/trending.json"),
        }
        : new[]
        {
            new ProbeTarget("publicGraphql", "Nexus Public GraphQL", NexusClient.GraphQlEndpoint),
            new ProbeTarget("nexusImages", "Nexus Image CDN", NexusClient.ImageCdnBase),
            new ProbeTarget("smapi", "SMAPI", NexusClient.SmapiLookupEndpoint),
        };

    async Task<NexusRouteSnapshot> ProbeRouteAsync(ProbeTarget target, bool hasApiKey)
    {
        var startedAt = Environment.TickCount64;
        string? error = null;
        try
        {
            switch (target.RouteId)
            {
                case "publicGraphql":
                {
                    var variables = new JsonObject
                    {
                        ["count"] = 1,
                        ["filter"] = new JsonObject
                        {
                            ["adultContent"] = new JsonArray(new JsonObject { ["op"] = "EQUALS", ["value"] = false }),
                            ["filter"] = new JsonArray(),
                            ["gameDomainName"] = new JsonArray(new JsonObject { ["op"] = "EQUALS", ["value"] = NexusClient.DefaultGameDomain }),
                            ["name"] = new JsonArray(),
                        },
                        ["offset"] = 0,
                        ["sort"] = new JsonObject { ["createdAt"] = new JsonObject { ["direction"] = "DESC" } },
                    };
                    var client = new NexusClient(null);
                    var response = await client.PostGraphqlPublicAsync("GameModsListing", PublicGraphqlProbeQuery, variables, "https://www.nexusmods.com/", CancellationToken.None).ConfigureAwait(false);
                    if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("errors", out _))
                        throw new LauncherCommandException("nexus", "GraphQL errors in probe response");
                    break;
                }
                case "privateGraphql":
                {
                    var variables = new JsonObject
                    {
                        ["filter"] = new JsonObject
                        {
                            ["adultContent"] = new JsonArray(new JsonObject { ["op"] = "EQUALS", ["value"] = false }),
                            ["gameDomainName"] = new JsonArray(new JsonObject { ["op"] = "EQUALS", ["value"] = NexusClient.DefaultGameDomain }),
                        },
                        ["sort"] = new JsonArray(new JsonObject { ["createdAt"] = new JsonObject { ["direction"] = "DESC" } }),
                        ["offset"] = 0,
                        ["count"] = 1,
                    };
                    var client = new NexusClient(LauncherRuntimeService.LoadOrCreateSettings().NexusApiKey);
                    await client.PostGraphqlLauncherAsync("CatalogMods", PrivateGraphqlProbeQuery, variables, CancellationToken.None).ConfigureAwait(false);
                    break;
                }
                case "nexusImages":
                {
                    using var response = await NexusClient.GetAsync(target.Endpoint, CancellationToken.None).ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    if (!(response.IsSuccessStatusCode || (status >= 300 && status < 500)))
                        error = $"HTTP {status}";
                    break;
                }
                case "smapi":
                {
                    var (status, _) = await NexusClient.PostJsonAsync(target.Endpoint, "{}", CancellationToken.None).ConfigureAwait(false);
                    if (!(status >= 200 && status < 500))
                        error = $"HTTP {status}";
                    break;
                }
                case "nexusApi":
                {
                    var client = new NexusClient(LauncherRuntimeService.LoadOrCreateSettings().NexusApiKey);
                    var (status, _) = await client.GetRestAsync(target.Endpoint, CancellationToken.None).ConfigureAwait(false);
                    if (status is < 200 or >= 300)
                        error = $"HTTP {status}";
                    break;
                }
                default:
                    error = "unknown route";
                    break;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        var latency = Environment.TickCount64 - startedAt;
        if (error is null)
        {
            return new NexusRouteSnapshot
            {
                RouteId = target.RouteId,
                Label = target.Label,
                Endpoint = target.Endpoint,
                Status = "success",
                Attempts = 1,
                Available = true,
                LatencyMs = latency,
                Message = "Connected after 1 attempt.",
            };
        }

        return new NexusRouteSnapshot
        {
            RouteId = target.RouteId,
            Label = target.Label,
            Endpoint = target.Endpoint,
            Status = "warning",
            Attempts = 1,
            Available = false,
            LatencyMs = null,
            Message = $"Failed after 1 attempt: {error}",
        };
    }

    async Task<NexusDiagnosticsResult> RunDiagnosticsAsync(bool forceOffline)
    {
        var result = new NexusDiagnosticsResult();
        if (forceOffline)
        {
            foreach (var target in ProbeTargets(_client.HasApiKey))
            {
                result.Routes.Add(new NexusRouteSnapshot
                {
                    RouteId = target.RouteId,
                    Label = target.Label,
                    Endpoint = target.Endpoint,
                    Status = "warning",
                    Attempts = 3,
                    Available = false,
                    Message = "Forced offline by debug override.",
                });
            }

            return result;
        }

        foreach (var target in ProbeTargets(_client.HasApiKey))
            result.Routes.Add(await ProbeRouteAsync(target, _client.HasApiKey).ConfigureAwait(false));

        return result;
    }

    public Task<JsonElement?> LoadLauncherNexusDiagnosticsAsync()
    {
        return Task.Run<JsonElement?>(async () =>
        {
            var result = await RunDiagnosticsAsync(LauncherRuntimeService.LoadOrCreateSettings().ForceOffline).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.NexusDiagnosticsResult);
        });
    }

    public Task<JsonElement?> RestartLauncherNexusDiagnosticsAsync()
    {
        return Task.Run<JsonElement?>(async () =>
        {
            var result = await RunDiagnosticsAsync(LauncherRuntimeService.LoadOrCreateSettings().ForceOffline).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.NexusDiagnosticsResult);
        });
    }

    public Task<JsonElement?> RetryLauncherNexusDiagnosticsRouteAsync(string routeId)
    {
        return Task.Run<JsonElement?>(async () =>
        {
            var target = ProbeTargets(_client.HasApiKey).FirstOrDefault(item => item.RouteId == routeId)
                ?? throw new LauncherCommandException("invalid_args", $"Unknown launcher Nexus diagnostics route: {routeId}");

            var snapshot = await ProbeRouteAsync(target, _client.HasApiKey).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(new NexusDiagnosticsResult { Routes = new List<NexusRouteSnapshot> { snapshot } }, LauncherJsonContext.Default.NexusDiagnosticsResult);
        });
    }

    public Task<JsonElement?> SetLauncherNexusForceOfflineAsync(bool forceOffline)
    {
        return Task.Run<JsonElement?>(async () =>
        {
            LauncherRuntimeService.SetNexusForceOfflinePreference(forceOffline);
            var result = await RunDiagnosticsAsync(forceOffline).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.NexusDiagnosticsResult);
        });
    }

    // --- image cache ---

    public Task<JsonElement?> ResolveLauncherImageAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(async () =>
        {
            var req = LibraryService.Deserialize(request, LauncherJsonContext.Default.NexusResolveImageRequest);
            var url = req.Url?.Trim() ?? string.Empty;
            if (url.Length == 0)
                throw new LauncherCommandException("invalid_args", "url is required.");

            if (File.Exists(url))
                return JsonSerializer.SerializeToElement(new NexusResolveImageResult { SourceUrl = url, LocalPath = url, MimeType = MimeFromExtension(Path.GetExtension(url)) }, LauncherJsonContext.Default.NexusResolveImageResult);

            var cached = (req.Refresh ?? false) ? null : FindCachedImage(url);
            if (cached is not null)
                return JsonSerializer.SerializeToElement(cached, LauncherJsonContext.Default.NexusResolveImageResult);

            try
            {
                using var response = await NexusClient.GetAsync(url, CancellationToken.None).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new LauncherCommandException("network", $"Failed to fetch launcher image {url}: HTTP {(int)response.StatusCode}");

                var contentType = response.Content?.Headers.ContentType?.MediaType ?? "image/jpeg";
                var bytes = await response.Content.ReadAsByteArrayAsync(CancellationToken.None).ConfigureAwait(false);
                Directory.CreateDirectory(ImagesCacheDirectory);
                var cacheKey = Sha256Hex(url);
                var extension = ExtensionFromMime(contentType) ?? ExtensionFromUrl(url) ?? "jpg";
                var localPath = Path.Combine(ImagesCacheDirectory, $"{cacheKey}.{extension}");
                foreach (var existing in Directory.GetFiles(ImagesCacheDirectory, cacheKey + ".*"))
                    File.Delete(existing);
                await File.WriteAllBytesAsync(localPath, bytes, CancellationToken.None).ConfigureAwait(false);

                return JsonSerializer.SerializeToElement(new NexusResolveImageResult { SourceUrl = url, LocalPath = localPath, MimeType = contentType }, LauncherJsonContext.Default.NexusResolveImageResult);
            }
            catch (LauncherCommandException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var message = ex.Message;
                var lower = message.ToLowerInvariant();
                if (lower.Contains("connection reset") || lower.Contains("econnreset") || lower.Contains("unexpected eof"))
                {
                    ModForgeBridge.Instance?.DispatchEvent(ImageFetchDisconnectedEvent, new JsonObject
                    {
                        ["sourceUrl"] = url,
                        ["modKey"] = req.ModKey,
                        ["error"] = message,
                        ["elapsedMs"] = 0,
                    });
                }

                throw new LauncherCommandException("network", $"Failed to fetch launcher image {url}: {message}");
            }
        });
    }

    public Task<JsonElement?> ResolveCachedLauncherImageAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(() =>
        {
            var req = LibraryService.Deserialize(request, LauncherJsonContext.Default.NexusResolveImageRequest);
            var url = req.Url?.Trim() ?? string.Empty;
            if (url.Length == 0)
                throw new LauncherCommandException("invalid_args", "url is required.");

            if (File.Exists(url))
                return JsonSerializer.SerializeToElement(new NexusResolveImageResult { SourceUrl = url, LocalPath = url, MimeType = MimeFromExtension(Path.GetExtension(url)) }, LauncherJsonContext.Default.NexusResolveImageResult);

            var cached = (req.Refresh ?? false) ? null : FindCachedImage(url);
            return cached is null ? null : JsonSerializer.SerializeToElement(cached, LauncherJsonContext.Default.NexusResolveImageResult);
        });
    }

    public Task<JsonElement?> ClearLauncherImageCacheAsync()
    {
        return Task.Run<JsonElement?>(() =>
        {
            try
            {
                if (Directory.Exists(ImagesCacheDirectory))
                    Directory.Delete(ImagesCacheDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine("NexusService: image cache clear failed: " + ex.Message);
            }

            return (JsonElement?)null;
        });
    }

    static NexusResolveImageResult? FindCachedImage(string url)
    {
        try
        {
            if (!Directory.Exists(ImagesCacheDirectory))
                return null;

            var cacheKey = Sha256Hex(url);
            var existing = Directory.GetFiles(ImagesCacheDirectory, cacheKey + ".*").FirstOrDefault();
            if (existing is null)
                return null;

            return new NexusResolveImageResult
            {
                SourceUrl = url,
                LocalPath = existing,
                MimeType = MimeFromExtension(Path.GetExtension(existing)),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    static string? ExtensionFromMime(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/jpeg" => "jpg",
        "image/png" => "png",
        "image/webp" => "webp",
        "image/gif" => "gif",
        _ => null,
    };

    static string? ExtensionFromUrl(string url)
    {
        var extension = Path.GetExtension(url.Split('?')[0]).TrimStart('.').ToLowerInvariant();
        return extension.Length is > 0 and <= 5 ? extension : null;
    }

    static string MimeFromExtension(string extension) => extension.TrimStart('.').ToLowerInvariant() switch
    {
        "png" => "image/png",
        "webp" => "image/webp",
        "gif" => "image/gif",
        _ => "image/jpeg",
    };
}
