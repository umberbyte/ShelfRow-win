using System;
using System.IO;
using System.Threading.Tasks;
using ShelfRow.Core.Models;
using ShelfRow.Storage;
using Xunit;

namespace ShelfRow.Storage.Tests;

public class ThumbnailStorageManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ThumbnailStorageManager _manager;

    public ThumbnailStorageManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ShelfRow_Test_" + Guid.NewGuid().ToString("N"));
        _manager = new ThumbnailStorageManager(_tempDir);
    }

    [Fact]
    public void GetLocalThumbnailPath_ReturnsJpgNamedByGuid()
    {
        var id = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
        string path = _manager.GetLocalThumbnailPath(id);

        Assert.EndsWith("550e8400-e29b-41d4-a716-446655440000.jpg", path);
    }

    [Fact]
    public void GetNasThumbnailPath_Returns256ShardLowerPrefixAndUpperGuid()
    {
        var id = Guid.Parse("c4a52301-1122-3344-5566-778899aabbcc");
        string nasRoot = @"\\NAS\Books\ShelfRowThumbnails";
        string path = _manager.GetNasThumbnailPath(nasRoot, id);

        // Shard folder should be lowercase 2 letters: "c4"
        // File name should be uppercase: "C4A52301-1122-3344-5566-778899AABBCC.jpg"
        string expected = Path.Combine(nasRoot, "c4", "C4A52301-1122-3344-5566-778899AABBCC.jpg");
        Assert.Equal(expected, path);
    }

    [Fact]
    public async Task MarkerReadAndWrite_PreservesMetadata()
    {
        string nasRoot = Path.Combine(_tempDir, ThumbnailStorageManager.DistributionFolderName);
        var libId = Guid.NewGuid();

        await _manager.WriteMarkerAsync(nasRoot, libId);
        var marker = await _manager.ReadMarkerAsync(nasRoot);

        Assert.NotNull(marker);
        Assert.Equal(ThumbnailStorageManager.SupportedFormatVersion, marker.FormatVersion);
        Assert.Equal(libId, marker.LibraryId);
        Assert.Equal("ShelfRow for Windows", marker.CreatedBy);
    }

    [Fact]
    public async Task ManifestReadAndWrite_PreservesEntries()
    {
        string nasRoot = Path.Combine(_tempDir, ThumbnailStorageManager.DistributionFolderName);
        var itemId1 = Guid.NewGuid();
        var itemId2 = Guid.NewGuid();

        var manifest = new ThumbnailDistributionManifest();
        manifest.SetEntry(itemId1, version: 1, bytes: 45678);
        manifest.SetEntry(itemId2, version: 2, bytes: 123456);

        await _manager.WriteManifestAsync(nasRoot, manifest);
        var loaded = await _manager.ReadManifestAsync(nasRoot);

        Assert.NotNull(loaded);
        Assert.Equal(ThumbnailStorageManager.SupportedFormatVersion, loaded.FormatVersion);
        Assert.Equal("ShelfRow for Windows", loaded.UpdatedBy);

        var entry1 = loaded.GetEntry(itemId1);
        Assert.NotNull(entry1);
        Assert.Equal(1, entry1.Version);
        Assert.Equal(45678, entry1.Bytes);

        var entry2 = loaded.GetEntry(itemId2);
        Assert.NotNull(entry2);
        Assert.Equal(2, entry2.Version);
        Assert.Equal(123456, entry2.Bytes);
    }

    [Fact]
    public async Task CopyToAndFromNasDistribution_TransfersThumbnailCorrectly()
    {
        string nasRoot = Path.Combine(_tempDir, "NasShare", ThumbnailStorageManager.DistributionFolderName);
        var itemId = Guid.NewGuid();

        // 1. Create a mock local thumbnail file
        string localPath = _manager.GetLocalThumbnailPath(itemId);
        byte[] dummyData = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0x03 };
        await File.WriteAllBytesAsync(localPath, dummyData);

        // 2. Upload to NAS
        await _manager.CopyToNasDistributionAsync(nasRoot, itemId);

        string expectedNasPath = _manager.GetNasThumbnailPath(nasRoot, itemId);
        Assert.True(File.Exists(expectedNasPath), "NAS thumbnail file should exist in the 256-shard directory");
        byte[] nasBytes = await File.ReadAllBytesAsync(expectedNasPath);
        Assert.Equal(dummyData, nasBytes);

        // 3. Delete local and download back
        File.Delete(localPath);
        Assert.False(_manager.HasLocalThumbnail(itemId));

        bool copied = await _manager.CopyFromNasDistributionAsync(nasRoot, itemId);
        Assert.True(copied);
        Assert.True(_manager.HasLocalThumbnail(itemId));
        byte[] downloadedBytes = await File.ReadAllBytesAsync(localPath);
        Assert.Equal(dummyData, downloadedBytes);
    }

    [Fact]
    public async Task SyncAllThumbnailsFromNasAsync_DownloadsMissingThumbnailsInParallel()
    {
        string nasRoot = Path.Combine(_tempDir, "NAS", "ShelfRowThumbnails");
        var item1 = Guid.NewGuid();
        var item2 = Guid.NewGuid();
        var item3 = Guid.NewGuid();

        // 1. Setup local files and upload to NAS
        byte[] dummy = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x10, 0x20 };
        await File.WriteAllBytesAsync(_manager.GetLocalThumbnailPath(item1), dummy);
        await File.WriteAllBytesAsync(_manager.GetLocalThumbnailPath(item2), dummy);
        await File.WriteAllBytesAsync(_manager.GetLocalThumbnailPath(item3), dummy);

        await _manager.CopyToNasDistributionAsync(nasRoot, item1);
        await _manager.CopyToNasDistributionAsync(nasRoot, item2);
        await _manager.CopyToNasDistributionAsync(nasRoot, item3);

        // 2. Write manifest
        var manifest = new ThumbnailDistributionManifest();
        manifest.SetEntry(item1, 1, dummy.Length);
        manifest.SetEntry(item2, 1, dummy.Length);
        manifest.SetEntry(item3, 1, dummy.Length);
        await _manager.WriteManifestAsync(nasRoot, manifest);

        // 3. Delete item1 and item2 from local, keep item3
        File.Delete(_manager.GetLocalThumbnailPath(item1));
        File.Delete(_manager.GetLocalThumbnailPath(item2));
        Assert.False(_manager.HasLocalThumbnail(item1));
        Assert.False(_manager.HasLocalThumbnail(item2));
        Assert.True(_manager.HasLocalThumbnail(item3));

        // 4. Run bulk sync
        var progressReports = new System.Collections.Concurrent.ConcurrentBag<ThumbnailSyncProgress>();
        var progress = new Progress<ThumbnailSyncProgress>(p => progressReports.Add(p));

        var result = await _manager.SyncAllThumbnailsFromNasAsync(nasRoot, progress, maxConcurrency: 2);

        // 5. Assertions
        Assert.Equal(3, result.TotalFoundInNas);
        Assert.Equal(2, result.Fetched);
        Assert.Equal(1, result.AlreadyCached);
        Assert.Equal(0, result.Failed);

        Assert.True(_manager.HasLocalThumbnail(item1));
        Assert.True(_manager.HasLocalThumbnail(item2));
        Assert.True(_manager.HasLocalThumbnail(item3));
    }

    [Fact]
    public async Task SyncAllThumbnailsFromNasAsync_ReplacesStaleVersionAndRecordsState()
    {
        string nasRoot = Path.Combine(_tempDir, "NAS", "ShelfRowThumbnails");
        var itemId = Guid.NewGuid();
        byte[] stale = { 1, 2, 3 };
        byte[] current = { 4, 5, 6, 7, 8 };
        await File.WriteAllBytesAsync(_manager.GetLocalThumbnailPath(itemId), stale);

        string nasPath = _manager.GetNasThumbnailPath(nasRoot, itemId);
        Directory.CreateDirectory(Path.GetDirectoryName(nasPath)!);
        await File.WriteAllBytesAsync(nasPath, current);
        var manifest = new ThumbnailDistributionManifest();
        manifest.SetEntry(itemId, version: 2, bytes: current.Length);
        await _manager.WriteManifestAsync(nasRoot, manifest);

        var states = new Dictionary<Guid, LocalCoverState>
        {
            [itemId] = new() { ItemId = itemId, Version = 1, Bytes = stale.Length }
        };
        var result = await _manager.SyncAllThumbnailsFromNasAsync(
            nasRoot, new[] { itemId }, states);

        Assert.Equal(1, result.Fetched);
        Assert.Equal(current, await File.ReadAllBytesAsync(_manager.GetLocalThumbnailPath(itemId)));
        var update = Assert.Single(result.StateUpdates);
        Assert.Equal(2, update.Version);
        Assert.Equal(current.Length, update.Bytes);
        Assert.Equal(0, update.Attempts);
    }

    [Fact]
    public async Task SyncAllThumbnailsFromNasAsync_StopsAfterThreeFailuresForSameVersion()
    {
        string nasRoot = Path.Combine(_tempDir, "NAS", "ShelfRowThumbnails");
        Directory.CreateDirectory(nasRoot);
        var itemId = Guid.NewGuid();
        var manifest = new ThumbnailDistributionManifest();
        manifest.SetEntry(itemId, version: 4, bytes: 100);
        await _manager.WriteManifestAsync(nasRoot, manifest);
        var states = new Dictionary<Guid, LocalCoverState>
        {
            [itemId] = new()
            {
                ItemId = itemId,
                Version = 3,
                Attempts = 3,
                AttemptedVersion = 4,
                LastErrorCode = 2
            }
        };

        var result = await _manager.SyncAllThumbnailsFromNasAsync(
            nasRoot, new[] { itemId }, states);

        Assert.Equal(1, result.SuppressedAfterFailures);
        Assert.Equal(0, result.Failed);
        Assert.Empty(result.StateUpdates);
    }

    [Fact]
    public async Task SyncAllThumbnailsFromNasAsync_DoesNotScanShardsWithoutManifest()
    {
        string nasRoot = Path.Combine(_tempDir, "NAS", "ShelfRowThumbnails");
        var itemId = Guid.NewGuid();
        string nasPath = _manager.GetNasThumbnailPath(nasRoot, itemId);
        Directory.CreateDirectory(Path.GetDirectoryName(nasPath)!);
        await File.WriteAllBytesAsync(nasPath, new byte[] { 1, 2, 3 });

        var result = await _manager.SyncAllThumbnailsFromNasAsync(nasRoot);

        Assert.True(result.ManifestMissing);
        Assert.Equal(0, result.TotalFoundInNas);
        Assert.False(_manager.HasLocalThumbnail(itemId));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }
}
