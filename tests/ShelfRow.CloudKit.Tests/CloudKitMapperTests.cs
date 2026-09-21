using System;
using ShelfRow.CloudKit;
using ShelfRow.Core.Models;
using Xunit;

namespace ShelfRow.CloudKit.Tests;

public class CloudKitMapperTests
{
    [Fact]
    public void Item_ToCKRecord_And_Back_MaintainsData()
    {
        var item = new Item
        {
            Id = Guid.NewGuid(),
            LegacyId = 555,
            Title = "CloudKit Sync Test",
            Author = "Author Name",
            Rating = 4,
            IsUnread = false,
            Genre = "Tech",
            RelativePath = "Folder/file.zip",
            CoverVersion = 3,
            CoverBytes = 102400,
            AddedDate = new DateTime(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc)
        };

        var ckRecord = CloudKitMapper.ToCKRecord(item);
        Assert.Equal(CloudKitMapper.ItemRecordType, ckRecord.RecordType);
        Assert.Equal(item.Id.ToString("D"), ckRecord.RecordName);

        var restored = CloudKitMapper.ToItem(ckRecord);
        Assert.NotNull(restored);
        Assert.Equal(item.Id, restored.Id);
        Assert.Equal(item.LegacyId, restored.LegacyId);
        Assert.Equal(item.Title, restored.Title);
        Assert.Equal(item.Author, restored.Author);
        Assert.Equal(item.Rating, restored.Rating);
        Assert.Equal(item.IsUnread, restored.IsUnread);
        Assert.Equal(item.Genre, restored.Genre);
        Assert.Equal(item.RelativePath, restored.RelativePath);
        Assert.Equal(item.CoverVersion, restored.CoverVersion);
        Assert.Equal(item.CoverBytes, restored.CoverBytes);
        Assert.Equal(item.AddedDate, restored.AddedDate);
    }

    [Fact]
    public void Shelf_ToCKRecord_And_Back_MaintainsData()
    {
        var shelf = new Shelf
        {
            Id = Guid.NewGuid(),
            Title = "Manga Shelf",
            Icon = 3,
            Type = 1,
            SortOrder = 20,
            SortAscending = true,
            SortKey = "author",
            SmartConditionsJson = "{\"field\":\"genre\",\"value\":\"Manga\"}"
        };

        var ckRecord = CloudKitMapper.ToCKRecord(shelf);
        Assert.Equal(CloudKitMapper.ShelfRecordType, ckRecord.RecordType);

        var restored = CloudKitMapper.ToShelf(ckRecord);
        Assert.NotNull(restored);
        Assert.Equal(shelf.Id, restored.Id);
        Assert.Equal(shelf.Title, restored.Title);
        Assert.Equal(shelf.Icon, restored.Icon);
        Assert.Equal(shelf.Type, restored.Type);
        Assert.Equal(shelf.SortOrder, restored.SortOrder);
        Assert.Equal(shelf.SortKey, restored.SortKey);
        Assert.Equal(shelf.SmartConditionsJson, restored.SmartConditionsJson);
    }

    [Fact]
    public void Volume_ToCKRecord_And_Back_MaintainsData()
    {
        var volume = new Volume
        {
            Id = Guid.NewGuid(),
            Name = "Manga Share",
            LastKnownPath = "/Volumes/Manga"
        };

        var ckRecord = CloudKitMapper.ToCKRecord(volume);
        Assert.Equal(CloudKitMapper.VolumeRecordType, ckRecord.RecordType);

        var restored = CloudKitMapper.ToVolume(ckRecord);
        Assert.NotNull(restored);
        Assert.Equal(volume.Id, restored.Id);
        Assert.Equal(volume.Name, restored.Name);
        Assert.Equal(volume.LastKnownPath, restored.LastKnownPath);
    }
}
