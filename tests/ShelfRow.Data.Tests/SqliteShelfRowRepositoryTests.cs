using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ShelfRow.Core.Models;
using ShelfRow.Data;
using Xunit;

namespace ShelfRow.Data.Tests;

public class SqliteShelfRowRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteShelfRowRepository _repository;

    public SqliteShelfRowRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shelfrow_test_{Guid.NewGuid():N}.db");
        _repository = new SqliteShelfRowRepository(_dbPath);
    }

    [Fact]
    public async Task InitializeAndCrud_ItemLifecycle_Works()
    {
        await _repository.InitializeAsync();

        var volume = new Volume
        {
            Name = "Books Share",
            LastKnownPath = "/Volumes/Books",
            WindowsMountPath = @"\\NAS\Books"
        };
        await _repository.UpsertVolumeAsync(volume);

        var shelf = new Shelf
        {
            Title = "SciFi",
            Icon = 1,
            Type = 0,
            SortOrder = 10
        };
        await _repository.UpsertShelfAsync(shelf);

        var item = new Item
        {
            LegacyId = 42,
            VolumeId = volume.Id,
            RelativePath = "SciFi/book.zip",
            Title = "Solaris",
            Author = "Stanislaw Lem",
            Rating = 5,
            IsUnread = false,
            Genre = "Sci-Fi",
            ShelfIds = { shelf.Id }
        };
        await _repository.UpsertItemAsync(item);

        // Fetch by Id
        var fetched = await _repository.GetItemByIdAsync(item.Id);
        Assert.NotNull(fetched);
        Assert.Equal("Solaris", fetched.Title);
        Assert.Equal("Stanislaw Lem", fetched.Author);
        Assert.Equal(5, fetched.Rating);
        Assert.False(fetched.IsUnread);
        Assert.Single(fetched.ShelfIds);
        Assert.Equal(shelf.Id, fetched.ShelfIds[0]);

        // Fetch by LegacyId
        var fetchedByLegacy = await _repository.GetItemByLegacyIdAsync(42);
        Assert.NotNull(fetchedByLegacy);
        Assert.Equal(item.Id, fetchedByLegacy.Id);

        // Search test
        var searchResults = await _repository.GetItemsAsync(0, 10, search: "Solar");
        Assert.Single(searchResults);

        // Shelf filter test
        var shelfItems = await _repository.GetItemsAsync(0, 10, shelfId: shelf.Id);
        Assert.Single(shelfItems);

        // Delete test
        await _repository.DeleteItemAsync(item.Id);
        var deleted = await _repository.GetItemByIdAsync(item.Id);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task BatchUpsert_HighPerformance()
    {
        await _repository.InitializeAsync();

        var items = Enumerable.Range(1, 1000).Select(i => new Item
        {
            Title = $"Book #{i}",
            Author = $"Author #{i % 10}",
            RelativePath = $"Path/book_{i}.zip",
            Rating = i % 6,
            Pages = 100 + i
        }).ToList();

        await _repository.UpsertItemsBatchAsync(items);

        int count = await _repository.GetItemCountAsync();
        Assert.Equal(1000, count);

        var paged = await _repository.GetItemsAsync(skip: 0, take: 50);
        Assert.Equal(50, paged.Count);
    }

    [Fact]
    public async Task SyncMetadata_CanStoreAndRetrieve()
    {
        await _repository.InitializeAsync();

        await _repository.SetSyncMetadataAsync("TokenA", "xyz123");
        var val = await _repository.GetSyncMetadataAsync("TokenA");

        Assert.Equal("xyz123", val);
    }

    public void Dispose()
    {
        _repository.Dispose();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }
}
