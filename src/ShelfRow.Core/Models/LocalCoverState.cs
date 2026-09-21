using System;

namespace ShelfRow.Core.Models;

/// <summary>
/// Device-local bookkeeping for a cached cover. This table is never uploaded to
/// CloudKit; only the small NAS manifest is shared between devices.
/// </summary>
public class LocalCoverState
{
    public Guid ItemId { get; set; }
    public int Version { get; set; }
    public long Bytes { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool PendingUpload { get; set; }
    public int Attempts { get; set; }
    public int AttemptedVersion { get; set; }
    public int LastErrorCode { get; set; }
}

