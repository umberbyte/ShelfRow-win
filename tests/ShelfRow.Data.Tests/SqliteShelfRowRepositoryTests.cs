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

    /// <summary>
    /// The zone names a link's endpoints by CloudKit record name and gives no ordering
    /// guarantee, so a link must still attach when it is seen before its item and shelf.
    /// </summary>
    [Fact]
    public async Task ItemShelfLinks_ResolveByRecordName_RegardlessOfArrivalOrder()
    {
        await _repository.InitializeAsync();

        var link = new ItemShelfLink(
            RecordName: "D846ED40-C227-48C9-B621-EBBCD385D5D8",
            ItemRecordName: "05140F16-9D4B-4470-87CA-73431CEA040C",
            ShelfRecordName: "7CC916B2-9460-4647-BDBC-9093D41C714B");

        Assert.Equal(0, await _repository.ApplyItemShelfLinksAsync(new[] { link }));

        var shelf = new Shelf { Title = "Doujin", CloudKitRecordName = link.ShelfRecordName };
        await _repository.UpsertShelfAsync(shelf);

        var item = new Item { Title = "A Book", RelativePath = "a.zip", CloudKitRecordName = link.ItemRecordName };
        await _repository.UpsertItemsBatchAsync(new[] { item });

        Assert.Equal(1, await _repository.ApplyItemShelfLinksAsync(new[] { link }));

        var stored = await _repository.GetItemByIdAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Contains(shelf.Id, stored.ShelfIds);

        // A deleted CDMR record identifies only itself, so removal has to work from its
        // own record name.
        await _repository.DeleteByCloudKitRecordNameAsync(link.RecordName);

        var afterDelete = await _repository.GetItemByIdAsync(item.Id);
        Assert.NotNull(afterDelete);
        Assert.Empty(afterDelete.ShelfIds);
    }

    [Fact]
    public async Task VolumeReference_IsResolvedFromRecordName_AndKeepsWindowsMountPath()
    {
        await _repository.InitializeAsync();

        var volume = new Volume
        {
            Name = "Files",
            LastKnownPath = "/Volumes/Files",
            WindowsMountPath = @"\\NAS\Files",
            CloudKitRecordName = "3EC13F09-CBC3-4EBE-888F-18A6298B6EC0"
        };
        await _repository.UpsertVolumeAsync(volume);

        var item = new Item
        {
            Title = "A Book",
            RelativePath = "a.zip",
            CloudKitRecordName = "54D142FC-04F3-47A6-ADC5-F5B24E6BB46A",
            VolumeRecordName = volume.CloudKitRecordName
        };
        await _repository.UpsertItemsBatchAsync(new[] { item });

        Assert.Equal(1, await _repository.ResolveVolumeReferencesAsync());

        var stored = await _repository.GetItemByIdAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(volume.Id, stored.VolumeId);

        // A volume arriving from sync carries no Windows path; it must not wipe this
        // machine's mapping.
        await _repository.UpsertVolumeAsync(new Volume
        {
            Id = volume.Id,
            Name = volume.Name,
            LastKnownPath = volume.LastKnownPath,
            CloudKitRecordName = volume.CloudKitRecordName
        });

        var storedVolume = await _repository.GetVolumeByIdAsync(volume.Id);
        Assert.NotNull(storedVolume);
        Assert.Equal(@"\\NAS\Files", storedVolume.WindowsMountPath);
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
