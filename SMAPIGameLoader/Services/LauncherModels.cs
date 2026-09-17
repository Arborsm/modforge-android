using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SMAPIGameLoader.Services;

// JSON contract types for the launcher bridge. Wire naming is camelCase (set by
// LauncherJsonContext); fields marked null-on-write are omitted from payloads,
// matching the Rust skip_serializing_if attributes of the desktop protocol.

public sealed class LauncherSettings
{
    public string? GamePath { get; set; }
    public string? ModsPath { get; set; }
    public string? DownloadPath { get; set; }
    public string? NexusApiKey { get; set; }
    public bool AutoInstallDownloads { get; set; }
    public bool KeepDownloadedArchives { get; set; }
    public bool AutoCheckModUpdates { get; set; } = true;
    public bool GmcmParsingEnabled { get; set; } = true;
    public bool ShowConsoleWindow { get; set; }

    /// <summary>Android-side debug override for Nexus diagnostics; not part of the desktop contract.</summary>
    public bool ForceOffline { get; set; }
}

/// <summary>Save request merge rules mirror the desktop protocol: absent/null means keep the current value.</summary>
public sealed class SaveLauncherSettingsRequest
{
    public string? GamePath { get; set; }
    public string? ModsPath { get; set; }
    public string? DownloadPath { get; set; }

    // Tri-state: absent → keep, JSON null → clear, string → set.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? NexusApiKey { get; set; }

    public bool? AutoInstallDownloads { get; set; }
    public bool? KeepDownloadedArchives { get; set; }
    public bool? AutoCheckModUpdates { get; set; }
    public bool? GmcmParsingEnabled { get; set; }
    public bool? ShowConsoleWindow { get; set; }
}

public sealed class ScanLauncherLibraryRequest
{
    public string ModsPath { get; set; } = string.Empty;
}

public sealed class LauncherLibraryDependency
{
    public string UniqueId { get; set; } = string.Empty;
    public bool Required { get; set; } = true;
}

public sealed class LauncherLibraryModSummary
{
    public string Id { get; set; } = string.Empty;
    public string LabelKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? UniqueId { get; set; }
    public string FolderName { get; set; } = string.Empty;
    public string AbsolutePath { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool HasConfig { get; set; }
    public long? NexusModId { get; set; }
    public List<string> UpdateKeys { get; set; } = new();
    public string? ModUrl { get; set; }
    public string? ImageUrl { get; set; }
    public List<LauncherLibraryDependency> Dependencies { get; set; } = new();
    public List<string> RequiredDependencies { get; set; } = new();
    public List<string> MissingRequiredDependencies { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MinimumApiVersion { get; set; }

    public bool RequiresNewerSmapi { get; set; }
}

public sealed class LauncherLibraryScanResult
{
    public string ModsPath { get; set; } = string.Empty;
    public List<LauncherLibraryModSummary> Mods { get; set; } = new();
}

public sealed class SetLauncherModEnabledRequest
{
    public string ModPath { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

public sealed class SetLauncherModEnabledResult
{
    public string AbsolutePath { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

public sealed class LauncherLibraryStorageFolder
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> ModKeys { get; set; } = new();
}

public sealed class LauncherLibraryPackPreset
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> ModKeys { get; set; } = new();
    public string FolderClassificationMode { get; set; } = "global";
}

public sealed class LauncherLibraryChildModGroup
{
    public string ParentModKey { get; set; } = string.Empty;
    public List<string> ChildModKeys { get; set; } = new();
}

public sealed class LauncherLibraryFolder
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? PackId { get; set; }
    public bool Hidden { get; set; }
    public string? ParentFolderId { get; set; }
    public List<string> ModKeys { get; set; } = new();
    public List<string> CoverModKeys { get; set; } = new();
}

public sealed class LauncherLibraryState
{
    public List<LauncherLibraryStorageFolder> StorageFolders { get; set; } = new();
    public List<string> HiddenModKeys { get; set; } = new();
    public List<LauncherLibraryPackPreset> PackPresets { get; set; } = new();
    public List<LauncherLibraryChildModGroup> ChildModGroups { get; set; } = new();
    public List<LauncherLibraryFolder> LibraryFolders { get; set; } = new();
    public SortedDictionary<string, List<string>> CustomOrders { get; set; } = new(StringComparer.Ordinal);
    public string? CurrentPackId { get; set; }
    public string ScopeMode { get; set; } = "all";
}

public sealed class LauncherLibraryCover
{
    public string LabelKey { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
}

public sealed class LauncherLibraryCoversState
{
    public List<LauncherLibraryCover> Covers { get; set; } = new();
}

public sealed class SetLauncherLibraryCoverRequest
{
    public string LabelKey { get; set; } = string.Empty;
    public string? ImagePath { get; set; }
}

public sealed class LauncherImageFailureEntry
{
    public string ModKey { get; set; } = string.Empty;
    public uint FailureCount { get; set; }
    public bool Blocked { get; set; }
    public string LastError { get; set; } = string.Empty;
    public ulong LastFailedAtMs { get; set; }
}

public sealed class LauncherImageFailuresState
{
    public List<LauncherImageFailureEntry> Entries { get; set; } = new();
}

public sealed class RecordLauncherImageFailureRequest
{
    public string ModKey { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

public sealed class LauncherRuntimeInfo
{
    public string? GameVersion { get; set; }
    public string? SmapiVersion { get; set; }
}

public sealed class LauncherGameLaunchResult
{
    public string ExecutablePath { get; set; } = string.Empty;
    public string Target { get; set; } = "smapi";
}

public sealed class OpenLauncherUrlRequest
{
    public string Url { get; set; } = string.Empty;
}

public sealed class LauncherModConfigField
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Section { get; set; }
    public string FieldType { get; set; } = "unknown";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UiHint { get; set; }

    public JsonElement Value { get; set; }
    public JsonElement? DefaultValue { get; set; }
    public List<JsonElement> AllowValues { get; set; } = new();
    public bool AllowBlank { get; set; }
    public bool AllowMultiple { get; set; }
    public bool Editable { get; set; } = true;
    public string Source { get; set; } = "config-json";
}

public sealed class LauncherModConfigResult
{
    public string ModPath { get; set; } = string.Empty;
    public string ConfigPath { get; set; } = string.Empty;
    public bool ConfigExists { get; set; }
    public List<LauncherModConfigField> Fields { get; set; } = new();
    public List<string> SchemaSources { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string ProbeStatus { get; set; } = "not-run";
}

public sealed class LoadLauncherModConfigRequest
{
    public string ModPath { get; set; } = string.Empty;
    public string? Locale { get; set; }
}

public sealed class SaveLauncherModConfigRequest
{
    public string ModPath { get; set; } = string.Empty;
    public string? Locale { get; set; }
    public Dictionary<string, JsonElement> Values { get; set; } = new();
}

public sealed class InstallLauncherArchiveRequest
{
    public string ArchivePath { get; set; } = string.Empty;
    public string? ModsPath { get; set; }
}

public sealed class InstallLauncherArchiveInstalledMod
{
    public string ModName { get; set; } = string.Empty;
    public string? UniqueId { get; set; }
    public string? Version { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public bool PreservedConfig { get; set; }
    public long PreservedI18nFiles { get; set; }
}

public sealed class InstallLauncherArchiveResult
{
    public string ModName { get; set; } = string.Empty;
    public string? UniqueId { get; set; }
    public string? Version { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public bool PreservedConfig { get; set; }
    public long PreservedI18nFiles { get; set; }
    public List<InstallLauncherArchiveInstalledMod> InstalledMods { get; set; } = new();
    public string BackupId { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousVersion { get; set; }

    public bool Upgraded { get; set; }
}

public sealed class InspectLauncherArchiveRequest
{
    public string ArchivePath { get; set; } = string.Empty;
    public string? ModsPath { get; set; }
}

public sealed class LauncherArchiveTreeNode
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long? SizeBytes { get; set; }
    public List<LauncherArchiveTreeNode> Children { get; set; } = new();
}

public sealed class LauncherArchiveFileDiff
{
    public string Path { get; set; } = string.Empty;
    public string ChangeKind { get; set; } = "added";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OldSize { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? NewSize { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? OldModifiedMs { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? NewModifiedMs { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextDiff { get; set; }

    public bool TextDiffTruncated { get; set; }
}

public sealed class LauncherArchiveDiffSummary
{
    public long Added { get; set; }
    public long Changed { get; set; }
    public long Removed { get; set; }
    public List<LauncherArchiveFileDiff> Files { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TruncatedFileCount { get; set; }
}

public sealed class LauncherArchiveModRootInfo
{
    public string Path { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManifestUniqueId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManifestName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManifestVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExistingUniqueId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExistingVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExistingPath { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LauncherArchiveDiffSummary? DiffSummary { get; set; }
}

public sealed class InspectLauncherArchiveResult
{
    public string ArchivePath { get; set; } = string.Empty;
    public string ArchiveFileName { get; set; } = string.Empty;
    public long TotalEntries { get; set; }
    public long TotalFiles { get; set; }
    public List<LauncherArchiveModRootInfo> ModRoots { get; set; } = new();
    public List<LauncherArchiveTreeNode> Tree { get; set; } = new();
}

public sealed class ListLauncherInstallBackupsRequest
{
    public string? ModsPath { get; set; }
}

public sealed class LauncherInstallBackupSummary
{
    public string BackupId { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public long DeleteCount { get; set; }
    public long OverwriteCount { get; set; }
    public ulong CreatedAtMs { get; set; }
    public string? PrimaryModName { get; set; }
    public string? PrimaryVersion { get; set; }
    public long ModCount { get; set; }
}

public sealed class RestoreLauncherInstallBackupRequest
{
    public string BackupId { get; set; } = string.Empty;
    public string? ModsPath { get; set; }
}

public sealed class RestoreLauncherInstallBackupResult
{
    public string BackupId { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public List<string> RestoredPaths { get; set; } = new();
}

public sealed class SmapiUpdateRequiredByMod
{
    public string ModId { get; set; } = string.Empty;
    public string ModName { get; set; } = string.Empty;
    public string MinimumApiVersion { get; set; } = string.Empty;
}

public sealed class SmapiUpdateDownloadInfo
{
    public string Source { get; set; } = "github";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; set; }

    public long SizeBytes { get; set; }
    public string AssetName { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NexusModPageUrl { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NexusDownloadPopupUrl { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? NexusFileId { get; set; }
}

public sealed class SmapiUpdateCheckResult
{
    public string InstalledVersion { get; set; } = string.Empty;
    public string GameVersion { get; set; } = string.Empty;
    public string LatestStableVersion { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public bool UpdateAvailable { get; set; }
    public string VersionSource { get; set; } = "github";
    public List<SmapiUpdateRequiredByMod> RequiredByMods { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SmapiUpdateDownloadInfo? Download { get; set; }
}

public sealed class InstallSmapiUpdateRequest
{
    public string? JobId { get; set; }
    public string? DownloadUrl { get; set; }
    public string? ExpectedSha256 { get; set; }
    public string TargetVersion { get; set; } = string.Empty;
    public string? LocalFilePath { get; set; }
}

public sealed class InstallSmapiUpdateResult
{
    public bool Success { get; set; }
    public string InstalledVersion { get; set; } = string.Empty;
}

public sealed class SmapiInstallerDownloadCandidate
{
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool DoubleZipped { get; set; }
    public string Naming { get; set; } = "github";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Compatible { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SatisfiesTarget { get; set; }
}

public sealed class FindSmapiInstallerDownloadsResult
{
    public List<SmapiInstallerDownloadCandidate> Candidates { get; set; } = new();
}

// --- Nexus request payloads ---

public sealed class NexusSearchRequest
{
    public string? Query { get; set; }
    public string? TitleQuery { get; set; }
    public string? DescriptionQuery { get; set; }
    public string? AuthorQuery { get; set; }
    public string? UploaderQuery { get; set; }
    public long? Page { get; set; }
    public long? PageSize { get; set; }
    public string? TimeRange { get; set; }
    public string? Sort { get; set; }
    public string? Category { get; set; }
    public string? Language { get; set; }
    public string? TagsInclude { get; set; }
    public string? TagsExclude { get; set; }
    public bool? Ascending { get; set; }
    public bool? IncludeAdult { get; set; }
    public long? MinFileSize { get; set; }
    public long? MaxFileSize { get; set; }
    public long? MinDownloads { get; set; }
    public long? MaxDownloads { get; set; }
    public long? MinEndorsements { get; set; }
    public long? MaxEndorsements { get; set; }
}

public sealed class NexusDetailRequest
{
    public long ModId { get; set; }
    public bool? IncludeFiles { get; set; }
}

public sealed class NexusChangelogRequest
{
    public long ModId { get; set; }
}

public sealed class NexusCheckUpdatesRequest
{
    public string? ModsPath { get; set; }
    public bool? ForceRefresh { get; set; }
    public string? SessionId { get; set; }
}

public sealed class NexusCachedUpdatesRequest
{
    public string? ModsPath { get; set; }
}

public sealed class NexusDownloadRequest
{
    public string? DownloadId { get; set; }
    public long? ModId { get; set; }
    public long? FileId { get; set; }
    public string? Version { get; set; }
    public string? Title { get; set; }
}

public sealed class NexusResolveImageRequest
{
    public string? Url { get; set; }
    public bool? Refresh { get; set; }
    public string? ModKey { get; set; }
}

// --- internal persistence models (metadata.json for install backups) ---

public sealed class InstallBackupEntryMetadata
{
    public string EntryId { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public bool ExistedBefore { get; set; }
    public List<string> SavedPaths { get; set; } = new();
    public List<string> AddedPaths { get; set; } = new();
}

public sealed class InstallBackupInstalledModMetadata
{
    public string ModName { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string Operation { get; set; } = "freshInstall";
    public string TargetPath { get; set; } = string.Empty;
}

public sealed class InstallBackupSessionMetadata
{
    public string BackupId { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public ulong CreatedAtMs { get; set; }
    public string ModsPath { get; set; } = string.Empty;
    public string? PrimaryModName { get; set; }
    public string? PrimaryVersion { get; set; }
    public List<InstallBackupInstalledModMetadata> InstalledMods { get; set; } = new();
    public List<InstallBackupEntryMetadata> Entries { get; set; } = new();
}
