using System;
using System.Text.Json;
using ShelfRow.Core.Models;

namespace ShelfRow.CloudKit;

/// <summary>
/// Maps between ShelfRow .NET domain models and CoreData-mirrored CloudKit records
/// (CD_Item, CD_Shelf, CD_Volume).
/// </summary>
public static class CloudKitMapper
{
    public const string CoreDataZoneName = "com.apple.coredata.cloudkit.zone";
    public const string ItemRecordType = "CD_Item";
    public const string ShelfRecordType = "CD_Shelf";
    public const string VolumeRecordType = "CD_Volume";

    /// <summary>
    /// Core Data's join record for many-to-many relationships. Item-to-Shelf membership
    /// lives here rather than on either record.
    /// </summary>
    public const string ManyToManyRecordType = "CDMR";

    // Field values arrive as JsonElement when deserialized and as CLR primitives when a
    // record is built in code, so every accessor has to cope with both.
    private static string? StringField(CKRecord record, string name)
    {
        if (!record.Fields.TryGetValue(name, out var field) || field.Value is null)
            return null;

        return field.Value is JsonElement element
            ? element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()
            : field.Value.ToString();
    }

    private static long? LongField(CKRecord record, string name)
    {
        if (!record.Fields.TryGetValue(name, out var field) || field.Value is null)
            return null;

        if (field.Value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Number => element.TryGetInt64(out long n) ? n : null,
                JsonValueKind.String => long.TryParse(element.GetString(), out long s) ? s : null,
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
                _ => null
            };
        }

        return field.Value is IConvertible convertible
            ? Convert.ToInt64(convertible)
            : null;
    }

    private static int? IntField(CKRecord record, string name) => (int?)LongField(record, name);

    private static DateTime? DateField(CKRecord record, string name)
        => LongField(record, name) is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : null;

    public static Item? ToItem(CKRecord record)
    {
        if (record.RecordType != ItemRecordType) return null;

        var item = new Item
        {
            CloudKitRecordName = record.RecordName,
            CloudKitChangeTag = record.RecordChangeTag
        };

        // The model's own identity is CD_id. record.RecordName is Core Data's separate
        // identifier and does not equal it.
        if (Guid.TryParse(StringField(record, "CD_id"), out var id))
        {
            item.Id = id;
        }

        item.Title = StringField(record, "CD_title") ?? "";
        item.Author = StringField(record, "CD_author") ?? "";
        item.RelativePath = StringField(record, "CD_relativePath") ?? "";
        item.Genre = StringField(record, "CD_genre") ?? "";
        item.Relation = StringField(record, "CD_relation") ?? "";
        item.KeywordA = StringField(record, "CD_keywordA") ?? "";
        item.KeywordB = StringField(record, "CD_keywordB") ?? "";
        item.Memo = StringField(record, "CD_memo") ?? "";

        item.Rating = IntField(record, "CD_rating") ?? 0;
        item.IsUnread = (IntField(record, "CD_isUnread") ?? 1) != 0;
        item.Pages = IntField(record, "CD_pages") ?? 0;
        item.BookType = IntField(record, "CD_bookType") ?? 0;
        item.FileType = IntField(record, "CD_fileType") ?? 0;
        item.CoverVersion = IntField(record, "CD_coverVersion") ?? 0;
        item.CoverBytes = LongField(record, "CD_coverBytes") ?? 0;
        item.LegacyId = IntField(record, "CD_legacyID");

        item.AddedDate = DateField(record, "CD_addedDate") ?? item.AddedDate;
        item.LastReadDate = DateField(record, "CD_lastReadDate");

        item.CoverImageName = StringField(record, "CD_coverImageName") ?? "";
        item.CoverImagePath = StringField(record, "CD_coverImagePath") ?? "";

        // Core Data writes to-one relationships as the target's record name in a plain
        // string field, not as a CKReference.
        item.VolumeRecordName = StringField(record, "CD_volume");

        return item;
    }

    public static CKRecord ToCKRecord(Item item)
    {
        var record = new CKRecord
        {
            RecordName = item.CloudKitRecordName ?? Guid.NewGuid().ToString("D").ToUpperInvariant(),
            RecordType = ItemRecordType,
            RecordChangeTag = item.CloudKitChangeTag
        };

        record.Fields["CD_entityName"] = new CKRecordField("Item");
        record.Fields["CD_id"] = new CKRecordField(item.Id.ToString("D").ToUpperInvariant());
        record.Fields["CD_coverImageName"] = new CKRecordField(item.CoverImageName);
        record.Fields["CD_coverImagePath"] = new CKRecordField(item.CoverImagePath);
        record.Fields["CD_title"] = new CKRecordField(item.Title);
        record.Fields["CD_author"] = new CKRecordField(item.Author);
        record.Fields["CD_relativePath"] = new CKRecordField(item.RelativePath);
        record.Fields["CD_rating"] = new CKRecordField(item.Rating);
        record.Fields["CD_isUnread"] = new CKRecordField(item.IsUnread ? 1 : 0);
        record.Fields["CD_genre"] = new CKRecordField(item.Genre);
        record.Fields["CD_relation"] = new CKRecordField(item.Relation);
        record.Fields["CD_keywordA"] = new CKRecordField(item.KeywordA);
        record.Fields["CD_keywordB"] = new CKRecordField(item.KeywordB);
        record.Fields["CD_memo"] = new CKRecordField(item.Memo);
        record.Fields["CD_pages"] = new CKRecordField(item.Pages);
        record.Fields["CD_bookType"] = new CKRecordField(item.BookType);
        record.Fields["CD_fileType"] = new CKRecordField(item.FileType);
        record.Fields["CD_coverVersion"] = new CKRecordField(item.CoverVersion);
        record.Fields["CD_coverBytes"] = new CKRecordField(item.CoverBytes);
        record.Fields["CD_addedDate"] = new CKRecordField(new DateTimeOffset(item.AddedDate).ToUnixTimeMilliseconds());

        if (item.LegacyId.HasValue)
            record.Fields["CD_legacyID"] = new CKRecordField(item.LegacyId.Value);

        if (item.LastReadDate.HasValue)
            record.Fields["CD_lastReadDate"] = new CKRecordField(new DateTimeOffset(item.LastReadDate.Value).ToUnixTimeMilliseconds());

        if (!string.IsNullOrEmpty(item.VolumeRecordName))
            record.Fields["CD_volume"] = new CKRecordField(item.VolumeRecordName);

        return record;
    }

    public static Shelf? ToShelf(CKRecord record)
    {
        if (record.RecordType != ShelfRecordType) return null;

        var shelf = new Shelf
        {
            CloudKitRecordName = record.RecordName,
            CloudKitChangeTag = record.RecordChangeTag
        };

        if (Guid.TryParse(StringField(record, "CD_id"), out var id))
        {
            shelf.Id = id;
        }

        shelf.Title = StringField(record, "CD_title") ?? "";
        shelf.Icon = IntField(record, "CD_icon") ?? 0;
        shelf.Type = IntField(record, "CD_type") ?? 0;
        shelf.SortOrder = IntField(record, "CD_sortOrder") ?? 0;
        shelf.SortAscending = (IntField(record, "CD_sortAscending") ?? 1) != 0;
        shelf.SortKey = StringField(record, "CD_sortKey") ?? "title";
        shelf.SmartConditionsJson = StringField(record, "CD_smartConditionsJson");

        return shelf;
    }

    public static CKRecord ToCKRecord(Shelf shelf)
    {
        var record = new CKRecord
        {
            RecordName = shelf.CloudKitRecordName ?? Guid.NewGuid().ToString("D").ToUpperInvariant(),
            RecordType = ShelfRecordType,
            RecordChangeTag = shelf.CloudKitChangeTag
        };

        record.Fields["CD_entityName"] = new CKRecordField("Shelf");
        record.Fields["CD_id"] = new CKRecordField(shelf.Id.ToString("D").ToUpperInvariant());
        record.Fields["CD_title"] = new CKRecordField(shelf.Title);
        record.Fields["CD_icon"] = new CKRecordField(shelf.Icon);
        record.Fields["CD_type"] = new CKRecordField(shelf.Type);
        record.Fields["CD_sortOrder"] = new CKRecordField(shelf.SortOrder);
        record.Fields["CD_sortAscending"] = new CKRecordField(shelf.SortAscending ? 1 : 0);
        record.Fields["CD_sortKey"] = new CKRecordField(shelf.SortKey);

        if (!string.IsNullOrWhiteSpace(shelf.SmartConditionsJson))
            record.Fields["CD_smartConditionsJson"] = new CKRecordField(shelf.SmartConditionsJson);

        return record;
    }

    public static Volume? ToVolume(CKRecord record)
    {
        if (record.RecordType != VolumeRecordType) return null;

        var vol = new Volume
        {
            CloudKitRecordName = record.RecordName,
            CloudKitChangeTag = record.RecordChangeTag
        };

        if (Guid.TryParse(StringField(record, "CD_id"), out var id))
        {
            vol.Id = id;
        }

        vol.Name = StringField(record, "CD_name") ?? "";
        vol.LastKnownPath = StringField(record, "CD_lastKnownPath") ?? "";

        return vol;
    }

    public static CKRecord ToCKRecord(Volume volume)
    {
        var record = new CKRecord
        {
            RecordName = volume.CloudKitRecordName ?? Guid.NewGuid().ToString("D").ToUpperInvariant(),
            RecordType = VolumeRecordType,
            RecordChangeTag = volume.CloudKitChangeTag
        };

        record.Fields["CD_entityName"] = new CKRecordField("Volume");
        record.Fields["CD_id"] = new CKRecordField(volume.Id.ToString("D").ToUpperInvariant());
        record.Fields["CD_name"] = new CKRecordField(volume.Name);
        record.Fields["CD_lastKnownPath"] = new CKRecordField(volume.LastKnownPath);

        return record;
    }

    /// <summary>
    /// One row of Core Data's many-to-many join table, as stored in a CDMR record.
    /// </summary>
    public readonly record struct ManyToManyLink(
        string LeftEntity,
        string RightEntity,
        string LeftRecordName,
        string RightRecordName);

    /// <summary>
    /// Reads a CDMR record, which encodes the pair as three parallel colon-separated
    /// fields: CD_entityNames "Item:Shelf", CD_recordNames "&lt;item&gt;:&lt;shelf&gt;",
    /// CD_relationships "shelves:items".
    /// </summary>
    public static ManyToManyLink? ToManyToManyLink(CKRecord record)
    {
        if (record.RecordType != ManyToManyRecordType) return null;

        var entities = StringField(record, "CD_entityNames")?.Split(':');
        var names = StringField(record, "CD_recordNames")?.Split(':');

        if (entities is not { Length: 2 } || names is not { Length: 2 })
            return null;

        return new ManyToManyLink(entities[0], entities[1], names[0], names[1]);
    }
}
