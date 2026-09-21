using System;
using System.Text.Json;
using ShelfRow.CloudKit;
using ShelfRow.Core.Models;
using Xunit;

namespace ShelfRow.CloudKit.Tests;

public class CloudKitMapperTests
{
    [Fact]
    public void ItemShelfChange_MapsToCoreDataJoinRecord()
    {
        var change = new PendingItemShelfChange(
            Guid.NewGuid(), Guid.NewGuid(), null, "ITEM-RECORD", "SHELF-RECORD", false);

        var record = CloudKitMapper.ToCKRecord(change);

        Assert.Equal(CloudKitMapper.ManyToManyRecordType, record.RecordType);
        Assert.Equal("Item:Shelf", record.Fields["CD_entityNames"].Value);
        Assert.Equal("ITEM-RECORD:SHELF-RECORD", record.Fields["CD_recordNames"].Value);
        Assert.Equal("shelves:items", record.Fields["CD_relationships"].Value);
    }
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

        item.CloudKitRecordName = "54D142FC-04F3-47A6-ADC5-F5B24E6BB46A";

        var ckRecord = CloudKitMapper.ToCKRecord(item);
        Assert.Equal(CloudKitMapper.ItemRecordType, ckRecord.RecordType);

        // The record name is Core Data's own identifier and must be round-tripped as-is;
        // the model's identity travels in CD_id.
        Assert.Equal(item.CloudKitRecordName, ckRecord.RecordName);
        Assert.Equal(item.Id.ToString("D").ToUpperInvariant(), ckRecord.Fields["CD_id"].Value);

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

    /// <summary>
    /// Records that come off the wire hold their values as <see cref="JsonElement"/>,
    /// not as CLR primitives. Reading them with Convert.ToInt32 threw on every numeric
    /// field, which took down the whole download.
    /// </summary>
    [Fact]
    public void Item_FromDeserializedRecord_ReadsNumericAndDateFields()
    {
        var source = new CKRecord
        {
            RecordName = "54D142FC-04F3-47A6-ADC5-F5B24E6BB46A",
            RecordType = CloudKitMapper.ItemRecordType,
            Fields =
            {
                ["CD_id"] = new CKRecordField("54D299EB-17EE-4B89-AF83-10E51D69559F"),
                ["CD_title"] = new CKRecordField("Title"),
                ["CD_rating"] = new CKRecordField(3),
                ["CD_isUnread"] = new CKRecordField(0),
                ["CD_pages"] = new CKRecordField(24),
                ["CD_coverBytes"] = new CKRecordField(102400L),
                ["CD_legacyID"] = new CKRecordField(14699),
                ["CD_addedDate"] = new CKRecordField(1534186019000L),
                ["CD_volume"] = new CKRecordField("3EC13F09-CBC3-4EBE-888F-18A6298B6EC0")
            }
        };

        string json = JsonSerializer.Serialize(source);
        var overTheWire = JsonSerializer.Deserialize<CKRecord>(json)!;

        var item = CloudKitMapper.ToItem(overTheWire);

        Assert.NotNull(item);
        Assert.Equal(Guid.Parse("54D299EB-17EE-4B89-AF83-10E51D69559F"), item.Id);
        Assert.Equal("54D142FC-04F3-47A6-ADC5-F5B24E6BB46A", item.CloudKitRecordName);
        Assert.Equal(3, item.Rating);
        Assert.False(item.IsUnread);
        Assert.Equal(24, item.Pages);
        Assert.Equal(102400L, item.CoverBytes);
        Assert.Equal(14699, item.LegacyId);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1534186019000L).UtcDateTime, item.AddedDate);
        Assert.Equal("3EC13F09-CBC3-4EBE-888F-18A6298B6EC0", item.VolumeRecordName);
    }

    /// <summary>
    /// Item-to-shelf membership lives only in CDMR join records, which encode the pair
    /// as colon-separated parallel fields.
    /// </summary>
    [Fact]
    public void ManyToManyRecord_IsReadAsItemShelfLink()
    {
        var record = new CKRecord
        {
            RecordName = "D846ED40-C227-48C9-B621-EBBCD385D5D8",
            RecordType = CloudKitMapper.ManyToManyRecordType,
            Fields =
            {
                ["CD_entityNames"] = new CKRecordField("Item:Shelf"),
                ["CD_recordNames"] = new CKRecordField("05140F16-9D4B-4470-87CA-73431CEA040C:7CC916B2-9460-4647-BDBC-9093D41C714B"),
                ["CD_relationships"] = new CKRecordField("shelves:items")
            }
        };

        var link = CloudKitMapper.ToManyToManyLink(record);

        Assert.NotNull(link);
        Assert.Equal("Item", link.Value.LeftEntity);
        Assert.Equal("Shelf", link.Value.RightEntity);
        Assert.Equal("05140F16-9D4B-4470-87CA-73431CEA040C", link.Value.LeftRecordName);
        Assert.Equal("7CC916B2-9460-4647-BDBC-9093D41C714B", link.Value.RightRecordName);
    }
}
