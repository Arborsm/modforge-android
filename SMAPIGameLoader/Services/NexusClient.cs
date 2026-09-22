using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Services;

/// <summary>
///     Nexus Mods HTTP access ported from the desktop http.rs: two header sets
///     (credentialed launcher API vs anonymous browser-impersonation GraphQL), short
///     timeouts, header-driven retry on 429/5xx with a small jitter floor.
/// </summary>
internal sealed class NexusClient
{
    public const string GraphQlEndpoint = "https://api.nexusmods.com/v2/graphql";
    public const string RestBase = "https://api.nexusmods.com/v1";
    public const string ImageCdnBase = "https://staticdelivery.nexusmods.com/";
    public const string SmapiLookupEndpoint = "https://smapi.io/api/v3.0/mods";
    public const string DefaultGameDomain = "stardewvalley";
    public const long DefaultGameId = 1303;
    public const string ModPageUrlTemplate = "https://www.nexusmods.com/stardewvalley/mods/{0}";
    public const string DownloadPopupUrlTemplate = "https://www.nexusmods.com/Core/Libs/Common/Widgets/DownloadPopUp?id={0}&game_id=1303";

    static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(12) };

    readonly string? _apiKey;

    public NexusClient(string? apiKey)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey!.Trim();
    }

    public bool HasApiKey => _apiKey is not null;

    // Latest X-RL-* rate-limit headers seen on any Nexus REST response; the
    // validate command reports these so the account card shows real quotas
    // instead of zeros. Headers arrive on every REST call, not just validate.
    static long? _dailyRemaining, _hourlyRemaining, _dailyResetAt, _hourlyResetAt;

    public (long? DailyRemaining, long? HourlyRemaining, long? DailyResetAt, long? HourlyResetAt) RateLimitSnapshot
        => (_dailyRemaining, _hourlyRemaining, _dailyResetAt, _hourlyResetAt);

    static void CaptureRateLimitHeaders(HttpResponseMessage response)
    {
        _dailyRemaining = ParseInt64Header(response, "X-RL-Daily-Remaining") ?? _dailyRemaining;
        _hourlyRemaining = ParseInt64Header(response, "X-RL-Hourly-Remaining") ?? _hourlyRemaining;
        _dailyResetAt = ParseInt64Header(response, "X-RL-Daily-Reset") ?? _dailyResetAt;
        _hourlyResetAt = ParseInt64Header(response, "X-RL-Hourly-Reset") ?? _hourlyResetAt;
    }

    static long? ParseInt64Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values)
            && long.TryParse(values.FirstOrDefault()?.Trim(), out var value)
                ? value
                : null;

    public string RequireApiKey(string missingMessage)
    {
        if (_apiKey is null)
            throw new LauncherCommandException("no_api_key", missingMessage);

        return _apiKey!;
    }

    static void ApplyLauncherHeaders(HttpRequestMessage request, string apiKey)
    {
        request.Headers.UserAgent.ParseAdd("ModForge Studio/0.1");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("Application-Name", "ModForge Studio");
        request.Headers.Add("Application-Version", "0.1");
        request.Headers.Add("apikey", apiKey);
    }

    static void ApplyPublicGraphqlHeaders(HttpRequestMessage request, string referer, string operationName)
    {
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36");
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
        request.Headers.Add("Priority", "u=1, i");
        request.Headers.Add("sec-ch-ua", "\"Chromium\";v=\"148\", \"Google Chrome\";v=\"148\", \"Not/A)Brand\";v=\"99\"");
        request.Headers.Add("sec-ch-ua-mobile", "?0");
        request.Headers.Add("sec-ch-ua-platform", "\"Windows\"");
        request.Headers.Add("sec-fetch-dest", "empty");
        request.Headers.Add("sec-fetch-mode", "cors");
        request.Headers.Add("sec-fetch-site", "same-site");
        request.Headers.Add("Origin", "https://www.nexusmods.com");
        request.Headers.Referrer = new Uri(referer);
        request.Headers.Add("x-graphql-operationname", operationName);
    }

    sealed record Attempt(HttpResponseMessage Response, string? Body, string? TransportError);

    static async Task<Attempt> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellation)
    {
        try
        {
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellation).ConfigureAwait(false);
            CaptureRateLimitHeaders(response);
            var body = response.Content is null ? null : await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            return new Attempt(response, body, null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Attempt(null!, null, ex.Message);
        }
    }

    static bool IsRetryableStatus(int statusCode) => statusCode is 429 or 408 or 502 or 503 or 504 || statusCode >= 500;

    static async Task RetryDelayAsync(HttpResponseMessage? response, int attempt, CancellationToken cancellation)
    {
        var delayMs = 45 + Random.Shared.Next(36);
        if (response is not null)
        {
            if (response.Headers.RetryAfter?.Delta is { } delta)
                delayMs = (int)Math.Clamp(delta.TotalMilliseconds, 100, 5000);
            else if (response.Headers.TryGetValues("Retry-After", out var retryValues)
                     && int.TryParse(retryValues.FirstOrDefault(), out var retrySeconds) && retrySeconds > 0)
            {
                delayMs = Math.Min(retrySeconds * 1000, 5000);
            }
        }

        await Task.Delay(delayMs * (attempt + 1), cancellation).ConfigureAwait(false);
    }

    /// <summary>POSTs a GraphQL payload with the launcher (credentialed) header set.</summary>
    public async Task<JsonElement> PostGraphqlLauncherAsync(string operationName, string query, object variables, CancellationToken cancellation)
    {
        var apiKey = RequireApiKey("Configure a Nexus API key before querying Nexus Mods.");
        var payload = JsonSerializer.Serialize(new { operationName, query, variables });
        return (await SendGraphqlAsync(payload, apiKeyHeaders: true, apiKey, GraphQlEndpoint, GraphQlEndpoint, cancellation).ConfigureAwait(false)).Item2!.Value;
    }

    /// <summary>POSTs a GraphQL payload with the anonymous browser header set.</summary>
    public async Task<JsonElement> PostGraphqlPublicAsync(string operationName, string query, object variables, string referer, CancellationToken cancellation)
    {
        var payload = JsonSerializer.Serialize(new { operationName, query, variables });
        return (await SendGraphqlAsync(payload, apiKeyHeaders: false, null, GraphQlEndpoint, referer, cancellation, operationName).ConfigureAwait(false)).Item2!.Value;
    }

    async Task<(int Status, JsonElement? Body, string? Error)> SendGraphqlAsync(
        string payload,
        bool apiKeyHeaders,
        string? apiKey,
        string endpoint,
        string referer,
        CancellationToken cancellation,
        string? operationName = null)
    {
        for (var attempt = 0; attempt < 5; attempt += 1)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            if (apiKeyHeaders)
                ApplyLauncherHeaders(request, apiKey!);
            else
                ApplyPublicGraphqlHeaders(request, referer, operationName ?? string.Empty);

            var result = await SendAsync(SharedClient, request, cancellation).ConfigureAwait(false);
            if (result.TransportError is not null)
            {
                if (attempt == 4)
                    return (0, null, result.TransportError);

                await Task.Delay(300 * (attempt + 1), cancellation).ConfigureAwait(false);
                continue;
            }

            var status = (int)result.Response.StatusCode;
            if (!result.Response.IsSuccessStatusCode)
            {
                if (IsRetryableStatus(status) && attempt < 4)
                {
                    await RetryDelayAsync(result.Response, attempt, cancellation);
                    continue;
                }

                return (status, null, $"HTTP {status}");
            }

            try
            {
                using var document = JsonDocument.Parse(result.Body ?? "{}");
                return (status, document.RootElement.Clone(), null);
            }
            catch (Exception ex)
            {
                return (status, null, ex.Message);
            }
        }

        return (0, null, "unreachable");
    }

    /// <summary>Throws the first GraphQL errors[0].message when the payload reports errors.</summary>
    public static void ThrowOnGraphqlErrors(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var error in errors.EnumerateArray())
            {
                var message = NexusJson.Str(error, "message")?.Trim();
                if (!string.IsNullOrEmpty(message))
                    throw new LauncherCommandException("nexus", message!);
            }
        }
    }

    /// <summary>GETs a Nexus REST v1 endpoint with the launcher header set; returns (status, body).</summary>
    public async Task<(int Status, string? Body)> GetRestAsync(string url, CancellationToken cancellation)
    {
        var apiKey = RequireApiKey("Configure a Nexus API key before querying the Nexus REST API.");
        for (var attempt = 0; attempt < 5; attempt += 1)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyLauncherHeaders(request, apiKey);
            var result = await SendAsync(SharedClient, request, cancellation).ConfigureAwait(false);
            if (result.TransportError is not null)
            {
                if (attempt == 4)
                    return (0, result.TransportError);

                await Task.Delay(300 * (attempt + 1), cancellation).ConfigureAwait(false);
                continue;
            }

            var status = (int)result.Response.StatusCode;
            if (IsRetryableStatus(status) && attempt < 4)
            {
                await RetryDelayAsync(result.Response, attempt, cancellation);
                continue;
            }

            return (status, result.Body);
        }

        return (0, null);
    }

    /// <summary>Plain GET used for image downloads and connectivity probes.</summary>
    public static async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellation)
    {
        return await SharedClient.GetAsync(url, cancellation).ConfigureAwait(false);
    }

    public static async Task<(int Status, string? Body)> PostJsonAsync(string url, string payload, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        try
        {
            using var response = await SharedClient.SendAsync(request, cancellation).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            return ((int)response.StatusCode, body);
        }
        catch (Exception ex)
        {
            return (0, ex.Message);
        }
    }
}
