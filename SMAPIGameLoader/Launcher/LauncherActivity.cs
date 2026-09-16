using Android.Webkit;
using AndroidX.WebKit;
using SMAPIGameLoader.Bridge;
using SMAPIGameLoader.Tool;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Launcher;

/// <summary>
///     ModForge launcher host: serves the ModForge web front-end (SMAPIGameLoader/Assets/www/)
///     from a fullscreen WebView through androidx.webkit WebViewAssetLoader, exposes the
///     <c>modforgeBridge</c> JS interface and routes back-button handling.
/// </summary>
[Activity(
    Label = "ModForge Launcher",
    MainLauncher = true,
    Theme = "@style/AppTheme",
    AlwaysRetainTaskState = true,
    LaunchMode = LaunchMode.SingleInstance,
    ScreenOrientation = Android.Content.PM.ScreenOrientation.FullSensor
)]
public class LauncherActivity : AndroidX.AppCompat.App.AppCompatActivity
{
    public static LauncherActivity Instance { get; private set; }

    private static bool IsDeviceSupport => IntPtr.Size == 8;

    /// <summary>Origin every front-end URL lives under; must match the platform adapter in ModForge Studio.</summary>
    public const string AssetHostOrigin = "https://appassets.androidplatform.net";

    WebView? _webView;
    WebViewAssetLoader? _assetLoader;
    ModForgeBridge? _bridge;

    const int PickFileRequestCode = 9101;
    const int PickDirectoryRequestCode = 9102;
    const int CreateDocumentRequestCode = 9103;

    TaskCompletionSource<Android.Net.Uri[]>? _pendingPickFile;
    TaskCompletionSource<Android.Net.Uri?>? _pendingPickDirectory;
    TaskCompletionSource<Android.Net.Uri?>? _pendingCreateDocument;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Instance = this;
        base.OnCreate(savedInstanceState);

        Xamarin.Essentials.Platform.Init(this, savedInstanceState);
        ActivityTool.Init(this);

        //32bit devices can never host the game; upstream behaves the same.
        if (IsDeviceSupport is false)
        {
            ToastNotifyTool.Notify("Not support on device 32bit");
            Finish();
            return;
        }

        SetupWebView();
    }

    private void SetupWebView()
    {
        _assetLoader = new WebViewAssetLoader.Builder()
            .SetDomain("appassets.androidplatform.net")
            .AddPathHandler("/", new WwwAssetPathHandler(this))
            .AddPathHandler("/local-file/", new SandboxFilePathHandler(this, "/local-file/"))
            .AddPathHandler("/plugins/", new PluginPathHandler(this))
            .Build();

        _webView = new WebView(this)
        {
            LayoutParameters = new Android.Views.ViewGroup.LayoutParams(
                Android.Views.ViewGroup.LayoutParams.MatchParent,
                Android.Views.ViewGroup.LayoutParams.MatchParent),
        };
        var settings = _webView.Settings;
        settings.JavaScriptEnabled = true;
        settings.DomStorageEnabled = true;
        settings.AllowFileAccess = false;
        settings.CacheMode = CacheModes.Default;
        _webView.SetWebViewClient(new AssetLoaderWebViewClient(_assetLoader));
        _bridge = new ModForgeBridge(this, _webView);
        _webView.AddJavascriptInterface(_bridge, "modforgeBridge");

        SetContentView(_webView);
        _webView.LoadUrl(AssetHostOrigin + "/index.html");
    }

    /// <summary>Runs one JS snippet on the UI thread; the bridge uses it to push response/event frames.</summary>
    public void EvaluateJavaScript(string script)
    {
        RunOnUiThread(() =>
        {
            _webView?.EvaluateJavascript(script, null);
        });
    }

    /// <summary>Pushes one event frame (<c>{event, payload}</c>) to the front-end dispatch sink.</summary>
    public void DispatchEventToJs(string eventName, string payloadJson)
    {
        var frame = "{\"event\":" + ModForgeBridge.QuoteJsString(eventName)
            + ",\"payload\":" + payloadJson + "}";
        EvaluateJavaScript("window.__modforgeDispatch && window.__modforgeDispatch(" + frame + ");");
    }

    public override void OnBackPressed()
    {
        if (_webView?.CanGoBack() == true)
        {
            _webView.GoBack();
            return;
        }

        //Back returns to the Android home screen; the app keeps running in background.
        MoveTaskToBack(true);
    }

    /// <summary>Opens the SAF document picker; resolves with the picked URIs, or null when cancelled.</summary>
    public Task<Android.Net.Uri[]?> PickFilesAsync(bool multiple)
    {
        var completion = new TaskCompletionSource<Android.Net.Uri[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPickFile = completion;
        RunOnUiThread(() =>
        {
            try
            {
                var intent = new Android.Content.Intent(Android.Content.Intent.ActionOpenDocument);
                intent.AddCategory(Android.Content.Category.Openable);
                intent.SetType("*/*");
                intent.PutExtra(Android.Content.Intent.ExtraAllowMultiple, multiple);
                StartActivityForResult(intent, PickFileRequestCode);
            }
            catch (Exception ex)
            {
                _pendingPickFile = null;
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    /// <summary>Opens the SAF directory tree picker; resolves with the tree URI, or null when cancelled.</summary>
    public Task<Android.Net.Uri?> PickDirectoryAsync()
    {
        var completion = new TaskCompletionSource<Android.Net.Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPickDirectory = completion;
        RunOnUiThread(() =>
        {
            try
            {
                var intent = new Android.Content.Intent(Android.Content.Intent.ActionOpenDocumentTree);
                StartActivityForResult(intent, PickDirectoryRequestCode);
            }
            catch (Exception ex)
            {
                _pendingPickDirectory = null;
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    /// <summary>Opens the SAF document creator; resolves with the created document URI, or null when cancelled.</summary>
    public Task<Android.Net.Uri?> CreateDocumentAsync(string mimeType, string displayName)
    {
        var completion = new TaskCompletionSource<Android.Net.Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCreateDocument = completion;
        RunOnUiThread(() =>
        {
            try
            {
                var intent = new Android.Content.Intent(Android.Content.Intent.ActionCreateDocument);
                intent.AddCategory(Android.Content.Category.Openable);
                intent.SetType(string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType);
                intent.PutExtra(Android.Content.Intent.ExtraTitle, displayName);
                StartActivityForResult(intent, CreateDocumentRequestCode);
            }
            catch (Exception ex)
            {
                _pendingCreateDocument = null;
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    protected override void OnActivityResult(int requestCode, Android.App.Result resultCode, Android.Content.Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        switch (requestCode)
        {
            case PickFileRequestCode:
                _pendingPickFile?.TrySetResult(resultCode == Android.App.Result.Ok ? CollectResultUris(data) : null);
                _pendingPickFile = null;
                break;
            case PickDirectoryRequestCode:
                _pendingPickDirectory?.TrySetResult(resultCode == Android.App.Result.Ok ? data?.Data : null);
                _pendingPickDirectory = null;
                break;
            case CreateDocumentRequestCode:
                _pendingCreateDocument?.TrySetResult(resultCode == Android.App.Result.Ok ? data?.Data : null);
                _pendingCreateDocument = null;
                break;
        }
    }

    static Android.Net.Uri[] CollectResultUris(Android.Content.Intent? data)
    {
        if (data is null)
            return Array.Empty<Android.Net.Uri>();

        var uris = new List<Android.Net.Uri>();
        var clip = data.ClipData;
        if (clip is not null)
        {
            for (var index = 0; index < clip.ItemCount; index += 1)
            {
                var uri = clip.GetItemAt(index)?.Uri;
                if (uri is not null)
                    uris.Add(uri);
            }
        }

        if (uris.Count == 0 && data.Data is not null)
            uris.Add(data.Data);

        return uris.ToArray();
    }

    /// <summary>
    ///     Serves the front-end build output under assets <c>www/</c> at the origin root, so the
    ///     Vite relative-base bundle resolves <c>./assets/...</c> references correctly.
    /// </summary>
    class WwwAssetPathHandler : Java.Lang.Object, WebViewAssetLoader.IPathHandler
    {
        const string AssetRoot = "www";
        readonly LauncherActivity _activity;

        public WwwAssetPathHandler(LauncherActivity activity)
        {
            _activity = activity;
        }

        public WebResourceResponse Handle(Uri url)
        {
            var path = url.Path ?? "/";
            if (path == "/")
                path = "/index.html";

            try
            {
                var stream = _activity.Assets!.Open(AssetRoot + path);
                return new WebResourceResponse(GuessMime(path), GuessEncoding(path), stream);
            }
            catch (Exception)
            {
                return NotFoundResponse();
            }
        }
    }

    /// <summary>Serves one file from the app sandbox; the URL carries the absolute path after the prefix.</summary>
    class SandboxFilePathHandler : Java.Lang.Object, WebViewAssetLoader.IPathHandler
    {
        readonly LauncherActivity _activity;
        readonly string _prefix;

        public SandboxFilePathHandler(LauncherActivity activity, string prefix)
        {
            _activity = activity;
            _prefix = prefix;
        }

        public WebResourceResponse Handle(Uri url)
        {
            var path = url.Path ?? string.Empty;
            if (path.StartsWith(_prefix) is false)
                return NotFoundResponse();

            var filePath = path[_prefix.Length..];
            if (SandboxFileTool.IsInsideAppSandbox(_activity, filePath) is false)
                return NotFoundResponse();

            try
            {
                var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return new WebResourceResponse(GuessMime(filePath), GuessEncoding(filePath), stream);
            }
            catch (Exception)
            {
                return NotFoundResponse();
            }
        }
    }

    /// <summary>
    ///     Serves plugin runtime assets: <c>/plugins/&lt;pluginId&gt;[/__v&lt;epoch&gt;]/&lt;relative path&gt;</c>
    ///     resolves under <c>&lt;external files dir&gt;/plugins/&lt;pluginId&gt;</c>; the optional
    ///     <c>__v&lt;N&gt;</c> cache-buster segment is stripped before resolving.
    /// </summary>
    class PluginPathHandler : Java.Lang.Object, WebViewAssetLoader.IPathHandler
    {
        const string PluginsDirName = "plugins";
        readonly LauncherActivity _activity;

        public PluginPathHandler(LauncherActivity activity)
        {
            _activity = activity;
        }

        public WebResourceResponse Handle(Uri url)
        {
            var segments = new List<string>(url.PathSegments);
            //segments[0] is the registered "plugins" prefix.
            if (segments.Count < 2)
                return NotFoundResponse();

            segments.RemoveAt(0);
            //strip the optional __v<N> hot-reload cache-buster segment after the plugin id
            if (segments.Count > 1 && segments[1].StartsWith("__v"))
                segments.RemoveAt(1);

            var relativePath = Path.Combine(segments.ToArray());
            var root = Path.Combine(_activity.GetExternalFilesDir(null)?.AbsolutePath ?? string.Empty, PluginsDirName);
            var filePath = Path.GetFullPath(Path.Combine(root, relativePath));
            if (SandboxFileTool.IsInsideAppSandbox(_activity, filePath) is false)
                return NotFoundResponse();

            try
            {
                var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return new WebResourceResponse(GuessMime(relativePath), GuessEncoding(relativePath), stream);
            }
            catch (Exception)
            {
                return NotFoundResponse();
            }
        }
    }

    class AssetLoaderWebViewClient : WebViewClient
    {
        readonly WebViewAssetLoader _assetLoader;

        public AssetLoaderWebViewClient(WebViewAssetLoader assetLoader)
        {
            _assetLoader = assetLoader;
        }

        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
        {
            if (request?.Url is null)
                return null;

            return _assetLoader.ShouldInterceptRequest(request.Url);
        }
    }

    internal static WebResourceResponse NotFoundResponse()
    {
        var body = Encoding.UTF8.GetBytes("not found");
        return new WebResourceResponse("text/plain", "utf-8", 404, "Not Found", null, new MemoryStream(body));
    }

    internal static string GuessMime(string path)
    {
        var extension = Path.GetExtension(path)?.TrimStart('.')?.ToLowerInvariant();
        if (string.IsNullOrEmpty(extension))
            return "application/octet-stream";

        var mime = MimeTypeMap.Singleton?.GetMimeTypeFromExtension(extension);
        if (mime is not null)
            return mime;

        //MimeTypeMap misses a few types the front-end bundle needs.
        return extension switch
        {
            "html" or "htm" => "text/html",
            "js" or "mjs" => "text/javascript",
            "css" => "text/css",
            "json" => "application/json",
            "svg" => "image/svg+xml",
            "woff2" => "font/woff2",
            "woff" => "font/woff",
            "ttf" => "font/ttf",
            _ => "application/octet-stream",
        };
    }

    internal static string? GuessEncoding(string path)
    {
        var mime = GuessMime(path);
        return mime.StartsWith("text/") || mime == "application/json" || mime == "image/svg+xml" ? "utf-8" : null;
    }
}
