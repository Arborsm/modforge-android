using Android.App;
using Android.Webkit;
using Android.Runtime;
using LauncherActivity = SMAPIGameLoader.Launcher.LauncherActivity;
using SMAPIGameLoader.Services;
using SMAPIGameLoader.Tool;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Bridge;

/// <summary>
///     JS bridge object injected into the WebView as <c>window.modforgeBridge</c>. Commands
///     arrive on a WebView binder thread and are only enqueued here; a single background
///     worker executes them serially (never on the entry thread) and answers through the
///     <c>window.__modforgeDispatch</c> sink with <c>{id, ok, payload}</c> frames. Host
///     events travel through the same sink as <c>{event, payload}</c> frames.
/// </summary>
public sealed class ModForgeBridge : Java.Lang.Object
{
    public const string PickFileCommand = "android:pick_file";
    public const string PickDirectoryCommand = "android:pick_dir";
    public const string CreateDocumentCommand = "android:create_document";
    public const string SetSystemBarsCommand = "android:set_system_bars";

    /// <summary>Single bridge instance; services use it to push event frames.</summary>
    public static ModForgeBridge? Instance { get; private set; }

    readonly LauncherActivity _activity;
    readonly LauncherCommandServices _services;
    readonly BlockingCollection<PendingCommand> _queue = new();

    readonly record struct PendingCommand(string Command, string ArgsJson, string CallbackId);

    public ModForgeBridge(LauncherActivity activity)
    {
        _activity = activity;
        _services = LauncherCommandServices.Loaded;
        Instance = this;
        _ = Task.Run(ProcessLoopAsync);
    }

    [JavascriptInterface]
    [Java.Interop.Export("invokeCommand")]
    public void InvokeCommand(string command, string argsJson, string callbackId)
    {
        //Entry point only enqueues; blocking IO/network happen on the worker.
        _queue.TryAdd(new PendingCommand(command ?? string.Empty, argsJson ?? string.Empty, callbackId ?? string.Empty));
    }

    /// <summary>
    ///     Front-end acknowledgement for an <c>android:back</c> event: the SPA closed its
    ///     topmost overlay, so the activity must not move the task to the background.
    /// </summary>
    [JavascriptInterface]
    [Java.Interop.Export("backHandled")]
    public void BackHandled()
    {
        _activity.MarkBackHandled();
    }

    /// <summary>Pushes one host event frame to the front-end; callable from services.</summary>
    public void DispatchEvent(string eventName, JsonNode payload)
    {
        _activity.DispatchEventToJs(eventName, payload.ToJsonString());
    }

    async Task ProcessLoopAsync()
    {
        foreach (var pending in _queue.GetConsumingEnumerable())
        {
            JsonObject frame;
            try
            {
                var payload = await InvokeAsync(pending).ConfigureAwait(false);
                frame = BuildFrame(pending.CallbackId, true, payload);
            }
            catch (LauncherCommandUnavailableException ex)
            {
                frame = ErrorFrame(pending.CallbackId, ex.Message);
            }
            catch (LauncherCommandException ex)
            {
                frame = ErrorFrame(pending.CallbackId, ex.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine("ModForgeBridge: command '" + pending.Command + "' failed: " + ex);
                frame = ErrorFrame(pending.CallbackId, ex.Message);
            }

            DispatchFrame(frame);
        }
    }

    async Task<JsonNode?> InvokeAsync(PendingCommand pending)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(pending.ArgsJson) ? "{}" : pending.ArgsJson);
        var args = document.RootElement.Clone();
        switch (pending.Command)
        {
            case PickFileCommand:
                return await PickFileAsync(args).ConfigureAwait(false);
            case PickDirectoryCommand:
                return await PickDirectoryAsync(args).ConfigureAwait(false);
            case CreateDocumentCommand:
                return await CreateDocumentAsync(args).ConfigureAwait(false);
            case SetSystemBarsCommand:
                return SetSystemBars(args);
            default:
                if (BootstrapCommands.Handles(pending.Command))
                    return await BootstrapCommands.HandleAsync(pending.Command, args).ConfigureAwait(false);

                return await DispatchLauncherCommandAsync(pending.Command, args).ConfigureAwait(false);
        }
    }

    async Task<JsonNode?> DispatchLauncherCommandAsync(string command, JsonElement args)
    {
        var result = await LauncherBridgeGenerated.DispatchAsync(command, args, _services).ConfigureAwait(false);
        if (result is null)
            return null;

        if (result is JsonElement element)
            return JsonNode.Parse(element.GetRawText());

        throw new LauncherCommandException("bad_result_type", $"Launcher command '{command}' returned an unexpected payload type.");
    }

    async Task<JsonNode?> PickFileAsync(JsonElement args)
    {
        var multiple = args.TryGetProperty("multiple", out var multipleElement) && multipleElement.ValueKind == JsonValueKind.True;
        var uris = await _activity.PickFilesAsync(multiple).ConfigureAwait(false);
        if (uris is null)
            return null;

        var paths = new JsonArray();
        foreach (var uri in uris)
            paths.Add(await CopyToSandboxAsync(uri).ConfigureAwait(false));

        if (multiple)
            return paths;

        return paths.Count > 0 ? paths[0] : null;
    }

    async Task<JsonNode?> PickDirectoryAsync(JsonElement args)
    {
        var uri = await _activity.PickDirectoryAsync().ConfigureAwait(false);
        //SAF tree URIs are not filesystem paths; the front-end only needs a stable handle.
        return uri?.ToString();
    }

    async Task<JsonNode?> CreateDocumentAsync(JsonElement args)
    {
        var defaultPath = args.TryGetProperty("defaultPath", out var defaultPathElement) && defaultPathElement.ValueKind == JsonValueKind.String
            ? defaultPathElement.GetString()
            : null;
        var mimeType = ResolveMimeType(args);
        var uri = await _activity.CreateDocumentAsync(mimeType, string.IsNullOrWhiteSpace(defaultPath) ? "export.bin" : defaultPath!).ConfigureAwait(false);
        return uri?.ToString();
    }

    JsonNode? SetSystemBars(JsonElement args)
    {
        var hex = args.TryGetProperty("hex", out var hexElement) ? hexElement.GetString() : null;
        var lightBars = !args.TryGetProperty("lightBars", out var lightElement) || lightElement.ValueKind != JsonValueKind.False;
        if (!string.IsNullOrWhiteSpace(hex))
            _activity.SetSystemBars(hex!, lightBars);
        return null;
    }

    async Task<string> CopyToSandboxAsync(Android.Net.Uri uri)
    {
        var displayName = QueryDisplayName(uri) ?? uri.LastPathSegment;
        return await Task.Run(() => SandboxFileTool.CopyPickedFileToSandbox(uri, displayName)).ConfigureAwait(false);
    }

    static string? QueryDisplayName(Android.Net.Uri uri)
    {
        try
        {
            var cursor = Application.Context.ContentResolver?.Query(uri, new[] { Android.Provider.OpenableColumns.DisplayName }, null, null, null);
            using (cursor)
            {
                if (cursor?.MoveToFirst() == true)
                {
                    var index = cursor.GetColumnIndex(Android.Provider.OpenableColumns.DisplayName);
                    if (index >= 0)
                        return cursor.GetString(index);
                }
            }
        }
        catch (Exception)
        {
            //Cosmetic fallback: the sandbox copy derives its own name when the query fails.
        }

        return null;
    }

    static string ResolveMimeType(JsonElement args)
    {
        if (args.TryGetProperty("filters", out var filters) && filters.ValueKind == JsonValueKind.Array)
        {
            foreach (var filter in filters.EnumerateArray())
            {
                if (filter.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var extensionValue in extensions.EnumerateArray())
                    {
                        var extension = extensionValue.GetString()?.TrimStart('.').ToLowerInvariant();
                        var mime = extension is null || extension.Length == 0
                            ? null
                            : MimeTypeMap.Singleton?.GetMimeTypeFromExtension(extension);
                        if (mime is not null)
                            return mime;
                    }
                }
            }
        }

        return "application/octet-stream";
    }

    static JsonObject BuildFrame(string callbackId, bool ok, JsonNode? payload)
    {
        return new JsonObject
        {
            ["id"] = callbackId,
            ["ok"] = ok,
            ["payload"] = payload?.DeepClone(),
        };
    }

    static JsonObject ErrorFrame(string callbackId, string message)
    {
        return new JsonObject
        {
            ["id"] = callbackId,
            ["ok"] = false,
            ["payload"] = message,
        };
    }

    void DispatchFrame(JsonObject frame)
    {
        //System.Text.Json's default encoder escapes U+2028/2029, so the JSON text is safe as a JS object literal.
        var json = frame.ToJsonString();
        _activity.EvaluateJavaScript("window.__modforgeDispatch && window.__modforgeDispatch(" + json + ");");
    }

    /// <summary>Escapes one JS string literal; used for event names embedded into dispatch snippets.</summary>
    internal static string QuoteJsString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < 0x20)
                        builder.Append("\\u").Append(((int)character).ToString("x4"));
                    else
                        builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
