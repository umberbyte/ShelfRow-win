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
    public async Task GetItemsForImportMerge_LoadsAllItemsAndShelfMemberships()
    {
        await _repository.InitializeAsync();

        var shelf = new Shelf { Title = "Imported shelf", Type = 0 };
        await _repository.UpsertShelfAsync(shelf);

        var first = new Item
        {
            LegacyId = 101,
            Title = "First",
            RelativePath = "first.zip",
            ShelfIds = { shelf.Id }
        };
        var second = new Item
        {
            LegacyId = 202,
            Title = "Second",
            RelativePath = "second.zip"
        };
        await _repository.UpsertItemsBatchAsync(new[] { first, second });

        var items = await _repository.GetItemsForImportMergeAsync();

        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Id == first.Id && item.ShelfIds.SequenceEqual(new[] { shelf.Id }));
        Assert.Contains(items, item => item.Id == second.Id && item.ShelfIds.Count == 0);
    }

    [Fact]
    public async Task LocalCoverStates_RoundTripWithoutEnteringCloudUploadQueue()
    {
        await _repository.InitializeAsync();
        var itemId = Guid.NewGuid();
        var state = new LocalCoverState
        {
            ItemId = itemId,
            Version = 7,
            Bytes = 12345,
            PendingUpload = true,
            Attempts = 2,
            AttemptedVersion = 8,
            LastErrorCode = 53
        };

        await _repository.UpsertLocalCoverStatesAsync(new[] { state });

        var loaded = await _repository.GetLocalCoverStateAsync(itemId);
        Assert.NotNull(loaded);
        Assert.Equal(7, loaded.Version);
        Assert.Equal(12345, loaded.Bytes);
        Assert.True(loaded.PendingUpload);
        Assert.Equal(2, loaded.Attempts);
        Assert.Equal(8, loaded.AttemptedVersion);
        Assert.Equal(53, loaded.LastErrorCode);

        var pending = await _repository.GetPendingUploadsAsync();
        Assert.Empty(pending.Items);
        Assert.Empty(pending.Shelves);
        Assert.Empty(pending.Volumes);
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

    /// <summary>
    /// A smart shelf stores no membership, so its contents have to be computed from its
    /// conditions at query time. The conditions string here is the live library's.
    /// </summary>
    [Fact]
    public async Task SmartShelf_FiltersByConditions_WithoutStoredMembership()
    {
        await _repository.InitializeAsync();

        var smart = new Shelf
        {
            Title = "Bavel",
            Type = 1,
            SmartConditionsJson = """{"Keyword Condition":{"Key":"Bavel","Condition":"Title","Option":0}}"""
        };
        await _repository.UpsertShelfAsync(smart);

        await _repository.UpsertItemsBatchAsync(new[]
        {
            new Item { Title = "Bavel 2018-01", RelativePath = "a.zip" },
            new Item { Title = "COMIC BAVEL 2019-05", RelativePath = "b.zip" },
            new Item { Title = "Something Else", RelativePath = "c.zip" }
        });

        var matched = await _repository.GetItemsAsync(0, 100, smart.Id);

        Assert.Equal(2, matched.Count);
        Assert.Equal(2, await _repository.GetItemCountAsync(smart.Id));
        Assert.All(matched, item => Assert.Contains("bavel", item.Title.ToLowerInvariant()));
    }

    [Fact]
    public async Task SmartShelf_CombinesConditionsWithAnd()
    {
        await _repository.InitializeAsync();

        var smart = new Shelf
        {
            Title = "未読の高評価",
            Type = 1,
            SmartConditionsJson = """{"Rate Condition":{"Key":[4,5]},"Unseen Condition":{"Key":true}}"""
        };
        await _repository.UpsertShelfAsync(smart);

        await _repository.UpsertItemsBatchAsync(new[]
        {
            new Item { Title = "unread five", RelativePath = "a.zip", Rating = 5, IsUnread = true },
            new Item { Title = "read five", RelativePath = "b.zip", Rating = 5, IsUnread = false },
            new Item { Title = "unread two", RelativePath = "c.zip", Rating = 2, IsUnread = true }
        });

        var matched = await _repository.GetItemsAsync(0, 100, smart.Id);

        Assert.Single(matched);
        Assert.Equal("unread five", matched[0].Title);
    }

    /// <summary>
    /// A manual shelf must keep using its stored membership even though the smart path
    /// now shares the same entry point.
    /// </summary>
    [Fact]
    public async Task ManualShelf_StillUsesStoredMembership()
    {
        await _repository.InitializeAsync();

        var shelf = new Shelf { Title = "Manual", Type = 0 };
        await _repository.UpsertShelfAsync(shelf);

        var member = new Item { Title = "In the shelf", RelativePath = "a.zip", ShelfIds = { shelf.Id } };
        await _repository.UpsertItemsBatchAsync(new[]
        {
            member,
            new Item { Title = "Not in the shelf", RelativePath = "b.zip" }
        });

        var matched = await _repository.GetItemsAsync(0, 100, shelf.Id);

        Assert.Single(matched);
        Assert.Equal("In the shelf", matched[0].Title);
    }

    /// <summary>
    /// An edit made here has to reach iCloud; a record that came from iCloud must not be
    /// sent straight back.
    /// </summary>
    [Fact]
    public async Task OnlyLocalEdits_AreQueuedForUpload()
    {
        await _repository.InitializeAsync();

        var fromCloud = new Item { Title = "From iCloud", RelativePath = "a.zip", CloudKitRecordName = "R1" };
        await _repository.UpsertItemsBatchAsync(new[] { fromCloud }, markPendingUpload: false);

        var editedHere = new Item { Title = "Edited here", RelativePath = "b.zip" };
        await _repository.UpsertItemAsync(editedHere);

        var pending = await _repository.GetPendingUploadsAsync();

        Assert.Single(pending.Items);
        Assert.Equal("Edited here", pending.Items[0].Title);
    }

    [Fact]
    public async Task ConfirmUploaded_ClearsTheQueueAndStoresTheServerVersion()
    {
        await _repository.InitializeAsync();

        var item = new Item { Title = "Edited here", RelativePath = "a.zip" };
        await _repository.UpsertItemAsync(item);

        await _repository.ConfirmUploadedAsync("Items", item.Id, "SERVER-RECORD-NAME", "tag-2");

        var pending = await _repository.GetPendingUploadsAsync();
        Assert.Empty(pending.Items);

        var stored = await _repository.GetItemByIdAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal("SERVER-RECORD-NAME", stored.CloudKitRecordName);
        Assert.Equal("tag-2", stored.CloudKitChangeTag);
    }

    /// <summary>
    /// A download landing on top of an edit that has not gone up yet must not drop it,
    /// or the edit would be lost with no sign that anything happened.
    /// </summary>
    [Fact]
    public async Task IncomingRecord_DoesNotClearAnUnsentLocalEdit()
    {
        await _repository.InitializeAsync();

        var item = new Item { Title = "Edited here", RelativePath = "a.zip" };
        await _repository.UpsertItemAsync(item);

        await _repository.UpsertItemsBatchAsync(new[]
        {
            new Item { Id = item.Id, Title = "Server copy", RelativePath = "a.zip", CloudKitRecordName = "R1" }
        }, markPendingUpload: false);

        var pending = await _repository.GetPendingUploadsAsync();
        Assert.Single(pending.Items);
    }

    [Fact]
    public async Task MarkAllPendingUpload_QueuesEveryRow()
    {
        await _repository.InitializeAsync();

        await _repository.UpsertItemsBatchAsync(new[]
        {
            new Item { Title = "a", RelativePath = "a.zip" },
            new Item { Title = "b", RelativePath = "b.zip" }
        }, markPendingUpload: false);
        await _repository.UpsertShelfAsync(new Shelf { Title = "s" }, markPendingUpload: false);
        await _repository.UpsertVolumeAsync(new Volume { Name = "v" }, markPendingUpload: false);

        Assert.Equal(0, (await _repository.GetPendingUploadsAsync()).Count);

        await _repository.MarkAllPendingUploadAsync();

        var pending = await _repository.GetPendingUploadsAsync();
        Assert.Equal(2, pending.Items.Count);
        Assert.Single(pending.Shelves);
        Assert.Single(pending.Volumes);
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
