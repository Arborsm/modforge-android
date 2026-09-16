using SMAPIGameLoader.Launcher;
using SMAPIGameLoader.Tool;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SMAPIGameLoader.Services;

/// <summary>
///     Launcher mods library: scanning the Mods folder, manifest parsing, dependency health
///     graph, enable/disable via dot-prefix renames, cover assignment and image failure
///     bookkeeping. Ported from the desktop launcher domain (library.rs / image_failures.rs),
///     adapted where the desktop implementation is platform specific (SMAPI version detection
///     reads the Android SMAPI assembly instead of Windows file versions).
/// </summary>
public sealed class LibraryService
{
    const string ModsSubDirectory = "Mods";
    const string ManifestFileName = "manifest.json";
    const string UnsortedStorageFolderId = "unsorted";
    const string UnsortedStorageFolderName = "Unsorted";
    const uint ImageFailureThreshold = 3;

    static readonly string[] SkippedScanDirectories =
    {
        ".git", ".hg", ".svn", ".idea", ".vs", "__MACOSX", "node_modules", "target", "bin", "obj",
    };

    static string DataDirectory => Path.Combine(FileTool.ExternalFilesDir, "ModForge", "launcher");
    static string LibraryStateFilePath => Path.Combine(DataDirectory, "library.json");
    static string CoversFilePath => Path.Combine(DataDirectory, "covers.json");
    static string ImageFailuresFilePath => Path.Combine(DataDirectory, "image-failures.json");

    // --- commands ---

    public Task<JsonElement?> ScanLauncherLibraryAsync(JsonElement request)
    {
        return Task.Run(() =>
        {
            var parsed = Deserialize(request, LauncherJsonContext.Default.ScanLauncherLibraryRequest);
            var modsPath = parsed.ModsPath.Trim();
            if (modsPath.Length == 0)
                throw new LauncherCommandException("invalid_args", "modsPath is required.");

            var scan = ScanLibraryAtPath(modsPath);
            ApplyCoverEnrichment(scan);
            ApplySmapiRequirementFlags(scan.Mods);
            return SerializeElement(scan, LauncherJsonContext.Default.LauncherLibraryScanResult);
        });
    }

    public Task<JsonElement?> SetLauncherModEnabledAsync(JsonElement request)
    {
        return Task.Run(() =>
        {
            var parsed = Deserialize(request, LauncherJsonContext.Default.SetLauncherModEnabledRequest);
            var modPath = parsed.ModPath.Trim();
            if (modPath.Length == 0)
                throw new LauncherCommandException("invalid_args", "modPath is required.");

            var result = SetModEnabledAtPath(modPath, parsed.Enabled);
            return SerializeElement(result, LauncherJsonContext.Default.SetLauncherModEnabledResult);
        });
    }

    public Task<JsonElement?> LoadLauncherLibraryStateAsync()
    {
        return Task.Run(() =>
        {
            var state = LoadOrCreateLibraryState();
            return SerializeElement(state, LauncherJsonContext.Default.LauncherLibraryState);
        });
    }

    public Task<JsonElement?> SaveLauncherLibraryStateAsync(JsonElement request)
    {
        return Task.Run(() =>
        {
            var state = Deserialize(request, LauncherJsonContext.Default.LauncherLibraryState);
            SaveLibraryState(state);
            return SerializeElement(state, LauncherJsonContext.Default.LauncherLibraryState);
        });
    }

    public Task<JsonElement?> LoadLauncherLibraryCoversAsync()
    {
        return Task.Run(() =>
        {
            var covers = LoadOrCreateLibraryCovers();
            return SerializeElement(covers, LauncherJsonContext.Default.LauncherLibraryCoversState);
        });
    }

    public Task<JsonElement?> SetLauncherLibraryCoverAsync(JsonElement request)
    {
        return Task.Run(() =>
        {
            var parsed = Deserialize(request, LauncherJsonContext.Default.SetLauncherLibraryCoverRequest);
            var labelKey = parsed.LabelKey.Trim();
            if (labelKey.Length == 0)
                throw new LauncherCommandException("invalid_args", "labelKey is required.");

            var covers = LoadOrCreateLibraryCovers();
            covers.Covers.RemoveAll(cover => LauncherJsonHelper.NormalizeUniqueId(cover.LabelKey) == LauncherJsonHelper.NormalizeUniqueId(labelKey));

            var imagePath = parsed.ImagePath?.Trim();
            if (!string.IsNullOrEmpty(imagePath))
            {
                if (!File.Exists(imagePath))
                    throw new LauncherCommandException("invalid_args", $"Launcher cover image {imagePath} does not exist.");

                covers.Covers.Add(new LauncherLibraryCover { LabelKey = labelKey, ImagePath = imagePath });
            }

            SaveLibraryCovers(covers);
            return SerializeElement(covers, LauncherJsonContext.Default.LauncherLibraryCoversState);
        });
    }

    public Task<JsonElement?> LoadLauncherImageFailuresAsync()
    {
        return Task.Run(() =>
        {
            var state = LoadOrCreateImageFailures();
            return SerializeElement(state, LauncherJsonContext.Default.LauncherImageFailuresState);
        });
    }

    public Task<JsonElement?> RecordLauncherImageFailureAsync(JsonElement request)
    {
        return Task.Run(() =>
        {
            var parsed = Deserialize(request, LauncherJsonContext.Default.RecordLauncherImageFailureRequest);
            var modKey = parsed.ModKey.Trim();
            if (modKey.Length == 0)
                throw new LauncherCommandException("invalid_args", "modKey is required.");

            var state = LoadOrCreateImageFailures();
            var normalizedKey = LauncherJsonHelper.NormalizeUniqueId(modKey);
            var nextFailureCount = state.Entries
                .Where(entry => LauncherJsonHelper.NormalizeUniqueId(entry.ModKey) == normalizedKey)
                .Select(entry => entry.FailureCount)
                .FirstOrDefault();
            nextFailureCount = nextFailureCount == uint.MaxValue ? nextFailureCount : nextFailureCount + 1;
            state.Entries.RemoveAll(entry => LauncherJsonHelper.NormalizeUniqueId(entry.ModKey) == normalizedKey);

            var error = parsed.Error.Trim();
            //Nexus-mod-unavailable errors are terminal; jump straight to the block threshold.
            var skipRetry = error.StartsWith("Nexus mod unavailable:", StringComparison.Ordinal)
                || (error.StartsWith("Nexus mod ", StringComparison.Ordinal) && error.EndsWith(" is unavailable.", StringComparison.Ordinal));
            var failureCount = skipRetry ? Math.Max(nextFailureCount, ImageFailureThreshold) : nextFailureCount;

            state.Entries.Add(new LauncherImageFailureEntry
            {
                ModKey = modKey,
                FailureCount = failureCount,
                Blocked = failureCount >= ImageFailureThreshold,
                LastError = error,
                LastFailedAtMs = LauncherJsonHelper.CurrentTimestampMs(),
            });

            SaveImageFailures(state);
            return SerializeElement(state, LauncherJsonContext.Default.LauncherImageFailuresState);
        });
    }

    // --- scan ---

    internal LauncherLibraryScanResult ScanLibraryAtPath(string modsPath)
    {
        var scanRoot = DiscoverModsRoot(modsPath);
        var projectRoots = DiscoverProjectRoots(scanRoot);
        var projects = CollectScannedProjects(projectRoots);
        var graph = BuildDependencyHealthGraph(projects);

        var mods = new List<LauncherLibraryModSummary>();
        foreach (var project in projects)
            mods.Add(BuildModSummary(project, graph));

        //Byte-wise ordering, matching the Rust sort (name, then absolute path).
        mods.Sort((left, right) =>
        {
            var nameComparison = string.CompareOrdinal(left.Name, right.Name);
            return nameComparison != 0 ? nameComparison : string.CompareOrdinal(left.AbsolutePath, right.AbsolutePath);
        });

        return new LauncherLibraryScanResult
        {
            ModsPath = scanRoot,
            Mods = mods,
        };
    }

    static string DiscoverModsRoot(string path)
    {
        var modsDirectory = Path.Combine(path, ModsSubDirectory);
        return Directory.Exists(modsDirectory) ? modsDirectory : path;
    }

    internal static List<string> DiscoverProjectRoots(string scanRoot)
    {
        var discovered = new SortedSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(scanRoot))
            return discovered.ToList();

        var pending = new Stack<string>();
        pending.Push(scanRoot);
        while (pending.Count > 0)
        {
            var currentDirectory = pending.Pop();
            var manifestPath = Path.Combine(currentDirectory, ManifestFileName);
            if (File.Exists(manifestPath))
                discovered.Add(currentDirectory.Replace('\\', '/'));

            string[] entries;
            try
            {
                entries = Directory.GetDirectories(currentDirectory);
            }
            catch (Exception ex)
            {
                throw new LauncherCommandException("io", $"Failed to read launcher mods directory {currentDirectory}: {ex.Message}");
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (SkippedScanDirectories.Any(skipped => string.Equals(skipped, name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                pending.Push(entry);
            }
        }

        return discovered.ToList();
    }

    sealed record ScannedProject(string ProjectPath, JsonElement Manifest, bool Enabled);

    static List<ScannedProject> CollectScannedProjects(List<string> projectRoots)
    {
        var projects = new List<ScannedProject>();
        foreach (var projectPath in projectRoots)
        {
            var manifestPath = Path.Combine(projectPath, ManifestFileName);
            var manifest = LauncherJsonHelper.TryParseJsonFile(manifestPath);
            if (manifest is null)
            {
                //Unreadable manifests skip the mod instead of failing the scan.
                Console.WriteLine($"LibraryService: skipping mod with unreadable manifest: {manifestPath}");
                continue;
            }

            var folderName = Path.GetFileName(projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? string.Empty;
            var enabled = !folderName.StartsWith('.');
            projects.Add(new ScannedProject(projectPath, manifest.Value, enabled));
        }

        return projects;
    }

    LauncherLibraryModSummary BuildModSummary(ScannedProject project, Dictionary<string, DependencyNode> graph)
    {
        var manifest = project.Manifest;
        var folderName = Path.GetFileName(project.ProjectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? string.Empty;
        var updateKeys = LauncherJsonHelper.StringArrayField(manifest, "UpdateKeys");
        var nexusModId = ExtractNexusModId(updateKeys);
        var dependencies = ManifestDependencies(manifest);
        var requiredDependencies = dependencies.Where(dependency => dependency.Required).Select(dependency => dependency.UniqueId).ToList();
        var uniqueId = LauncherJsonHelper.StringField(manifest, "UniqueID");

        return new LauncherLibraryModSummary
        {
            Id = project.ProjectPath.Replace('\\', '/'),
            LabelKey = PreferredCoverLabelKey(nexusModId, uniqueId, folderName),
            Name = LauncherJsonHelper.StringField(manifest, "Name") ?? folderName ?? "Unnamed Mod",
            Author = LauncherJsonHelper.StringField(manifest, "Author"),
            Version = LauncherJsonHelper.StringField(manifest, "Version"),
            Description = LauncherJsonHelper.StringField(manifest, "Description"),
            UniqueId = uniqueId,
            FolderName = folderName,
            AbsolutePath = project.ProjectPath,
            Enabled = project.Enabled,
            HasConfig = HasLauncherModConfig(project.ProjectPath, manifest),
            NexusModId = nexusModId,
            UpdateKeys = updateKeys,
            ModUrl = nexusModId is > 0 ? $"https://www.nexusmods.com/stardewvalley/mods/{nexusModId}" : null,
            ImageUrl = null,
            Dependencies = dependencies,
            RequiredDependencies = requiredDependencies,
            MissingRequiredDependencies = MissingRequiredDependenciesForProject(manifest, graph),
            MinimumApiVersion = LauncherJsonHelper.StringField(manifest, "MinimumApiVersion"),
            RequiresNewerSmapi = false,
        };
    }

    static long? ExtractNexusModId(List<string> updateKeys)
    {
        foreach (var updateKey in updateKeys)
        {
            var separator = updateKey.IndexOf(':');
            if (separator <= 0)
                continue;

            var provider = updateKey[..separator];
            if (!provider.Equals("Nexus", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = updateKey[(separator + 1)..].Trim();
            if (long.TryParse(value, out var modId) && modId > 0)
                return modId;
        }

        return null;
    }

    static string PreferredCoverLabelKey(long? nexusModId, string? uniqueId, string folderName)
    {
        if (nexusModId is > 0)
            return nexusModId.Value.ToString();

        if (!string.IsNullOrEmpty(uniqueId))
            return uniqueId.Trim();

        return folderName.TrimStart('.');
    }

    static bool HasLauncherModConfig(string root, JsonElement manifest)
    {
        if (File.Exists(Path.Combine(root, "config.json")))
            return true;
        if (File.Exists(Path.Combine(root, "assets", "options.json")))
            return true;
        if (manifest.ValueKind == JsonValueKind.Object
            && manifest.TryGetProperty("ConfigSchema", out var schema)
            && schema.ValueKind == JsonValueKind.Object
            && schema.EnumerateObject().Any())
        {
            return true;
        }

        var contentJson = LauncherJsonHelper.TryParseJsonFile(Path.Combine(root, "content.json"));
        return contentJson is not null
            && contentJson.Value.ValueKind == JsonValueKind.Object
            && contentJson.Value.TryGetProperty("ConfigSchema", out var contentSchema)
            && contentSchema.ValueKind == JsonValueKind.Object
            && contentSchema.EnumerateObject().Any();
    }

    static List<LauncherLibraryDependency> ManifestDependencies(JsonElement manifest)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dependencies = new List<LauncherLibraryDependency>();
        if (manifest.ValueKind != JsonValueKind.Object || !manifest.TryGetProperty("Dependencies", out var value) || value.ValueKind != JsonValueKind.Array)
            return dependencies;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var uniqueId = LauncherJsonHelper.StringField(item, "UniqueID");
            if (uniqueId is null)
                continue;

            if (!seen.Add(LauncherJsonHelper.NormalizeUniqueId(uniqueId)))
                continue;

            dependencies.Add(new LauncherLibraryDependency
            {
                UniqueId = uniqueId,
                Required = LauncherJsonHelper.BoolField(item, "IsRequired", true),
            });
        }

        return dependencies;
    }

    sealed record DependencyNode(bool Enabled, List<string> RequiredDependencies);

    static Dictionary<string, DependencyNode> BuildDependencyHealthGraph(List<ScannedProject> projects)
    {
        var graph = new Dictionary<string, DependencyNode>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var uniqueId = LauncherJsonHelper.StringField(project.Manifest, "UniqueID");
            if (uniqueId is null)
                continue;

            var key = LauncherJsonHelper.NormalizeUniqueId(uniqueId);
            graph[key] = new DependencyNode(
                project.Enabled,
                ManifestDependencies(project.Manifest).Where(dependency => dependency.Required).Select(dependency => dependency.UniqueId).ToList());
        }

        return graph;
    }

    static bool DependencyHasIssue(string dependencyId, Dictionary<string, DependencyNode> graph, Dictionary<string, bool> memo, HashSet<string> visiting)
    {
        var key = LauncherJsonHelper.NormalizeUniqueId(dependencyId);
        if (memo.TryGetValue(key, out var memoized))
            return memoized;

        if (visiting.Contains(key))
            return false; //cycle back-edge counts as no issue

        visiting.Add(key);
        bool hasIssue;
        if (!graph.TryGetValue(key, out var node))
        {
            hasIssue = true; //dependency is not installed
        }
        else if (!node.Enabled)
        {
            hasIssue = true; //installed but disabled
        }
        else
        {
            hasIssue = node.RequiredDependencies.Any(child =>
            {
                var childKey = LauncherJsonHelper.NormalizeUniqueId(child);
                return childKey != key && DependencyHasIssue(child, graph, memo, visiting);
            });
        }

        visiting.Remove(key);
        memo[key] = hasIssue;
        return hasIssue;
    }

    static List<string> MissingRequiredDependenciesForProject(JsonElement manifest, Dictionary<string, DependencyNode> graph)
    {
        var memo = new Dictionary<string, bool>(StringComparer.Ordinal);
        var uniqueId = LauncherJsonHelper.StringField(manifest, "UniqueID");
        var projectKey = uniqueId is null ? string.Empty : LauncherJsonHelper.NormalizeUniqueId(uniqueId);
        var missing = new List<string>();
        foreach (var dependency in ManifestDependencies(manifest).Where(item => item.Required))
        {
            if (LauncherJsonHelper.NormalizeUniqueId(dependency.UniqueId) == projectKey)
                continue;

            if (DependencyHasIssue(dependency.UniqueId, graph, memo, new HashSet<string>(StringComparer.Ordinal)))
                missing.Add(dependency.UniqueId);
        }

        return missing;
    }

    // --- SMAPI requirement flags (Android adaptation: read the installed SMAPI assembly) ---

    internal static string? ResolveInstalledSmapiVersion()
    {
        try
        {
            return SMAPIInstaller.GetCurrentVersion()?.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine("LibraryService: SMAPI version detection failed: " + ex.Message);
            return null;
        }
    }

    internal void ApplySmapiRequirementFlags(List<LauncherLibraryModSummary> mods)
    {
        var installedVersion = ResolveInstalledSmapiVersion();
        if (installedVersion is null)
            return;

        foreach (var mod in mods)
        {
            if (mod.MinimumApiVersion is null)
                continue;

            mod.RequiresNewerSmapi = LauncherJsonHelper.VersionIsNewer(installedVersion, mod.MinimumApiVersion);
        }
    }

    // --- covers enrichment ---

    void ApplyCoverEnrichment(LauncherLibraryScanResult scan)
    {
        var covers = LoadOrCreateLibraryCovers();
        var coverMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cover in covers.Covers)
        {
            if (File.Exists(cover.ImagePath))
                coverMap[LauncherJsonHelper.NormalizeUniqueId(cover.LabelKey)] = cover.ImagePath;
        }

        foreach (var mod in scan.Mods)
        {
            mod.ImageUrl = CoverLookupKeys(mod)
                .Select(key => coverMap.TryGetValue(LauncherJsonHelper.NormalizeUniqueId(key), out var imagePath) ? imagePath : null)
                .FirstOrDefault(imagePath => imagePath is not null);
        }
    }

    static IEnumerable<string> CoverLookupKeys(LauncherLibraryModSummary mod)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<string>
        {
            PreferredCoverLabelKey(mod.NexusModId, mod.UniqueId, mod.FolderName),
            mod.LabelKey,
            mod.UniqueId ?? string.Empty,
        };

        foreach (var candidate in candidates)
        {
            var trimmed = candidate.Trim();
            if (trimmed.Length == 0)
                continue;

            if (seen.Add(LauncherJsonHelper.NormalizeUniqueId(trimmed)))
                yield return trimmed;
        }
    }

    // --- enable/disable ---

    SetLauncherModEnabledResult SetModEnabledAtPath(string modPath, bool enabled)
    {
        if (!Directory.Exists(modPath))
            throw new LauncherCommandException("not_found", $"Launcher mod path {modPath} does not exist.");

        var currentName = Path.GetFileName(modPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(currentName))
            throw new LauncherCommandException("io", "Unable to resolve launcher mod folder name.");

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(modPath));
        if (string.IsNullOrEmpty(parent))
            throw new LauncherCommandException("io", "Unable to resolve launcher mod parent folder.");

        var isEnabled = !currentName.StartsWith('.');
        if (isEnabled == enabled)
            return new SetLauncherModEnabledResult { AbsolutePath = modPath, Enabled = enabled };

        var nextName = enabled ? currentName.TrimStart('.') : "." + currentName;
        var nextPath = Path.Combine(parent, nextName);
        if (Directory.Exists(nextPath) || File.Exists(nextPath))
            throw new LauncherCommandException("conflict", $"Cannot rename launcher mod to {nextPath} because that path already exists.");

        try
        {
            Directory.Move(modPath, nextPath);
        }
        catch (Exception ex)
        {
            throw new LauncherCommandException("io", $"Failed to toggle launcher mod {modPath}: {ex.Message}");
        }

        return new SetLauncherModEnabledResult { AbsolutePath = nextPath, Enabled = enabled };
    }

    // --- persistence: library state ---

    LauncherLibraryState LoadOrCreateLibraryState()
    {
        if (!File.Exists(LibraryStateFilePath))
        {
            var defaults = CreateDefaultLibraryState();
            SaveLibraryState(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(LibraryStateFilePath);
            var state = JsonSerializer.Deserialize(json, LauncherJsonContext.Default.LauncherLibraryState);
            if (state is null)
                throw new LauncherCommandException("invalid_json", $"Launcher library state {LibraryStateFilePath} is invalid JSON.");

            EnsureUnsortedStorageFolder(state);
            return state;
        }
        catch (LauncherCommandException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LauncherCommandException("invalid_json", $"Launcher library state {LibraryStateFilePath} is invalid JSON: {ex.Message}");
        }
    }

    static LauncherLibraryState CreateDefaultLibraryState()
    {
        return new LauncherLibraryState
        {
            StorageFolders = new List<LauncherLibraryStorageFolder>
            {
                new() { Id = UnsortedStorageFolderId, Name = UnsortedStorageFolderName },
            },
        };
    }

    static void EnsureUnsortedStorageFolder(LauncherLibraryState state)
    {
        if (state.StorageFolders.Any(folder => LauncherJsonHelper.NormalizeUniqueId(folder.Id) == UnsortedStorageFolderId))
            return;

        state.StorageFolders.Add(new LauncherLibraryStorageFolder { Id = UnsortedStorageFolderId, Name = UnsortedStorageFolderName });
    }

    void SaveLibraryState(LauncherLibraryState state)
    {
        EnsureUnsortedStorageFolder(state);
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(LibraryStateFilePath, JsonSerializer.Serialize(state, LauncherJsonContext.Default.LauncherLibraryState) + "\n");
        }
        catch (Exception ex)
        {
            throw new LauncherCommandException("io", $"Failed to write launcher library state {LibraryStateFilePath}: {ex.Message}");
        }
    }

    // --- persistence: covers ---

    LauncherLibraryCoversState LoadOrCreateLibraryCovers()
    {
        if (!File.Exists(CoversFilePath))
        {
            var empty = new LauncherLibraryCoversState();
            SaveLibraryCovers(empty);
            return empty;
        }

        try
        {
            var json = File.ReadAllText(CoversFilePath);
            var covers = JsonSerializer.Deserialize(json, LauncherJsonContext.Default.LauncherLibraryCoversState) ?? new LauncherLibraryCoversState();

            //prune covers whose image file no longer exists
            var before = covers.Covers.Count;
            covers.Covers.RemoveAll(cover => string.IsNullOrWhiteSpace(cover.LabelKey) || string.IsNullOrWhiteSpace(cover.ImagePath) || !File.Exists(cover.ImagePath));
            if (covers.Covers.Count != before)
                SaveLibraryCovers(covers);

            return covers;
        }
        catch (Exception ex)
        {
            throw new LauncherCommandException("invalid_json", $"Launcher library covers {CoversFilePath} is invalid JSON: {ex.Message}");
        }
    }

    void SaveLibraryCovers(LauncherLibraryCoversState covers)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(CoversFilePath, JsonSerializer.Serialize(covers, LauncherJsonContext.Default.LauncherLibraryCoversState) + "\n");
        }
        catch (Exception ex)
        {
            throw new LauncherCommandException("io", $"Failed to write launcher library covers {CoversFilePath}: {ex.Message}");
        }
    }

    // --- persistence: image failures ---

    LauncherImageFailuresState LoadOrCreateImageFailures()
    {
        if (!File.Exists(ImageFailuresFilePath))
        {
            var empty = new LauncherImageFailuresState();
            SaveImageFailures(empty);
            return empty;
        }

        try
        {
            var json = File.ReadAllText(ImageFailuresFilePath);
            var state = JsonSerializer.Deserialize(json, LauncherJsonContext.Default.LauncherImageFailuresState) ?? new LauncherImageFailuresState();
            return state;
        }
        catch (Exception ex)
        {
            throw new LauncherCommandException("invalid_json", $"Launcher image failures {ImageFailuresFilePath} is invalid JSON: {ex.Message}");
        }
    }

    void SaveImageFailures(LauncherImageFailuresState state)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(ImageFailuresFilePath, JsonSerializer.Serialize(state, LauncherJsonContext.Default.LauncherImageFailuresState) + "\n");
        }
        catch (Exception ex)
        {
            throw new LauncherCommandException("io", $"Failed to write launcher image failures {ImageFailuresFilePath}: {ex.Message}");
        }
    }

    internal static T Deserialize<T>(JsonElement payload, JsonTypeInfo<T> typeInfo)
    {
        return JsonSerializer.Deserialize(payload.Clone(), typeInfo)
            ?? throw new LauncherCommandException("invalid_args", "Launcher command payload was null.");
    }

    static JsonElement SerializeElement<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        return JsonSerializer.SerializeToElement(value, typeInfo);
    }
}
