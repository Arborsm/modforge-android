using Android.Content;
using Android.Net;
using SMAPIGameLoader.Launcher;
using SMAPIGameLoader.Tool;
using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Services;

/// <summary>
///     Nexus Mods SSO over the wss://sso.nexusmods.com websocket, ported from the desktop
///     sso.rs: handshake {"id", "token", "protocol":2} → connection response with the
///     authorization URL → browser → authorization response carrying the API key. The key
///     is validated and persisted into launcher settings; snapshots drive the front-end.
/// </summary>
public sealed class NexusSso
{
    const string SsoWebSocketUrl = "wss://sso.nexusmods.com";
    const string SsoAuthUrlBase = "https://www.nexusmods.com/sso";
    static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromSeconds(120);

    static readonly object StateLock = new();
    static string _status = "idle";
    static string? _errorKind;
    static string? _errorMessage;
    static string? _userName;
    static bool _isPremium;
    static string? _ssoId;
    static string? _connectionToken;
    static ulong _generation;
    static bool _cancelled;
    static bool _running;

    // --- commands ---

    public Task<JsonElement?> StartNexusSsoAsync()
    {
        lock (StateLock)
        {
            if (_running)
                throw new LauncherCommandException("sso_busy", "SSO flow already in progress.");

            var ssoId = Guid.NewGuid().ToString();
            _generation += 1;
            _status = "connecting";
            _ssoId = ssoId;
            _connectionToken = null;
            _cancelled = false;
            _errorKind = null;
            _errorMessage = null;
            _userName = null;
            _isPremium = false;
            _running = true;

            var generation = _generation;
            var threadSsoId = ssoId;
            _ = Task.Run(async () =>
            {
                string? apiKey = null;
                string? failureKind = null;
                string? failureMessage = null;
                try
                {
                    apiKey = await RunSsoFlowAsync(threadSsoId, generation).ConfigureAwait(false);
                }
                catch (LauncherCommandException ex)
                {
                    failureKind = ex.Code;
                    failureMessage = ex.Message;
                }
                catch (Exception ex)
                {
                    failureKind = "networkError";
                    failureMessage = ex.Message;
                }

                lock (StateLock)
                {
                    if (_generation != generation || _ssoId != threadSsoId)
                        return;

                    // Persist BEFORE exposing "authorized": the front-end reloads
                    // settings and re-validates the moment it sees the authorized
                    // snapshot, so the key must already be on disk — otherwise it
                    // reads the previous (empty) key and reports "API unavailable"
                    // until the next cold start.
                    if (apiKey is not null)
                    {
                        try
                        {
                            LauncherRuntimeService.SetNexusApiKey(apiKey);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("NexusSso: persisting the authorized key failed: " + ex);
                            apiKey = null;
                            failureKind = "persistFailed";
                            failureMessage = ex.Message;
                        }
                    }

                    _connectionToken = null;
                    _running = false;
                    if (apiKey is not null)
                    {
                        _status = "authorized";
                        _errorKind = null;
                        _errorMessage = null;
                    }
                    else
                    {
                        _status = "failed";
                        _errorKind = failureKind ?? "networkError";
                        _errorMessage = failureMessage ?? "SSO failed.";
                    }
                }

                // The flow opened the in-app browser for authorization; settle it
                // either way so the user lands back on the configuration page.
                CloseAuthorizationBrowser();

                if (apiKey is not null)
                {
                    //Profile lookup is cosmetic (name/premium badge); authorization
                    //already succeeded and the key is persisted.
                    try
                    {
                        await ValidateProfileAsync(apiKey, generation, threadSsoId).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        lock (StateLock)
                        {
                            if (_generation == generation && _ssoId == threadSsoId)
                                _errorMessage = $"Validation failed: {ex.Message}";
                        }
                    }
                }
            });

            var startResult = new SsoStartResult { SsoId = ssoId, Status = _status };
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(startResult, LauncherJsonContext.Default.SsoStartResult));
        }
    }

    public Task<JsonElement?> GetNexusSsoStatusAsync()
    {
        return Task.FromResult(SnapshotElement());
    }

    public Task<JsonElement?> CancelNexusSsoAsync()
    {
        lock (StateLock)
        {
            _generation += 1;
            _cancelled = true;
            _status = "failed";
            _errorKind = "cancelled";
            _errorMessage = "SSO cancelled by user.";
            _connectionToken = null;
            _running = false;
        }

        return Task.FromResult(SnapshotElement());
    }

    static JsonElement? SnapshotElement()
    {
        lock (StateLock)
        {
            var snapshot = new SsoSnapshot
            {
                Status = _status,
                ErrorKind = _errorKind,
                ErrorMessage = _errorMessage,
                UserName = _userName,
                IsPremium = _isPremium,
                SsoId = _ssoId,
            };
            return JsonSerializer.SerializeToElement(snapshot, LauncherJsonContext.Default.SsoSnapshot);
        }
    }

    static string? TrimOrNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    static bool IsCancelled(ulong generation)
    {
        lock (StateLock)
        {
            return _generation != generation || _cancelled;
        }
    }

    static string? ConnectionToken(ulong generation)
    {
        lock (StateLock)
        {
            return _generation != generation || _cancelled ? null : _connectionToken;
        }
    }

    static bool StoreConnectionToken(ulong generation, string token)
    {
        lock (StateLock)
        {
            if (_generation != generation || _cancelled)
                return false;

            _connectionToken = token;
            return true;
        }
    }

    static void EnterAwaitingAuthorization(ulong generation)
    {
        lock (StateLock)
        {
            if (_generation != generation || _cancelled)
                throw new LauncherCommandException("cancelled", "Cancelled.");

            _status = "awaitingAuthorization";
        }
    }

    // --- websocket flow ---

    static async Task<string?> RunSsoFlowAsync(string ssoId, ulong generation)
    {
        if (IsCancelled(generation))
            throw new LauncherCommandException("cancelled", "Cancelled.");

        //Connect with retries within the connection timeout.
        using var ws = new ClientWebSocket { Options = { KeepAliveInterval = TimeSpan.FromSeconds(30) } };
        var startedAt = Environment.TickCount64;
        string lastError = string.Empty;
        while (Environment.TickCount64 - startedAt < ConnectionTimeout.TotalMilliseconds)
        {
            if (IsCancelled(generation))
                throw new LauncherCommandException("cancelled", "Cancelled.");

            try
            {
                await ws.ConnectAsync(new System.Uri(SsoWebSocketUrl), CancellationToken.None).ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                await Task.Delay(500).ConfigureAwait(false);
            }
        }

        if (ws.State != WebSocketState.Open)
            throw new LauncherCommandException("connectionTimeout", $"Connection timed out: {lastError}");

        if (IsCancelled(generation))
            throw new LauncherCommandException("cancelled", "Cancelled.");

        var handshake = JsonSerializer.Serialize(new
        {
            id = ssoId,
            token = ConnectionToken(generation),
            protocol = 2,
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(handshake), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);

        var connectionMessage = await ReceiveWithTimeoutAsync(ws, ConnectionTimeout, generation).ConfigureAwait(false)
            ?? throw new LauncherCommandException("connectionTimeout", "No SSO connection response.");
        var connection = ParseSsoResponse(connectionMessage);

        if (connection.ConnectionToken is { } token)
        {
            if (!StoreConnectionToken(generation, token))
                throw new LauncherCommandException("cancelled", "Cancelled.");
        }

        if (connection.ApiKey is { } immediateKey)
            return immediateKey;

        var authorizationUrl = connection.AuthorizationUrl ?? $"{SsoAuthUrlBase}?id={ssoId}";
        EnterAwaitingAuthorization(generation);
        OpenBrowser(authorizationUrl);

        var authorizationMessage = await ReceiveWithTimeoutAsync(ws, AuthorizationTimeout, generation).ConfigureAwait(false)
            ?? throw new LauncherCommandException("authorizationTimeout", "No authorization response.");
        if (IsCancelled(generation))
            throw new LauncherCommandException("cancelled", "Cancelled.");

        return ParseApiKey(authorizationMessage)
            ?? throw new LauncherCommandException("networkError", "Missing api_key.");
    }

    /// <summary>Receives one text frame within the total timeout; returns null on timeout/cancel/close.</summary>
    static async Task<string?> ReceiveWithTimeoutAsync(ClientWebSocket ws, TimeSpan timeout, ulong generation)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
        var buffer = new byte[8192];
        try
        {
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                if (IsCancelled(generation))
                    return null;

                var result = await ws.ReceiveAsync(buffer, linked.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                if (result.MessageType == WebSocketMessageType.Text)
                    return Encoding.UTF8.GetString(buffer, 0, result.Count);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (WebSocketException)
        {
            return null;
        }

        return null;
    }

    sealed record SsoResponse(string? AuthorizationUrl, string? ConnectionToken, string? ApiKey);

    static SsoResponse ParseSsoResponse(string message)
    {
        var trimmed = message.Trim();
        if (!trimmed.StartsWith('{'))
        {
            if (trimmed.Length == 0)
                throw new LauncherCommandException("networkError", "Missing SSO connection response.");

            return new SsoResponse(null, null, trimmed);
        }

        using var document = JsonDocument.Parse(trimmed);
        var payload = document.RootElement.Clone();
        if (NexusJson.Bool(payload, "success") != true)
        {
            var error = NexusJson.Str(payload, "error") ?? "Unknown";
            throw new LauncherCommandException("networkError", ClassifySsoError(error));
        }

        var data = NexusJson.Obj(payload, "data") ?? default;
        return new SsoResponse(
            TrimOrNull(NexusJson.Str(data, "url")),
            TrimOrNull(NexusJson.Str(data, "connection_token")),
            TrimOrNull(NexusJson.Str(data, "api_key")));
    }

    static string? ParseApiKey(string message)
    {
        var trimmed = message.Trim();
        if (!trimmed.StartsWith('{'))
            return trimmed.Length == 0 ? null : trimmed;

        using var document = JsonDocument.Parse(trimmed);
        var payload = document.RootElement.Clone();
        if (NexusJson.Bool(payload, "success") != true)
        {
            var error = NexusJson.Str(payload, "error") ?? "Unknown";
            throw new LauncherCommandException("networkError", ClassifySsoError(error));
        }

        var data = NexusJson.Obj(payload, "data") ?? default;
        return TrimOrNull(NexusJson.Str(data, "api_key")) ?? TrimOrNull(NexusJson.Str(payload, "api_key"));
    }

    static string ClassifySsoError(string error)
    {
        var normalized = error.ToLowerInvariant();
        if (normalized.Contains("timeout"))
            return "authorizationTimeout: " + error;
        if (normalized.Contains("refused"))
            return "connectionRefused: " + error;
        return "networkError: " + error;
    }

    static void OpenBrowser(string url)
    {
        try
        {
            // Prefer the in-app browser: the app stays in the foreground, so the
            // SSO websocket is never background-frozen mid-handshake, and the
            // overlay closes itself when the flow settles. The external browser
            // is the fallback when the launcher view is not ready.
            var activity = LauncherActivity.Instance;
            if (activity?.OpenInAppBrowser(url) == true)
                return;

            var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(url));
            intent.AddFlags(ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            Console.WriteLine("NexusSso: browser open failed: " + ex.Message);
        }
    }

    /// <summary>Closes the in-app browser the authorization step opened (no-op when absent).</summary>
    static void CloseAuthorizationBrowser()
    {
        try
        {
            LauncherActivity.Instance?.CloseInAppBrowser();
        }
        catch (Exception ex)
        {
            Console.WriteLine("NexusSso: closing the authorization browser failed: " + ex.Message);
        }
    }

    static async Task ValidateProfileAsync(string apiKey, ulong generation, string threadSsoId)
    {
        string? userName = null;
        bool isPremium = false;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            using var request = new HttpRequestMessage(HttpMethod.Get, NexusClient.RestBase + "/users/validate.json");
            request.Headers.UserAgent.ParseAdd("ModForge Studio/0.1");
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Add("Application-Name", "ModForge Studio");
            request.Headers.Add("Application-Version", "0.1");
            request.Headers.Add("apikey", apiKey);
            using var response = await client.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);
                userName = NexusJson.Str(document.RootElement.Clone(), "name");
                isPremium = NexusJson.Bool(document.RootElement.Clone(), "is_premium") ?? false;
            }
        }
        catch (Exception)
        {
            //Validation is cosmetic; the authorization itself already succeeded.
        }

        // The key was already persisted before the status flipped to authorized;
        // this pass only fills the profile badge.
        lock (StateLock)
        {
            if (_generation != generation || _ssoId != threadSsoId)
                return;

            _userName = userName;
            _isPremium = isPremium;
        }
    }
}
