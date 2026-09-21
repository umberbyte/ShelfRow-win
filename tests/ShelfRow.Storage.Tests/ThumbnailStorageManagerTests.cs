using System;
using System.IO;
using System.Threading.Tasks;
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

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }
}
