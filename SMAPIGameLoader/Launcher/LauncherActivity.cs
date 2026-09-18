using Android.App;
using Android.Content.PM;
using Android.OS;
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
    Android.Widget.FrameLayout? _rootLayout;
    InAppBrowserOverlay? _inAppBrowser;

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

        // Capture game/SMAPI/.NET console output before anything logs.
        LogCapture.Install();

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

        //Remote WebView debugging (chrome://inspect + CDP) for launcher bring-up.
        WebView.SetWebContentsDebuggingEnabled(true);

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
        _bridge = new ModForgeBridge(this);
        _webView.AddJavascriptInterface(_bridge, "modforgeBridge");

        // Android 15 enforces edge-to-edge: pad the WebView by the system-bar
        // insets so the front-end never renders under the status/gesture bars.
        // The listener runs before the first layout, so the SPA never sees the
        // overlap. Light status-bar icons: the launcher chrome is light.
        _webView.SetBackgroundColor(Android.Graphics.Color.Rgb(245, 245, 248));
        AndroidX.Core.View.WindowCompat.SetDecorFitsSystemWindows(Window, false);
        var insetsController = AndroidX.Core.View.WindowCompat.GetInsetsController(Window, _webView);
        insetsController.AppearanceLightStatusBars = true;
        var rootLayout = new Android.Widget.FrameLayout(this)
        {
            LayoutParameters = new Android.Views.ViewGroup.LayoutParams(
                Android.Views.ViewGroup.LayoutParams.MatchParent,
                Android.Views.ViewGroup.LayoutParams.MatchParent),
        };
        // The WebView is inset below the status bar, so the strip above it
        // shows this root background; tint it to match the launcher surface
        // (the front-end re-tints it live whenever the theme changes).
        rootLayout.SetBackgroundColor(Android.Graphics.Color.Rgb(245, 245, 248));
        _rootLayout = rootLayout;
        rootLayout.AddView(_webView);
        SetContentView(rootLayout);
        AndroidX.Core.View.ViewCompat.SetOnApplyWindowInsetsListener(rootLayout, new SystemBarInsetsListener(this));
        _webView.LoadUrl(AssetHostOrigin + "/index.html");
    }

    sealed class SystemBarInsetsListener : Java.Lang.Object, AndroidX.Core.View.IOnApplyWindowInsetsListener
    {
        readonly LauncherActivity _activity;

        public SystemBarInsetsListener(LauncherActivity activity)
        {
            _activity = activity;
        }

        public AndroidX.Core.View.WindowInsetsCompat OnApplyWindowInsets(Android.Views.View v, AndroidX.Core.View.WindowInsetsCompat insets)
        {
            var bars = insets.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.StatusBars());
            Android.Util.Log.Info("MODFORGE", $"SystemBarInsetsListener fired, top={bars?.Top}, left={bars?.Left}");
            if (bars != null)
            {
                v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            }

            return insets;
        }
    }

    /// <summary>Runs one JS snippet on the UI thread; the bridge uses it to push response/event frames.</summary>
    public void EvaluateJavaScript(string script)
    {
        RunOnUiThread(() =>
        {
            _webView?.EvaluateJavascript(script, null);
        });
    }

    /// <summary>Root layout hosting the app WebView; the in-app browser overlay mounts on top of it.</summary>
    internal Android.Widget.FrameLayout? RootLayout => _rootLayout;

    /// <summary>
    ///     Opens the built-in in-app browser overlay at an http(s) URL. Used by the
    ///     front-end for Nexus download pages and by the manual-download fallback.
    ///     Returns false only when the activity has no content view yet.
    /// </summary>
    public bool OpenInAppBrowser(string url)
    {
        if (_rootLayout is null)
            return false;

        _inAppBrowser ??= new InAppBrowserOverlay(this);
        _inAppBrowser.Open(url);
        return true;
    }

    /// <summary>
    /// Tints the system-bar strip to the front-end app surface color and picks
    /// light/dark system-bar icons. The WebView sits below the status bar, so
    /// the visible strip is the root layout background painted by this call.
    /// </summary>
    public void SetSystemBars(string hexColor, bool lightBars)
    {
        Android.Graphics.Color color;
        try
        {
            color = Android.Graphics.Color.ParseColor(hexColor);
        }
        catch (Exception)
        {
            //Malformed theme color from the front-end: keep the current chrome.
            return;
        }

        RunOnUiThread(() =>
        {
            _rootLayout?.SetBackgroundColor(color);
            if (Window is null)
            {
                return;
            }

            Window.SetStatusBarColor(color);
            Window.SetNavigationBarColor(color);
            var controller = AndroidX.Core.View.WindowCompat.GetInsetsController(Window, _webView ?? Window.DecorView);
            controller.AppearanceLightStatusBars = lightBars;
            controller.AppearanceLightNavigationBars = lightBars;
        });
    }

    /// <summary>Pushes one event frame (<c>{event, payload}</c>) to the front-end dispatch sink.</summary>
    public void DispatchEventToJs(string eventName, string payloadJson)
    {
        var frame = "{\"event\":" + ModForgeBridge.QuoteJsString(eventName)
            + ",\"payload\":" + payloadJson + "}";
        EvaluateJavaScript("window.__modforgeDispatch && window.__modforgeDispatch(" + frame + ");");
    }

    /// <summary>Set by the front-end through <see cref="ModForgeBridge.BackHandled" /> when it closed an overlay for this back press.</summary>
    volatile bool _backHandled;

    /// <summary>Acknowledgement from the front-end for the most recent <c>android:back</c> event.</summary>
    public void MarkBackHandled()
    {
        _backHandled = true;
    }

    public override void OnBackPressed()
    {
        // The in-app browser owns back while open: WebView history first, then close.
        if (_inAppBrowser?.HandleBack() == true)
            return;

        // Ask the SPA to close its topmost overlay (mobile pages); if it claims
        // the back press within the window we stay, otherwise move to background.
        _backHandled = false;
        DispatchEventToJs("android:back", "{}");
        Task.Run(async () =>
        {
            await Task.Delay(150).ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                if (!_backHandled)
                {
                    //Back returns to the Android home screen; the app keeps running in background.
                    MoveTaskToBack(true);
                }
            });
        });
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
                intent.AddCategory(Android.Content.Intent.CategoryOpenable);
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
                intent.AddCategory(Android.Content.Intent.CategoryOpenable);
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

        public WebResourceResponse Handle(string url)
        {
            var path = RequestPathFromUrl(url);
            if (path == "/")
                path = "/index.html";

            //The asset loader routes bare-path local-file/plugins requests here; re-dispatch.
            if (path.StartsWith("/local-file/", StringComparison.Ordinal))
                return new SandboxFilePathHandler(_activity, "/local-file/").Handle(url);
            if (path.StartsWith("/plugins/", StringComparison.Ordinal))
                return new PluginPathHandler(_activity).Handle(url);

            var assetPath = AssetRoot + path;
            try
            {
                var stream = _activity.Assets!.Open(assetPath);
                return new WebResourceResponse(GuessMime(path), GuessEncoding(path), stream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WwwAssetPathHandler: asset miss url='{url}' asset='{assetPath}': {ex}");
                return NotFoundResponse(assetPath);
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

        public WebResourceResponse Handle(string url)
        {
            var path = RequestPathFromUrl(url);
            if (path.StartsWith(_prefix) is false)
                return NotFoundResponse(path);

            var filePath = path[_prefix.Length..];
            if (SandboxFileTool.IsInsideAppSandbox(_activity, filePath) is false)
                return NotFoundResponse(filePath);

            try
            {
                var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return new WebResourceResponse(GuessMime(filePath), GuessEncoding(filePath), stream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SandboxFilePathHandler: file miss url='{url}' path='{filePath}': {ex}");
                return NotFoundResponse(filePath);
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

        public WebResourceResponse Handle(string url)
        {
            var segments = new List<string>(Android.Net.Uri.Parse(RequestPathFromUrl(url))?.PathSegments
                ?? (IList<string>)Array.Empty<string>());
            //segments[0] is the registered "plugins" prefix.
            if (segments.Count < 2)
                return NotFoundResponse(url ?? "empty");

            segments.RemoveAt(0);
            //strip the optional __v<N> hot-reload cache-buster segment after the plugin id
            if (segments.Count > 1 && segments[1].StartsWith("__v"))
                segments.RemoveAt(1);

            var relativePath = Path.Combine(segments.ToArray());
            var root = Path.Combine(_activity.GetExternalFilesDir(null)?.AbsolutePath ?? string.Empty, PluginsDirName);
            var filePath = Path.GetFullPath(Path.Combine(root, relativePath));
            if (SandboxFileTool.IsInsideAppSandbox(_activity, filePath) is false)
                return NotFoundResponse(relativePath);

            try
            {
                var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return new WebResourceResponse(GuessMime(relativePath), GuessEncoding(relativePath), stream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PluginPathHandler: file miss url='{url}' path='{filePath}': {ex}");
                return NotFoundResponse(relativePath);
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

    /// <summary>
    ///     Extracts the decoded request path from the string the WebKit binding hands to
    ///     IPathHandler; accepts a full URL, an absolute path or a bare relative target.
    /// </summary>
    internal static string RequestPathFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return "/";

        var path = url!;
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/android_asset", StringComparison.OrdinalIgnoreCase))
        {
            path = Android.Net.Uri.Parse(path)?.Path ?? path;
        }
        else if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            path = "/" + path;
        }

        var cut = path.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0)
            path = path[..cut];

        return path;
    }

    internal static WebResourceResponse NotFoundResponse(string detail)
    {
        var body = Encoding.UTF8.GetBytes("not found: " + detail);
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
