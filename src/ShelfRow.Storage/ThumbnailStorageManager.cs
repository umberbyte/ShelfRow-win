using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ShelfRow.Core.Models;

namespace ShelfRow.Storage;

/// <summary>
/// Written when a folder is made a distribution root, matching macOS ThumbnailDistribution.Marker.
/// </summary>
public class ThumbnailDistributionMarker
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("libraryID")]
    public Guid LibraryId { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = "ShelfRow for Windows";
}

/// <summary>
/// Manifest representing the catalog of thumbnails stored in the distribution root.
/// Matches macOS ThumbnailDistribution.Manifest.
/// </summary>
public class ThumbnailDistributionManifest
{
    public class Entry
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("bytes")]
        public long Bytes { get; set; }
    }

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedBy")]
    public string UpdatedBy { get; set; } = "ShelfRow for Windows";

    /// <summary>
    /// Keyed by uppercase UUID string matching macOS itemID.uuidString.
    /// </summary>
    [JsonPropertyName("entries")]
    public Dictionary<string, Entry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Entry? GetEntry(Guid itemId)
    {
        string key = itemId.ToString("D").ToUpperInvariant();
        if (Entries.TryGetValue(key, out var entry))
            return entry;

        // Fallback for case variations
        foreach (var kv in Entries)
        {
            if (Guid.TryParse(kv.Key, out var g) && g == itemId)
                return kv.Value;
        }

        return null;
    }

    public void SetEntry(Guid itemId, int version, long bytes)
    {
        string key = itemId.ToString("D").ToUpperInvariant();
        Entries[key] = new Entry { Version = version, Bytes = bytes };
    }
}

/// <summary>
/// Manages thumbnail cache locally and shared over NAS distribution root.
/// Fully compatible with ShelfRow for macOS (ThumbnailDistribution.swift).
/// </summary>
public class ThumbnailStorageManager
{
    public const string DistributionFolderName = "ShelfRowThumbnails";
    public const string MarkerFileName = ".shelfrow-thumbnails.json";
    public const string ManifestFileName = ".shelfrow-thumbnails-index.json";
    public const int SupportedFormatVersion = 1;

    private readonly string _localCacheDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ThumbnailStorageManager(string? localCacheDirectory = null)
    {
        _localCacheDirectory = localCacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShelfRow",
            "Thumbnails"
        );
        Directory.CreateDirectory(_localCacheDirectory);
    }

    public string LocalCacheDirectory => _localCacheDirectory;

    public string GetLocalThumbnailPath(Guid itemId)
    {
        return Path.Combine(_localCacheDirectory, $"{itemId:D}.jpg");
    }

    public bool HasLocalThumbnail(Guid itemId)
    {
        return File.Exists(GetLocalThumbnailPath(itemId));
    }

    /// <summary>
    /// Resolves the NAS distribution thumbnail path using the macOS 256-shard convention:
    /// {nasDistributionRoot}/{prefix2_lower}/{UUID_UPPER}.jpg
    /// </summary>
    public string GetNasThumbnailPath(string nasDistributionRoot, Guid itemId)
    {
        string uuidUpper = itemId.ToString("D").ToUpperInvariant();
        string shard = uuidUpper[..2].ToLowerInvariant();
        return Path.Combine(nasDistributionRoot, shard, $"{uuidUpper}.jpg");
    }

    /// <summary>
    /// Finds the NAS thumbnail file, checking both uppercase and lowercase variations.
    /// </summary>
    public string? FindNasThumbnailFile(string nasDistributionRoot, Guid itemId)
    {
        string expectedPath = GetNasThumbnailPath(nasDistributionRoot, itemId);
        if (File.Exists(expectedPath))
            return expectedPath;

        // Check fallback with lowercase file name
        string uuidLower = itemId.ToString("D").ToLowerInvariant();
        string shard = uuidLower[..2];
        string altPath = Path.Combine(nasDistributionRoot, shard, $"{uuidLower}.jpg");
        if (File.Exists(altPath))
            return altPath;

        return null;
    }

    /// <summary>
    /// Copies a thumbnail from NAS distribution folder to local cache.
    /// Uses atomic write (temporary file and move) to avoid reading partially written files.
    /// </summary>
    public async Task<bool> CopyFromNasDistributionAsync(string nasDistributionRoot, Guid itemId, CancellationToken cancellationToken = default)
    {
        string? nasPath = FindNasThumbnailFile(nasDistributionRoot, itemId);
        if (nasPath == null)
            return false;

        string localPath = GetLocalThumbnailPath(itemId);
        string localDir = Path.GetDirectoryName(localPath)!;
        Directory.CreateDirectory(localDir);

        string tempPath = Path.Combine(localDir, $".{itemId:D}.{Process.GetCurrentProcess().Id}.tmp");
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(nasPath, cancellationToken);
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);

            File.Move(tempPath, localPath, overwrite: true);
            return true;
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
            throw;
        }
    }

    /// <summary>
    /// Uploads a local thumbnail to the NAS distribution folder using the 256-shard convention
    /// and atomic rename within the destination shard.
    /// </summary>
    public async Task CopyToNasDistributionAsync(string nasDistributionRoot, Guid itemId, CancellationToken cancellationToken = default)
    {
        string localPath = GetLocalThumbnailPath(itemId);
        if (!File.Exists(localPath))
            return;

        string nasDestination = GetNasThumbnailPath(nasDistributionRoot, itemId);
        string shardDir = Path.GetDirectoryName(nasDestination)!;
        Directory.CreateDirectory(shardDir);

        string tempNasPath = Path.Combine(shardDir, $".{itemId:D}.{Process.GetCurrentProcess().Id}.tmp");
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(localPath, cancellationToken);
            await File.WriteAllBytesAsync(tempNasPath, bytes, cancellationToken);

            File.Move(tempNasPath, nasDestination, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempNasPath))
            {
                try { File.Delete(tempNasPath); } catch { }
            }
            throw;
        }
    }

    /// <summary>
    /// Reads the distribution marker (.shelfrow-thumbnails.json).
    /// </summary>
    public async Task<ThumbnailDistributionMarker?> ReadMarkerAsync(string nasDistributionRoot, CancellationToken cancellationToken = default)
    {
        string markerPath = Path.Combine(nasDistributionRoot, MarkerFileName);
        if (!File.Exists(markerPath)) return null;

        string json = await File.ReadAllTextAsync(markerPath, cancellationToken);
        return JsonSerializer.Deserialize<ThumbnailDistributionMarker>(json, JsonOptions);
    }

    /// <summary>
    /// Writes the distribution marker (.shelfrow-thumbnails.json) atomically.
    /// </summary>
    public async Task WriteMarkerAsync(string nasDistributionRoot, Guid libraryId, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(nasDistributionRoot);
        string markerPath = Path.Combine(nasDistributionRoot, MarkerFileName);
        string tempPath = Path.Combine(nasDistributionRoot, $"{MarkerFileName}.{Process.GetCurrentProcess().Id}.tmp");

        var marker = new ThumbnailDistributionMarker
        {
            FormatVersion = SupportedFormatVersion,
            LibraryId = libraryId,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "ShelfRow for Windows"
        };
        string json = JsonSerializer.Serialize(marker, JsonOptions);

        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, markerPath, overwrite: true);
    }

    /// <summary>
    /// Reads the distribution manifest (.shelfrow-thumbnails-index.json).
    /// </summary>
    public async Task<ThumbnailDistributionManifest?> ReadManifestAsync(string nasDistributionRoot, CancellationToken cancellationToken = default)
    {
        string manifestPath = Path.Combine(nasDistributionRoot, ManifestFileName);
        if (!File.Exists(manifestPath)) return null;

        string json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        return JsonSerializer.Deserialize<ThumbnailDistributionManifest>(json, JsonOptions);
    }

    /// <summary>
    /// Writes or updates the distribution manifest (.shelfrow-thumbnails-index.json) atomically.
    /// </summary>
    public async Task WriteManifestAsync(string nasDistributionRoot, ThumbnailDistributionManifest manifest, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(nasDistributionRoot);
        string manifestPath = Path.Combine(nasDistributionRoot, ManifestFileName);
        string tempPath = Path.Combine(nasDistributionRoot, $"{ManifestFileName}.{Process.GetCurrentProcess().Id}.tmp");

        manifest.UpdatedAt = DateTime.UtcNow;
        manifest.UpdatedBy = "ShelfRow for Windows";

        string json = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, manifestPath, overwrite: true);
    }

    /// <summary>
    /// Attempts to locate the ShelfRowThumbnails distribution root within or adjacent to a volume mount path.
    /// </summary>
    public static string? FindDistributionRoot(string? volumeWindowsPath)
    {
        if (string.IsNullOrWhiteSpace(volumeWindowsPath) || !Directory.Exists(volumeWindowsPath))
            return null;

        // 1. Direct subfolder: {Volume}\ShelfRowThumbnails
        string direct = Path.Combine(volumeWindowsPath, DistributionFolderName);
        if (Directory.Exists(direct))
            return direct;

        // 2. Sibling folder: e.g. \\NAS\ShelfRowThumbnails if volume is \\NAS\Books
        try
        {
            var parent = Directory.GetParent(volumeWindowsPath);
            if (parent != null && parent.Exists)
            {
                string sibling = Path.Combine(parent.FullName, DistributionFolderName);
                if (Directory.Exists(sibling))
                    return sibling;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Synchronizes missing thumbnails from NAS distribution root to local cache in parallel.
    /// Uses manifest index if available, with sharded directory scan fallback.
    /// </summary>
    public async Task<ThumbnailSyncResult> SyncAllThumbnailsFromNasAsync(
        string nasDistributionRoot,
        IProgress<ThumbnailSyncProgress>? progress = null,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default)
    {
        return await SyncAllThumbnailsFromNasAsync(
            nasDistributionRoot,
            libraryItemIds: null,
            localStates: null,
            progress,
            maxConcurrency,
            cancellationToken);
    }

    /// <summary>
    /// Synchronizes covers by comparing the NAS manifest with this device's local
    /// bookkeeping. No shard enumeration is used: a missing manifest is not a
    /// reason to issue tens of thousands of network file-system calls.
    /// </summary>
    public async Task<ThumbnailSyncResult> SyncAllThumbnailsFromNasAsync(
        string nasDistributionRoot,
        IReadOnlyCollection<Guid>? libraryItemIds,
        IReadOnlyDictionary<Guid, LocalCoverState>? localStates,
        IProgress<ThumbnailSyncProgress>? progress = null,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default)
    {
        var result = new ThumbnailSyncResult();
        if (!Directory.Exists(nasDistributionRoot))
            return result;

        var manifest = await ReadManifestAsync(nasDistributionRoot, cancellationToken);
        if (manifest is null)
        {
            result.ManifestMissing = true;
            return result;
        }

        HashSet<Guid>? libraryIds = libraryItemIds is null ? null : new HashSet<Guid>(libraryItemIds);
        var candidates = new List<(Guid ItemId, int Version, long ExpectedBytes)>();
        foreach (var (key, entry) in manifest.Entries)
        {
            if (Guid.TryParse(key, out var itemId) && (libraryIds is null || libraryIds.Contains(itemId)))
            {
                candidates.Add((itemId, entry.Version, entry.Bytes));
            }
        }

        result.TotalFoundInNas = candidates.Count;
        if (candidates.Count == 0)
            return result;

        // 2. Compare the manifest version with device-local state. A same-sized
        // file already in cache can be adopted without another network copy.
        var missing = new List<(Guid ItemId, int Version, long ExpectedBytes)>();
        foreach (var item in candidates)
        {
            LocalCoverState? state = null;
            localStates?.TryGetValue(item.ItemId, out state);
            string localPath = GetLocalThumbnailPath(item.ItemId);
            long localBytes = File.Exists(localPath) ? new FileInfo(localPath).Length : 0;
            bool sizeMatches = localBytes > 0 && (item.ExpectedBytes <= 0 || localBytes == item.ExpectedBytes);

            if (state?.Version == item.Version && sizeMatches)
            {
                result.AlreadyCached++;
            }
            else if (state is null && sizeMatches)
            {
                result.AlreadyCached++;
                result.StateUpdates.Add(CreateSuccessfulState(item.ItemId, item.Version, localBytes));
            }
            else if (state is not null
                     && state.AttemptedVersion == item.Version
                     && state.Attempts >= 3
                     && state.LastErrorCode != 0)
            {
                result.SuppressedAfterFailures++;
            }
            else
            {
                missing.Add(item);
            }
        }

        if (missing.Count == 0)
            return result;

        // 3. Parallel download using SemaphoreSlim
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        int processedCount = 0;
        int fetchedCount = 0;
        int failedCount = 0;
        var stateUpdates = new System.Collections.Concurrent.ConcurrentBag<LocalCoverState>();

        var tasks = missing.Select(async target =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool copied = await CopyFromNasDistributionAsync(nasDistributionRoot, target.ItemId, cancellationToken);
                string localPath = GetLocalThumbnailPath(target.ItemId);
                long bytes = copied && File.Exists(localPath) ? new FileInfo(localPath).Length : 0;
                bool valid = copied && bytes > 0 && (target.ExpectedBytes <= 0 || bytes == target.ExpectedBytes);
                if (valid)
                {
                    Interlocked.Increment(ref fetchedCount);
                    stateUpdates.Add(CreateSuccessfulState(target.ItemId, target.Version, bytes));
                }
                else
                {
                    Interlocked.Increment(ref failedCount);
                    LocalCoverState? previous = null;
                    localStates?.TryGetValue(target.ItemId, out previous);
                    stateUpdates.Add(CreateFailedState(previous, target.ItemId, target.Version, copied ? 13 : 2));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failedCount);
                LocalCoverState? previous = null;
                localStates?.TryGetValue(target.ItemId, out previous);
                stateUpdates.Add(CreateFailedState(previous, target.ItemId, target.Version, ex.HResult));
            }
            finally
            {
                int current = Interlocked.Increment(ref processedCount);
                progress?.Report(new ThumbnailSyncProgress
                {
                    Processed = current,
                    Total = missing.Count,
                    CurrentItemId = target.ItemId.ToString("D")
                });
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        result.Fetched = fetchedCount;
        result.Failed = failedCount;
        result.StateUpdates.AddRange(stateUpdates);
        return result;
    }

    private static LocalCoverState CreateSuccessfulState(Guid itemId, int version, long bytes) => new()
    {
        ItemId = itemId,
        Version = version,
        Bytes = bytes,
        UpdatedAt = DateTime.UtcNow,
        Attempts = 0,
        AttemptedVersion = version,
        LastErrorCode = 0
    };

    private static LocalCoverState CreateFailedState(LocalCoverState? previous, Guid itemId, int attemptedVersion, int errorCode) => new()
    {
        ItemId = itemId,
        Version = previous?.Version ?? 0,
        Bytes = previous?.Bytes ?? 0,
        UpdatedAt = DateTime.UtcNow,
        PendingUpload = previous?.PendingUpload ?? false,
        Attempts = previous?.AttemptedVersion == attemptedVersion ? previous.Attempts + 1 : 1,
        AttemptedVersion = attemptedVersion,
        LastErrorCode = errorCode == 0 ? -1 : errorCode
    };
}

public class ThumbnailSyncProgress
{
    public int Processed { get; set; }
    public int Total { get; set; }
    public string CurrentItemId { get; set; } = string.Empty;
}

public class ThumbnailSyncResult
{
    public int TotalFoundInNas { get; set; }
    public int Fetched { get; set; }
    public int AlreadyCached { get; set; }
    public int Failed { get; set; }
    public int SuppressedAfterFailures { get; set; }
    public bool ManifestMissing { get; set; }
    public List<LocalCoverState> StateUpdates { get; } = new();
}

