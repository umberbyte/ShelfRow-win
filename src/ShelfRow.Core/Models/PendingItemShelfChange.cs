using System;

namespace ShelfRow.Core.Models;

public record PendingItemShelfChange(
    Guid ItemId,
    Guid ShelfId,
    string? CloudKitRecordName,
    string ItemRecordName,
    string ShelfRecordName,
    bool IsDelete);

