using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ShelfRow.Core.Models;

namespace ShelfRow.Storage;

public class ThumbnailDistributionMarker
{
    public int FormatVersion { get; set; } = 1;
    public Guid LibraryId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "ShelfRow for Windows";
}

public class ThumbnailStorageManager
{
    public const string DistributionFolderName = "ShelfRowThumbnails";
    public const string MarkerFileName = ".shelfrow-thumbnails.json";
    public const int SupportedFormatVersion = 1;

    private readonly string _localCacheDirectory;

    public ThumbnailStorageManager(string? localCacheDirectory = null)
    {
        _localCacheDirectory = localCacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShelfRow",
            "Thumbnails"
        );
        Directory.CreateDirectory(_localCacheDirectory);
    }

    public string GetLocalThumbnailPath(Guid itemId)
    {
        return Path.Combine(_localCacheDirectory, $"{itemId:D}.jpg");
    }

    public bool HasLocalThumbnail(Guid itemId)
    {
        return File.Exists(GetLocalThumbnailPath(itemId));
    }

    public string GetNasThumbnailPath(string nasDistributionRoot, Guid itemId)
    {
        return Path.Combine(nasDistributionRoot, $"{itemId:D}.jpg");
    }

    public async Task<bool> CopyFromNasDistributionAsync(string nasDistributionRoot, Guid itemId, CancellationToken cancellationToken = default)
    {
        string nasPath = GetNasThumbnailPath(nasDistributionRoot, itemId);
        string localPath = GetLocalThumbnailPath(itemId);

        if (!File.Exists(nasPath))
            return false;

        byte[] bytes = await File.ReadAllBytesAsync(nasPath, cancellationToken);
        await File.WriteAllBytesAsync(localPath, bytes, cancellationToken);
        return true;
    }

    public async Task CopyToNasDistributionAsync(string nasDistributionRoot, Guid itemId, CancellationToken cancellationToken = default)
    {
        string localPath = GetLocalThumbnailPath(itemId);
        if (!File.Exists(localPath))
            return;

        Directory.CreateDirectory(nasDistributionRoot);
        string nasPath = GetNasThumbnailPath(nasDistributionRoot, itemId);

        byte[] bytes = await File.ReadAllBytesAsync(localPath, cancellationToken);
        await File.WriteAllBytesAsync(nasPath, bytes, cancellationToken);
    }

    public async Task<ThumbnailDistributionMarker?> ReadMarkerAsync(string nasDistributionRoot, CancellationToken cancellationToken = default)
    {
        string markerPath = Path.Combine(nasDistributionRoot, MarkerFileName);
        if (!File.Exists(markerPath)) return null;

        string json = await File.ReadAllTextAsync(markerPath, cancellationToken);
        return JsonSerializer.Deserialize<ThumbnailDistributionMarker>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    public async Task WriteMarkerAsync(string nasDistributionRoot, Guid libraryId, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(nasDistributionRoot);
        string markerPath = Path.Combine(nasDistributionRoot, MarkerFileName);
        var marker = new ThumbnailDistributionMarker
        {
            FormatVersion = SupportedFormatVersion,
            LibraryId = libraryId,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "ShelfRow for Windows"
        };
        string json = JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(markerPath, json, cancellationToken);
    }
}
