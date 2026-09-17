using System;

namespace SMAPIGameLoader.Services;

/// <summary>
///     Thrown by the generated dispatch when a launcher command has no Android handler yet
///     (Nexus download/search, SSO, image CDN, GMCM probe). The message is what the front-end
///     notification shows, so it must describe the missing capability plainly.
/// </summary>
public sealed class LauncherCommandUnavailableException : Exception
{
    public string Command { get; }

    public LauncherCommandUnavailableException(string command)
        : base($"Launcher command '{command}' is not available on this Android launcher yet.")
    {
        Command = command;
    }
}

/// <summary>Bridge-level failure with a stable code; the message text reaches the front-end.</summary>
public sealed class LauncherCommandException : Exception
{
    public string Code { get; }

    public LauncherCommandException(string code, string message) : base(message)
    {
        Code = code;
    }
}

/// <summary>Aggregates the launcher services behind the generated dispatch switch.</summary>
public sealed class LauncherCommandServices
{
    public required LauncherRuntimeService Runtime { get; init; }
    public required LibraryService Library { get; init; }
    public required InstallService Install { get; init; }
    public required SmapiService Smapi { get; init; }
    public required ModConfigService ModConfig { get; init; }

    /// <summary>Shared instance backing the WebView bridge; services are stateless apart from file caches.</summary>
    public static LauncherCommandServices Loaded { get; } = CreateDefault();

    public static LauncherCommandServices CreateDefault() => new()
    {
        Runtime = new LauncherRuntimeService(),
        Library = new LibraryService(),
        Install = new InstallService(),
        Smapi = new SmapiService(),
        ModConfig = new ModConfigService(),
    };
}
