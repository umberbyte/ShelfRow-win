using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

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
}

