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

    public static Item? ToItem(CKRecord record)
    {
        if (record.RecordType != ItemRecordType) return null;

        var item = new Item();
        if (Guid.TryParse(record.RecordName, out var id))
        {
            item.Id = id;
        }

        if (record.Fields.TryGetValue("CD_title", out var fTitle) && fTitle.Value != null)
            item.Title = fTitle.Value.ToString() ?? "";

        if (record.Fields.TryGetValue("CD_author", out var fAuthor) && fAuthor.Value != null)
            item.Author = fAuthor.Value.ToString() ?? "";

        if (record.Fields.TryGetValue("CD_relativePath", out var fPath) && fPath.Value != null)
            item.RelativePath = fPath.Value.ToString() ?? "";

        if (record.Fields.TryGetValue("CD_rating", out var fRate) && fRate.Value != null)
            item.Rating = Convert.ToInt32(fRate.Value);

        if (record.Fields.TryGetValue("CD_isUnread", out var fUnread) && fUnread.Value != null)
            item.IsUnread = Convert.ToInt32(fUnread.Value) != 0;

        if (record.Fields.TryGetValue("CD_genre", out var fGen) && fGen.Value != null)
            item.Genre = fGen.Value.ToString() ?? "";

        if (record.Fields.TryGetValue("CD_keywordA", out var fKa) && fKa.Value != null)
            item.KeywordA = fKa.Value.ToString() ?? "";

        if (record.Fields.TryGetValue("CD_keywordB", out var fKb) && fKb.Value != null)
            item.KeywordB = fKb.Value.ToString() ?? "";

        if (record.Fields.TryGetValue("CD_memo", out var fMemo) && fMemo.Value != null)
            item.Memo = fMemo.Value.ToString() ?? "";

        if (record.Fields.TryGetValue("CD_pages", out var fPages) && fPages.Value != null)
            item.Pages = Convert.ToInt32(fPages.Value);

        if (record.Fields.TryGetValue("CD_coverVersion", out var fCvv) && fCvv.Value != null)
            item.CoverVersion = Convert.ToInt32(fCvv.Value);

        if (record.Fields.TryGetValue("CD_coverBytes", out var fCvb) && fCvb.Value != null)
            item.CoverBytes = Convert.ToInt64(fCvb.Value);

        if (record.Fields.TryGetValue("CD_legacyID", out var fLeg) && fLeg.Value != null)
            item.LegacyId = Convert.ToInt32(fLeg.Value);

        if (record.Fields.TryGetValue("CD_addedDate", out var fAdded) && fAdded.Value != null)
        {
            if (long.TryParse(fAdded.Value.ToString(), out long ms))
                item.AddedDate = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        }

        if (record.Fields.TryGetValue("CD_lastReadDate", out var fLrd) && fLrd.Value != null)
        {
            if (long.TryParse(fLrd.Value.ToString(), out long ms))
                item.LastReadDate = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        }

        if (record.Fields.TryGetValue("CD_volume", out var fVol) && fVol.Value is JsonElement je && je.TryGetProperty("recordName", out var vn))
        {
            if (Guid.TryParse(vn.GetString(), out var vId))
                item.VolumeId = vId;
        }

        return item;
    }

    public static CKRecord ToCKRecord(Item item)
    {
        var record = new CKRecord
        {
            RecordName = item.Id.ToString("D"),
            RecordType = ItemRecordType
        };

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

        if (item.VolumeId.HasValue)
        {
            record.Fields["CD_volume"] = new CKRecordField(new CKRecordReference
            {
                RecordName = item.VolumeId.Value.ToString("D")
            });
        }

        return record;
    }

    public static Shelf? ToShelf(CKRecord record)
    {
        if (record.RecordType != ShelfRecordType) return null;

        var shelf = new Shelf();
        if (Guid.TryParse(record.RecordName, out var id))
        {
            shelf.Id = id;
        }

        if (record.Fields.TryGetValue("CD_title", out var fTitle) && fTitle.Value != null)
            shelf.Title = fTitle.Value.ToString() ?? "";

        if (record.Fields.TryGetValue("CD_icon", out var fIcon) && fIcon.Value != null)
            shelf.Icon = Convert.ToInt32(fIcon.Value);

        if (record.Fields.TryGetValue("CD_type", out var fType) && fType.Value != null)
            shelf.Type = Convert.ToInt32(fType.Value);

        if (record.Fields.TryGetValue("CD_sortOrder", out var fOrder) && fOrder.Value != null)
            shelf.SortOrder = Convert.ToInt32(fOrder.Value);

        if (record.Fields.TryGetValue("CD_sortAscending", out var fAsc) && fAsc.Value != null)
            shelf.SortAscending = Convert.ToInt32(fAsc.Value) != 0;

        if (record.Fields.TryGetValue("CD_sortKey", out var fKey) && fKey.Value != null)
            shelf.SortKey = fKey.Value.ToString() ?? "title";

        if (record.Fields.TryGetValue("CD_smartConditionsJson", out var fJson) && fJson.Value != null)
            shelf.SmartConditionsJson = fJson.Value.ToString();

        return shelf;
    }

    public static CKRecord ToCKRecord(Shelf shelf)
    {
        var record = new CKRecord
        {
            RecordName = shelf.Id.ToString("D"),
            RecordType = ShelfRecordType
        };

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

        var vol = new Volume();
        if (Guid.TryParse(record.RecordName, out var id))
        {
            vol.Id = id;
        }

        if (record.Fields.TryGetValue("CD_name", out var fName) && fName.Value != null)
            vol.Name = fName.Value.ToString() ?? "";

        if (record.Fields.TryGetValue("CD_lastKnownPath", out var fPath) && fPath.Value != null)
            vol.LastKnownPath = fPath.Value.ToString() ?? "";

        return vol;
    }

    public static CKRecord ToCKRecord(Volume volume)
    {
        var record = new CKRecord
        {
            RecordName = volume.Id.ToString("D"),
            RecordType = VolumeRecordType
        };

        record.Fields["CD_name"] = new CKRecordField(volume.Name);
        record.Fields["CD_lastKnownPath"] = new CKRecordField(volume.LastKnownPath);

        return record;
    }
}
