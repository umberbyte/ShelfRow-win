namespace ShelfRow.Core.Models;

/// <summary>
/// One item-to-shelf membership as the CloudKit zone expresses it: a pair of record
/// names plus the name of the join record itself, which is what a later deletion of
/// that membership will refer to.
/// </summary>
public record ItemShelfLink(string RecordName, string ItemRecordName, string ShelfRecordName);
