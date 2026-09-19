using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Services;

/// <summary>
///     Launch version gates pulled from a remote JSON document so a stale APK does not hardcode
///     them. <see cref="PrefetchFromRemote"/> fetches at startup; until a fetch succeeds (or
///     after it fails) the built-in defaults apply — the fetch can only relax/tighten gates,
///     never break the launcher.
/// </summary>
public static class LauncherRequirements
{
    /// <summary>Gates document served from the fork's default branch.</summary>
    public const string RemoteUrl = "https://raw.githubusercontent.com/Arborsm/modforge-android/master/version-gates.json";

    //1.6.15.0 is the first 1.6.15 Android rollout (merged sideload APKs report this
    //version); the 1.6.15 content build is identical across the patch rollups.
    public static readonly Version DefaultMinimumGameVersion = new(1, 6, 15, 0);
    public static readonly Version DefaultMinimumSmapiVersion = new(4, 0, 0);

    static volatile Version? _minimumGameVersion;
    static volatile Version? _minimumSmapiVersion;
    static volatile bool _prefetched;

    public static Version MinimumGameVersion => _minimumGameVersion ?? DefaultMinimumGameVersion;
    public static Version MinimumSmapiVersion => _minimumSmapiVersion ?? DefaultMinimumSmapiVersion;

    /// <summary>Fetches the remote gates once in the background; failures keep the current gates.</summary>
    public static void PrefetchFromRemote()
    {
        if (_prefetched)
            return;

        _prefetched = true;
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var text = await client.GetStringAsync(RemoteUrl).ConfigureAwait(false);
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("minimumGameVersion", out var gameVersionElement)
                        && gameVersionElement.ValueKind == JsonValueKind.String
                        && Version.TryParse(gameVersionElement.GetString(), out var gameVersion))
                    {
                        _minimumGameVersion = gameVersion;
                    }

                    if (root.TryGetProperty("minimumSmapiVersion", out var smapiVersionElement)
                        && smapiVersionElement.ValueKind == JsonValueKind.String
                        && Version.TryParse(smapiVersionElement.GetString(), out var smapiVersion))
                    {
                        _minimumSmapiVersion = smapiVersion;
                    }
                }

                Console.WriteLine($"LauncherRequirements: remote gates loaded (game>={MinimumGameVersion}, smapi>={MinimumSmapiVersion})");
            }
            catch (Exception ex)
            {
                Console.WriteLine("LauncherRequirements: remote gate fetch failed, using built-in defaults: " + ex.Message);
            }
        });
    }
}
