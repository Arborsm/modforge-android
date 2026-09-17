using SMAPIGameLoader.Launcher;
using SMAPIGameLoader.Tool;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Services;

/// <summary>
///     Launcher archive installation: staging, install planning, backup sessions, snapshot
///     replacement with config.json/i18n merges, archive inspection and backup restore.
///     Ported from the desktop install_manager/archive domains; on Android only zip archives
///     are supported (System.IO.Compression) — the desktop 7z/rar/tar handlers are not ported.
/// </summary>
public sealed class InstallService
{
    const string ManifestFileName = "manifest.json";
    const string BackupIdPrefix = "install-";

    static string BackupRoot => Path.Combine(FileTool.ExternalFilesDir, "ModForge", "launcher", "backups");

    // --- commands ---

    public Task<JsonElement?> GetLauncherBackupDirectoryAsync()
    {
        return Task.Run<JsonElement?>(() =>
        {
            var backupDirectory = BackupRoot;
            Directory.CreateDirectory(backupDirectory);
            return JsonSerializer.SerializeToElement(backupDirectory);
        });
    }

    public Task<JsonElement?> InstallLauncherArchiveAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(() =>
        {
            var parsed = LibraryService.Deserialize(request, LauncherJsonContext.Default.InstallLauncherArchiveRequest);
            var result = InstallLauncherArchive(parsed);
            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.InstallLauncherArchiveResult);
        });
    }

    public Task<JsonElement?> InspectLauncherArchiveAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(() =>
        {
            var parsed = LibraryService.Deserialize(request, LauncherJsonContext.Default.InspectLauncherArchiveRequest);
            var result = InspectLauncherArchive(parsed);
            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.InspectLauncherArchiveResult);
        });
    }

    public Task<JsonElement?> ListLauncherInstallBackupsAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(() =>
        {
            var parsed = LibraryService.Deserialize(request, LauncherJsonContext.Default.ListLauncherInstallBackupsRequest);
            var summaries = ListBackups(parsed.ModsPath);
            return JsonSerializer.SerializeToElement(summaries, LauncherJsonContext.Default.LauncherInstallBackupSummary);
        });
    }

    public Task<JsonElement?> RestoreLauncherInstallBackupAsync(JsonElement request)
    {
        return Task.Run<JsonElement?>(() =>
        {
            var parsed = LibraryService.Deserialize(request, LauncherJsonContext.Default.RestoreLauncherInstallBackupRequest);
            var result = RestoreBackup(parsed);
            return JsonSerializer.SerializeToElement(result, LauncherJsonContext.Default.RestoreLauncherInstallBackupResult);
        });
    }

    // --- install ---

    internal InstallLauncherArchiveResult InstallLauncherArchive(InstallLauncherArchiveRequest request)
    {
        var archivePath = request.ArchivePath.Trim();
        if (!File.Exists(archivePath))
            throw new LauncherCommandException("not_found", $"Launcher archive {archivePath} does not exist.");

        var modsPath = ResolveModsPath(request.ModsPath);
        Directory.CreateDirectory(modsPath);
        var backupRoot = BackupRoot;
        Directory.CreateDirectory(backupRoot);

        var workRoot = Path.Combine(Path.GetTempPath(), "modforge-launcher-install-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        try
        {
            var stagedRoot = Path.Combine(workRoot, "staged");
            try
            {
                ExpandZipArchive(archivePath, stagedRoot);
            }
            catch (Exception ex) when (ex is not LauncherCommandException)
            {
                throw new LauncherCommandException("io", $"Failed to stage archive bundle {archivePath}: {ex.Message}");
            }

            return InstallStagedBundle(stagedRoot, modsPath, backupRoot);
        }
        finally
        {
            TryDeleteRecursive(workRoot);
        }
    }

    static string ResolveModsPath(string? requestModsPath)
    {
        var candidate = requestModsPath?.Trim();
        if (!string.IsNullOrEmpty(candidate))
            return candidate;

        var settings = LauncherRuntimeService.LoadOrCreateSettings();
        var settingsModsPath = settings.ModsPath?.Trim();
        if (!string.IsNullOrEmpty(settingsModsPath))
            return settingsModsPath;

        return ModTool.ModsDir;
    }

    sealed record ModBundle(string Root, string ModName, string? UniqueId, string? Version, string FolderName);

    sealed record InstallPlanEntry(
        ModBundle Bundle,
        string TargetPath,
        string FolderName,
        string? PreviousVersion,
        bool IsUpgrade,
        bool PreservedConfig,
        long PreservedI18nFiles);

    InstallLauncherArchiveResult InstallStagedBundle(string stagedRoot, string modsPath, string backupRoot)
    {
        if (!Directory.Exists(stagedRoot))
            throw new LauncherCommandException("io", $"Bundle root {stagedRoot} does not exist.");

        Directory.CreateDirectory(modsPath);
        Directory.CreateDirectory(backupRoot);

        //Existing install targets keyed by normalized unique id.
        var existingScan = LauncherLibraryScanForInstall(modsPath);
        var existingByUniqueId = new Dictionary<string, LauncherLibraryModSummary>(StringComparer.Ordinal);
        foreach (var mod in existingScan.Mods)
        {
            if (mod.UniqueId is { Length: > 0 })
                existingByUniqueId[LauncherJsonHelper.NormalizeUniqueId(mod.UniqueId)] = mod;
        }

        //Incoming mod bundles from the staged tree.
        var bundleScanRoot = Directory.Exists(Path.Combine(stagedRoot, "Mods")) ? Path.Combine(stagedRoot, "Mods") : stagedRoot;
        var bundles = new List<ModBundle>();
        foreach (var root in DiscoverProjectRootsForInstall(bundleScanRoot))
        {
            var manifest = LauncherJsonHelper.ParseJsonFile(Path.Combine(root, ManifestFileName));
            var rootDirName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? string.Empty;
            var modName = LauncherJsonHelper.StringField(manifest, "Name") ?? rootDirName ?? "Unnamed Mod";
            var uniqueId = LauncherJsonHelper.StringField(manifest, "UniqueID");
            var version = LauncherJsonHelper.StringField(manifest, "Version");
            var folderName = string.Equals(root, stagedRoot, StringComparison.Ordinal)
                ? SanitizeOrFallback(LauncherJsonHelper.SanitizeFileName(modName).Trim())
                : rootDirName;
            bundles.Add(new ModBundle(root, modName, uniqueId, version, folderName));
        }

        bundles.Sort((left, right) =>
        {
            var nameComparison = string.CompareOrdinal(LauncherJsonHelper.NormalizeUniqueId(left.ModName), LauncherJsonHelper.NormalizeUniqueId(right.ModName));
            return nameComparison != 0 ? nameComparison : string.CompareOrdinal(left.Root, right.Root);
        });

        if (bundles.Count == 0)
            throw new LauncherCommandException("invalid_archive", "The bundle did not contain any installable mods or overlays.");

        //Plan one operation per bundle: same unique id replaces in place, otherwise a fresh folder.
        var plans = new List<InstallPlanEntry>();
        var plannedUniqueIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bundle in bundles)
        {
            string targetPath;
            string folderName;
            string? previousVersion = null;
            var existing = bundle.UniqueId is { Length: > 0 } ? existingByUniqueId.GetValueOrDefault(LauncherJsonHelper.NormalizeUniqueId(bundle.UniqueId)) : null;
            if (existing is not null)
            {
                targetPath = existing.AbsolutePath;
                folderName = existing.FolderName;
                previousVersion = existing.Version;
            }
            else
            {
                targetPath = LauncherJsonHelper.UniquePath(Path.Combine(modsPath, bundle.FolderName));
                folderName = Path.GetFileName(targetPath);
            }

            if (bundle.UniqueId is { Length: > 0 })
            {
                var normalized = LauncherJsonHelper.NormalizeUniqueId(bundle.UniqueId);
                if (!plannedUniqueIds.Add(normalized))
                    throw new LauncherCommandException("conflict", $"The bundle contains multiple install targets for unique ID {bundle.UniqueId}.");
            }

            plans.Add(new InstallPlanEntry(bundle, targetPath, folderName, previousVersion, Directory.Exists(targetPath), false, 0));
        }

        //Overlay pass: loose bundle directories that match an installed/planned mod by name
        //alias merge their files into that mod's target folder (mod-pack overlay layouts).
        var aliasMap = new Dictionary<string, string>(StringComparer.Ordinal);
        void RegisterAlias(string? alias, string targetPath)
        {
            if (string.IsNullOrWhiteSpace(alias))
                return;

            var normalized = LauncherJsonHelper.NormalizeUniqueId(alias.TrimStart('.'));
            if (normalized.Length == 0)
                return;

            aliasMap.TryAdd(normalized, targetPath);
        }

        foreach (var mod in existingScan.Mods)
        {
            RegisterAlias(mod.Name, mod.AbsolutePath);
            RegisterAlias(mod.UniqueId, mod.AbsolutePath);
            RegisterAlias(mod.FolderName, mod.AbsolutePath);
        }

        foreach (var plan in plans)
        {
            RegisterAlias(plan.Bundle.ModName, plan.TargetPath);
            RegisterAlias(plan.Bundle.UniqueId, plan.TargetPath);
            RegisterAlias(plan.FolderName, plan.TargetPath);
        }

        var plannedModRoots = plans.Select(plan => Path.GetFullPath(plan.Bundle.Root)).ToList();
        var overlayOperations = new List<(string OverlayRoot, string TargetPath)>();
        foreach (var relativePath in CollectRelativeFiles(stagedRoot))
        {
            var absolutePath = Path.Combine(stagedRoot, relativePath);
            if (plannedModRoots.Any(root => absolutePath.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                continue;

            var components = relativePath.Replace('\\', '/').Split('/');
            string? matchedTarget = null;
            var matchedPrefixComponents = 0;
            for (var take = 1; take <= components.Length - 1; take += 1)
            {
                if (aliasMap.TryGetValue(LauncherJsonHelper.NormalizeUniqueId(components[take - 1].TrimStart('.')), out var target))
                {
                    matchedTarget = target;
                    matchedPrefixComponents = take;
                    break;
                }
            }

            if (matchedTarget is null)
                continue;

            var overlayRootRelative = string.Join(Path.DirectorySeparatorChar, components.Take(matchedPrefixComponents));
            var overlayRoot = Path.Combine(stagedRoot, overlayRootRelative);
            if (!overlayOperations.Any(entry => string.Equals(entry.OverlayRoot, overlayRoot, StringComparison.Ordinal)))
                overlayOperations.Add((overlayRoot, matchedTarget));
        }

        //Backup session.
        var backupId = BackupIdPrefix + LauncherJsonHelper.CurrentTimestampMs();
        var backupPath = LauncherJsonHelper.UniquePath(Path.Combine(backupRoot, backupId));
        Directory.CreateDirectory(backupPath);
        backupId = Path.GetFileName(backupPath);
        var workDirectory = Path.Combine(backupPath, "_work");
        Directory.CreateDirectory(workDirectory);

        var entriesMetadata = new List<InstallBackupEntryMetadata>();
        var executedOperations = new List<ExecutedInstallOperation>();
        try
        {
            var entryIndex = 0;
            foreach (var plan in plans)
            {
                var entryId = $"entry-{++entryIndex:D2}";
                var entryRoot = Path.Combine(backupPath, "entries", entryId);
                var beforeRoot = Path.Combine(entryRoot, "before");
                Directory.CreateDirectory(beforeRoot);

                var recorder = new BackupRecorder(plan.TargetPath, Directory.Exists(plan.TargetPath));

                //Stage the incoming bundle into a prepared copy.
                var prepared = Path.Combine(workDirectory, entryId, "prepared");
                CopyTree(plan.Bundle.Root, prepared);

                if (plan.IsUpgrade)
                    PreserveExistingFilesForUpgrade(plan.TargetPath, prepared);

                ApplyReplaceSnapshot(prepared, plan.TargetPath, beforeRoot, recorder);

                executedOperations.Add(new ExecutedInstallOperation(
                    new InstallLauncherArchiveInstalledMod
                    {
                        ModName = plan.Bundle.ModName,
                        UniqueId = plan.Bundle.UniqueId,
                        Version = plan.Bundle.Version,
                        TargetPath = plan.TargetPath,
                        PreservedConfig = recorder.PreservedConfig,
                        PreservedI18nFiles = recorder.PreservedI18nFiles,
                    },
                    Operation: plan.IsUpgrade ? "upgradeReplace" : "freshInstall",
                    IsUpgrade: plan.IsUpgrade,
                    PreviousVersion: plan.PreviousVersion));

                entriesMetadata.Add(new InstallBackupEntryMetadata
                {
                    EntryId = entryId,
                    TargetPath = plan.TargetPath,
                    ExistedBefore = recorder.ExistedBefore,
                    SavedPaths = recorder.SavedPaths.OrderBy(path => path, StringComparer.Ordinal).ToList(),
                    AddedPaths = recorder.AddedPaths.OrderBy(path => path, StringComparer.Ordinal).ToList(),
                });
            }

            foreach (var overlay in overlayOperations)
            {
                var entryId = $"entry-{++entryIndex:D2}";
                var entryRoot = Path.Combine(backupPath, "entries", entryId);
                var beforeRoot = Path.Combine(entryRoot, "before");
                Directory.CreateDirectory(beforeRoot);

                var recorder = new BackupRecorder(overlay.TargetPath, Directory.Exists(overlay.TargetPath));
                ApplyOverlayMerge(overlay.OverlayRoot, overlay.TargetPath, beforeRoot, recorder);

                executedOperations.Add(new ExecutedInstallOperation(
                    new InstallLauncherArchiveInstalledMod
                    {
                        ModName = Path.GetFileName(Path.TrimEndingDirectorySeparator(overlay.TargetPath)),
                        UniqueId = null,
                        Version = null,
                        TargetPath = overlay.TargetPath,
                        PreservedConfig = recorder.PreservedConfig,
                        PreservedI18nFiles = recorder.PreservedI18nFiles,
                    },
                    Operation: "overlayMerge",
                    IsUpgrade: false,
                    PreviousVersion: null));

                entriesMetadata.Add(new InstallBackupEntryMetadata
                {
                    EntryId = entryId,
                    TargetPath = overlay.TargetPath,
                    ExistedBefore = recorder.ExistedBefore,
                    SavedPaths = recorder.SavedPaths.OrderBy(path => path, StringComparer.Ordinal).ToList(),
                    AddedPaths = recorder.AddedPaths.OrderBy(path => path, StringComparer.Ordinal).ToList(),
                });
            }
        }
        catch (Exception)
        {
            TryDeleteRecursive(workDirectory);
            throw;
        }

        TryDeleteRecursive(workDirectory);

        var installedMods = executedOperations.Select(operation => operation.Mod).ToList();
        //Primary = first direct mod (non content pack) by name, matching the desktop priority.
        var primary = executedOperations
            .OrderBy(operation => operation.Mod.UniqueId is null ? 0 : 1)
            .ThenBy(operation => LauncherJsonHelper.NormalizeUniqueId(operation.Mod.ModName), StringComparer.Ordinal)
            .ThenBy(operation => operation.Mod.TargetPath, StringComparer.Ordinal)
            .FirstOrDefault();
        if (primary is null)
            throw new LauncherCommandException("io", "The install manager did not produce any installed targets.");

        var metadata = new InstallBackupSessionMetadata
        {
            BackupId = backupId,
            BackupPath = backupPath,
            CreatedAtMs = LauncherJsonHelper.CurrentTimestampMs(),
            ModsPath = modsPath,
            PrimaryModName = primary.Mod.ModName,
            PrimaryVersion = primary.Mod.Version,
            InstalledMods = executedOperations
                .Select(operation => new InstallBackupInstalledModMetadata
                {
                    ModName = operation.Mod.ModName,
                    Version = operation.Mod.Version,
                    Operation = operation.Operation,
                    TargetPath = operation.Mod.TargetPath,
                })
                .ToList(),
            Entries = entriesMetadata,
        };
        LauncherJsonHelper.WriteJsonFile(Path.Combine(backupPath, "metadata.json"), JsonSerializer.SerializeToNode(metadata, LauncherJsonContext.Default.InstallBackupSessionMetadata)!);

        return new InstallLauncherArchiveResult
        {
            ModName = primary.Mod.ModName,
            UniqueId = primary.Mod.UniqueId,
            Version = primary.Mod.Version,
            TargetPath = primary.Mod.TargetPath,
            PreservedConfig = primary.Mod.PreservedConfig,
            PreservedI18nFiles = primary.Mod.PreservedI18nFiles,
            InstalledMods = installedMods,
            BackupId = backupId,
            BackupPath = backupPath,
            PreviousVersion = primary.PreviousVersion,
            Upgraded = primary.IsUpgrade,
        };
    }

    sealed record ExecutedInstallOperation(InstallLauncherArchiveInstalledMod Mod, string Operation, bool IsUpgrade, string? PreviousVersion);

    static string SanitizeOrFallback(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "InstalledMod" : value;
    }

    sealed class BackupRecorder
    {
        public string TargetPath { get; }
        public bool ExistedBefore { get; }
        public HashSet<string> SavedPaths { get; } = new(StringComparer.Ordinal);
        public HashSet<string> AddedPaths { get; } = new(StringComparer.Ordinal);
        public bool PreservedConfig { get; set; }
        public long PreservedI18nFiles { get; set; }

        public BackupRecorder(string targetPath, bool existedBefore)
        {
            TargetPath = targetPath;
            ExistedBefore = existedBefore;
        }
    }

    /// <summary>Copies the existing config.json and i18n/*.json files into the prepared copy with ExistingWins merges.</summary>
    static void PreserveExistingFilesForUpgrade(string existingRoot, string preparedRoot)
    {
        var existingConfig = Path.Combine(existingRoot, "config.json");
        if (File.Exists(existingConfig))
        {
            var preparedConfig = Path.Combine(preparedRoot, "config.json");
            if (File.Exists(preparedConfig))
            {
                var merged = LauncherJsonHelper.MergeJsonValues(
                    LauncherJsonHelper.ParseJsonFile(existingConfig).Deserialize<JsonNode>() ?? new JsonObject(),
                    LauncherJsonHelper.ParseJsonFile(preparedConfig).Deserialize<JsonNode>() ?? new JsonObject(),
                    existingWins: true);
                LauncherJsonHelper.WriteJsonFile(preparedConfig, merged);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(preparedConfig)!);
                File.Copy(existingConfig, preparedConfig, overwrite: true);
            }
        }

        var existingI18n = Path.Combine(existingRoot, "i18n");
        if (!Directory.Exists(existingI18n))
            return;

        foreach (var relativePath in CollectRelativeFiles(existingI18n))
        {
            if (!relativePath.EndsWith(".json", StringComparison.Ordinal))
                continue;

            var preparedPath = Path.Combine(preparedRoot, "i18n", relativePath);
            var existingPath = Path.Combine(existingI18n, relativePath);
            if (File.Exists(preparedPath))
            {
                var merged = LauncherJsonHelper.MergeJsonValues(
                    LauncherJsonHelper.ParseJsonFile(existingPath).Deserialize<JsonNode>() ?? new JsonObject(),
                    LauncherJsonHelper.ParseJsonFile(preparedPath).Deserialize<JsonNode>() ?? new JsonObject(),
                    existingWins: true);
                LauncherJsonHelper.WriteJsonFile(preparedPath, merged);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(preparedPath)!);
                File.Copy(existingPath, preparedPath, overwrite: true);
            }
        }
    }

    /// <summary>Snapshot replacement: delete stale files, back up changed ones, copy all prepared files.</summary>
    static void ApplyReplaceSnapshot(string preparedRoot, string targetPath, string beforeRoot, BackupRecorder recorder)
    {
        var existingFiles = Directory.Exists(targetPath) ? CollectRelativeFiles(targetPath) : new List<string>();
        var newFiles = CollectRelativeFiles(preparedRoot);
        var newSet = newFiles.Select(LauncherJsonHelper.NormalizeRelativePath).ToHashSet(StringComparer.Ordinal);
        var existingSet = existingFiles.Select(LauncherJsonHelper.NormalizeRelativePath).ToHashSet(StringComparer.Ordinal);

        foreach (var relativePath in existingFiles)
        {
            var normalized = LauncherJsonHelper.NormalizeRelativePath(relativePath);
            var targetFile = Path.Combine(targetPath, relativePath);
            var preparedFile = Path.Combine(preparedRoot, relativePath);
            if (!newSet.Contains(normalized))
            {
                BackupExistingFile(beforeRoot, targetPath, relativePath, recorder);
                if (File.Exists(targetFile))
                    File.Delete(targetFile);
                continue;
            }

            if (LauncherJsonHelper.FilesDiffer(targetFile, preparedFile))
                BackupExistingFile(beforeRoot, targetPath, relativePath, recorder);
        }

        CleanupEmptyTree(targetPath);

        foreach (var relativePath in newFiles)
        {
            var normalized = LauncherJsonHelper.NormalizeRelativePath(relativePath);
            if (!existingSet.Contains(normalized))
                recorder.AddedPaths.Add(normalized);

            var destination = Path.Combine(targetPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(preparedRoot, relativePath), destination, overwrite: true);
        }

        CleanupEmptyTree(targetPath);
    }

    /// <summary>
    ///     Overlay merge: copies overlay files into the target mod folder; existing .json files
    ///     deep-merge with IncomingWins (after backing up the original), everything else is
    ///     copied over with a backup.
    /// </summary>
    static void ApplyOverlayMerge(string overlayRoot, string targetPath, string beforeRoot, BackupRecorder recorder)
    {
        if (!Directory.Exists(targetPath))
            throw new LauncherCommandException("not_found", $"Overlay target {targetPath} does not exist.");

        foreach (var relativePath in CollectRelativeFiles(overlayRoot))
        {
            var source = Path.Combine(overlayRoot, relativePath);
            var destination = Path.Combine(targetPath, relativePath);
            var normalized = LauncherJsonHelper.NormalizeRelativePath(relativePath);

            if (File.Exists(destination))
                BackupExistingFile(beforeRoot, targetPath, relativePath, recorder);
            else
                recorder.AddedPaths.Add(normalized);

            var isJson = relativePath.EndsWith(".json", StringComparison.Ordinal);
            if (isJson && File.Exists(destination))
            {
                var merged = LauncherJsonHelper.MergeJsonValues(
                    LauncherJsonHelper.ParseJsonFile(destination).Deserialize<JsonNode>() ?? new JsonObject(),
                    LauncherJsonHelper.ParseJsonFile(source).Deserialize<JsonNode>() ?? new JsonObject(),
                    existingWins: false);
                LauncherJsonHelper.WriteJsonFile(destination, merged);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
        }
    }

    static void BackupExistingFile(string beforeRoot, string targetRoot, string relativePath, BackupRecorder recorder)
    {
        var normalized = LauncherJsonHelper.NormalizeRelativePath(relativePath);
        if (!recorder.SavedPaths.Add(normalized))
            return;

        var source = Path.Combine(targetRoot, relativePath);
        if (!File.Exists(source))
            return;

        var destination = Path.Combine(beforeRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    static void CleanupEmptyTree(string root)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch (Exception)
            {
                //best-effort pruning
            }
        }
    }

    static void TryDeleteRecursive(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine("InstallService: cleanup failed for " + path + ": " + ex.Message);
        }
    }

    /// <summary>Recursively collects file paths relative to root, sorted ordinally; directories are never returned.</summary>
    internal static List<string> CollectRelativeFiles(string root)
    {
        var result = new List<string>();
        if (!Directory.Exists(root))
            return result;

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.GetFiles(current))
                result.Add(Path.GetRelativePath(root, file));

            foreach (var directory in Directory.GetDirectories(current))
                pending.Push(directory);
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    static void CopyTree(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var relativePath in CollectRelativeFiles(sourceRoot))
        {
            var destination = Path.Combine(destinationRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(sourceRoot, relativePath), destination, overwrite: true);
        }
    }

    // --- zip extraction ---

    /// <summary>Expands a zip archive under destination with the desktop entry-path sanitization rules.</summary>
    static void ExpandZipArchive(string archivePath, string destination)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var relativePath = SanitizeArchiveEntryPath(archivePath, entry.FullName);
            var outputPath = Path.Combine(destination, relativePath);
            if (relativePath.EndsWith("/", StringComparison.Ordinal) || string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var entryStream = entry.Open();
            using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            entryStream.CopyTo(outputStream);
        }
    }

    /// <summary>Rejects absolute paths, drive prefixes and '..' components (sanitize_archive_entry_path).</summary>
    static string SanitizeArchiveEntryPath(string archivePath, string entryName)
    {
        var normalized = entryName.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw new LauncherCommandException("invalid_archive", $"Launcher archive {archivePath} contains an unsafe path entry: {entryName}");

        var components = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safe = new List<string>();
        foreach (var component in components)
        {
            if (component == ".")
                continue;
            if (component == "..")
                throw new LauncherCommandException("invalid_archive", $"Launcher archive {archivePath} contains an unsafe path entry: {entryName}");

            safe.Add(component);
        }

        if (safe.Count == 0)
            throw new LauncherCommandException("invalid_archive", $"Launcher archive {archivePath} contains an empty path entry: {entryName}");

        return string.Join(Path.DirectorySeparatorChar, safe);
    }

    // --- shared discovery helpers (reuse the library scanner) ---

    static LauncherLibraryScanResult LauncherLibraryScanForInstall(string modsPath)
    {
        var service = new LibraryService();
        return service.ScanLibraryAtPath(modsPath);
    }

    static List<string> DiscoverProjectRootsForInstall(string scanRoot)
    {
        return LibraryService.DiscoverProjectRoots(scanRoot);
    }

    // --- inspection ---

    internal InspectLauncherArchiveResult InspectLauncherArchive(InspectLauncherArchiveRequest request)
    {
        var archivePath = request.ArchivePath.Trim();
        if (archivePath.Length == 0)
            throw new LauncherCommandException("invalid_args", "archivePath is required.");
        if (!File.Exists(archivePath))
            throw new LauncherCommandException("not_found", $"Launcher archive {archivePath} does not exist.");

        var workRoot = Path.Combine(Path.GetTempPath(), "modforge-launcher-inspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        try
        {
            var extractedRoot = Path.Combine(workRoot, "payload");
            ExpandZipArchive(archivePath, extractedRoot);

            var totalEntries = 0L;
            var totalFiles = 0L;
            var modRoots = new SortedSet<string>(StringComparer.Ordinal);
            var tree = BuildArchiveTree(extractedRoot, extractedRoot, modRoots, ref totalEntries, ref totalFiles);

            LauncherLibraryScanResult? existingLibrary = null;
            var modsPath = request.ModsPath?.Trim();
            if (!string.IsNullOrEmpty(modsPath) && Directory.Exists(modsPath))
                existingLibrary = LauncherLibraryScanForInstall(modsPath!);

            var rootInfos = new List<LauncherArchiveModRootInfo>();
            foreach (var root in modRoots)
                rootInfos.Add(BuildModRootInfo(extractedRoot, root, existingLibrary));

            return new InspectLauncherArchiveResult
            {
                ArchivePath = archivePath,
                ArchiveFileName = Path.GetFileName(archivePath) ?? string.Empty,
                TotalEntries = totalEntries,
                TotalFiles = totalFiles,
                ModRoots = rootInfos,
                Tree = tree,
            };
        }
        finally
        {
            TryDeleteRecursive(workRoot);
        }
    }

    static List<LauncherArchiveTreeNode> BuildArchiveTree(
        string archiveRoot,
        string currentDirectory,
        SortedSet<string> modRoots,
        ref long totalEntries,
        ref long totalFiles)
    {
        var nodes = new List<LauncherArchiveTreeNode>();
        foreach (var directory in Directory.GetDirectories(currentDirectory))
        {
            totalEntries += 1;
            nodes.Add(new LauncherArchiveTreeNode
            {
                Name = Path.GetFileName(directory),
                Path = Path.GetRelativePath(archiveRoot, directory).Replace('\\', '/'),
                IsDirectory = true,
                SizeBytes = null,
                Children = BuildArchiveTree(archiveRoot, directory, modRoots, ref totalEntries, ref totalFiles),
            });
        }

        foreach (var file in Directory.GetFiles(currentDirectory))
        {
            totalEntries += 1;
            totalFiles += 1;
            var relativePath = Path.GetRelativePath(archiveRoot, file).Replace('\\', '/');
            if (string.Equals(Path.GetFileName(file), ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(relativePath)?.Replace('\\', '/').Trim('/') ?? string.Empty;
                modRoots.Add(parent.Length == 0 ? "." : parent);
            }

            nodes.Add(new LauncherArchiveTreeNode
            {
                Name = Path.GetFileName(file),
                Path = relativePath,
                IsDirectory = false,
                SizeBytes = new FileInfo(file).Length,
                Children = new List<LauncherArchiveTreeNode>(),
            });
        }

        //directories first, then lowercase name, then path (build_archive_tree ordering)
        nodes.Sort((left, right) =>
        {
            if (left.IsDirectory != right.IsDirectory)
                return left.IsDirectory ? -1 : 1;
            var nameComparison = string.CompareOrdinal(left.Name.ToLowerInvariant(), right.Name.ToLowerInvariant());
            return nameComparison != 0 ? nameComparison : string.CompareOrdinal(left.Path, right.Path);
        });

        return nodes;
    }

    static LauncherArchiveModRootInfo BuildModRootInfo(string archiveRoot, string rootPath, LauncherLibraryScanResult? existingLibrary)
    {
        var rootDirectory = rootPath == "." ? archiveRoot : Path.Combine(archiveRoot, rootPath);
        var manifest = LauncherJsonHelper.TryParseJsonFile(Path.Combine(rootDirectory, ManifestFileName));

        var info = new LauncherArchiveModRootInfo { Path = rootPath };
        if (manifest is not null)
        {
            info.ManifestUniqueId = LauncherJsonHelper.StringField(manifest.Value, "UniqueID");
            info.ManifestName = LauncherJsonHelper.StringField(manifest.Value, "Name");
            info.ManifestVersion = LauncherJsonHelper.StringField(manifest.Value, "Version");
        }

        if (info.ManifestUniqueId is not null && existingLibrary is not null)
        {
            var existing = existingLibrary.Mods.FirstOrDefault(mod =>
                mod.UniqueId is not null
                && LauncherJsonHelper.NormalizeUniqueId(mod.UniqueId) == LauncherJsonHelper.NormalizeUniqueId(info.ManifestUniqueId));
            if (existing is not null)
            {
                info.ExistingUniqueId = existing.UniqueId;
                info.ExistingVersion = existing.Version;
                info.ExistingPath = existing.AbsolutePath;
                if (Directory.Exists(existing.AbsolutePath))
                    info.DiffSummary = ComputeDiffSummary(rootDirectory, existing.AbsolutePath);
            }
        }

        return info;
    }

    /// <summary>Added/removed/changed counts with a capped per-root file list; text diffs are not produced on Android.</summary>
    static LauncherArchiveDiffSummary ComputeDiffSummary(string incomingRoot, string existingRoot)
    {
        const int maxDiffFilesPerRoot = 300;

        var incomingFiles = CollectRelativeFiles(incomingRoot);
        var existingFiles = CollectRelativeFiles(existingRoot);
        var incomingSet = incomingFiles.Select(LauncherJsonHelper.NormalizeRelativePath).ToHashSet(StringComparer.Ordinal);
        var existingSet = existingFiles.Select(LauncherJsonHelper.NormalizeRelativePath).ToHashSet(StringComparer.Ordinal);

        var summary = new LauncherArchiveDiffSummary();
        var truncated = 0L;
        void AddDiff(LauncherArchiveFileDiff diff)
        {
            if (summary.Files.Count < maxDiffFilesPerRoot)
                summary.Files.Add(diff);
            else
                truncated += 1;
        }

        foreach (var relativePath in incomingFiles)
        {
            var normalized = LauncherJsonHelper.NormalizeRelativePath(relativePath);
            if (!existingSet.Contains(normalized))
            {
                summary.Added += 1;
                AddDiff(new LauncherArchiveFileDiff
                {
                    Path = normalized,
                    ChangeKind = "added",
                    NewSize = new FileInfo(Path.Combine(incomingRoot, relativePath)).Length,
                });
            }
            else if (LauncherJsonHelper.FilesDiffer(Path.Combine(existingRoot, relativePath), Path.Combine(incomingRoot, relativePath)))
            {
                summary.Changed += 1;
                AddDiff(new LauncherArchiveFileDiff
                {
                    Path = normalized,
                    ChangeKind = "changed",
                    OldSize = new FileInfo(Path.Combine(existingRoot, relativePath)).Length,
                    NewSize = new FileInfo(Path.Combine(incomingRoot, relativePath)).Length,
                    OldModifiedMs = (ulong?)ToUnixMs(File.GetLastWriteTimeUtc(Path.Combine(existingRoot, relativePath))),
                    NewModifiedMs = (ulong?)ToUnixMs(File.GetLastWriteTimeUtc(Path.Combine(incomingRoot, relativePath))),
                });
            }
        }

        foreach (var relativePath in existingFiles)
        {
            var normalized = LauncherJsonHelper.NormalizeRelativePath(relativePath);
            if (incomingSet.Contains(normalized))
                continue;

            summary.Removed += 1;
            AddDiff(new LauncherArchiveFileDiff
            {
                Path = normalized,
                ChangeKind = "removed",
                OldSize = new FileInfo(Path.Combine(existingRoot, relativePath)).Length,
                OldModifiedMs = (ulong?)ToUnixMs(File.GetLastWriteTimeUtc(Path.Combine(existingRoot, relativePath))),
            });
        }

        summary.TruncatedFileCount = truncated > 0 ? truncated : null;
        return summary;
    }

    static long ToUnixMs(DateTime value)
    {
        return (long)new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeMilliseconds();
    }

    // --- backup listing & restore ---

    List<LauncherInstallBackupSummary> ListBackups(string? requestModsPath)
    {
        var summaries = new List<LauncherInstallBackupSummary>();
        var backupRoot = BackupRoot;
        if (!Directory.Exists(backupRoot))
            return summaries;

        var modsPath = ResolveModsPath(requestModsPath);
        foreach (var directory in Directory.GetDirectories(backupRoot))
        {
            var metadataPath = Path.Combine(directory, "metadata.json");
            if (!File.Exists(metadataPath))
                continue;

            InstallBackupSessionMetadata? metadata;
            try
            {
                metadata = JsonSerializer.Deserialize(File.ReadAllText(metadataPath), LauncherJsonContext.Default.InstallBackupSessionMetadata);
            }
            catch (Exception)
            {
                continue; //unreadable metadata: not a restorable backup session
            }

            if (metadata is null)
                continue;
            if (!string.Equals(LauncherJsonHelper.NormalizeUniqueId(metadata.ModsPath), LauncherJsonHelper.NormalizeUniqueId(modsPath), StringComparison.Ordinal)
                && !string.Equals(Path.TrimEndingDirectorySeparator(metadata.ModsPath), Path.TrimEndingDirectorySeparator(modsPath), StringComparison.OrdinalIgnoreCase))
            {
                continue; //bound to a different Mods folder
            }

            summaries.Add(new LauncherInstallBackupSummary
            {
                BackupId = metadata.BackupId,
                BackupPath = metadata.BackupPath,
                DeleteCount = metadata.Entries.Sum(entry => entry.AddedPaths.Count),
                OverwriteCount = metadata.Entries.Sum(entry => entry.SavedPaths.Count),
                CreatedAtMs = metadata.CreatedAtMs,
                PrimaryModName = metadata.PrimaryModName,
                PrimaryVersion = metadata.PrimaryVersion,
                ModCount = metadata.InstalledMods.Count > 0 ? metadata.InstalledMods.Count : metadata.Entries.Count,
            });
        }

        //newest first; backup ids embed the creation timestamp
        summaries.Sort((left, right) => string.CompareOrdinal(right.BackupId, left.BackupId));
        return summaries;
    }

    RestoreLauncherInstallBackupResult RestoreBackup(RestoreLauncherInstallBackupRequest request)
    {
        var backupId = request.BackupId.Trim();
        if (backupId.Length == 0)
            throw new LauncherCommandException("invalid_args", "backupId is required.");
        if (backupId.Contains('/') || backupId.Contains('\\') || backupId.Contains(':'))
            throw new LauncherCommandException("invalid_args", $"backupId {backupId} must identify a direct backup entry.");

        var backupRoot = BackupRoot;
        var backupPath = Path.Combine(backupRoot, backupId);
        if (!Directory.Exists(backupPath))
            throw new LauncherCommandException("not_found", $"backupId {backupId} must identify a direct backup entry.");

        var metadataPath = Path.Combine(backupPath, "metadata.json");
        if (!File.Exists(metadataPath))
            throw new LauncherCommandException("not_found", $"Backup metadata {metadataPath} is missing.");

        InstallBackupSessionMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize(File.ReadAllText(metadataPath), LauncherJsonContext.Default.InstallBackupSessionMetadata);
        }
        catch (Exception ex)
        {
            throw new LauncherCommandException("invalid_json", $"Backup metadata {metadataPath} is invalid JSON: {ex.Message}");
        }

        if (metadata is null)
            throw new LauncherCommandException("invalid_json", $"Backup metadata {metadataPath} is invalid JSON.");

        var restoredPaths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in metadata.Entries.AsEnumerable().Reverse())
        {
            var targetRoot = entry.TargetPath;
            var beforeRoot = Path.Combine(backupPath, "entries", entry.EntryId, "before");

            foreach (var relativePath in entry.AddedPaths)
            {
                var targetPath = Path.Combine(targetRoot, relativePath);
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
                else if (Directory.Exists(targetPath))
                {
                    Directory.Delete(targetPath, recursive: true);
                }

                CleanupEmptyParents(targetRoot, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(targetPath)));
            }

            foreach (var relativePath in entry.SavedPaths)
            {
                var source = Path.Combine(beforeRoot, relativePath);
                if (!File.Exists(source))
                    throw new LauncherCommandException("not_found", $"Backup {backupId} is missing backup file {source}.");

                var destination = Path.Combine(targetRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }

            if (!entry.ExistedBefore)
            {
                CleanupEmptyTree(targetRoot);
                if (Directory.Exists(targetRoot) && !Directory.EnumerateFileSystemEntries(targetRoot).Any())
                    TryDeleteRecursive(targetRoot);
            }
            else
            {
                CleanupEmptyTree(targetRoot);
            }

            restoredPaths.Add(entry.TargetPath);
        }

        return new RestoreLauncherInstallBackupResult
        {
            BackupId = metadata.BackupId,
            BackupPath = metadata.BackupPath,
            RestoredPaths = restoredPaths.ToList(),
        };
    }

    static void CleanupEmptyParents(string root, string? directory)
    {
        if (string.IsNullOrEmpty(directory))
            return;

        var current = Path.GetFullPath(directory);
        var stop = Path.GetFullPath(Path.TrimEndingDirectorySeparator(root));
        while (current.StartsWith(stop, StringComparison.Ordinal) && current != stop)
        {
            if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                break;

            Directory.Delete(current);
            current = Path.GetDirectoryName(current) ?? string.Empty;
        }
    }
}
