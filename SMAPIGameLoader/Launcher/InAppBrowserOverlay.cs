using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using SMAPIGameLoader.Bridge;
using SMAPIGameLoader.Services;
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Launcher;

/// <summary>
///     Built-in browser: a toolbar plus a second WebView hosted on top of the launcher
///     WebView. Replaces the external browser for Nexus mod download pages. File downloads
///     started inside the overlay are captured and routed through the app's own download
///     directory and — when the launcher setting is on — the Mods installer, so users
///     without a Nexus API key still get one-tap download-and-install. Outcomes are pushed
///     to the front-end as <c>android:in-app-browser-download</c> events so they surface
///     as notifications (toast + notification center).
/// </summary>
sealed class InAppBrowserOverlay
{
    public const string DownloadEvent = "android:in-app-browser-download";

    const int ToolbarHeightDp = 52;
    const int ToolbarButtonSizeDp = 40;
    const int ToolbarButtonTextSizeSp = 16;
    const int TitleTextSizeSp = 14;
    const int ProgressHeightDp = 3;

    static readonly Color SurfaceColor = Color.Rgb(245, 245, 248);
    static readonly Color TitleColor = Color.Rgb(36, 38, 43);
    static readonly Color AccentColor = Color.Rgb(0x5b, 0x54, 0xd6);

    static readonly HttpClient DownloadClient = new();

    readonly LauncherActivity _activity;

    FrameLayout? _container;
    WebView? _webView;
    TextView? _titleView;
    ProgressView? _progressView;
    int _activeDownloads;

    public InAppBrowserOverlay(LauncherActivity activity)
    {
        _activity = activity;
    }

    public bool IsOpen => _container != null;

    int DpToPx(int value) => (int)Math.Ceiling(value * (_activity.Resources?.DisplayMetrics?.Density ?? 1f));

    /// <summary>
    ///     Strips the WebView tokens ("; wv", " Version/4.0") from the engine's own
    ///     default UA so third-party bot checks see the matching plain Chrome Mobile
    ///     build instead of a fingerprintable WebView shell.
    /// </summary>
    static string ChromeMobileUserAgent(string? defaultAgent)
    {
        if (string.IsNullOrWhiteSpace(defaultAgent))
            return defaultAgent ?? string.Empty;

        return defaultAgent
            .Replace("; wv)", ")", StringComparison.Ordinal)
            .Replace(" Version/4.0", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Shows the overlay at the URL, or navigates the existing overlay.</summary>
    public void Open(string url)
    {
        _activity.RunOnUiThread(() =>
        {
            if (_container != null)
            {
                _webView?.LoadUrl(url);
                return;
            }

            Build(url);
        });
    }

    /// <summary>Closes the overlay and destroys its WebView.</summary>
    public void Close()
    {
        _activity.RunOnUiThread(() =>
        {
            var root = _activity.RootLayout;
            if (_container != null && root != null)
                root.RemoveView(_container);

            _webView?.Destroy();
            _container = null;
            _webView = null;
            _titleView = null;
            _progressView = null;
        });
    }

    /// <summary>
    ///     Consumes one back press: WebView history first, then close the overlay.
    ///     Returns false when the overlay is not open so the activity keeps its
    ///     default back behavior.
    /// </summary>
    public bool HandleBack()
    {
        if (_container is null)
            return false;

        if (_webView?.CanGoBack() == true)
        {
            _webView.GoBack();
            return true;
        }

        Close();
        return true;
    }

    void Build(string url)
    {
        var root = _activity.RootLayout;
        if (root is null)
            return;

        var container = new FrameLayout(_activity)
        {
            LayoutParameters = new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent),
        };
        container.SetBackgroundColor(Color.White);

        var column = new LinearLayout(_activity)
        {
            Orientation = Orientation.Vertical,
            LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent),
        };

        var toolbar = new LinearLayout(_activity)
        {
            Orientation = Orientation.Horizontal,
            LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                DpToPx(ToolbarHeightDp)),
        };
        toolbar.SetBackgroundColor(SurfaceColor);

        var closeButton = CreateToolbarButton("✕");
        closeButton.Click += (_, _) => Close();
        toolbar.AddView(closeButton);

        var titleView = new TextView(_activity)
        {
            LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f),
            Gravity = GravityFlags.CenterVertical,
            Ellipsize = Android.Text.TextUtils.TruncateAt.End,
            TextSize = TitleTextSizeSp,
        };
        titleView.SetMaxLines(1);
        titleView.SetTextColor(TitleColor);
        titleView.SetPadding(DpToPx(4), 0, DpToPx(4), 0);
        toolbar.AddView(titleView);

        var externalButton = CreateToolbarButton("↗");
        externalButton.Click += (_, _) => OpenCurrentPageExternally();
        toolbar.AddView(externalButton);

        var progressView = new ProgressView(_activity);

        var webView = new WebView(_activity)
        {
            LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                0,
                1f),
        };
        var settings = webView.Settings;
        settings.JavaScriptEnabled = true;
        settings.DomStorageEnabled = true;
        settings.AllowFileAccess = false;
        settings.SetSupportZoom(true);
        settings.BuiltInZoomControls = false;
        settings.DisplayZoomControls = false;
        // The default WebView UA carries "; wv" + "Version/4.0" markers that
        // bot protection (Cloudflare in front of Nexus) fingerprints into an
        // interactive challenge; present as the plain Chrome Mobile build.
        settings.UserAgentString = ChromeMobileUserAgent(settings.UserAgentString);
        webView.SetWebViewClient(new OverlayWebViewClient(this));
        webView.SetWebChromeClient(new OverlayWebChromeClient(this));
        webView.SetDownloadListener(new OverlayDownloadListener(this));

        column.AddView(toolbar);
        column.AddView(progressView.View);
        column.AddView(webView);
        container.AddView(column);
        root.AddView(container);

        _container = container;
        _webView = webView;
        _titleView = titleView;
        _progressView = progressView;
        webView.LoadUrl(url);
    }

    TextView CreateToolbarButton(string glyph)
    {
        var button = new TextView(_activity)
        {
            LayoutParameters = new LinearLayout.LayoutParams(DpToPx(ToolbarButtonSizeDp), DpToPx(ToolbarButtonSizeDp)),
            Gravity = GravityFlags.Center,
            Text = glyph,
            TextSize = ToolbarButtonTextSizeSp,
        };
        button.SetTextColor(TitleColor);
        return button;
    }

    void SetPageTitle(string? title)
    {
        _activity.RunOnUiThread(() =>
        {
            if (_titleView != null && _activeDownloads == 0)
                _titleView.Text = title ?? string.Empty;
        });
    }

    void SetLoadProgress(int progress)
    {
        _activity.RunOnUiThread(() =>
        {
            if (_progressView == null)
                return;

            if (_activeDownloads > 0)
            {
                //A captured file download owns the thin bar until it settles.
                return;
            }

            _progressView.SetDeterminate(progress);
        });
    }

    void OnDownloadStarted()
    {
        _activity.RunOnUiThread(() =>
        {
            _activeDownloads += 1;
            _progressView?.SetBusy();
            if (_titleView != null)
                _titleView.Text = "Downloading…";
        });
    }

    void OnDownloadSettled()
    {
        _activity.RunOnUiThread(() =>
        {
            _activeDownloads = Math.Max(0, _activeDownloads - 1);
            if (_activeDownloads == 0)
            {
                _progressView?.SetIdle();
                if (_titleView != null)
                    _titleView.Text = _webView?.Title ?? string.Empty;
            }
        });
    }

    void OpenCurrentPageExternally()
    {
        var url = _webView?.Url;
        if (string.IsNullOrWhiteSpace(url))
            return;

        TryOpenExternal(url);
    }

    /// <summary>Hands one URL to the system browser; used for non-http(s) schemes and the toolbar escape hatch.</summary>
    void TryOpenExternal(string url)
    {
        try
        {
            var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(url));
            intent.AddFlags(ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            Console.WriteLine("InAppBrowserOverlay: external open failed: " + ex);
        }
    }

    void OnDownloadStart(string url, string? contentDisposition, string? mimeType)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var fileName = URLUtil.GuessFileName(url, contentDisposition, mimeType);
        OnDownloadStarted();
        _ = Task.Run(() => DownloadCapturedFileAsync(url, fileName));
    }

    async Task DownloadCapturedFileAsync(string url, string fileName)
    {
        string completedName = fileName;
        var installed = false;
        try
        {
            var settings = LauncherRuntimeService.LoadOrCreateSettings();
            var downloadDirectory = NexusService.ResolveDownloadDirectory(settings.DownloadPath);
            Directory.CreateDirectory(downloadDirectory);
            var archivePath = NexusService.UniqueDownloadPath(
                System.IO.Path.Combine(downloadDirectory, NexusService.SanitizeDownloadFileName(fileName)));
            completedName = System.IO.Path.GetFileName(archivePath);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var cookies = CookieManager.Instance?.GetCookie(url);
            if (!string.IsNullOrEmpty(cookies))
                request.Headers.TryAddWithoutValidation("Cookie", cookies);
            request.Headers.TryAddWithoutValidation("User-Agent", _webView?.Settings?.UserAgentString ?? "ModForge-Android");

            using var response = await DownloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var destination = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write))
            {
                await source.CopyToAsync(destination).ConfigureAwait(false);
            }

            if (settings.AutoInstallDownloads)
            {
                var installResult = new InstallService().InstallLauncherArchive(new InstallLauncherArchiveRequest
                {
                    ArchivePath = archivePath,
                    ModsPath = settings.ModsPath,
                });
                installed = true;
                if (!settings.KeepDownloadedArchives)
                    TryDeleteFile(archivePath);
            }

            DispatchDownloadEvent("completed", completedName, installed, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine("InAppBrowserOverlay: captured download failed: " + ex);
            DispatchDownloadEvent("failed", completedName, installed, ex.Message);
        }
        finally
        {
            OnDownloadSettled();
        }
    }

    void DispatchDownloadEvent(string status, string fileName, bool installed, string? message)
    {
        try
        {
            ModForgeBridge.Instance?.DispatchEvent(DownloadEvent, new JsonObject
            {
                ["status"] = status,
                ["fileName"] = fileName,
                ["installed"] = installed,
                ["message"] = message,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("InAppBrowserOverlay: download event dispatch failed: " + ex);
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
            Console.WriteLine("InAppBrowserOverlay: temp cleanup failed for " + path + ": " + ex);
        }
    }

    /// <summary>Routes http(s) through the in-WebView flow and hands exotic schemes to the system.</summary>
    sealed class OverlayWebViewClient : WebViewClient
    {
        readonly InAppBrowserOverlay _overlay;

        public OverlayWebViewClient(InAppBrowserOverlay overlay)
        {
            _overlay = overlay;
        }

        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            var scheme = request?.Url?.Scheme;
            if (scheme == "http" || scheme == "https")
                return false;

            if (scheme != null)
                _overlay.TryOpenExternal(request!.Url!.ToString()!);
            return true;
        }

        public override void OnPageStarted(WebView? view, string? url, Bitmap? favicon)
        {
            base.OnPageStarted(view, url, favicon);
            _overlay.SetLoadProgress(0);
        }

        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            _overlay.SetLoadProgress(100);
            _overlay.SetPageTitle(view?.Title);
        }
    }

    sealed class OverlayWebChromeClient : WebChromeClient
    {
        readonly InAppBrowserOverlay _overlay;

        public OverlayWebChromeClient(InAppBrowserOverlay overlay)
        {
            _overlay = overlay;
        }

        public override void OnProgressChanged(WebView? view, int newProgress)
        {
            base.OnProgressChanged(view, newProgress);
            _overlay.SetLoadProgress(newProgress);
        }
    }

    sealed class OverlayDownloadListener : Java.Lang.Object, IDownloadListener
    {
        readonly InAppBrowserOverlay _overlay;

        public OverlayDownloadListener(InAppBrowserOverlay overlay)
        {
            _overlay = overlay;
        }

        public void OnDownloadStart(string? url, string? userAgent, string? contentDisposition, string? mimeType, long contentLength)
        {
            if (url != null)
                _overlay.OnDownloadStart(url, contentDisposition, mimeType);
        }
    }

    /// <summary>
    ///     Thin determinate load bar that flips to indeterminate while a captured file
    ///     download is in flight; tinted to the launcher accent.
    /// </summary>
    sealed class ProgressView
    {
        public ProgressBar View { get; }

        public ProgressView(Android.Content.Context context)
        {
            View = new ProgressBar(context, null, Android.Resource.Attribute.ProgressBarStyleHorizontal)
            {
                LayoutParameters = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    (int)Math.Ceiling(ProgressHeightDp * (context.Resources?.DisplayMetrics?.Density ?? 1f))),
                Indeterminate = false,
                Max = 100,
            };
            View.ProgressDrawable?.SetColorFilter(AccentColor, PorterDuff.Mode.SrcIn);
            View.SetBackgroundColor(SurfaceColor);
        }

        public void SetDeterminate(int progress)
        {
            View.Indeterminate = false;
            View.Progress = progress;
        }

        public void SetBusy()
        {
            View.Indeterminate = true;
        }

        public void SetIdle()
        {
            View.Indeterminate = false;
            View.Progress = 0;
        }
    }
}
